using Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Source.Features.Conversations.EventHandlers;
using Source.Features.Conversations.Models;
using Source.Features.RuntimeLifecycle.Events;
using Source.Features.RuntimeLifecycle.Models;

namespace Api.Tests.Features.Conversations.EventHandlers;

/// <summary>
/// Unit tests for <see cref="FailOrphanSessionsOnRuntimeOnlineHandler"/>.
///
/// <para>The handler reaps in-flight <see cref="AgentSession"/> rows when a
/// runtime transitions Booting/Bootstrapping → Online, on the assumption that
/// any Running/Canceling session at the moment of a fresh-bootstrap promotion
/// came from a previous (now-dead) daemon. The watershed filter (added 2026-05-13
/// after a production miss) restricts the reap to sessions whose CreatedAt
/// predates the runtime's last <c>FromState=Online</c> transition — sessions
/// younger than that watershed were queued during downtime and belong to the
/// NEW daemon, so they MUST NOT be reaped.</para>
///
/// <para>The bug this guards against: on a runtime's first ever boot, a user
/// prompt that arrives in Pending state is enqueued via the ForceQueue path.
/// When the runtime reaches Online, <c>DispatchQueuedSessionsOnRuntimeOnlineHandler</c>
/// flips that session Pending → Running on the SAME RuntimeStateChanged event
/// this handler observes. Without the watershed filter, the freshly-dispatched
/// session shows up here as Running and gets wrongly reaped with
/// <c>runtime_respawned</c>, while the new daemon happily executes it anyway —
/// so the chat panel showed "couldn't finish" but the agent's events kept
/// streaming under the failed session id.</para>
/// </summary>
public class FailOrphanSessionsOnRuntimeOnlineHandlerTests : HandlerTestBase
{
    private FailOrphanSessionsOnRuntimeOnlineHandler BuildHandler()
        => new(Context, CreateLogger<FailOrphanSessionsOnRuntimeOnlineHandler>());

    private async Task<Guid> SeedConversation()
    {
        var convo = new Conversation
        {
            Id = Guid.NewGuid(),
            ProjectId = Guid.NewGuid(),
            BranchId = Guid.NewGuid(),
            Title = "test",
        };
        Context.Conversations.Add(convo);
        await Context.SaveChangesAsync();
        return convo.Id;
    }

    private async Task<Guid> SeedSession(
        Guid conversationId,
        Guid runtimeId,
        AgentSessionStatus status,
        DateTime? createdAt = null)
    {
        var session = new AgentSession
        {
            Id = Guid.NewGuid(),
            ConversationId = conversationId,
            RuntimeId = runtimeId,
            Prompt = "test prompt",
            Status = status,
        };
        Context.AgentSessions.Add(session);
        await Context.SaveChangesAsync();

        // CreatedAt is stamped by the audit interceptor; backdate explicitly
        // when the test needs to pre-date or post-date the runtime's watershed.
        if (createdAt is not null)
        {
            session.CreatedAt = createdAt.Value;
            await Context.SaveChangesAsync();
        }

        return session.Id;
    }

    private async Task SeedStateEvent(
        Guid runtimeId,
        RuntimeState? fromState,
        RuntimeState toState,
        DateTime createdAt)
    {
        var ev = new RuntimeStateEvent
        {
            Id = Guid.NewGuid(),
            RuntimeId = runtimeId,
            FromState = fromState,
            ToState = toState,
            Reason = "test",
            TriggeredBy = "test",
        };
        Context.RuntimeStateEvents.Add(ev);
        await Context.SaveChangesAsync();
        // Backdate after save because CreatedAt is auto-stamped by the
        // audit interceptor and we need controlled ordering for the watershed.
        ev.CreatedAt = createdAt;
        await Context.SaveChangesAsync();
    }

