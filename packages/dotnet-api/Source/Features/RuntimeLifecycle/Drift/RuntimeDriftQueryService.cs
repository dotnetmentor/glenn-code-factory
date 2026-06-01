using Microsoft.EntityFrameworkCore;
using Source.Features.FlyManagement;
using Source.Features.FlyManagement.Models;
using Source.Features.RuntimeLifecycle.Models;
using Source.Infrastructure;

namespace Source.Features.RuntimeLifecycle.Drift;

/// <summary>
/// Default implementation of <see cref="IRuntimeDriftQueryService"/>. Pulls
/// every non-soft-deleted <see cref="ProjectRuntime"/> in one shot (with
/// Project → Workspace and Branch eager-loaded so the DTO can carry display
/// names), pulls Fly's machine listing in one shot, then walks both in memory
/// to produce the merged drift view.
///
/// <para><b>Orphan filtering heuristic.</b> Project runtime machines are
/// created via <c>RuntimeProvisionerJob</c> / <c>RespawnRuntimeJob</c> with
/// the name pattern <c>"rt_{runtime.Id:N}".Substring(0, 30)</c>. Anything
/// else in the Fly app (a control-plane daemon-base machine, a one-shot
/// migration runner, etc.) won't share that prefix. We therefore treat any
/// machine whose name starts with <c>"rt_"</c> AND isn't referenced by a
/// <see cref="ProjectRuntime.FlyMachineId"/> as a true runtime orphan; other
/// names are skipped so infrastructure noise doesn't pollute the operator
/// view. If we ever add other naming patterns for project runtimes (sandboxes,
/// preview envs, …) extend the predicate here.</para>
/// </summary>
public sealed class RuntimeDriftQueryService : IRuntimeDriftQueryService
{
    /// <summary>
    /// Name prefix every project-runtime Fly machine is created with — see
    /// <c>RuntimeProvisionerJob.ProvisionAsync</c> and
    /// <c>RespawnRuntimeJob</c>. Anything else in the Fly app is infrastructure
    /// and not eligible to be reported as an orphan runtime.
    /// </summary>
    public const string ProjectRuntimeMachineNamePrefix = "rt_";

    private readonly ApplicationDbContext _db;
    private readonly FlyClient _fly;
    private readonly ILogger<RuntimeDriftQueryService> _logger;

    public RuntimeDriftQueryService(
        ApplicationDbContext db,
        FlyClient fly,
        ILogger<RuntimeDriftQueryService> logger)
    {
        _db = db;
        _fly = fly;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<RuntimeDriftListResponse> BuildSnapshotAsync(CancellationToken ct = default)
    {
        // Stamp the snapshot time BEFORE the DB / Fly round trips so all the
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

        // ---- 2. Pull Fly's view once. FlyApiException bubbles to the controller. ----
        var flyMachines = await _fly.ListMachinesAsync(ct);
        var flyById = flyMachines.ToDictionary(m => m.Id, m => m);

        // ---- 3. Build a DTO per runtime + evaluate the drift rules. ----
        var items = new List<RuntimeDriftDto>(runtimes.Count + 4);

        // Track which Fly machine ids are claimed by a runtime so the orphan
        // pass below can subtract them out in O(n). Using a HashSet because
        // multiple runtimes claiming the same machine id is "shouldn't happen"
        // but we want the dedup to be cheap if it does.
        var claimedFlyIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var runtime in runtimes)
        {
            FlyMachine? flyMachine = null;
            if (!string.IsNullOrEmpty(runtime.FlyMachineId)
                && flyById.TryGetValue(runtime.FlyMachineId, out var match))
            {
                flyMachine = match;
                claimedFlyIds.Add(runtime.FlyMachineId);
            }

            var (severity, reasons) = DriftEvaluator.EvaluateRuntime(runtime, flyMachine, now);

            items.Add(new RuntimeDriftDto
            {
                RuntimeId = runtime.Id,
                ProjectId = runtime.ProjectId,
                ProjectName = runtime.Project?.Name,
                WorkspaceSlug = runtime.Project?.Workspace?.Slug,
                BranchId = runtime.BranchId,
                BranchName = runtime.Branch?.Name,
                DbState = runtime.State,
                FlyState = flyMachine?.State,
                FlyMachineId = runtime.FlyMachineId,
                // Prefer the Fly-reported region when we have it (it's the live
                // truth); fall back to the DB's snapshot when Fly's row is gone.
                Region = flyMachine?.Region ?? runtime.Region,
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

        // ---- 4. Orphan pass. Any Fly machine whose name signals "project runtime"
        // but that no DB row references is added as an orphan DTO. We deliberately
        // do NOT flag arbitrary non-runtime machines — the Fly app also hosts our
        // own control plane / daemon-base machinery that's managed out of band.
        foreach (var fly in flyMachines)
        {
            if (claimedFlyIds.Contains(fly.Id)) continue;
            if (string.IsNullOrEmpty(fly.Name)) continue;
            if (!fly.Name.StartsWith(ProjectRuntimeMachineNamePrefix, StringComparison.Ordinal)) continue;

            items.Add(DriftEvaluator.BuildOrphanDto(fly));
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
            "RuntimeDrift snapshot: runtimes={Runtimes} flyMachines={FlyCount} orphans={Orphans} drift={Drift}",
            runtimes.Count, flyMachines.Count, items.Count - runtimes.Count, driftCount);

        return new RuntimeDriftListResponse
        {
            Items = items,
            TotalCount = items.Count,
            DriftCount = driftCount,
            GeneratedAt = now,
        };
    }
}
