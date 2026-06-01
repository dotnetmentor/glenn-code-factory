using Source.Features.FlyManagement.Models;
using Source.Features.RuntimeLifecycle.Models;

namespace Source.Features.RuntimeLifecycle.Drift;

/// <summary>
/// Pure drift-rule evaluator. No <c>DbContext</c>, no Fly client, no clock —
/// every input is passed in so the rules are trivial to unit test against
/// hand-rolled <c>(ProjectRuntime, FlyMachine?, DateTime now)</c> tuples.
///
/// <para>The rule names returned in <see cref="EvaluateRuntime(ProjectRuntime, FlyMachine?, DateTime)"/>
/// must stay stable strings — the operator UI keys legend / colour mappings off them, and
/// dashboards group incidents by them. Add new rules, don't rename old ones.</para>
///
/// <para><b>Multi-match policy.</b> All matched rule names are returned; the
/// final <see cref="DriftSeverity"/> is the max of every matched rule's
/// severity. <see cref="DriftSeverity.Ok"/> is only returned when nothing
/// matched.</para>
/// </summary>
public static class DriftEvaluator
{
    /// <summary>How long a runtime can sit in a transitional state before <see cref="Rules.StuckInTransition"/> fires.</summary>
    public static readonly TimeSpan StuckInTransitionThreshold = TimeSpan.FromMinutes(5);

    /// <summary>How long Online+started can go without a heartbeat before <see cref="Rules.StaleHeartbeat"/> fires.</summary>
    public static readonly TimeSpan StaleHeartbeatThreshold = TimeSpan.FromSeconds(60);

    /// <summary>States the runtime is considered "in transition" — eligible for the StuckInTransition rule.</summary>
    private static readonly HashSet<RuntimeState> _transitionalStates = new()
    {
        RuntimeState.Booting,
        RuntimeState.Bootstrapping,
        RuntimeState.Suspending,
        RuntimeState.Waking,
        RuntimeState.Deleting,
    };

    /// <summary>
    /// Canonical rule names. Kept as constants so the controller can reference
    /// them without stringly-typed magic, and the test suite can pin them to
    /// detect accidental renames.
    /// </summary>
    public static class Rules
    {
        public const string MachineVanished = "MachineVanished";
        public const string OrphanFlyMachine = "OrphanFlyMachine";
        public const string StateMismatchOnlineButStopped = "StateMismatch_OnlineButStopped";
        public const string StateMismatchSuspendedButStarted = "StateMismatch_SuspendedButStarted";
        public const string StateMismatchOnlineButNotStarted = "StateMismatch_OnlineButNotStarted";
        public const string StuckInTransition = "StuckInTransition";
        public const string StaleHeartbeat = "StaleHeartbeat";
    }

