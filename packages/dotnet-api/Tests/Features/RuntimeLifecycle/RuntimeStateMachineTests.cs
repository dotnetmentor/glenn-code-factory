using Source.Features.RuntimeLifecycle;
using Source.Features.RuntimeLifecycle.Models;

namespace Api.Tests.Features.RuntimeLifecycle;

/// <summary>
/// Pure unit tests for the closed transition graph in
/// <see cref="RuntimeStateMachine"/>. No DB, no DI, no fixture — just the matrix.
///
/// <para>The tests come in two flavours: <b>positive</b> (every legal edge in the
/// graph is asserted with a <c>[Theory] + [InlineData]</c>) and <b>negative</b> (a
/// generator emits all 11×11 = 121 pairs, subtracts the legal set, and asserts the
/// rest are rejected). The negative half is what catches regressions when someone
/// adds a state and forgets to update the graph — they'll find out on test run, not
/// in production.</para>
/// </summary>
public class RuntimeStateMachineTests
{
    /// <summary>
    /// Single source of truth for what the graph is supposed to allow. Both the
    /// positive and the exhaustive negative tests read from this set, so adding a
    /// new edge means updating exactly one list.
    /// </summary>
    private static readonly HashSet<(RuntimeState From, RuntimeState To)> LegalEdges = new()
    {
        (RuntimeState.Pending,       RuntimeState.Booting),
        (RuntimeState.Pending,       RuntimeState.Failed),
        (RuntimeState.Pending,       RuntimeState.Deleting),

        (RuntimeState.Booting,       RuntimeState.Bootstrapping),
        // Daemon's RuntimeReady broadcast is authoritative and may arrive
        // before the reconciler flips Booting → Bootstrapping; allowing the
        // direct edge closes that race. See RuntimeReadyCommand.
        (RuntimeState.Booting,       RuntimeState.Online),
        (RuntimeState.Booting,       RuntimeState.Crashed),
        (RuntimeState.Booting,       RuntimeState.Deleting),
        // Operator-only force-stop: park a stuck mid-boot runtime without it
        // having reached Online. Triggered exclusively by
        // RuntimeAdminController.ForceStop. Automated callers must still go
        // via Online → Suspending.
        (RuntimeState.Booting,       RuntimeState.Suspending),

        (RuntimeState.Bootstrapping, RuntimeState.Online),
        (RuntimeState.Bootstrapping, RuntimeState.Crashed),
        (RuntimeState.Bootstrapping, RuntimeState.Deleting),
        // Operator-only force-stop — see note on Booting above.
        (RuntimeState.Bootstrapping, RuntimeState.Suspending),

        (RuntimeState.Online,        RuntimeState.Suspending),
        (RuntimeState.Online,        RuntimeState.Crashed),
        (RuntimeState.Online,        RuntimeState.Deleting),
        // Operator-only force-rebootstrap walks an Online runtime back into
        // Bootstrapping so the lifecycle UI reflects the in-flight wipe-and-rerun.
        // Triggered exclusively by ForceRebootstrapAdminController.
        (RuntimeState.Online,        RuntimeState.Bootstrapping),

        (RuntimeState.Suspending,    RuntimeState.Suspended),
        (RuntimeState.Suspending,    RuntimeState.Crashed),
        (RuntimeState.Suspending,    RuntimeState.Deleting),

        (RuntimeState.Suspended,     RuntimeState.Waking),
        (RuntimeState.Suspended,     RuntimeState.Booting),
        (RuntimeState.Suspended,     RuntimeState.Deleting),

        (RuntimeState.Waking,        RuntimeState.Bootstrapping),
        (RuntimeState.Waking,        RuntimeState.Crashed),
        (RuntimeState.Waking,        RuntimeState.Deleting),
        // Operator-only force-stop — see note on Booting above.
        (RuntimeState.Waking,        RuntimeState.Suspending),

        (RuntimeState.Crashed,       RuntimeState.Booting),
        (RuntimeState.Crashed,       RuntimeState.Failed),
        (RuntimeState.Crashed,       RuntimeState.Deleting),
        // User-initiated restart (ProjectRuntime.Restart): same recovery
        // semantics as Failed → Pending — re-arm the failure budget and let
        // the provisioner recreate the machine on the existing volume. Used
        // when the automated respawn path can't recover (e.g. Fly token
        // expired) so the user can manually nudge the runtime forward.
        (RuntimeState.Crashed,       RuntimeState.Pending),

        (RuntimeState.Failed,        RuntimeState.Pending),
        (RuntimeState.Failed,        RuntimeState.Deleting),

        (RuntimeState.Deleting,      RuntimeState.Deleted),
    };

