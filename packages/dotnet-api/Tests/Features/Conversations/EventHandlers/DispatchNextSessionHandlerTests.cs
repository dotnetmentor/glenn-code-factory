using Api.Tests.Infrastructure;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Source.Features.Conversations.EventHandlers;
using Source.Features.Conversations.Events;
using Source.Features.Conversations.Models;
using Source.Features.RuntimeLifecycle.Models;
using Source.Features.SignalR.Contracts;
using Source.Features.SignalR.Hubs;

namespace Api.Tests.Features.Conversations.EventHandlers;

/// <summary>
/// Unit tests for <see cref="DispatchNextSessionHandler"/>. The handler runs
/// after a session terminates on a runtime and is responsible for picking
/// the head of the per-runtime queue (lowest <c>QueuePosition</c>) and
/// dispatching it via the typed runtime hub.
///
/// <list type="bullet">
///   <item>Lowest-<c>QueuePosition</c> Pending session wins and is flipped
///         to Running with <c>QueuePosition=null</c>; <see cref="StartTurnPayload"/>
///         goes to <c>runtime-{id}</c>.</item>
///   <item>No queued sessions → no fan-out, no DB mutation.</item>
///   <item>Concurrent terminal events on the same runtime → exactly one
///         session ends up Running (status filter + Dispatch() guard).</item>
///   <item>Hub fan-out failures are swallowed — the session is already
///         Running in the DB and the orphan janitor (Card 8) is the recovery
///         path; a SignalR exception must not surface back into the domain
///         dispatcher.</item>
/// </list>
///
/// Mirrors <see cref="BroadcastAgentEventHandlerTests"/> for hub mocking and
/// <see cref="HandlerTestBase"/> for the in-memory DB.
/// </summary>
public class DispatchNextSessionHandlerTests : HandlerTestBase
{
    private readonly Mock<IHubClients<IRuntimeClient>> _clients = new();
    private readonly Mock<IHubContext<RuntimeHub, IRuntimeClient>> _hub = new();
    private readonly Mock<IRuntimeClient> _groupClient = new();

    // The handler dispatches StartTurn via fire-and-forget Task.Run (see the
    // big comment block in DispatchNextSessionHandler.cs for why — short
    // version: the synchronous fan-out raced with the daemon's outbound
    // emitEvent and made every queued message fail in prod). Tests therefore
    // can't assume StartTurn has fired by the time `Handle()` returns; they
    // must `await WaitForStartTurnCountAsync(...)` first.
    private readonly List<StartTurnPayload> _startTurnInvocations = new();
    private readonly object _startTurnLock = new();

    public DispatchNextSessionHandlerTests()
    {
        _hub.SetupGet(h => h.Clients).Returns(_clients.Object);
        _clients.Setup(c => c.Group(It.IsAny<string>())).Returns(_groupClient.Object);
        _groupClient
            .Setup(c => c.StartTurn(It.IsAny<StartTurnPayload>()))
            .Callback<StartTurnPayload>(p =>
            {
                lock (_startTurnLock) { _startTurnInvocations.Add(p); }
            })
            .Returns(Task.CompletedTask);
    }

