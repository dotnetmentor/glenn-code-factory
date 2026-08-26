using Source.Features.RuntimeLifecycle.Models;

namespace Source.Features.RuntimeLifecycle.Drift;

/// <summary>
/// One row in the operator drift view: a side-by-side snapshot of what the
/// database thinks a runtime is doing versus what Box's live listing
/// reports. Drift rules (see <see cref="DriftEvaluator"/>) walk the pair and
/// flag mismatches with a severity + list of rule names.
///
/// <para>Orphan boxes — ones we have no <see cref="ProjectRuntime"/> row
/// for — are surfaced as DTOs with <see cref="RuntimeId"/> / <see cref="ProjectId"/>
/// / <see cref="DbState"/> all <c>null</c>, severity Critical, and reasons
/// <c>["OrphanBox"]</c>. The frontend distinguishes them by the null
/// RuntimeId.</para>
///
/// <para>Everything that can be <c>null</c> is left explicitly nullable so the
/// orphan case (no DB row) and the "missing-on-Box" case (no box) both
/// round-trip cleanly through Swagger / Orval without inventing sentinel
/// values.</para>
/// </summary>
public sealed class RuntimeDriftDto
{
    /// <summary>DB primary key of the <see cref="ProjectRuntime"/>. Null when this row represents an orphan box.</summary>
    public Guid? RuntimeId { get; init; }

    /// <summary>Owning project's id. Null when this row represents an orphan box.</summary>
    public Guid? ProjectId { get; init; }

    /// <summary>Owning project's display name. Null when this row represents an orphan box.</summary>
    public string? ProjectName { get; init; }

    /// <summary>Owning workspace's slug — the tenant prefix in URLs. Null when this row represents an orphan box.</summary>
    public string? WorkspaceSlug { get; init; }

    /// <summary>Pinned branch id. Null when this row represents an orphan box.</summary>
    public Guid? BranchId { get; init; }

    /// <summary>Pinned branch display name. Null when this row represents an orphan box.</summary>
    public string? BranchName { get; init; }

    /// <summary>What the database thinks the runtime is doing. Null on orphan rows.</summary>
    public RuntimeState? DbState { get; init; }

    /// <summary>
    /// Raw box status string (<c>"provisioning"</c>, <c>"ready"</c>, <c>"idle"</c>,
    /// <c>"running"</c>, <c>"archived"</c>, <c>"error"</c>). Null when Box's
    /// listing did not include the runtime's <see cref="BoxId"/> — i.e. the
    /// BoxVanished drift rule has fired.
    /// </summary>
    public string? BoxStatus { get; init; }

    /// <summary>Box id either persisted on the runtime or harvested from the orphan listing.</summary>
    public string? BoxId { get; init; }

    /// <summary>Box region (EU: de/fi/fr). On orphan rows this is Box's reported region; otherwise the DB's value.</summary>
    public string? Region { get; init; }

    /// <summary>Last daemon heartbeat UTC timestamp from the DB. Null on orphans / cold-boot runtimes.</summary>
    public DateTime? LastHeartbeatAt { get; init; }

    /// <summary>Convenience seconds-since-heartbeat used for the StaleHeartbeat rule + UI freshness colouring.</summary>
    public int? SecondsSinceHeartbeat { get; init; }

    /// <summary>Last <see cref="ProjectRuntime.State"/> change UTC timestamp.</summary>
    public DateTime? StateChangedAt { get; init; }

    /// <summary>Convenience seconds-since-state-change used for the StuckInTransition rule + UI age column.</summary>
    public int? SecondsSinceStateChange { get; init; }

    /// <summary>Worst severity matched by any <see cref="DriftReasons"/> rule. <c>Ok</c> when nothing matched.</summary>
    public DriftSeverity DriftSeverity { get; init; }

    /// <summary>
    /// Names of every drift rule that matched this row, e.g.
    /// <c>["StaleHeartbeat","StuckInTransition"]</c>. Empty list when
    /// <see cref="DriftSeverity"/> is <see cref="DriftSeverity.Ok"/>.
    /// </summary>
    public List<string> DriftReasons { get; init; } = new();
}
