using Source.Features.BoxManagement.Models;
using Source.Features.RuntimeLifecycle.Models;

namespace Source.Features.RuntimeLifecycle.Drift;

/// <summary>
/// Pure drift-rule evaluator. No <c>DbContext</c>, no Box client, no clock —
/// every input is passed in so the rules are trivial to unit test against
/// hand-rolled <c>(ProjectRuntime, BoxVm?, DateTime now)</c> tuples.
///
/// <para>The rule names returned in <see cref="EvaluateRuntime(ProjectRuntime, BoxVm?, DateTime)"/>
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

    /// <summary>How long Online+up can go without a heartbeat before <see cref="Rules.StaleHeartbeat"/> fires.</summary>
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
        public const string BoxVanished = "BoxVanished";
        public const string OrphanBox = "OrphanBox";
        public const string StateMismatchOnlineButArchived = "StateMismatch_OnlineButArchived";
        public const string StateMismatchSuspendedButRunning = "StateMismatch_SuspendedButRunning";
        public const string StateMismatchOnlineButNotUp = "StateMismatch_OnlineButNotUp";
        public const string StuckInTransition = "StuckInTransition";
        public const string StaleHeartbeat = "StaleHeartbeat";
    }

    /// <summary>
    /// Evaluate every drift rule for a single runtime + its optional box.
    /// Pass <paramref name="boxVm"/> = <c>null</c> when the runtime's
    /// <see cref="ProjectRuntime.BoxId"/> wasn't found in the Box listing —
    /// that's the BoxVanished signal.
    ///
    /// <para><paramref name="now"/> is supplied by the caller (typically
    /// <see cref="DateTime.UtcNow"/>) so the rules stay deterministic under
    /// test.</para>
    /// </summary>
    public static (DriftSeverity Severity, List<string> Reasons) EvaluateRuntime(
        ProjectRuntime runtime,
        BoxVm? boxVm,
        DateTime now)
    {
        var reasons = new List<string>();
        var severity = DriftSeverity.Ok;

        // Rule 1: BoxVanished — DB has a box id, Box returned no row for it.
        // Only flagged when the DB has actually assigned an id; Pending runtimes
        // have a null BoxId by design and aren't drift.
        if (boxVm is null && !string.IsNullOrEmpty(runtime.BoxId))
        {
            reasons.Add(Rules.BoxVanished);
            severity = Max(severity, DriftSeverity.Critical);
        }

        // Anything below needs the Box side present, since the rules compare DB
        // state to box status. BoxVanished above already covered the missing case.
        var status = boxVm?.State;
        var up = status is not null && BoxStates.IsUp(status);
        var archived = status is not null && BoxStates.IsArchived(status);

        // Rule 3: StateMismatch_OnlineButArchived — Online runtime whose box has
        // been archived (self-archived at TTL, stopped out-of-band, ...). The
        // reconciler's drift map walks this through Suspending, but until it gets
        // a chance the DB lies.
        var onlineButArchived = runtime.State == RuntimeState.Online && archived;
        if (onlineButArchived)
        {
            reasons.Add(Rules.StateMismatchOnlineButArchived);
            severity = Max(severity, DriftSeverity.High);
        }

        // Rule 4: StateMismatch_SuspendedButRunning — DB believes the runtime is
        // suspended but the box is up. Either the stop never landed or the box
        // was resumed outside our control plane. Also a cost signal: an
        // unexpectedly-running box bills.
        if (runtime.State == RuntimeState.Suspended && up)
        {
            reasons.Add(Rules.StateMismatchSuspendedButRunning);
            severity = Max(severity, DriftSeverity.High);
        }

        // Rule 5: StateMismatch_OnlineButNotUp — Online runtime whose box reports
        // anything other than an up status. Excludes rule 3's archived pair so we
        // don't double-flag with the same severity — but per the multi-match
        // policy BOTH reasons land when both genuinely match.
        if (runtime.State == RuntimeState.Online
            && status is not null
            && !up
            && !onlineButArchived)
        {
            reasons.Add(Rules.StateMismatchOnlineButNotUp);
            severity = Max(severity, DriftSeverity.High);
        }

        // Rule 6: StuckInTransition — a transitional state that hasn't moved in 5+
        // minutes. These states should resolve quickly via the provisioner /
        // reconciler chain; sitting there is the smoke for a real fire (Box
        // outage, dead worker, start-budget exhaustion, ...).
        if (_transitionalStates.Contains(runtime.State)
            && (now - runtime.StateChangedAt) > StuckInTransitionThreshold)
        {
            reasons.Add(Rules.StuckInTransition);
            severity = Max(severity, DriftSeverity.Medium);
        }

        // Rule 7: StaleHeartbeat — Online + box-up runtime that hasn't checked in.
        // The HeartbeatWatcherJob normally crashes these out, but the operator
        // wants to see them in the drift view before that job's next tick — and
        // the watcher can also be paused / misconfigured, so the drift surface
        // calls them out independently. Null heartbeat counts as stale here
        // because by the time the gate (Online + up) is true the daemon should
        // have populated it.
        if (runtime.State == RuntimeState.Online && up)
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
    /// Build the orphan DTO for a box that has no <see cref="ProjectRuntime"/>
    /// counterpart. Always Critical with reason <see cref="Rules.OrphanBox"/>;
    /// the caller is responsible for filtering out non-runtime boxes (golden
    /// templates, scratch boxes, ...) before handing the list to this method.
    /// Note the TTL guardrail means an orphan box archives itself and stops
    /// billing — Critical here signals "clean me up", not "money is burning".
    /// </summary>
    public static RuntimeDriftDto BuildOrphanDto(BoxVm boxVm)
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
            BoxStatus = boxVm.State,
            BoxId = boxVm.Id,
            Region = boxVm.Region,
            LastHeartbeatAt = null,
            SecondsSinceHeartbeat = null,
            StateChangedAt = null,
            SecondsSinceStateChange = null,
            DriftSeverity = DriftSeverity.Critical,
            DriftReasons = new List<string> { Rules.OrphanBox },
        };
    }

    private static DriftSeverity Max(DriftSeverity a, DriftSeverity b) => (int)a >= (int)b ? a : b;
}