    /// <summary>
    /// Polls until <see cref="_startTurnInvocations"/> contains at least
    /// <paramref name="expectedCount"/> entries, or times out. Use this AFTER
    /// calling <c>handler.Handle()</c> and BEFORE any <c>Times.Once/Exactly</c>
    /// verify — the handler's StartTurn fan-out is asynchronous (Task.Run),
    /// so a synchronous Verify race-loses.
    /// </summary>
    private async Task WaitForStartTurnCountAsync(int expectedCount, int timeoutMs = 2000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            int n;
            lock (_startTurnLock) { n = _startTurnInvocations.Count; }
            if (n >= expectedCount) return;
            await Task.Delay(10);
        }
        int finalCount;
        lock (_startTurnLock) { finalCount = _startTurnInvocations.Count; }
        finalCount.Should().BeGreaterOrEqualTo(
            expectedCount,
            $"StartTurn fan-out (Task.Run) did not fire {expectedCount} time(s) within {timeoutMs}ms");
    }

    /// <summary>
    /// Returns a snapshot of all StartTurn payloads observed so far.
    /// </summary>
    private IReadOnlyList<StartTurnPayload> StartTurnInvocations
    {
        get
        {
            lock (_startTurnLock) { return _startTurnInvocations.ToArray(); }
        }
    }

    [Fact]
    public async Task Handle_WithSinglePendingSession_DispatchesIt()
    {
        var runtimeId = Guid.NewGuid();
        var conversationId = await SeedConversation();
        var terminatedId = await SeedSession(conversationId, runtimeId, AgentSessionStatus.Succeeded);
        var queuedId = await SeedQueuedSession(conversationId, runtimeId, queuePosition: 1, prompt: "next-up", claudeSessionId: "claude-X");

        var handler = BuildHandler();
        await handler.Handle(
            new AgentSessionTerminated(terminatedId, runtimeId, AgentSessionStatus.Succeeded, null),
            CancellationToken.None);

        // Queued session is now Running, queue position cleared.
        var dispatched = await Context.AgentSessions.SingleAsync(s => s.Id == queuedId);
        dispatched.Status.Should().Be(AgentSessionStatus.Running);
        dispatched.QueuePosition.Should().BeNull();

        // StartTurn fan-out is fire-and-forget (Task.Run) — await it before
        // verifying.
        await WaitForStartTurnCountAsync(1);
        _clients.Verify(c => c.Group($"runtime-{runtimeId}"), Times.Once);
        _groupClient.Verify(c => c.StartTurn(It.IsAny<StartTurnPayload>()), Times.Once);

        var captured = StartTurnInvocations.Single();
        captured.SessionId.Should().Be(queuedId);
        captured.ConversationId.Should().Be(conversationId);
        captured.Prompt.Should().Be("next-up");
        captured.AgentId.Should().Be("claude-X");
    }

    [Fact]
    public async Task Handle_WithNoQueuedSessions_IsNoOp()
    {
        var runtimeId = Guid.NewGuid();
        var conversationId = await SeedConversation();
        var terminatedId = await SeedSession(conversationId, runtimeId, AgentSessionStatus.Succeeded);

        var handler = BuildHandler();
        await handler.Handle(
            new AgentSessionTerminated(terminatedId, runtimeId, AgentSessionStatus.Succeeded, null),
            CancellationToken.None);

        // No fan-out attempted.
        _groupClient.Verify(c => c.StartTurn(It.IsAny<StartTurnPayload>()), Times.Never);
        _clients.Verify(c => c.Group(It.IsAny<string>()), Times.Never);

        // Original session unchanged.
        var terminated = await Context.AgentSessions.SingleAsync(s => s.Id == terminatedId);
        terminated.Status.Should().Be(AgentSessionStatus.Succeeded);
    }

    [Fact]
    public async Task Handle_WithMultiplePendingSessions_PicksLowestQueuePosition()
    {
        var runtimeId = Guid.NewGuid();
        var conversationId = await SeedConversation();
        var terminatedId = await SeedSession(conversationId, runtimeId, AgentSessionStatus.Succeeded);

        // Insert in scrambled order to prove the ORDER BY is doing the work.
        var thirdId = await SeedQueuedSession(conversationId, runtimeId, queuePosition: 3, prompt: "third");
        var firstId = await SeedQueuedSession(conversationId, runtimeId, queuePosition: 1, prompt: "first");
        var secondId = await SeedQueuedSession(conversationId, runtimeId, queuePosition: 2, prompt: "second");

        var handler = BuildHandler();
        await handler.Handle(
            new AgentSessionTerminated(terminatedId, runtimeId, AgentSessionStatus.Succeeded, null),
            CancellationToken.None);

        // Only the lowest-position session was dispatched.
        var first = await Context.AgentSessions.SingleAsync(s => s.Id == firstId);
        first.Status.Should().Be(AgentSessionStatus.Running);
        first.QueuePosition.Should().BeNull();

        var second = await Context.AgentSessions.SingleAsync(s => s.Id == secondId);
        second.Status.Should().Be(AgentSessionStatus.Pending);
        second.QueuePosition.Should().Be(2);

        var third = await Context.AgentSessions.SingleAsync(s => s.Id == thirdId);
        third.Status.Should().Be(AgentSessionStatus.Pending);
        third.QueuePosition.Should().Be(3);

        await WaitForStartTurnCountAsync(1);
        var captured = StartTurnInvocations.Single();
        captured.SessionId.Should().Be(firstId);
        captured.Prompt.Should().Be("first");
    }

    [Fact]
    public async Task Handle_OnlyConsidersSessionsForTheTerminatedRuntime()
    {
        // Two runtimes: A terminates, B has its own queue. We must NOT dispatch
        // anything from runtime B's queue.
        var runtimeA = Guid.NewGuid();
        var runtimeB = Guid.NewGuid();
        var conversationId = await SeedConversation();
        var terminatedOnA = await SeedSession(conversationId, runtimeA, AgentSessionStatus.Succeeded);
        var queuedOnA = await SeedQueuedSession(conversationId, runtimeA, queuePosition: 1, prompt: "for-A");
        var queuedOnB = await SeedQueuedSession(conversationId, runtimeB, queuePosition: 1, prompt: "for-B");

        var handler = BuildHandler();
        await handler.Handle(
            new AgentSessionTerminated(terminatedOnA, runtimeA, AgentSessionStatus.Succeeded, null),
            CancellationToken.None);

        // A's queued session dispatched.
        (await Context.AgentSessions.SingleAsync(s => s.Id == queuedOnA))
            .Status.Should().Be(AgentSessionStatus.Running);

        // B's queued session untouched.
        var bSession = await Context.AgentSessions.SingleAsync(s => s.Id == queuedOnB);
        bSession.Status.Should().Be(AgentSessionStatus.Pending);
        bSession.QueuePosition.Should().Be(1);

        // Single fan-out, scoped to runtime A's group.
        await WaitForStartTurnCountAsync(1);
        _clients.Verify(c => c.Group($"runtime-{runtimeA}"), Times.Once);
        _clients.Verify(c => c.Group($"runtime-{runtimeB}"), Times.Never);
        _groupClient.Verify(c => c.StartTurn(It.IsAny<StartTurnPayload>()), Times.Once);
    }

    [Fact]
    public async Task Handle_IgnoresRunningAndTerminalSessions()
    {
        // Pending sessions with QueuePosition != null are eligible. Anything
        // else — Running, Succeeded, Failed, Canceled, Pending-without-queue-
        // position — must be skipped. Otherwise we'd re-dispatch in-flight
        // turns or already-terminated rows.
        var runtimeId = Guid.NewGuid();
        var conversationId = await SeedConversation();
        var terminatedId = await SeedSession(conversationId, runtimeId, AgentSessionStatus.Succeeded);

        await SeedSession(conversationId, runtimeId, AgentSessionStatus.Running); // currently running
        await SeedSession(conversationId, runtimeId, AgentSessionStatus.Failed);  // already terminal
        await SeedSession(conversationId, runtimeId, AgentSessionStatus.Pending); // pending but no queue position (transient)

        var handler = BuildHandler();
        await handler.Handle(
            new AgentSessionTerminated(terminatedId, runtimeId, AgentSessionStatus.Succeeded, null),
            CancellationToken.None);

        // Nothing was dispatched.
        _groupClient.Verify(c => c.StartTurn(It.IsAny<StartTurnPayload>()), Times.Never);
    }

    [Fact]
    public async Task Handle_TwoConcurrentTerminals_OnlyOneSessionDispatched()
    {
        // Two terminal events fire on the same runtime in parallel; both
        // handlers see the same head-of-queue. Idempotency requires that only
        // one handler successfully flips the session to Running and sends
        // StartTurn. The second handler's Dispatch() either sees Pending →
        // succeeds and the first SaveChanges then conflicts (DbUpdateConcurrencyException)
        // — or, more commonly with InMemory's last-writer-wins semantics,
        // both writes "succeed" but Dispatch() throws on the second attempt
        // because the entity's local Status was already flipped.
        //
        // We use a SHARED context across both handler invocations to mirror
        // the production scoped-DbContext behavior most closely (the
        // DomainEventInterceptor publishes both events in the same SaveChanges
        // scope; the event handlers run sequentially, not concurrently). Even
        // sequentially, the second invocation must be a no-op.
        var runtimeId = Guid.NewGuid();
        var conversationId = await SeedConversation();
        var firstTerminated = await SeedSession(conversationId, runtimeId, AgentSessionStatus.Succeeded);
        var secondTerminated = await SeedSession(conversationId, runtimeId, AgentSessionStatus.Failed);
        var queuedId = await SeedQueuedSession(conversationId, runtimeId, queuePosition: 1, prompt: "queued");

        var handler = BuildHandler();

        // Fire both terminal events in sequence — like the interceptor does.
        await handler.Handle(
            new AgentSessionTerminated(firstTerminated, runtimeId, AgentSessionStatus.Succeeded, null),
            CancellationToken.None);
        await handler.Handle(
            new AgentSessionTerminated(secondTerminated, runtimeId, AgentSessionStatus.Failed, "rate_limited"),
            CancellationToken.None);

        // Exactly one session ended up Running on this runtime (the originally
        // queued one), and exactly one StartTurn went to the daemon group.
        var runningCount = await Context.AgentSessions
            .CountAsync(s => s.RuntimeId == runtimeId && s.Status == AgentSessionStatus.Running);
        runningCount.Should().Be(1, "second terminal event must be a no-op on the dispatch path");

        var queued = await Context.AgentSessions.SingleAsync(s => s.Id == queuedId);
        queued.Status.Should().Be(AgentSessionStatus.Running);
        queued.QueuePosition.Should().BeNull();

        await WaitForStartTurnCountAsync(1);
        _groupClient.Verify(c => c.StartTurn(It.IsAny<StartTurnPayload>()), Times.Once);
    }

    [Fact]
    public async Task Handle_HubFanoutThrows_DoesNotPropagate()
    {
        // The session has already been flipped to Running by the time the hub
        // call happens. A SignalR exception must not propagate back to the
        // domain dispatcher — the orphan-session janitor (Card 8) is the
        // recovery path, not the event chain.
        var runtimeId = Guid.NewGuid();
        var conversationId = await SeedConversation();
        var terminatedId = await SeedSession(conversationId, runtimeId, AgentSessionStatus.Succeeded);
        var queuedId = await SeedQueuedSession(conversationId, runtimeId, queuePosition: 1);

        // ThrowsAsync still triggers the Callback first, so the StartTurn
        // is still recorded in _startTurnInvocations — we use that to know
        // when the fire-and-forget Task.Run actually ran.
        _groupClient
            .Setup(c => c.StartTurn(It.IsAny<StartTurnPayload>()))
            .Callback<StartTurnPayload>(p =>
            {
                lock (_startTurnLock) { _startTurnInvocations.Add(p); }
            })
            .ThrowsAsync(new InvalidOperationException("daemon disconnected"));

        var logger = new Mock<ILogger<DispatchNextSessionHandler>>();
        var handler = new DispatchNextSessionHandler(Context, _hub.Object, logger.Object);

        var act = async () => await handler.Handle(
            new AgentSessionTerminated(terminatedId, runtimeId, AgentSessionStatus.Succeeded, null),
            CancellationToken.None);
        await act.Should().NotThrowAsync();

        // Session is still Running in the DB — we don't roll back on hub
        // failure because the SessionDispatched audit was already raised.
        var dispatched = await Context.AgentSessions.SingleAsync(s => s.Id == queuedId);
        dispatched.Status.Should().Be(AgentSessionStatus.Running);

        // The fan-out Task.Run is async; wait for it to fire (and throw)
        // before checking the error was logged.
        await WaitForStartTurnCountAsync(1);
        // Allow one more scheduler tick for the catch-block to log after the
        // throw resolves.
        await Task.Delay(50);

        // Error logged so an operator can see the orphan situation.
        logger.Verify(
            l => l.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<InvalidOperationException>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task Handle_PicksUpAfterFailedAndCanceledTerminations()
    {
        // The handler treats every terminal status the same — Succeeded /
        // Failed / Canceled all free up the runtime. Verify both Failed and
        // Canceled trigger dispatch, mirroring the Succeeded path.
        var runtimeId = Guid.NewGuid();
        var conversationId = await SeedConversation();

        // Failed termination → next one dispatched.
        var failedId = await SeedSession(conversationId, runtimeId, AgentSessionStatus.Failed);
        var queuedAfterFailed = await SeedQueuedSession(conversationId, runtimeId, queuePosition: 1, prompt: "after-fail");

        var handler = BuildHandler();
        await handler.Handle(
            new AgentSessionTerminated(failedId, runtimeId, AgentSessionStatus.Failed, "rate_limited"),
            CancellationToken.None);

        (await Context.AgentSessions.SingleAsync(s => s.Id == queuedAfterFailed))
            .Status.Should().Be(AgentSessionStatus.Running);

        // Now cancel the freshly-dispatched one (manually flip for test) and
        // queue a new one — Canceled termination should also dispatch.
        var nowRunning = await Context.AgentSessions.SingleAsync(s => s.Id == queuedAfterFailed);
        nowRunning.Status = AgentSessionStatus.Canceled;
        nowRunning.CompletedAt = DateTime.UtcNow;
        await Context.SaveChangesAsync();

        var queuedAfterCanceled = await SeedQueuedSession(conversationId, runtimeId, queuePosition: 1, prompt: "after-cancel");

        await handler.Handle(
            new AgentSessionTerminated(queuedAfterFailed, runtimeId, AgentSessionStatus.Canceled, "user_requested"),
            CancellationToken.None);

        (await Context.AgentSessions.SingleAsync(s => s.Id == queuedAfterCanceled))
            .Status.Should().Be(AgentSessionStatus.Running);
    }

    // Regression: queued-session dispatch race (2026-05-12)
    //
    // The handler must NOT synchronously block on the SignalR fan-out. The
    // production failure mode was:
    //
    //   1. Daemon's TurnRunner awaits `emitEvent(TurnCompleted)`.
    //   2. Server's RuntimeHub.EmitEvent calls session.Succeed(), which
    //      synchronously raises AgentSessionTerminated → this handler runs.
    //   3. This handler `awaited` _runtimeHub.Clients.Group(...).StartTurn(...).
    //   4. The StartTurn message gets queued onto the SAME daemon connection's
    //      outbound buffer before the EmitEvent response. The daemon receives
    //      and processes the StartTurn BEFORE its own emitEvent await resolves
    //      → state still 'running' → refused with `turn_already_running`.
    //   5. Queued session marked Failed with daemon_refused_concurrent.
    //
    // The fix defers the fan-out via Task.Run. This test pins that behavior:
    // the SignalR call must NOT have happened by the time Handle returns —
    // it happens on a separate task continuation.
    [Fact]
    public async Task Handle_DoesNotBlockOnStartTurn_FanoutIsDeferredToTaskRun()
    {
        var runtimeId = Guid.NewGuid();
        var conversationId = await SeedConversation();
        var terminatedId = await SeedSession(conversationId, runtimeId, AgentSessionStatus.Succeeded);
        await SeedQueuedSession(conversationId, runtimeId, queuePosition: 1, prompt: "queued");

        // Make StartTurn hang forever — proves we're not awaiting it from
        // inside Handle. If the fix regresses and Handle awaits the fan-out
        // again, Handle itself will hang here.
        var startTurnEntered = new TaskCompletionSource<bool>();
        var releaseStartTurn = new TaskCompletionSource<bool>();
        _groupClient
            .Setup(c => c.StartTurn(It.IsAny<StartTurnPayload>()))
            .Returns<StartTurnPayload>(async _ =>
            {
                startTurnEntered.TrySetResult(true);
                await releaseStartTurn.Task;
            });

        var handler = BuildHandler();
        var handleTask = handler.Handle(
            new AgentSessionTerminated(terminatedId, runtimeId, AgentSessionStatus.Succeeded, null),
            CancellationToken.None);

        // Handle must complete promptly (well under a second) even though
        // StartTurn is hanging on the deferred task. This is the actual
        // regression guard — if someone removes the Task.Run wrapper and
        // re-awaits the SignalR call, this assertion fails with a timeout.
        var handleCompleted = await Task.WhenAny(handleTask, Task.Delay(TimeSpan.FromSeconds(2)));
        handleCompleted.Should().Be(handleTask,
            "Handle must return without awaiting the SignalR fan-out — otherwise the daemon's emitEvent await deadlocks against the StartTurn write back to the same connection (see DispatchNextSessionHandler comment block).");

        // The deferred Task.Run did fire (eventually).
        var enteredInTime = await Task.WhenAny(startTurnEntered.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        enteredInTime.Should().Be(startTurnEntered.Task,
            "the deferred StartTurn fan-out must still execute on the thread pool");

        // Cleanup — let the hanging StartTurn task complete.
        releaseStartTurn.SetResult(true);
    }

    // ------------------------------------------------------------------
    // helpers

    private DispatchNextSessionHandler BuildHandler() =>
        new(Context, _hub.Object, NullLogger<DispatchNextSessionHandler>.Instance);

    private async Task<Guid> SeedConversation()
    {
        var conversation = new Conversation
        {
            ProjectId = Guid.NewGuid(),
            Title = "test",
            BranchId = Guid.NewGuid(),
            Status = ConversationStatus.Active,
            LastActivityAt = DateTime.UtcNow,
        };
        Context.Conversations.Add(conversation);
        await Context.SaveChangesAsync();
        return conversation.Id;
    }

    private async Task<Guid> SeedSession(Guid conversationId, Guid runtimeId, AgentSessionStatus status)
    {
        var session = new AgentSession
        {
            ConversationId = conversationId,
            RuntimeId = runtimeId,
            Prompt = "seeded-" + status,
            Status = status,
            CompletedAt = status is AgentSessionStatus.Succeeded
                or AgentSessionStatus.Failed
                or AgentSessionStatus.Canceled
                ? DateTime.UtcNow
                : null,
        };
        Context.AgentSessions.Add(session);
        await Context.SaveChangesAsync();
        return session.Id;
    }

    private async Task<Guid> SeedQueuedSession(
        Guid conversationId,
        Guid runtimeId,
        int queuePosition,
        string prompt = "queued",
        string? claudeSessionId = null)
    {
        var session = new AgentSession
        {
            ConversationId = conversationId,
            RuntimeId = runtimeId,
            Prompt = prompt,
            Status = AgentSessionStatus.Pending,
            QueuePosition = queuePosition,
            AgentId = claudeSessionId,
        };
        Context.AgentSessions.Add(session);
        await Context.SaveChangesAsync();
        return session.Id;
    }
}
