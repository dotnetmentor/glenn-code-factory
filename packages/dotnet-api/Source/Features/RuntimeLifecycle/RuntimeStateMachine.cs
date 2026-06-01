using Source.Features.RuntimeLifecycle.Models;

namespace Source.Features.RuntimeLifecycle;

/// <summary>
/// The closed state graph for a <see cref="ProjectRuntime"/>. Encodes which
/// transitions are legal so callers can validate before mutating, and so the
/// invariant lives in exactly one place rather than scattered across handlers.
///
/// <para><b>The graph (per the runtime-lifecycle spec):</b></para>
/// <code>
///                 Pending
///                    │
///                    ▼ (provisioner)
///                 Booting ───────────────────────► Crashed ──► Failed
///                    │  ╲                             │           │
///       (fly:started)│   ╲ (daemon:runtime_ready,     │           │ (operator: reset)
///                    ▼    ╲  reconciler still lagging)│           ▼
///              Bootstrapping ──────────────────────►  │       Pending
///                    │  ╲                             │
///        (daemon:    │   ╲                            │
///         runtime_   │    ╲                           │
///         ready)     ▼     ╲                          │
///                  Online ◄─────────── Waking ◄──── Suspended
///                    │                    ▲           ▲
///         (idler)    │                    │           │
///                    ▼                    │  (fly:    │ (fly:stopped)
///               Suspending ───────────────┘  started) │
///                    │                                │
///                    └────────────────────────────────┘
///
///   Crashed ──► Booting (respawn within retry budget)
///       *  ──► Deleting (operator force-delete or project delete)
///   Deleting ──► Deleted (teardown done)
///
///   Operator force-stop override (RuntimeAdminController.ForceStop only):
///     Booting / Bootstrapping / Waking ──► Suspending
///   Lets the operator park a stuck mid-boot runtime without it ever having
///   reached Online. Automated callers still go via Online → Suspending.
/// </code>
///
/// <para><b>Why a closed graph and not "anything goes":</b> Lifecycle bugs are
/// some of the worst infra bugs — a runtime stuck in Bootstrapping, a runtime
/// that goes Online → Pending and re-provisions, etc. Validating up-front is
/// cheap and turns those bugs from data corruption into clear errors at the
/// point of mistake.</para>
/// </summary>
public static class RuntimeStateMachine
{
    /// <summary>
    /// The transition matrix. Looking up <c>_graph[from]</c> yields the set of
    /// states <c>from</c> can legally move to. A state with no outgoing edges
    /// (today: Deleted) returns an empty set — terminal.
    /// </summary>
    private static readonly IReadOnlyDictionary<RuntimeState, HashSet<RuntimeState>> _graph =
        new Dictionary<RuntimeState, HashSet<RuntimeState>>
        {
            // Birth path: Pending → Booting → Bootstrapping → Online
            [RuntimeState.Pending] = new()
            {
                RuntimeState.Booting,
                RuntimeState.Failed,   // provisioner couldn't even create Fly resources (e.g. Fly 422)
                RuntimeState.Deleting, // operator can delete a never-provisioned runtime
            },

            [RuntimeState.Booting] = new()
            {
                RuntimeState.Bootstrapping,
                // Daemon's RuntimeReady broadcast is the authoritative "I'm done
                // bootstrapping" signal and may arrive before the reconciler has
                // flipped Booting → Bootstrapping (the reconciler runs on a poll
                // interval and can lag the daemon by tens of seconds). Allowing
                // Booting → Online directly closes that race — the runtime is, in
                // fact, ready, and waiting for the reconciler to catch up before
                // accepting the daemon's signal causes the bootstrap_completed
                // broadcast to be dropped on the floor (the daemon does not
                // re-emit per process). See RuntimeReadyCommand.
                RuntimeState.Online,
                RuntimeState.Crashed,
                RuntimeState.Deleting,
                // Operator-only "force-stop": park a stuck mid-boot runtime
                // without going through Online (which it never reached). Used
                // when bootstrap is hung — e.g. a daemon contract mismatch
                // leaves the runtime looping forever in Bootstrapping/Booting.
                // Triggered exclusively by RuntimeAdminController.ForceStop;
                // automated callers must still go via Online → Suspending.
                RuntimeState.Suspending,
            },

            [RuntimeState.Bootstrapping] = new()
            {
                RuntimeState.Online,
                RuntimeState.Crashed,
                RuntimeState.Deleting,
                // Operator-only "force-stop" — see note on Booting above.
                RuntimeState.Suspending,
            },

            // Steady-state hub: Online ↔ Suspended (via Suspending / Waking)
            [RuntimeState.Online] = new()
            {
                RuntimeState.Suspending,
                RuntimeState.Crashed,
                RuntimeState.Deleting,
                // Operator-only: force-rebootstrap walks an Online runtime back
                // into Bootstrapping so the lifecycle UI reflects the in-flight
                // wipe-and-rerun. Triggered exclusively by ForceRebootstrapAdminController.
                RuntimeState.Bootstrapping,
            },

            [RuntimeState.Suspending] = new()
            {
                RuntimeState.Suspended,
                RuntimeState.Crashed,    // suspend itself can fail / Fly reports crashed
                RuntimeState.Deleting,
            },

            [RuntimeState.Suspended] = new()
            {
                RuntimeState.Waking,
                RuntimeState.Deleting,
                RuntimeState.Booting, // reconciler may force a fresh boot if Fly lost the machine
            },

            [RuntimeState.Waking] = new()
            {
                // Waking now hands off to Bootstrapping rather than going
                // directly to Online — the daemon-as-downloadable cold-boot
                // includes a tarball download + extract step, so the
                // "daemon is actually up" signal must come from the daemon's
                // own RuntimeReady hub call (Bootstrapping → Online).
                RuntimeState.Bootstrapping,
                RuntimeState.Crashed,
                RuntimeState.Deleting,
                // Operator-only "force-stop": park a runtime mid-wake without
                // requiring it to reach Online first. See note on Booting.
                RuntimeState.Suspending,
            },

            // Failure path: Crashed → respawn or Failed
            [RuntimeState.Crashed] = new()
            {
                RuntimeState.Booting,    // respawn (within retry budget)
                RuntimeState.Failed,     // exhausted retries
                RuntimeState.Deleting,
                RuntimeState.Pending,    // user-initiated restart (ProjectRuntime.Restart) —
                                         // same semantics as Failed → Pending: re-arm the
                                         // failure budget and re-provision on the existing
                                         // volume. Lets a user manually nudge a runtime back
                                         // to a fresh boot when the automated respawn path
                                         // can't recover (e.g. Fly token expired).
            },

            // Sticky failure: only operator action leaves Failed
            [RuntimeState.Failed] = new()
            {
                RuntimeState.Pending,    // operator: reset
                RuntimeState.Deleting,   // operator: delete
            },

            // Teardown path
            [RuntimeState.Deleting] = new() { RuntimeState.Deleted },

            // Terminal — no outgoing edges
            [RuntimeState.Deleted] = new(),
        };

    /// <summary>
    /// True if a runtime in <paramref name="from"/> may legally move to
    /// <paramref name="to"/>. Idempotent self-edges (<c>from == to</c>) are <b>not</b>
    /// legal — callers should short-circuit before calling here, or the graph would
    /// need 11 self-loops with no semantic value.
    /// </summary>
    public static bool CanTransition(RuntimeState from, RuntimeState to)
    {
        return _graph.TryGetValue(from, out var allowed) && allowed.Contains(to);
    }

    /// <summary>
    /// The set of states reachable from <paramref name="from"/> in one step.
    /// Used by admin UIs to render only the buttons that would succeed.
    /// </summary>
    public static IReadOnlyCollection<RuntimeState> AllowedTransitionsFrom(RuntimeState from)
    {
        return _graph.TryGetValue(from, out var allowed)
            ? allowed
            : Array.Empty<RuntimeState>();
    }

    /// <summary>True if no outgoing transitions exist (today: only <see cref="RuntimeState.Deleted"/>).</summary>
    public static bool IsTerminal(RuntimeState state) => AllowedTransitionsFrom(state).Count == 0;
}
