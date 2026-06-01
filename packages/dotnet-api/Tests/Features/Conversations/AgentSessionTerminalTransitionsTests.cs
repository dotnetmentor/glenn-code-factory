using Source.Features.Conversations.Events;
using Source.Features.Conversations.Models;

namespace Api.Tests.Features.Conversations;

/// <summary>
/// Unit tests for the terminal-transition rich methods on
/// <see cref="AgentSession"/> — <see cref="AgentSession.Succeed"/>,
/// <see cref="AgentSession.Fail"/>, and <see cref="AgentSession.MarkCanceled"/>.
///
/// <para>Each method must:</para>
/// <list type="bullet">
///   <item>Update <see cref="AgentSession.Status"/> to the matching terminal
///         value, stamp <see cref="AgentSession.CompletedAt"/>, and clear
///         <see cref="AgentSession.QueuePosition"/>.</item>
///   <item>Raise exactly one <see cref="AgentSessionTerminated"/> domain event
///         with the right <c>FinalStatus</c> and <c>Reason</c>.</item>
///   <item>Be idempotent on already-terminal sessions for the failure /
///         cancel paths (Succeed throws because a successful turn can't be
///         "re-succeeded" silently — that would mask a bug).</item>
/// </list>
///
/// We don't go through EF here — the rich methods are pure CLR behavior and
/// the in-memory provider would just add latency without changing the result.
/// </summary>
public class AgentSessionTerminalTransitionsTests
{
    [Fact]
    public void Succeed_FromRunning_RaisesAgentSessionTerminated()
    {
        var session = new AgentSession
        {
            RuntimeId = Guid.NewGuid(),
            Status = AgentSessionStatus.Running,
        };

        session.Succeed();

        session.Status.Should().Be(AgentSessionStatus.Succeeded);
        session.CompletedAt.Should().NotBeNull().And.BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(1));
        session.QueuePosition.Should().BeNull();