    /// <summary>
    /// First-boot regression: a fresh user prompt that's still in Pending+queue
    /// when the runtime reaches Online for the first time must NOT be reaped
    /// — there is no prior daemon, so no orphan is possible. (This is the case
    /// the user hit on branch 43233ef1 on 2026-05-13.)
    /// </summary>
    [Fact]
    public async Task Handle_FirstEverOnlineTransition_DoesNotReapAnySessions()
    {
        var runtimeId = Guid.NewGuid();
        var conversationId = await SeedConversation();

        // Simulate the DispatchQueuedSessionsOnRuntimeOnlineHandler winning
        // the race: it has already flipped Pending → Running by the time we
        // run. No prior Online state event exists.
        var freshSessionId = await SeedSession(conversationId, runtimeId, AgentSessionStatus.Running);

        var handler = BuildHandler();
        await handler.Handle(
            new RuntimeStateChanged(
                RuntimeId: runtimeId,
                ProjectId: Guid.NewGuid(),
                BranchId: Guid.NewGuid(),
                FromState: RuntimeState.Booting,
                ToState: RuntimeState.Online,
                Reason: "daemon:runtime_ready",
                TriggeredBy: "daemon",
                Metadata: null,
                OccurredAt: DateTime.UtcNow),
            CancellationToken.None);

        var fresh = await Context.AgentSessions.SingleAsync(s => s.Id == freshSessionId);
        fresh.Status.Should().Be(
            AgentSessionStatus.Running,
            "first-boot Online has no prior daemon → no orphans possible, the fresh session must not be reaped");
        fresh.FailureReason.Should().BeNull();
    }

    /// <summary>
    /// Second-boot watershed: when the runtime DID have a prior Online stretch,
    /// pre-watershed sessions are real orphans and must be reaped, but
    /// post-watershed sessions (queued during downtime) must be left alone.
    /// </summary>
    [Fact]
    public async Task Handle_RespawnAfterPriorOnline_OnlyReapsPreWatershedSessions()
    {
        var runtimeId = Guid.NewGuid();
        var conversationId = await SeedConversation();

        // Timeline:
        //   t0: runtime first reached Online
        //   t1: runtime left Online (crashed) — THIS is the watershed
        //   t2: user submits prompt during downtime → enqueued
        //   t3: runtime reaches Online again → both handlers fire on the same event
        var t0 = DateTime.UtcNow.AddMinutes(-10);
        var t1 = DateTime.UtcNow.AddMinutes(-5);  // watershed
        var preWatershedTime = t1.AddMinutes(-1); // session was Running before the crash
        var postWatershedTime = t1.AddMinutes(1); // session queued AFTER the crash

        await SeedStateEvent(runtimeId, RuntimeState.Bootstrapping, RuntimeState.Online, t0);
        await SeedStateEvent(runtimeId, RuntimeState.Online, RuntimeState.Crashed, t1);

        // Pre-watershed: a true orphan from the dead daemon.
        var orphanId = await SeedSession(conversationId, runtimeId, AgentSessionStatus.Running, createdAt: preWatershedTime);
        // Post-watershed: a fresh prompt the dispatch handler just flipped to Running.
        var freshId = await SeedSession(conversationId, runtimeId, AgentSessionStatus.Running, createdAt: postWatershedTime);

        var handler = BuildHandler();
        await handler.Handle(
            new RuntimeStateChanged(
                RuntimeId: runtimeId,
                ProjectId: Guid.NewGuid(),
                BranchId: Guid.NewGuid(),
                FromState: RuntimeState.Booting,
                ToState: RuntimeState.Online,
                Reason: "daemon:runtime_ready",
                TriggeredBy: "daemon",
                Metadata: null,
                OccurredAt: DateTime.UtcNow),
            CancellationToken.None);

        var orphan = await Context.AgentSessions.SingleAsync(s => s.Id == orphanId);
        orphan.Status.Should().Be(AgentSessionStatus.Failed, "pre-watershed Running session is a true orphan");
        orphan.FailureReason.Should().Be("runtime_respawned");

        var fresh = await Context.AgentSessions.SingleAsync(s => s.Id == freshId);
        fresh.Status.Should().Be(
            AgentSessionStatus.Running,
            "post-watershed session was queued during downtime and belongs to the new daemon — it must NOT be reaped");
        fresh.FailureReason.Should().BeNull();
    }

