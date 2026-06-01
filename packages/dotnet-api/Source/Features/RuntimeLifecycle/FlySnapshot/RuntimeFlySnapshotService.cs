using System.Net;
using Microsoft.EntityFrameworkCore;
using Source.Features.FlyManagement;
using Source.Features.FlyManagement.Models;
using Source.Infrastructure;

namespace Source.Features.RuntimeLifecycle.FlySnapshot;

/// <summary>
/// Default implementation of <see cref="IRuntimeFlySnapshotService"/>. One read-only
/// pass: load the <see cref="Models.ProjectRuntime"/>, pull the last 20
/// <see cref="FlyOperation"/> rows, optionally hit Fly's machines API for the live
/// view, then assemble the envelope.
///
/// <para><b>Snapshot clock.</b> A single <c>now</c> is stamped at the top of
/// <see cref="GetAsync"/> and used for <see cref="FlySnapshotResponse.GeneratedAt"/>.
/// Matches the drift service's pattern — all derived "since" values stay consistent
/// against one clock value.</para>
///
/// <para><b>Fly failure handling.</b> If <see cref="FlyClient.GetMachineAsync"/>
/// throws a <see cref="FlyApiException"/> (including 404 for a destroyed machine) or
/// the call fails at the transport layer, we log a warning and leave
/// <see cref="FlySnapshotResponse.FlyView"/> null. The operator panel still renders
/// the DB half + recent ops timeline — which is exactly the triage surface they need
/// when Fly is the thing that's broken.</para>
/// </summary>
public sealed class RuntimeFlySnapshotService : IRuntimeFlySnapshotService
{
    /// <summary>
    /// Cap on the recent-operations timeline. 20 rows comfortably covers a
    /// crash-loop / suspend-wake oscillation while keeping the worst-case JSON
    /// payload bounded even with multi-KB Fly request/response bodies inlined.
    /// </summary>
    public const int RecentOperationsLimit = 20;

    private readonly ApplicationDbContext _db;
    private readonly FlyClient _fly;
    private readonly ILogger<RuntimeFlySnapshotService> _logger;

    public RuntimeFlySnapshotService(
        ApplicationDbContext db,
        FlyClient fly,
        ILogger<RuntimeFlySnapshotService> logger)
    {
        _db = db;
        _fly = fly;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<FlySnapshotResponse?> GetAsync(Guid runtimeId, CancellationToken ct = default)
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
        var recentOps = await _db.FlyOperations
            .AsNoTracking()
            .Where(o => o.RuntimeId == runtimeId)
            .OrderByDescending(o => o.CreatedAt)
            .Take(RecentOperationsLimit)
            .Select(o => new FlyOperationView
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
            FlyMachineId = runtime.FlyMachineId,
            LastHeartbeatAt = runtime.LastHeartbeatAt,
            StateChangedAt = runtime.StateChangedAt,
            CreatedAt = runtime.CreatedAt,
        };

        // ---- 4. Best-effort Fly view. ----
        // Three reasons FlyView ends up null:
        //   a) The runtime is still pre-Booting and has no FlyMachineId yet.
        //   b) Fly returns 404 — machine was destroyed (suspended-and-deleted, force-
        //      destroyed, etc). The DB row hasn't caught up yet; that IS the drift.
        //   c) Fly is unreachable (FlyApiException for non-404 / transport blow-up).
        // In every case the panel must still render — the DB half + ops timeline is
        // exactly what the operator needs to triage the disconnect.
        FlyMachineView? flyView = null;
        if (!string.IsNullOrEmpty(runtime.FlyMachineId))
        {
            try
            {
                var machine = await _fly.GetMachineAsync(runtime.FlyMachineId, ct);
                flyView = new FlyMachineView
                {
                    Id = machine.Id,
                    Name = machine.Name,
                    State = machine.State,
                    Region = machine.Region,
                    InstanceId = machine.InstanceId,
                    PrivateIp = machine.PrivateIp,
                    CreatedAt = machine.CreatedAt,
                };
            }
            catch (FlyApiException ex) when (ex.StatusCode == (int)HttpStatusCode.NotFound)
            {
                // Machine is gone from Fly's side. That's information, not an error —
                // the snapshot's purpose is to expose exactly this kind of drift. Log
                // at Information so it doesn't pollute the warning channel.
                _logger.LogInformation(
                    "FlySnapshot for runtime {RuntimeId}: Fly machine {MachineId} returned 404; flyView=null",
                    runtimeId, runtime.FlyMachineId);
            }
            catch (FlyApiException ex)
            {
                // Fly is reachable but the call failed — auth, rate-limit, 5xx, etc.
                // Surface as a warning but DO NOT propagate: the panel is the operator's
                // tool for diagnosing exactly this kind of upstream trouble.
                _logger.LogWarning(
                    ex,
                    "FlySnapshot for runtime {RuntimeId}: Fly GetMachine failed with {StatusCode} {ErrorCode}; flyView=null",
                    runtimeId, ex.StatusCode, ex.ErrorCode);
            }
            catch (HttpRequestException ex)
            {
                // Transport-level failure (DNS, timeout, connection reset). Same
                // treatment as FlyApiException — log + null view, keep the rest.
                _logger.LogWarning(
                    ex,
                    "FlySnapshot for runtime {RuntimeId}: transport error reaching Fly API; flyView=null",
                    runtimeId);
            }
        }

        _logger.LogInformation(
            "FlySnapshot built for runtime {RuntimeId}: dbState={DbState} flyState={FlyState} ops={OpCount}",
            runtimeId, runtime.State, flyView?.State ?? "n/a", recentOps.Count);

        return new FlySnapshotResponse
        {
            OurView = ourView,
            FlyView = flyView,
            RecentOperations = recentOps,
            GeneratedAt = now,
        };
    }
}