        var terminated = session.DomainEvents.OfType<AgentSessionTerminated>().Single();
        terminated.SessionId.Should().Be(session.Id);
        terminated.RuntimeId.Should().Be(session.RuntimeId);
        terminated.FinalStatus.Should().Be(AgentSessionStatus.Succeeded);
        terminated.Reason.Should().BeNull();
    }

    [Fact]
    public void Succeed_FromCanceling_IsAllowed()
    {
        // Late completion: daemon emitted turn_completed after the user
        // pressed cancel. The session is already in Canceling. We let the
        // completion win because the work actually finished.
        var session = new AgentSession
        {
            RuntimeId = Guid.NewGuid(),
            Status = AgentSessionStatus.Canceling,
        };

        session.Succeed();

        session.Status.Should().Be(AgentSessionStatus.Succeeded);
        session.DomainEvents.OfType<AgentSessionTerminated>().Should().HaveCount(1);
    }

    [Fact]
    public void Succeed_FromPending_Throws()
    {
        var session = new AgentSession
        {
            RuntimeId = Guid.NewGuid(),
            Status = AgentSessionStatus.Pending,
        };

        var act = () => session.Succeed();
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Fail_FromRunning_RaisesAgentSessionTerminatedWithReason()
    {
        var session = new AgentSession
        {
            RuntimeId = Guid.NewGuid(),
            Status = AgentSessionStatus.Running,
        };

        session.Fail("rate_limited");

        session.Status.Should().Be(AgentSessionStatus.Failed);
        session.FailureReason.Should().Be("rate_limited");
        session.CompletedAt.Should().NotBeNull();
        session.QueuePosition.Should().BeNull();

        var terminated = session.DomainEvents.OfType<AgentSessionTerminated>().Single();
        terminated.FinalStatus.Should().Be(AgentSessionStatus.Failed);
        terminated.Reason.Should().Be("rate_limited");
    }

    [Fact]
    public void Fail_WithNullReason_PreservesExistingFailureReason()
    {
        var session = new AgentSession
        {
            RuntimeId = Guid.NewGuid(),
            Status = AgentSessionStatus.Running,
            FailureReason = "earlier_reason",
        };

        session.Fail(reason: null);

        session.FailureReason.Should().Be("earlier_reason", "null reason must not overwrite an existing one");
        var terminated = session.DomainEvents.OfType<AgentSessionTerminated>().Single();
        terminated.Reason.Should().Be("earlier_reason");
    }

    [Fact]
    public void Fail_OnAlreadyFailed_IsIdempotent()
    {
        var session = new AgentSession
        {
            RuntimeId = Guid.NewGuid(),
            Status = AgentSessionStatus.Failed,
            FailureReason = "first_reason",
            CompletedAt = DateTime.UtcNow.AddMinutes(-5),
        };
        var originalCompletedAt = session.CompletedAt;

        session.Fail("second_reason");

        // No second event raised — idempotency contract.
        session.DomainEvents.OfType<AgentSessionTerminated>().Should().BeEmpty();
        // State preserved — we didn't overwrite the original terminal state.
        session.Status.Should().Be(AgentSessionStatus.Failed);
        session.FailureReason.Should().Be("first_reason");
        session.CompletedAt.Should().Be(originalCompletedAt);
    }

    [Fact]
    public void Fail_OnAlreadySucceeded_IsIdempotent()
    {
        var session = new AgentSession
        {
            RuntimeId = Guid.NewGuid(),
            Status = AgentSessionStatus.Succeeded,
            CompletedAt = DateTime.UtcNow.AddMinutes(-5),
        };

        session.Fail("late_failure");

        session.Status.Should().Be(AgentSessionStatus.Succeeded, "a succeeded turn can't be retroactively failed");
        session.DomainEvents.OfType<AgentSessionTerminated>().Should().BeEmpty();
    }

    [Fact]
    public void Fail_FromPending_FlipsAndRaises()
    {
        // E.g. self-heal-maxed-out path — the session may not have flipped
        // to Running yet but we still need to mark it Failed.
        var session = new AgentSession
        {
            RuntimeId = Guid.NewGuid(),
            Status = AgentSessionStatus.Pending,
            QueuePosition = 2,
        };

        session.Fail("self_heal_maxed_out");

        session.Status.Should().Be(AgentSessionStatus.Failed);
        session.QueuePosition.Should().BeNull("Fail clears queue position so the dispatch-next path skips the row");
        session.DomainEvents.OfType<AgentSessionTerminated>().Should().HaveCount(1);
    }

    [Fact]
    public void MarkCanceled_FromRunning_RaisesAgentSessionTerminatedWithReason()
    {
        var session = new AgentSession
        {
            RuntimeId = Guid.NewGuid(),
            Status = AgentSessionStatus.Running,
        };

        session.MarkCanceled("user_requested");

        session.Status.Should().Be(AgentSessionStatus.Canceled);
        session.CancelReason.Should().Be("user_requested");
        session.CompletedAt.Should().NotBeNull();
        session.QueuePosition.Should().BeNull();

        var terminated = session.DomainEvents.OfType<AgentSessionTerminated>().Single();
        terminated.FinalStatus.Should().Be(AgentSessionStatus.Canceled);
        terminated.Reason.Should().Be("user_requested");
    }

    [Fact]
    public void MarkCanceled_FromCanceling_IsAllowed()
    {
        // The expected path for Card 4: user pressed cancel → Canceling, daemon
        // emits turn_canceled → MarkCanceled flips to terminal Canceled.
        var session = new AgentSession
        {
            RuntimeId = Guid.NewGuid(),
            Status = AgentSessionStatus.Canceling,
            CancelReason = "user_requested",
        };

        session.MarkCanceled(reason: null);

        session.Status.Should().Be(AgentSessionStatus.Canceled);
        session.CancelReason.Should().Be("user_requested", "null reason must not overwrite the earlier Canceling reason");
        session.DomainEvents.OfType<AgentSessionTerminated>().Should().HaveCount(1);
    }

    [Fact]
    public void MarkCanceled_OnAlreadyCanceled_IsIdempotent()
    {
        var session = new AgentSession
        {
            RuntimeId = Guid.NewGuid(),
            Status = AgentSessionStatus.Canceled,
            CancelReason = "first_reason",
            CompletedAt = DateTime.UtcNow.AddMinutes(-5),
        };
        var originalCompletedAt = session.CompletedAt;

        session.MarkCanceled("second_reason");

        session.DomainEvents.OfType<AgentSessionTerminated>().Should().BeEmpty();
        session.CancelReason.Should().Be("first_reason");
        session.CompletedAt.Should().Be(originalCompletedAt);
    }

    [Fact]
    public void MarkCanceling_FromRunning_RaisesSessionCancelRequested()
    {
        // Card 4 path: user clicks cancel while the turn is running. Status
        // flips Running -> Canceling, reason stamped, SessionCancelRequested
        // raised. Crucially this does NOT raise AgentSessionTerminated — the
        // runtime is still occupied draining the turn.
        var session = new AgentSession
        {
            RuntimeId = Guid.NewGuid(),
            Status = AgentSessionStatus.Running,
        };

        session.MarkCanceling("user_requested");

        session.Status.Should().Be(AgentSessionStatus.Canceling);
        session.CancelReason.Should().Be("user_requested");
        session.CompletedAt.Should().BeNull("Canceling is intermediate; the daemon's confirmation flips to terminal Canceled.");

        var requested = session.DomainEvents.OfType<SessionCancelRequested>().Single();
        requested.SessionId.Should().Be(session.Id);
        requested.RuntimeId.Should().Be(session.RuntimeId);
        requested.Reason.Should().Be("user_requested");

        session.DomainEvents.OfType<AgentSessionTerminated>().Should().BeEmpty(
            "MarkCanceling is intermediate; the runtime is still busy and AgentSessionTerminated is the dispatch-next signal.");
    }

    [Fact]
    public void MarkCanceling_OnAlreadyCanceling_IsIdempotent()
    {
        // Repeated cancel clicks must not re-raise SessionCancelRequested or
        // overwrite the original reason.
        var session = new AgentSession
        {
            RuntimeId = Guid.NewGuid(),
            Status = AgentSessionStatus.Canceling,
            CancelReason = "first_reason",
        };

        session.MarkCanceling("second_reason");

        session.Status.Should().Be(AgentSessionStatus.Canceling);
        session.CancelReason.Should().Be("first_reason", "first cancel wins for the audit trail");
        session.DomainEvents.OfType<SessionCancelRequested>().Should().BeEmpty();
    }

    [Fact]
    public void MarkCanceling_OnAlreadyTerminal_IsIdempotent()
    {
        // A late cancel arriving after the session already succeeded / failed /
        // was canceled must collapse to a no-op so the orphan janitor and
        // user retries don't clobber a recorded terminal state.
        foreach (var startState in new[]
        {
            AgentSessionStatus.Succeeded,
            AgentSessionStatus.Failed,
            AgentSessionStatus.Canceled,
        })
        {
            var session = new AgentSession
            {
                RuntimeId = Guid.NewGuid(),
                Status = startState,
                CompletedAt = DateTime.UtcNow.AddMinutes(-5),
            };

            session.MarkCanceling("user_requested");

            session.Status.Should().Be(startState, $"no-op when already {startState}");
            session.DomainEvents.OfType<SessionCancelRequested>().Should().BeEmpty($"no event raised when already {startState}");
        }
    }

    [Fact]
    public void MarkCanceling_FromPending_Throws()
    {
        // Pending sessions don't have an in-flight turn to drain — the cancel
        // command drops them straight to terminal Canceled via MarkCanceled.
        // Trying MarkCanceling on a Pending session is a programming error.
        var session = new AgentSession
        {
            RuntimeId = Guid.NewGuid(),
            Status = AgentSessionStatus.Pending,
            QueuePosition = 1,
        };

        var act = () => session.MarkCanceling("user_requested");
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void MarkCanceled_OnAlreadyTerminal_IsIdempotent()
    {
        // Already Failed or Succeeded → MarkCanceled is a no-op. The orphan
        // janitor uses MarkCanceled with reason="runtime_unavailable" and we
        // don't want it to clobber an earlier terminal state.
        foreach (var startState in new[] { AgentSessionStatus.Succeeded, AgentSessionStatus.Failed })
        {
            var session = new AgentSession
            {
                RuntimeId = Guid.NewGuid(),
                Status = startState,
                CompletedAt = DateTime.UtcNow.AddMinutes(-5),
            };

            session.MarkCanceled("runtime_unavailable");

            session.Status.Should().Be(startState, $"no-op when already {startState}");
            session.DomainEvents.OfType<AgentSessionTerminated>().Should().BeEmpty($"no event raised when already {startState}");
        }
    }
}