    /// <summary>
    /// Non-orphan-relevant transitions (e.g. Suspended → Online, Waking → Online)
    /// short-circuit the handler entirely. A different recovery path handles
    /// those states.
    /// </summary>
    [Fact]
    public async Task Handle_SuspendedToOnlineTransition_IsNoOp()
    {
        var runtimeId = Guid.NewGuid();
        var conversationId = await SeedConversation();
        var sessionId = await SeedSession(conversationId, runtimeId, AgentSessionStatus.Running);

        // Even though the watershed exists, this transition isn't a fresh
        // bootstrap, so the orphan handler doesn't even consider running.
        await SeedStateEvent(runtimeId, RuntimeState.Online, RuntimeState.Suspending, DateTime.UtcNow.AddMinutes(-5));

        var handler = BuildHandler();
        await handler.Handle(
            new RuntimeStateChanged(
                RuntimeId: runtimeId,
                ProjectId: Guid.NewGuid(),
                BranchId: Guid.NewGuid(),
                FromState: RuntimeState.Waking,
                ToState: RuntimeState.Online,
                Reason: "operator:wake",
                TriggeredBy: "user",
                Metadata: null,
                OccurredAt: DateTime.UtcNow),
            CancellationToken.None);

        var session = await Context.AgentSessions.SingleAsync(s => s.Id == sessionId);
        session.Status.Should().Be(AgentSessionStatus.Running);
        session.FailureReason.Should().BeNull();
    }

    /// <summary>
    /// Sessions on OTHER runtimes are never touched by this handler — the
    /// watershed is per-runtime.
    /// </summary>
    [Fact]
    public async Task Handle_OnlyConsidersSessionsOnTheTransitioningRuntime()
    {
        var runtimeA = Guid.NewGuid();
        var runtimeB = Guid.NewGuid();
        var conversationId = await SeedConversation();

        // Runtime A has a real respawn cycle with a true orphan.
        var watershed = DateTime.UtcNow.AddMinutes(-5);
        await SeedStateEvent(runtimeA, RuntimeState.Bootstrapping, RuntimeState.Online, watershed.AddMinutes(-5));
        await SeedStateEvent(runtimeA, RuntimeState.Online, RuntimeState.Crashed, watershed);
        var orphanOnA = await SeedSession(conversationId, runtimeA, AgentSessionStatus.Running, createdAt: watershed.AddMinutes(-1));

        // Runtime B also has an in-flight Running session, but B is not transitioning.
        // B's session must be untouched regardless of what A's watershed says.
        await SeedStateEvent(runtimeB, RuntimeState.Online, RuntimeState.Crashed, watershed);
        var bSessionId = await SeedSession(conversationId, runtimeB, AgentSessionStatus.Running, createdAt: watershed.AddMinutes(-1));

        var handler = BuildHandler();
        await handler.Handle(
            new RuntimeStateChanged(
                RuntimeId: runtimeA,
                ProjectId: Guid.NewGuid(),
                BranchId: Guid.NewGuid(),
                FromState: RuntimeState.Booting,
                ToState: RuntimeState.Online,
                Reason: "daemon:runtime_ready",
                TriggeredBy: "daemon",
                Metadata: null,
                OccurredAt: DateTime.UtcNow),
            CancellationToken.None);

        (await Context.AgentSessions.SingleAsync(s => s.Id == orphanOnA))
            .Status.Should().Be(AgentSessionStatus.Failed);
        (await Context.AgentSessions.SingleAsync(s => s.Id == bSessionId))
            .Status.Should().Be(AgentSessionStatus.Running, "runtime B was not transitioning; its sessions must not be touched");
    }
}
