using Tapper;

namespace Source.Features.RuntimeLifecycle.Models;

/// <summary>
/// Lifecycle states a <see cref="ProjectRuntime"/> walks through during its
/// lifetime. The state graph is the single source of truth for what the
/// runtime is doing — every Fly machine action, bootstrap attempt, idle
/// suspension and tenant deletion is reflected here.
///
/// <para>States are deliberately strings on the database (see
/// <c>ApplicationDbContext</c> configuration) so adding new states later
/// doesn't break existing rows. The actual transition rules / state machine
/// land in a follow-up card; this file is just the vocabulary.</para>
///
/// <para><see cref="TranspilationSourceAttribute"/> exposes the enum to
/// TypedSignalR's TypeScript generator so the SignalR contracts that ship
/// runtime-state values across the wire (e.g.
/// <c>RuntimeStateChangedNotification</c>) generate clean TS unions.</para>
/// </summary>
[TranspilationSource]
public enum RuntimeState
{
    /// <summary>Row has been created but no Fly resources exist yet. Default starting state.</summary>
    Pending,

    /// <summary>Fly machine + volume are being created and the machine is starting up.</summary>
    Booting,

    /// <summary>Machine is up; the bootstrap daemon is preparing the workspace (clone, install, etc.).</summary>
    Bootstrapping,

    /// <summary>Bootstrap completed; runtime is serving traffic.</summary>
    Online,

    /// <summary>Idle threshold tripped; we've issued a suspend to Fly but it hasn't acked yet.</summary>
    Suspending,

    /// <summary>Machine is suspended on Fly. Volume is retained so we can wake it cheaply later.</summary>
    Suspended,

    /// <summary>User triggered a wake; the machine is being resumed by Fly.</summary>
    Waking,

    /// <summary>Machine exited unexpectedly. Eligible for automatic respawn up to a retry budget.</summary>
    Crashed,

    /// <summary>Permanent failure — we've given up retrying and require operator attention.</summary>
    Failed,

    /// <summary>User-initiated tear-down in progress (machine + volume being destroyed on Fly).</summary>
    Deleting,

    /// <summary>Logically deleted. Soft-delete window before janitor hard-deletes after 30 days.</summary>
    Deleted,
}
