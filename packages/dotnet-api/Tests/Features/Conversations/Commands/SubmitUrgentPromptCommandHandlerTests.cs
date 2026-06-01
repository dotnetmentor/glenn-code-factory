using Api.Tests.Infrastructure;
using MediatR;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Source.Features.Conversations.Commands;
using Source.Features.Conversations.Events;
using Source.Features.Conversations.Models;
using Source.Features.RuntimeLifecycle.Models;
using Source.Features.SignalR.Contracts;
using Source.Features.SignalR.Hubs;

namespace Api.Tests.Features.Conversations.Commands;

/// <summary>
/// Unit tests for <see cref="SubmitUrgentPromptCommandHandler"/> — the command
/// behind <c>POST /api/conversations/{conversationId}/urgent-prompt</c>.
///
/// <list type="bullet">
///   <item>Idle runtime → urgent dispatches immediately
///         (<see cref="AgentSessionStatus.Running"/>, <c>QueuePosition</c>=null,
///         <see cref="IRuntimeClient.StartTurn"/> pushed, no
///         <see cref="IRuntimeClient.CancelTurn"/>, <c>CanceledSessionId</c>=null,
///         <c>Queued</c>=false).</item>
///   <item>Running runtime → current goes Canceling, urgent enqueued at
///         position 1, every existing Pending shifted +1, CancelTurn pushed
///         exactly once with reason <c>"urgent_preempted"</c>.</item>
///   <item>Already-Canceling runtime → urgent still enqueues at 1 but no
///         additional CancelTurn fan-out (the original cancel command already
///         pushed it; double-firing would confuse the daemon).</item>
///   <item>Empty / whitespace prompt → <c>Result.Failure</c>, no DB mutation.</item>
///   <item>Missing conversation → <c>Result.Failure("Conversation not found")</c>.</item>
///   <item>Missing runtime → <c>Result.Failure</c>, no DB mutation.</item>
///   <item><see cref="SessionUrgentPreempted"/> published once with correct
///         payload on successful idle and busy paths.</item>
/// </list>
///
/// Mirrors <see cref="CancelSessionCommandHandlerTests"/> for the SignalR mock
/// chain and <see cref="HandlerTestBase"/> for the in-memory DB.
/// </summary>
public class SubmitUrgentPromptCommandHandlerTests : HandlerTestBase
{
    // SignalR mock chain: hub.Clients.Group("runtime-{id}").CancelTurn|StartTurn(payload)
    private readonly Mock<IHubClients<IRuntimeClient>> _clients = new();
    private readonly Mock<IHubContext<RuntimeHub, IRuntimeClient>> _hub = new();
    private readonly Mock<IRuntimeClient> _groupClient = new();
    private readonly Mock<IMediator> _mediator = new();

