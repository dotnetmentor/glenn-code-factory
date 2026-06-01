using Tapper;

namespace Source.Features.RuntimeCuration.Models;

/// <summary>
/// Lifecycle of a single <see cref="RuntimeProposal"/>. The daemon writes one
/// row in <see cref="Pending"/> via the <c>propose_runtime_spec</c> tool; the
/// user resolves it through Approve / Edit / Reject; the main API drives it
/// through <see cref="Applied"/> or <see cref="Failed"/> based on the daemon's
/// reply to <c>apply_runtime_spec_delta</c>.
///
/// <para>Persisted as <c>int</c> (see <c>ApplicationDbContext</c> configuration)
/// — small, finite enum, so we trade off the human-readable column for a
/// compact representation. Mirrors <c>HookPoint</c> / <c>SecretAuditAction</c>
/// rather than <c>RuntimeState</c>.</para>
///
/// <para><see cref="TranspilationSourceAttribute"/> exposes the enum to
/// Tapper's TypeScript generator so daemon TS / SignalR contracts that ship
/// proposal status across the wire generate clean unions.</para>
/// </summary>
[TranspilationSource]
public enum RuntimeProposalStatus
{
    /// <summary>Daemon proposed; user has not acted yet. Default starting state.</summary>
    Pending = 0,

    /// <summary>User clicked Approve — apply the original ProposedSpec verbatim.</summary>
    Approved = 1,

    /// <summary>User clicked Edit — apply the user-edited body in <c>AppliedSpec</c>.</summary>
    Edited = 2,

    /// <summary>User clicked Reject — proposal is dismissed; runtime untouched.</summary>
    Rejected = 3,

    /// <summary>Daemon successfully executed the delta (mise + supervisord).</summary>
    Applied = 4,

    /// <summary>Daemon reported a failure during delta apply; see <c>ErrorMessage</c>.</summary>
    Failed = 5,
}