    /// <summary>
    /// Evaluate every drift rule for a single runtime + its optional Fly counterpart.
    /// Pass <paramref name="flyMachine"/> = <c>null</c> when the runtime's
    /// <see cref="ProjectRuntime.FlyMachineId"/> wasn't found in the Fly listing
    /// — that's the MachineVanished signal.
    ///
    /// <para><paramref name="now"/> is supplied by the caller (typically
    /// <see cref="DateTime.UtcNow"/>) so the rules stay deterministic under
    /// test.</para>
    /// </summary>
    public static (DriftSeverity Severity, List<string> Reasons) EvaluateRuntime(
        ProjectRuntime runtime,
        FlyMachine? flyMachine,
        DateTime now)
    {
        var reasons = new List<string>();
        var severity = DriftSeverity.Ok;

        // Rule 1: MachineVanished — DB has a machine id, Fly returned no row for it.
        // We only flag this when the DB has actually assigned a machine id; runtimes
        // still in Pending have a null FlyMachineId by design and aren't drift.
        if (flyMachine is null && !string.IsNullOrEmpty(runtime.FlyMachineId))
        {
            reasons.Add(Rules.MachineVanished);
            severity = Max(severity, DriftSeverity.Critical);
        }

        // Anything below this point needs the Fly side present, since the rules
        // compare DB state to Fly state. MachineVanished above already covered
        // the missing case at Critical.
        var flyState = flyMachine?.State?.ToLowerInvariant();

        // Rule 3: StateMismatch_OnlineButStopped — Online runtime whose Fly machine
        // is being / has been stopped. The reconciler's drift map walks this through
        // Suspending, but until it gets a chance the DB lies.
        var onlineButStopped = runtime.State == RuntimeState.Online
            && (flyState == "stopped" || flyState == "stopping");
        if (onlineButStopped)
        {
            reasons.Add(Rules.StateMismatchOnlineButStopped);
            severity = Max(severity, DriftSeverity.High);
        }

        // Rule 4: StateMismatch_SuspendedButStarted — DB believes the runtime is
        // suspended but Fly reports it started. Either the suspend never landed
        // or the machine was started outside our control plane.
        if (runtime.State == RuntimeState.Suspended && flyState == "started")
        {
            reasons.Add(Rules.StateMismatchSuspendedButStarted);
            severity = Max(severity, DriftSeverity.High);
        }

        // Rule 5: StateMismatch_OnlineButNotStarted — Online runtime whose Fly
        // machine reports anything other than "started". Note the explicit
        // exclusion of rule 3's pair (stopped/stopping) so we don't double-flag
        // them with the same severity — but per the spec we do include BOTH
        // reasons when both match.
        if (runtime.State == RuntimeState.Online
            && flyState is not null
            && flyState != "started"
            && !onlineButStopped)
        {
            reasons.Add(Rules.StateMismatchOnlineButNotStarted);
            severity = Max(severity, DriftSeverity.High);
        }

        // Rule 6: StuckInTransition — a transitional state that hasn't moved in 5+ minutes.
        // These are the states that should resolve themselves quickly via the
        // provisioner / reconciler / webhook chain; sitting there is the smoke for
        // a real fire (Fly outage, missed webhook, dead worker, etc.).
        if (_transitionalStates.Contains(runtime.State)
            && (now - runtime.StateChangedAt) > StuckInTransitionThreshold)
        {
            reasons.Add(Rules.StuckInTransition);
            severity = Max(severity, DriftSeverity.Medium);
        }

        // Rule 7: StaleHeartbeat — Online + Fly:started runtime that hasn't checked in.
        // The HeartbeatWatcherJob normally crashes these out, but the operator wants
        // to see them in the drift view before that job's next tick — and the
        // watcher can also be paused / misconfigured, so the drift surface needs to
        // call them out independently. Null heartbeat counts as stale here because
        // by the time the rule's gate (Online + Fly:started) is true the daemon
        // should have populated it.
        if (runtime.State == RuntimeState.Online && flyState == "started")
        {
            var heartbeatIsStale = runtime.LastHeartbeatAt is null
                || (now - runtime.LastHeartbeatAt.Value) > StaleHeartbeatThreshold;
            if (heartbeatIsStale)
            {
                reasons.Add(Rules.StaleHeartbeat);
                severity = Max(severity, DriftSeverity.Medium);
            }
        }

        return (severity, reasons);
    }

    /// <summary>
    /// Build the orphan DTO for a Fly machine that has no <see cref="ProjectRuntime"/>
    /// counterpart. Always Critical with reason <see cref="Rules.OrphanFlyMachine"/>;
    /// the caller is responsible for filtering out infrastructure machines (control
    /// plane, daemon-base, etc.) before handing the list to this method.
    /// </summary>
    public static RuntimeDriftDto BuildOrphanDto(FlyMachine flyMachine)
    {
        return new RuntimeDriftDto
        {
            RuntimeId = null,
            ProjectId = null,
            ProjectName = null,
            WorkspaceSlug = null,
            BranchId = null,
            BranchName = null,
            DbState = null,
            FlyState = flyMachine.State,
            FlyMachineId = flyMachine.Id,
            Region = flyMachine.Region,
            LastHeartbeatAt = null,
            SecondsSinceHeartbeat = null,
            StateChangedAt = null,
            SecondsSinceStateChange = null,
            DriftSeverity = DriftSeverity.Critical,
            DriftReasons = new List<string> { Rules.OrphanFlyMachine },
        };
    }

    private static DriftSeverity Max(DriftSeverity a, DriftSeverity b) => (int)a >= (int)b ? a : b;
}