    public SubmitUrgentPromptCommandHandlerTests()
    {
        _hub.SetupGet(h => h.Clients).Returns(_clients.Object);
        _clients.Setup(c => c.Group(It.IsAny<string>())).Returns(_groupClient.Object);
        _groupClient
            .Setup(c => c.CancelTurn(It.IsAny<CancelTurnPayload>()))
            .Returns(Task.CompletedTask);
        _groupClient
            .Setup(c => c.StartTurn(It.IsAny<StartTurnPayload>()))
            .Returns(Task.CompletedTask);
        _mediator
            .Setup(m => m.Publish(It.IsAny<SessionUrgentPreempted>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    [Fact]
    public async Task Handle_NoActiveSession_DispatchesImmediately()
    {
        // Idle runtime: no preempt, urgent goes straight to Running and we
        // push StartTurn over SignalR — same shape as the regular dispatcher's
        // idle branch. Queued=false, CanceledSessionId=null.
        var (conversationId, runtimeId, _) = await SeedRuntimeAndConversation();

        StartTurnPayload? startCaptured = null;
        _groupClient
            .Setup(c => c.StartTurn(It.IsAny<StartTurnPayload>()))
            .Callback<StartTurnPayload>(p => startCaptured = p)
            .Returns(Task.CompletedTask);

        var handler = BuildHandler();
        var result = await handler.Handle(
            new SubmitUrgentPromptCommand(conversationId, "do this now", "user-1"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Queued.Should().BeFalse();
        result.Value.CanceledSessionId.Should().BeNull();
        result.Value.QueuePosition.Should().BeNull();

        var session = await Context.AgentSessions.SingleAsync(s => s.Id == result.Value.SessionId);
        session.Status.Should().Be(AgentSessionStatus.Running);
        session.QueuePosition.Should().BeNull();
        session.Prompt.Should().Be("do this now");

        // CancelTurn never fired (nothing to cancel); StartTurn fired once.
        _groupClient.Verify(c => c.CancelTurn(It.IsAny<CancelTurnPayload>()), Times.Never);
        _groupClient.Verify(c => c.StartTurn(It.IsAny<StartTurnPayload>()), Times.Once);
        startCaptured.Should().NotBeNull();
        startCaptured!.SessionId.Should().Be(result.Value.SessionId);
        startCaptured.Prompt.Should().Be("do this now");
    }

    [Fact]
    public async Task Handle_RunningSession_PreemptsAndQueuesUrgent()
    {
        // Busy runtime: current Running session goes Canceling, urgent
        // session enqueued at position 1, every existing Pending shifted +1,
        // CancelTurn pushed once for the preempted session.
        var (conversationId, runtimeId, _) = await SeedRuntimeAndConversation();
        var runningId = await SeedSession(conversationId, runtimeId, AgentSessionStatus.Running);
        var pendingA = await SeedSession(conversationId, runtimeId, AgentSessionStatus.Pending, queuePosition: 1);
        var pendingB = await SeedSession(conversationId, runtimeId, AgentSessionStatus.Pending, queuePosition: 2);

        CancelTurnPayload? cancelCaptured = null;
        _groupClient
            .Setup(c => c.CancelTurn(It.IsAny<CancelTurnPayload>()))
            .Callback<CancelTurnPayload>(p => cancelCaptured = p)
            .Returns(Task.CompletedTask);

        var handler = BuildHandler();
        var result = await handler.Handle(
            new SubmitUrgentPromptCommand(conversationId, "urgent!", "user-7"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Queued.Should().BeTrue();
        result.Value.QueuePosition.Should().Be(1);
        result.Value.CanceledSessionId.Should().Be(runningId);

        // Current session is now Canceling with the urgent_preempted reason.
        var current = await Context.AgentSessions.SingleAsync(s => s.Id == runningId);
        current.Status.Should().Be(AgentSessionStatus.Canceling);
        current.CancelReason.Should().Be("urgent_preempted");

        // Urgent session at the head of the queue.
        var urgent = await Context.AgentSessions.SingleAsync(s => s.Id == result.Value.SessionId);
        urgent.Status.Should().Be(AgentSessionStatus.Pending);
        urgent.QueuePosition.Should().Be(1);

        // Existing queued sessions shifted by +1.
        var shiftedA = await Context.AgentSessions.SingleAsync(s => s.Id == pendingA);
        var shiftedB = await Context.AgentSessions.SingleAsync(s => s.Id == pendingB);
        shiftedA.QueuePosition.Should().Be(2);
        shiftedB.QueuePosition.Should().Be(3);

        // CancelTurn fired exactly once for the preempted session, with
        // reason "urgent_preempted".
        _groupClient.Verify(c => c.CancelTurn(It.IsAny<CancelTurnPayload>()), Times.Once);
        _groupClient.Verify(c => c.StartTurn(It.IsAny<StartTurnPayload>()), Times.Never,
            "StartTurn fires later when DispatchNextSessionHandler picks the urgent session off the queue.");
        cancelCaptured.Should().NotBeNull();
        cancelCaptured!.SessionId.Should().Be(runningId);
        cancelCaptured.Reason.Should().Be("urgent_preempted");
    }

    [Fact]
    public async Task Handle_CancelingSession_QueuesWithoutDoubleCancelTurn()
    {
        // The runtime's current session is already Canceling — the user (or
        // another path) already pressed cancel. An urgent prompt landing now
        // must still queue at position 1 but MUST NOT fan out a second
        // CancelTurn — the daemon already received the first one and a
        // duplicate would be confusing at best and racy at worst.
        var (conversationId, runtimeId, _) = await SeedRuntimeAndConversation();
        var cancelingId = await SeedSession(
            conversationId, runtimeId, AgentSessionStatus.Canceling);

        var handler = BuildHandler();
        var result = await handler.Handle(
            new SubmitUrgentPromptCommand(conversationId, "still urgent", "user-7"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Queued.Should().BeTrue();
        result.Value.QueuePosition.Should().Be(1);
        result.Value.CanceledSessionId.Should().Be(cancelingId);

        // Already-Canceling session stays Canceling; no second MarkCanceling
        // (idempotent on the entity) and no second CancelTurn over the wire.
        var current = await Context.AgentSessions.SingleAsync(s => s.Id == cancelingId);
        current.Status.Should().Be(AgentSessionStatus.Canceling);

        var urgent = await Context.AgentSessions.SingleAsync(s => s.Id == result.Value.SessionId);
        urgent.Status.Should().Be(AgentSessionStatus.Pending);
        urgent.QueuePosition.Should().Be(1);

        _groupClient.Verify(c => c.CancelTurn(It.IsAny<CancelTurnPayload>()), Times.Never,
            "Already-Canceling — second CancelTurn would race the daemon's drain.");
        _groupClient.Verify(c => c.StartTurn(It.IsAny<StartTurnPayload>()), Times.Never);
    }

    [Fact]
    public async Task Handle_EmptyPrompt_ReturnsFailure_NoDbMutation()
    {
        var (conversationId, runtimeId, _) = await SeedRuntimeAndConversation();
        await SeedSession(conversationId, runtimeId, AgentSessionStatus.Running);

        var handler = BuildHandler();
        var result = await handler.Handle(
            new SubmitUrgentPromptCommand(conversationId, "   ", "user-1"),
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("empty");

        // No urgent session inserted; existing session untouched.
        var sessions = await Context.AgentSessions.ToListAsync();
        sessions.Should().HaveCount(1);
        sessions[0].Status.Should().Be(AgentSessionStatus.Running);

        _groupClient.Verify(c => c.CancelTurn(It.IsAny<CancelTurnPayload>()), Times.Never);
        _groupClient.Verify(c => c.StartTurn(It.IsAny<StartTurnPayload>()), Times.Never);
        _mediator.Verify(
            m => m.Publish(It.IsAny<SessionUrgentPreempted>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Handle_MissingConversation_ReturnsFailure()
    {
        var handler = BuildHandler();
        var result = await handler.Handle(
            new SubmitUrgentPromptCommand(Guid.NewGuid(), "hello", "user-1"),
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("Conversation not found");

        _groupClient.Verify(c => c.CancelTurn(It.IsAny<CancelTurnPayload>()), Times.Never);
        _groupClient.Verify(c => c.StartTurn(It.IsAny<StartTurnPayload>()), Times.Never);
    }

    [Fact]
    public async Task Handle_MissingRuntime_ReturnsFailure()
    {
        // Conversation exists but no ProjectRuntime row for its project — the
        // command should not crash, just fail cleanly. Mirrors the "no runtime"
        // gate in AgentHub.SubmitPrompt.
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

        var handler = BuildHandler();
        var result = await handler.Handle(
            new SubmitUrgentPromptCommand(conversation.Id, "hello", "user-1"),
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("runtime");

        // No urgent session was inserted.
        Context.AgentSessions.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_PublishesSessionUrgentPreemptedOnce_BusyPath()
    {
        var (conversationId, runtimeId, _) = await SeedRuntimeAndConversation();
        var runningId = await SeedSession(conversationId, runtimeId, AgentSessionStatus.Running);

        SessionUrgentPreempted? captured = null;
        _mediator
            .Setup(m => m.Publish(It.IsAny<SessionUrgentPreempted>(), It.IsAny<CancellationToken>()))
            .Callback<object, CancellationToken>((evt, _) => captured = (SessionUrgentPreempted)evt)
            .Returns(Task.CompletedTask);

        var handler = BuildHandler();
        var result = await handler.Handle(
            new SubmitUrgentPromptCommand(conversationId, "urgent!", "user-99"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _mediator.Verify(
            m => m.Publish(It.IsAny<SessionUrgentPreempted>(), It.IsAny<CancellationToken>()),
            Times.Once);

        captured.Should().NotBeNull();
        captured!.NewSessionId.Should().Be(result.Value.SessionId);
        captured.CanceledSessionId.Should().Be(runningId);
        captured.RuntimeId.Should().Be(runtimeId);
        captured.ActorUserId.Should().Be("user-99");
    }

    [Fact]
    public async Task Handle_PublishesSessionUrgentPreemptedOnce_IdlePath()
    {
        // On the idle path there's no canceled session, but the event is
        // still published with CanceledSessionId=null so audit consumers see
        // a uniform "an urgent prompt landed" signal regardless of branch.
        var (conversationId, runtimeId, _) = await SeedRuntimeAndConversation();

        SessionUrgentPreempted? captured = null;
        _mediator
            .Setup(m => m.Publish(It.IsAny<SessionUrgentPreempted>(), It.IsAny<CancellationToken>()))
            .Callback<object, CancellationToken>((evt, _) => captured = (SessionUrgentPreempted)evt)
            .Returns(Task.CompletedTask);

        var handler = BuildHandler();
        var result = await handler.Handle(
            new SubmitUrgentPromptCommand(conversationId, "go", "user-99"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _mediator.Verify(
            m => m.Publish(It.IsAny<SessionUrgentPreempted>(), It.IsAny<CancellationToken>()),
            Times.Once);

        captured.Should().NotBeNull();
        captured!.NewSessionId.Should().Be(result.Value.SessionId);
        captured.CanceledSessionId.Should().BeNull();
        captured.RuntimeId.Should().Be(runtimeId);
    }

    [Fact]
    public async Task Handle_OnlyShiftsRequestedRuntimeQueue()
    {
        // Cross-runtime isolation: a queued session on a sibling runtime
        // must not be touched. Otherwise an urgent prompt on runtime A would
        // renumber B's queue and break the dispatch ordering on B.
        var (conversationId, runtimeA, _) = await SeedRuntimeAndConversation();
        var runtimeB = await SeedRuntime(Guid.NewGuid());
        var conversationB = await SeedConversationOnly(Guid.NewGuid());

        await SeedSession(conversationId, runtimeA, AgentSessionStatus.Running);
        var aQueue1 = await SeedSession(conversationId, runtimeA, AgentSessionStatus.Pending, queuePosition: 1);
        var bQueue1 = await SeedSession(conversationB, runtimeB, AgentSessionStatus.Pending, queuePosition: 1);
        var bQueue2 = await SeedSession(conversationB, runtimeB, AgentSessionStatus.Pending, queuePosition: 2);

        var handler = BuildHandler();
        var result = await handler.Handle(
            new SubmitUrgentPromptCommand(conversationId, "urgent!", "user-1"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        // Runtime A's existing queued session was shifted +1.
        var aShifted = await Context.AgentSessions.SingleAsync(s => s.Id == aQueue1);
        aShifted.QueuePosition.Should().Be(2);

        // Runtime B's queue is untouched.
        var b1 = await Context.AgentSessions.SingleAsync(s => s.Id == bQueue1);
        var b2 = await Context.AgentSessions.SingleAsync(s => s.Id == bQueue2);
        b1.QueuePosition.Should().Be(1);
        b2.QueuePosition.Should().Be(2);
    }

    [Fact]
    public async Task Handle_SeedsPromptReceivedAuditEvent()
    {
        // The urgent path must write a PromptReceived event row at sequence 0
        // exactly like the shared dispatcher does — replay / chat panel rely
        // on that history shape regardless of which entry point produced it.
        var (conversationId, _, _) = await SeedRuntimeAndConversation();

        var handler = BuildHandler();
        var result = await handler.Handle(
            new SubmitUrgentPromptCommand(conversationId, "urgent prompt text", "user-1"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        var seedEvent = await Context.AgentEvents
            .SingleAsync(e => e.SessionId == result.Value.SessionId && e.Sequence == 0);
        seedEvent.Kind.Should().Be(AgentEventKind.PromptReceived);
        // Cursor-native schema: the prompt body now lives on the first-class
        // Text column. The "urgent: true" flag previously stuffed into the
        // EventData JSON is gone — urgency is implied by the dispatch path
        // (urgent command vs. normal Hub.SubmitPrompt), not encoded on the
        // audit row itself.
        seedEvent.Text.Should().Contain("urgent prompt text");
    }

    [Fact]
    public async Task Handle_HubFanoutThrows_StillReturnsSuccess()
    {
        // Daemon disconnected during a CancelTurn fan-out — the DB has
        // already been mutated (current Canceling, urgent at position 1).
        // The orphan-session janitor (Card 8) is the recovery path; the
        // handler must not bubble the exception up to the user.
        var (conversationId, runtimeId, _) = await SeedRuntimeAndConversation();
        await SeedSession(conversationId, runtimeId, AgentSessionStatus.Running);

        _groupClient
            .Setup(c => c.CancelTurn(It.IsAny<CancelTurnPayload>()))
            .ThrowsAsync(new InvalidOperationException("daemon disconnected"));

        var handler = BuildHandler();
        var result = await handler.Handle(
            new SubmitUrgentPromptCommand(conversationId, "urgent!", "user-1"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Queued.Should().BeTrue();
        result.Value.QueuePosition.Should().Be(1);
    }

    // ------------------------------------------------------------------
    // helpers

    private SubmitUrgentPromptCommandHandler BuildHandler() =>
        new(Context, _hub.Object, _mediator.Object,
            NullLogger<SubmitUrgentPromptCommandHandler>.Instance);

    private async Task<(Guid ConversationId, Guid RuntimeId, Guid ProjectId)> SeedRuntimeAndConversation()
    {
        var projectId = Guid.NewGuid();
        var runtimeId = await SeedRuntime(projectId);
        var conversationId = await SeedConversationOnly(projectId);
        return (conversationId, runtimeId, projectId);
    }

    private async Task<Guid> SeedRuntime(Guid projectId)
    {
        var runtime = new ProjectRuntime
        {
            ProjectId = projectId,
        };
        Context.ProjectRuntimes.Add(runtime);
        await Context.SaveChangesAsync();
        return runtime.Id;
    }

    private async Task<Guid> SeedConversationOnly(Guid projectId)
    {
        var conversation = new Conversation
        {
            ProjectId = projectId,
            Title = "test",
            BranchId = Guid.NewGuid(),
            Status = ConversationStatus.Active,
            LastActivityAt = DateTime.UtcNow,
        };
        Context.Conversations.Add(conversation);
        await Context.SaveChangesAsync();
        return conversation.Id;
    }

    private async Task<Guid> SeedSession(
        Guid conversationId,
        Guid runtimeId,
        AgentSessionStatus status,
        int? queuePosition = null)
    {
        var session = new AgentSession
        {
            ConversationId = conversationId,
            RuntimeId = runtimeId,
            Prompt = $"seeded-{status}-{queuePosition?.ToString() ?? "null"}",
            Status = status,
            QueuePosition = queuePosition,
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
}
