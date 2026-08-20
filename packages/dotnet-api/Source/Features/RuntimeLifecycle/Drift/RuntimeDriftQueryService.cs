using Microsoft.EntityFrameworkCore;
using Source.Features.BoxManagement;
using Source.Features.BoxManagement.Models;
using Source.Features.RuntimeLifecycle.Models;
using Source.Infrastructure;

namespace Source.Features.RuntimeLifecycle.Drift;

/// <summary>
/// Default implementation of <see cref="IRuntimeDriftQueryService"/>. Pulls
/// every non-soft-deleted <see cref="ProjectRuntime"/> in one shot (with
/// Project → Workspace and Branch eager-loaded so the DTO can carry display
/// names), pulls Box's listing in one shot, then walks both in memory
/// to produce the merged drift view.
///
/// <para><b>Orphan filtering heuristic.</b> Project runtime boxes are forked
/// via <c>RuntimeProvisionerJob</c> / <c>RespawnRuntimeJob</c> / CopyBranch with
/// the name pattern <c>"rt-{runtime.Id:N}"[..30]</c> (see
/// <c>BoxRuntimeProvisioning.BuildBoxName</c>). Anything else on the account —
/// golden template boxes, scratch boxes an operator forked by hand — won't share
/// that prefix. We therefore treat any box whose name starts with <c>"rt-"</c>
/// AND isn't referenced by a <see cref="ProjectRuntime.BoxId"/> as a true
/// runtime orphan; other names are skipped so template noise doesn't pollute
/// the operator view.</para>
/// </summary>
public sealed class RuntimeDriftQueryService : IRuntimeDriftQueryService
{
    /// <summary>
    /// Name prefix every project-runtime box is forked with — see
    /// <c>BoxRuntimeProvisioning.BuildBoxName</c>. Anything else on the account
    /// (golden templates, scratch boxes) is not eligible to be reported as an
    /// orphan runtime.
    /// </summary>
    public const string ProjectRuntimeBoxNamePrefix = "rt-";

    private readonly ApplicationDbContext _db;
    private readonly BoxClient _box;
    private readonly ILogger<RuntimeDriftQueryService> _logger;

    public RuntimeDriftQueryService(
        ApplicationDbContext db,
        BoxClient box,
        ILogger<RuntimeDriftQueryService> logger)
    {
        _db = db;
        _box = box;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<RuntimeDriftListResponse> BuildSnapshotAsync(CancellationToken ct = default)
    {
        // Stamp the snapshot time BEFORE the DB / Box round trips so all the
        // "seconds since X" deltas in the DTOs are computed against a single
        // consistent clock value — otherwise rows further down the list would
        // drift seconds-of-clock relative to the first one.
        var now = DateTime.UtcNow;

        // ---- 1. Load every active runtime with the joins we need for the DTO. ----
        // Global soft-delete filter on ProjectRuntime already hides Deleted rows;
        // we explicitly skip RuntimeState.Deleted as well so a transient row that's
        // walked the terminal-state edge but not yet been soft-deleted doesn't
        // surface in the operator view either. (Reconciler / janitor handle that.)
        var runtimes = await _db.ProjectRuntimes
            .AsNoTracking()
            .Include(r => r.Project)
                .ThenInclude(p => p.Workspace)
            .Include(r => r.Branch)
            .Where(r => r.State != RuntimeState.Deleted)
            .ToListAsync(ct);

        // ---- 2. Pull Box's view once. BoxApiException bubbles to the controller. ----
        var boxes = await _box.ListBoxesAsync(ct);
        var boxById = boxes.ToDictionary(b => b.Id, b => b);

        // ---- 3. Build a DTO per runtime + evaluate the drift rules. ----
        var items = new List<RuntimeDriftDto>(runtimes.Count + 4);

        // Track which box ids are claimed by a runtime so the orphan
        // pass below can subtract them out in O(n). Using a HashSet because
        // multiple runtimes claiming the same machine id is "shouldn't happen"
        // but we want the dedup to be cheap if it does.
        var claimedBoxIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var runtime in runtimes)
        {
            BoxVm? boxVm = null;
            if (!string.IsNullOrEmpty(runtime.BoxId)
                && boxById.TryGetValue(runtime.BoxId, out var match))
            {
                boxVm = match;
                claimedBoxIds.Add(runtime.BoxId);
            }

            var (severity, reasons) = DriftEvaluator.EvaluateRuntime(runtime, boxVm, now);

            items.Add(new RuntimeDriftDto
            {
                RuntimeId = runtime.Id,
                ProjectId = runtime.ProjectId,
                ProjectName = runtime.Project?.Name,
                WorkspaceSlug = runtime.Project?.Workspace?.Slug,
                BranchId = runtime.BranchId,
                BranchName = runtime.Branch?.Name,
                DbState = runtime.State,
                BoxStatus = boxVm?.Status,
                BoxId = runtime.BoxId,
                // Prefer the Box-reported region when we have it (it's the live
                // truth); fall back to the DB's snapshot when the box is gone.
                Region = boxVm?.Region ?? runtime.Region,
                LastHeartbeatAt = runtime.LastHeartbeatAt,
                SecondsSinceHeartbeat = runtime.LastHeartbeatAt is null
                    ? null
                    : (int)Math.Max(0, (now - runtime.LastHeartbeatAt.Value).TotalSeconds),
                StateChangedAt = runtime.StateChangedAt,
                SecondsSinceStateChange = (int)Math.Max(0, (now - runtime.StateChangedAt).TotalSeconds),
                DriftSeverity = severity,
                DriftReasons = reasons,
            });
        }

        // ---- 4. Orphan pass. Any box whose name signals "project runtime" but
        // that no DB row references is added as an orphan DTO. We deliberately do
        // NOT flag arbitrary boxes — the account also holds the golden templates
        // and whatever an operator forked by hand.
        foreach (var boxVm in boxes)
        {
            if (claimedBoxIds.Contains(boxVm.Id)) continue;
            if (string.IsNullOrEmpty(boxVm.Name)) continue;
            if (!boxVm.Name.StartsWith(ProjectRuntimeBoxNamePrefix, StringComparison.Ordinal)) continue;

            items.Add(DriftEvaluator.BuildOrphanDto(boxVm));
        }

        // ---- 5. Sort: severity desc, then secondsSinceStateChange desc as a
        // stable secondary sort so the longest-running incidents in each bucket
        // float to the top. Orphans have null seconds-since-state-change; treat
        // those as 0 in the secondary key so they sort below same-severity rows
        // that have a real age (orphans are still surfaced via severity Critical,
        // which is the primary ordering).
        items.Sort((a, b) =>
        {
            var sev = ((int)b.DriftSeverity).CompareTo((int)a.DriftSeverity);
            if (sev != 0) return sev;
            return (b.SecondsSinceStateChange ?? 0).CompareTo(a.SecondsSinceStateChange ?? 0);
        });

        var driftCount = items.Count(i => i.DriftSeverity != DriftSeverity.Ok);

        _logger.LogInformation(
            "RuntimeDrift snapshot: runtimes={Runtimes} boxes={BoxCount} orphans={Orphans} drift={Drift}",
            runtimes.Count, boxes.Count, items.Count - runtimes.Count, driftCount);

        return new RuntimeDriftListResponse
        {
            Items = items,
            TotalCount = items.Count,
            DriftCount = driftCount,
            GeneratedAt = now,
        };
    }
}
