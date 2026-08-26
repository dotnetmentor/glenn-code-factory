using System.Net;
using Microsoft.EntityFrameworkCore;
using Source.Features.BoxManagement;
using Source.Features.BoxManagement.Models;
using Source.Infrastructure;

namespace Source.Features.RuntimeLifecycle.BoxSnapshot;

/// <summary>
/// Default implementation of <see cref="IRuntimeBoxSnapshotService"/>. One read-only
/// pass: load the <see cref="Models.ProjectRuntime"/>, pull the last 20
/// <see cref="BoxOperation"/> rows, optionally hit the Box API for the live
/// view, then assemble the envelope.
///
/// <para><b>Snapshot clock.</b> A single <c>now</c> is stamped at the top of
/// <see cref="GetAsync"/> and used for <see cref="BoxSnapshotResponse.GeneratedAt"/>.
/// Matches the drift service's pattern — all derived "since" values stay consistent
/// against one clock value.</para>
///
/// <para><b>Fly failure handling.</b> If <see cref="BoxClient.GetBoxAsync"/>
/// throws a <see cref="BoxApiException"/> (including 404 for a destroyed machine) or
/// the call fails at the transport layer, we log a warning and leave
/// <see cref="BoxSnapshotResponse.BoxView"/> null. The operator panel still renders
/// the DB half + recent ops timeline — which is exactly the triage surface they need
/// when Box is the thing that's broken.</para>
/// </summary>
public sealed class RuntimeBoxSnapshotService : IRuntimeBoxSnapshotService
{
    /// <summary>
    /// Cap on the recent-operations timeline. 20 rows comfortably covers a
    /// crash-loop / suspend-wake oscillation while keeping the worst-case JSON
    /// payload bounded even with multi-KB Fly request/response bodies inlined.
    /// </summary>
    public const int RecentOperationsLimit = 20;

    private readonly ApplicationDbContext _db;
    private readonly BoxClient _box;
    private readonly ILogger<RuntimeBoxSnapshotService> _logger;

    public RuntimeBoxSnapshotService(
        ApplicationDbContext db,
        BoxClient box,
        ILogger<RuntimeBoxSnapshotService> logger)
    {
        _db = db;
        _box = box;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<BoxSnapshotResponse?> GetAsync(Guid runtimeId, CancellationToken ct = default)
    {
        // Snapshot the clock up front so GeneratedAt reflects "we started building this
        // view at T0" rather than "we finished some milliseconds later" — matches the
        // drift service's pattern and keeps the timestamp meaningfully aligned with the
        // data the operator is looking at.
        var now = DateTime.UtcNow;

        // ---- 1. Load the runtime. AsNoTracking — purely read-only path. ----
        // The global soft-delete filter on ProjectRuntime already hides Deleted rows,
        // which is the right behaviour: a soft-deleted runtime belongs in the audit
        // trail, not in an active operator panel.
        var runtime = await _db.ProjectRuntimes
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == runtimeId, ct);

        if (runtime is null)
        {
            return null;
        }

        // ---- 2. Pull the last N operations for this runtime. ----
        // Sorted newest-first because that's how operators read incident timelines —
        // most recent attempt at the top, scroll down to see what led up to it.
        var recentOps = await _db.BoxOperations
            .AsNoTracking()
            .Where(o => o.RuntimeId == runtimeId)
            .OrderByDescending(o => o.CreatedAt)
            .Take(RecentOperationsLimit)
            .Select(o => new BoxOperationView
            {
                Id = o.Id,
                Operation = o.Operation,
                Status = o.Status.ToString(),
                HttpStatusCode = o.HttpStatusCode,
                LatencyMs = o.LatencyMs,
                ErrorCode = o.ErrorCode,
                CreatedAt = o.CreatedAt,
                RequestPayload = o.RequestPayload,
                ResponsePayload = o.ResponsePayload,
            })
            .ToListAsync(ct);

        // ---- 3. Build the "our DB" half. ----
        var ourView = new OurRuntimeView
        {
            RuntimeId = runtime.Id,
            ProjectId = runtime.ProjectId,
            State = runtime.State.ToString(),
            Region = runtime.Region,
            BoxId = runtime.BoxId,
            LastHeartbeatAt = runtime.LastHeartbeatAt,
            StateChangedAt = runtime.StateChangedAt,
            CreatedAt = runtime.CreatedAt,
        };

        // ---- 4. Best-effort Box view. ----
        // Three reasons BoxView ends up null:
        //   a) The runtime is still pre-Booting and has no BoxId yet.
        //   b) Box returns 404 — the box was deleted. The DB row hasn't caught up
        //      yet; that IS the drift.
        //   c) Box is unreachable (BoxApiException for non-404 / transport blow-up).
        // In every case the panel must still render — the DB half + ops timeline is
        // exactly what the operator needs to triage the disconnect.
        BoxVmView? boxView = null;
        if (!string.IsNullOrEmpty(runtime.BoxId))
        {
            try
            {
                var vm = await _box.GetBoxAsync(runtime.BoxId, ct);
                boxView = new BoxVmView
                {
                    Id = vm.Id,
                    Name = vm.Name,
                    Status = vm.State,
                    Size = vm.Type,
                    Region = vm.Region,
                    TtlSeconds = vm.TtlSeconds,
                    CreatedAt = vm.CreatedAt,
                };
            }
            catch (BoxApiException ex) when (ex.StatusCode == (int)HttpStatusCode.NotFound)
            {
                // The box is gone from Box's side. That's information, not an error —
                // the snapshot's purpose is to expose exactly this kind of drift. Log
                // at Information so it doesn't pollute the warning channel.
                _logger.LogInformation(
                    "BoxSnapshot for runtime {RuntimeId}: box {BoxId} returned 404; boxView=null",
                    runtimeId, runtime.BoxId);
            }
            catch (BoxApiException ex)
            {
                // Box is reachable but the call failed — auth, rate-limit, 5xx, etc.
                // Surface as a warning but DO NOT propagate: the panel is the operator's
                // tool for diagnosing exactly this kind of upstream trouble.
                _logger.LogWarning(
                    ex,
                    "BoxSnapshot for runtime {RuntimeId}: Box GetBox failed with {StatusCode} {ErrorCode}; boxView=null",
                    runtimeId, ex.StatusCode, ex.ErrorCode);
            }
            catch (HttpRequestException ex)
            {
                // Transport-level failure (DNS, timeout, connection reset). Same
                // treatment as BoxApiException — log + null view, keep the rest.
                _logger.LogWarning(
                    ex,
                    "BoxSnapshot for runtime {RuntimeId}: transport error reaching Box API; boxView=null",
                    runtimeId);
            }
        }

        _logger.LogInformation(
            "BoxSnapshot built for runtime {RuntimeId}: dbState={DbState} boxStatus={BoxStatus} ops={OpCount}",
            runtimeId, runtime.State, boxView?.Status ?? "n/a", recentOps.Count);

        return new BoxSnapshotResponse
        {
            OurView = ourView,
            BoxView = boxView,
            RecentOperations = recentOps,
            GeneratedAt = now,
        };
    }
}
