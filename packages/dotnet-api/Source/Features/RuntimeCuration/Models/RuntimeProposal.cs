using Source.Features.RuntimeLifecycle.Models;
using Source.Shared;
using Source.Shared.Events;

namespace Source.Features.RuntimeCuration.Models;

/// <summary>
/// One row per <c>propose_runtime_spec</c> tool call from the daemon — the
/// audit + decision record for the runtime-curation flow. The daemon never
/// installs anything on its own: it writes a <see cref="Pending"/> row, the
/// user clicks Approve / Edit / Reject in the confirmation card, the main API
/// pushes a delta back to the daemon to install via mise + supervisord, and
/// the proposal moves to <see cref="RuntimeProposalStatus.Applied"/> /
/// <see cref="RuntimeProposalStatus.Failed"/> based on the daemon's reply.
///
/// <list type="bullet">
///   <item>Soft-deletable so a noisy proposal can be hidden without losing the
///         underlying audit trail — mirrors <c>HookExecution</c> /
///         <c>GitOperation</c>.</item>
///   <item>FK to <see cref="ProjectRuntime"/> on <see cref="RuntimeId"/> with
///         <c>NoAction</c> — runtimes are soft-deleted, the proposal history
///         must outlive the runtime row within the 30-day janitor window.</item>
///   <item><see cref="ProjectId"/> is a plain Guid (no FK) — Project entity is
///         owned by another slice; mirrors the ProjectRuntime convention.</item>
///   <item><see cref="ProposedSpec"/> / <see cref="AppliedSpec"/> are stored
///         as <c>jsonb</c>. Schema is documented inline; the server treats the
///         body as opaque other than top-level shape validation in handlers.</item>
/// </list>
/// </summary>
public class RuntimeProposal : Entity, IAuditable, ISoftDelete
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// The project this proposal belongs to. Plain Guid (no FK) — Project is
    /// owned by another feature slice; mirrors <c>ProjectRuntime.ProjectId</c>.
    /// </summary>
    public Guid ProjectId { get; set; }

    /// <summary>The runtime this proposal targets. FK to <see cref="ProjectRuntime"/>.</summary>
    public Guid RuntimeId { get; set; }

    /// <summary>Current lifecycle position. Defaults to <see cref="RuntimeProposalStatus.Pending"/>.</summary>
    public RuntimeProposalStatus Status { get; set; } = RuntimeProposalStatus.Pending;

    /// <summary>
    /// V3 spec the daemon (or user) proposed — preset-based, source of truth
    /// for the proposal. Required. Shape: <c>RuntimeSpecV3</c>:
    /// <c>{ "version": 3, "services": [{ "kind": "...", "name": "...", "values": {...} }], "install"?, "setup"? }</c>.
    /// </summary>
    public string ProposedSpec { get; set; } = "{}";

    /// <summary>
    /// V3 spec is the source of truth (<see cref="ProposedSpec"/>). This is the
    /// daemon-bound V2 wire format produced by
    /// <see cref="Source.Features.RuntimePresets.Services.IPresetExpander"/> at
    /// proposal time. Persisting it means approval doesn't have to re-expand
    /// (and won't see drift if a preset is edited between propose + approve).
    /// Null when the V3 expansion has not been computed yet — e.g. legacy
    /// rows pre-dating the V3 cutover, or a proposal that was rejected before
    /// reaching the expander. Stored as <c>jsonb</c>.
    /// </summary>
    public string? ExpandedSpec { get; set; }

    /// <summary>
    /// V3 JSON spec finalized at apply time. On a direct Approve this equals
    /// <see cref="ProposedSpec"/>; on Edit this is the user-edited V3 body.
    /// Null while still Pending or after Reject.
    /// </summary>
    public string? AppliedSpec { get; set; }

    /// <summary>Daemon's natural-language reason for the proposal. Free-form text.</summary>
    public string? Reason { get; set; }

    /// <summary>
    /// IdentityUser id of the user who hit Approve / Edit / Reject. Null while
    /// the proposal is still <see cref="RuntimeProposalStatus.Pending"/>.
    /// </summary>
    public string? DecidedBy { get; set; }

    /// <summary>UTC timestamp when the user resolved the proposal. Null while Pending.</summary>
    public DateTime? DecidedAt { get; set; }

    /// <summary>
    /// If <see cref="Status"/> ends up <see cref="RuntimeProposalStatus.Failed"/>
    /// after delta apply, the daemon's error message. Null otherwise.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// End-to-end apply duration in milliseconds — the elapsed time between
    /// <see cref="DecidedAt"/> (when the user clicked Approve / Edit and the
    /// API pushed <c>ApplyRuntimeSpecDelta</c> to the daemon) and the daemon's
    /// terminal ack landing on <c>RuntimeHub.RuntimeSpecDeltaApplied</c>.
    /// Persisted on the proposal row so the Apply History surface survives the
    /// rolling 5000-event cap on the underlying <c>SpecDeltaApplied</c> /
    /// <c>SpecDeltaFailed</c> <see cref="Source.Features.RuntimeEvents.Models.RuntimeEvent"/>.
    /// Null until the daemon has acked.
    /// </summary>
    public long? TotalApplyMs { get; set; }

    /// <summary>
    /// Per-phase timing breakdown as a JSON string — typically
    /// <c>{ "installMs": X, "servicesMs": Y, "setupMs": Z }</c>. Populated from
    /// the matching <c>SpecDelta*</c> <see cref="Source.Features.RuntimeEvents.Models.RuntimeEvent"/>'s
    /// payload at the moment the daemon's ack lands. Same payload shape the
    /// Apply History query already extracts inline. Persisted here so the row
    /// is self-contained once the RuntimeEvent gets evicted by the rolling
    /// cap. Stored as jsonb on Postgres. Null until the daemon has acked, or
    /// when no matching phase data was emitted.
    /// </summary>
    public string? PhaseTimings { get; set; }

    // -------- IAuditable --------
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // -------- ISoftDelete --------
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }
    public string? DeletedBy { get; set; }

    /// <summary>
    /// Optional FK navigation back to the targeted runtime. Mirrors the
    /// <c>AgentSession.Runtime</c> pattern so handlers can <c>Include</c> the
    /// runtime when surfacing proposals in the UI.
    /// </summary>
    public ProjectRuntime? Runtime { get; set; }
}