    public static IEnumerable<object[]> AllLegalEdges =>
        LegalEdges.Select(e => new object[] { e.From, e.To });

    [Theory]
    [MemberData(nameof(AllLegalEdges))]
    public void CanTransition_returns_true_for_every_legal_edge(RuntimeState from, RuntimeState to)
    {
        RuntimeStateMachine.CanTransition(from, to).Should().BeTrue(
            $"edge {from} -> {to} is part of the documented graph");
    }

    /// <summary>
    /// Generates all 11×11 ordered pairs and asserts that the ones not in
    /// <see cref="LegalEdges"/> are rejected — including self-loops, which are
    /// deliberately not allowed (callers should short-circuit before calling).
    /// </summary>
    [Fact]
    public void CanTransition_rejects_every_pair_not_in_the_legal_set()
    {
        var allStates = Enum.GetValues<RuntimeState>();
        var illegal = new List<(RuntimeState, RuntimeState)>();

        foreach (var from in allStates)
        {
            foreach (var to in allStates)
            {
                var isLegal = LegalEdges.Contains((from, to));
                var actual = RuntimeStateMachine.CanTransition(from, to);

                if (!isLegal && actual)
                {
                    illegal.Add((from, to));
                }
            }
        }

        illegal.Should().BeEmpty(
            "every pair outside the documented graph must be rejected; "
            + "an unexpected legal edge usually means a typo in the matrix");
    }

    [Fact]
    public void CanTransition_rejects_self_loops_for_every_state()
    {
        foreach (var state in Enum.GetValues<RuntimeState>())
        {
            RuntimeStateMachine.CanTransition(state, state).Should().BeFalse(
                $"self-loop {state} -> {state} is not a real transition");
        }
    }

    [Fact]
    public void AllowedTransitionsFrom_matches_legal_edges_per_state()
    {
        foreach (var from in Enum.GetValues<RuntimeState>())
        {
            var expected = LegalEdges.Where(e => e.From == from).Select(e => e.To).ToHashSet();
            var actual = RuntimeStateMachine.AllowedTransitionsFrom(from).ToHashSet();

            actual.Should().BeEquivalentTo(expected,
                $"AllowedTransitionsFrom({from}) must match the legal edges for that state");
        }
    }

    [Fact]
    public void IsTerminal_only_true_for_Deleted()
    {
        foreach (var state in Enum.GetValues<RuntimeState>())
        {
            var expected = state == RuntimeState.Deleted;
            RuntimeStateMachine.IsTerminal(state).Should().Be(expected,
                $"IsTerminal({state}) — only Deleted should be terminal today");
        }
    }

    [Fact]
    public void Failed_is_sticky_and_only_leaves_via_operator()
    {
        // Failed is a manual-recovery state per the spec. The only edges out of it
        // are operator-driven (Pending = reset, Deleting = delete) — auto recovery
        // would defeat the "Failed indicates a real problem" intent.
        var allowed = RuntimeStateMachine.AllowedTransitionsFrom(RuntimeState.Failed);
        allowed.Should().BeEquivalentTo(new[] { RuntimeState.Pending, RuntimeState.Deleting });
    }
}
