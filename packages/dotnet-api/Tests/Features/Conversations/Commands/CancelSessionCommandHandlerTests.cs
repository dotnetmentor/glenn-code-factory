using Api.Tests.Infrastructure;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Source.Features.Conversations.Commands;
using Source.Features.Conversations.Events;
using Source.Features.Conversations.Models;
using Source.Features.SignalR.Contracts;
using Source.Features.SignalR.Hubs;

namespace Api.Tests.Features.Conversations.Commands;

/// <summary>
/// Unit tests for <see cref="CancelSessionCommandHandler"/> — the command
/// behind <c>POST /api/sessions/{id}/cancel</c>.
///
/// <list type="bullet">
///   <item>Pending → <see cref="AgentSessionStatus.Canceled"/> in one step,
///         no SignalR fan-out (no daemon to notify).</item>
///   <item>Running → <see cref="AgentSessionStatus.Canceling"/>; SignalR
///         <see cref="IRuntimeClient.CancelTurn"/> pushed to
///         <c>runtime-{id}</c> with the supplied reason.</item>
///   <item>Already-Canceled / -Canceling / -Succeeded / -Failed → idempotent
///         success, no SignalR fan-out, no DB mutation.</item>
///   <item>Missing session id → <c>Result.Failure</c>.</item>
///   <item>SignalR fan-out throws → handler still returns success (the
///         orphan-session janitor in Card 8 is the recovery path; a hub
///         exception must not surface back to the user).</item>
/// </list>
///
/// Mirrors <see cref="DispatchNextSessionHandlerTests"/> for hub mocking and
/// <see cref="HandlerTestBase"/> for the in-memory DB.
/// </summary>
public class CancelSessionCommandHandlerTests : HandlerTestBase
{
    // SignalR mock chain: hub.Clients.Group("runtime-{id}").CancelTurn(payload)
    private readonly Mock<IHubClients<IRuntimeClient>> _clients = new();
    private readonly Mock<IHubContext<RuntimeHub, IRuntimeClient>> _hub = new();
    private readonly Mock<IRuntimeClient> _groupClient = new();

    public CancelSessionCommandHandlerTests()
    {
        _hub.SetupGet(h => h.Clients).Returns(_clients.Object);
        _clients.Setup(c => c.Group(It.IsAny<string>())).Returns(_groupClient.Object);
        _groupClient
            .Setup(c => c.CancelTurn(It.IsAny<CancelTurnPayload>()))
            .Returns(Task.CompletedTask);
    }

    [Fact]
    public async Task Handle_PendingSession_TransitionsToCanceled_NoSignalR()
    {
        var runtimeId = Guid.NewGuid();
        var conversationId = await SeedConversation();
        var sessionId = await SeedSession(
            conversationId, runtimeId, AgentSessionStatus.Pending, queuePosition: 1);

        var handler = BuildHandler();
        var result = await handler.Handle(
            new CancelSessionCommand(sessionId, "user_requested"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.SessionId.Should().Be(sessionId);
        result.Value.FinalStatus.Should().Be(AgentSessionStatus.Canceled);

        // DB state matches: Canceled, reason stamped, queue cleared, completed
        // timestamp recorded — i.e. fully terminal.
        var session = await Context.AgentSessions.SingleAsync(s => s.Id == sessionId);
        session.Status.Should().Be(AgentSessionStatus.Canceled);
        session.CancelReason.Should().Be("user_requested");
        session.QueuePosition.Should().BeNull();
        session.CompletedAt.Should().NotBeNull();

        // No daemon to notify when the session never dispatched.
        _groupClient.Verify(c => c.CancelTurn(It.IsAny<CancelTurnPayload>()), Times.Never);
        _clients.Verify(c => c.Group(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Handle_RunningSession_TransitionsToCanceling_SignalRPushed()
    {
        var runtimeId = Guid.NewGuid();
        var conversationId = await SeedConversation();
        var sessionId = await SeedSession(conversationId, runtimeId, AgentSessionStatus.Running);

        CancelTurnPayload? captured = null;
        _groupClient
            .Setup(c => c.CancelTurn(It.IsAny<CancelTurnPayload>()))
            .Callback<CancelTurnPayload>(p => captured = p)
            .Returns(Task.CompletedTask);

        var handler = BuildHandler();
        var result = await handler.Handle(
            new CancelSessionCommand(sessionId, "user_requested"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.FinalStatus.Should().Be(
            AgentSessionStatus.Canceling,
            "Running -> Canceling is intermediate; the daemon confirms the terminal Canceled later.");

        // DB state: intermediate Canceling. CompletedAt MUST stay null —
        // the runtime is still draining the in-flight turn.
        var session = await Context.AgentSessions.SingleAsync(s => s.Id == sessionId);
        session.Status.Should().Be(AgentSessionStatus.Canceling);
        session.CancelReason.Should().Be("user_requested");
        session.CompletedAt.Should().BeNull("Canceling is intermediate; daemon confirmation flips to terminal Canceled.");

        // SignalR pushed exactly once to the right group with the right payload.
        _clients.Verify(c => c.Group($"runtime-{runtimeId}"), Times.Once);
        _groupClient.Verify(c => c.CancelTurn(It.IsAny<CancelTurnPayload>()), Times.Once);
        captured.Should().NotBeNull();
        captured!.SessionId.Should().Be(sessionId);
        captured.Reason.Should().Be("user_requested");
    }

    [Fact]
    public async Task Handle_RunningSession_RaisesSessionCancelRequestedNotTerminated()
    {
        // The intermediate Canceling transition must NOT raise
        // AgentSessionTerminated — the runtime is still busy. The dispatch-
        // next handler keys off Terminated only, so raising it here would
        // cause a premature dispatch of the next queued session.
        var runtimeId = Guid.NewGuid();
        var conversationId = await SeedConversation();
        var sessionId = await SeedSession(conversationId, runtimeId, AgentSessionStatus.Running);

        var handler = BuildHandler();
        await handler.Handle(
            new CancelSessionCommand(sessionId, "user_requested"),
            CancellationToken.None);

        // Re-load with a fresh tracker to inspect the raised events on the
        // tracked entity; the in-memory provider keeps DomainEvents on the
        // entity instance until ClearDomainEvents is called.
        var tracked = Context.AgentSessions.Local.Single(s => s.Id == sessionId);
        tracked.DomainEvents.OfType<SessionCancelRequested>().Should().HaveCount(1);
        tracked.DomainEvents.OfType<AgentSessionTerminated>().Should().BeEmpty(
            "Canceling is intermediate; the terminal event fires later when the daemon confirms.");
    }

    [Fact]
    public async Task Handle_AlreadyCanceled_IsIdempotent()
    {
        var runtimeId = Guid.NewGuid();
        var conversationId = await SeedConversation();
        var sessionId = await SeedSession(conversationId, runtimeId, AgentSessionStatus.Canceled);

        var handler = BuildHandler();
        var result = await handler.Handle(
            new CancelSessionCommand(sessionId, "user_requested"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.FinalStatus.Should().Be(AgentSessionStatus.Canceled);
        _groupClient.Verify(c => c.CancelTurn(It.IsAny<CancelTurnPayload>()), Times.Never);
    }

    [Fact]
    public async Task Handle_AlreadyCanceling_IsIdempotent()
    {
        // The user clicks cancel twice in quick succession. First call flipped
        // Running -> Canceling; second call must be a no-op so we don't
        // double-fan-out CancelTurn to the daemon.
        var runtimeId = Guid.NewGuid();
        var conversationId = await SeedConversation();
        var sessionId = await SeedSession(conversationId, runtimeId, AgentSessionStatus.Canceling);

        var handler = BuildHandler();
        var result = await handler.Handle(
            new CancelSessionCommand(sessionId, "user_requested"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.FinalStatus.Should().Be(AgentSessionStatus.Canceling);
        _groupClient.Verify(c => c.CancelTurn(It.IsAny<CancelTurnPayload>()), Times.Never);
    }

    [Fact]
    public async Task Handle_AlreadySucceeded_IsIdempotent()
    {
        var runtimeId = Guid.NewGuid();
        var conversationId = await SeedConversation();
        var sessionId = await SeedSession(conversationId, runtimeId, AgentSessionStatus.Succeeded);

        var handler = BuildHandler();
        var result = await handler.Handle(
            new CancelSessionCommand(sessionId, "user_requested"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.FinalStatus.Should().Be(AgentSessionStatus.Succeeded);
        _groupClient.Verify(c => c.CancelTurn(It.IsAny<CancelTurnPayload>()), Times.Never);
    }

    [Fact]
    public async Task Handle_AlreadyFailed_IsIdempotent()
    {
        var runtimeId = Guid.NewGuid();
        var conversationId = await SeedConversation();
        var sessionId = await SeedSession(conversationId, runtimeId, AgentSessionStatus.Failed);

        var handler = BuildHandler();
        var result = await handler.Handle(
            new CancelSessionCommand(sessionId, "user_requested"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.FinalStatus.Should().Be(AgentSessionStatus.Failed);
        _groupClient.Verify(c => c.CancelTurn(It.IsAny<CancelTurnPayload>()), Times.Never);
    }

    [Fact]
    public async Task Handle_MissingSession_ReturnsFailure()
    {
        var handler = BuildHandler();
        var result = await handler.Handle(
            new CancelSessionCommand(Guid.NewGuid(), "user_requested"),
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("Session not found");
        _groupClient.Verify(c => c.CancelTurn(It.IsAny<CancelTurnPayload>()), Times.Never);
    }

    [Fact]
    public async Task Handle_HubFanoutThrows_StillReturnsSuccess()
    {
        // The session has already been flipped to Canceling in the DB by the
        // time the SignalR push happens. A daemon-disconnected hub exception
        // must NOT surface back to the user — the orphan janitor (Card 8)
        // will reap a stuck Canceling session if the daemon never confirms.
        var runtimeId = Guid.NewGuid();
        var conversationId = await SeedConversation();
        var sessionId = await SeedSession(conversationId, runtimeId, AgentSessionStatus.Running);

        _groupClient
            .Setup(c => c.CancelTurn(It.IsAny<CancelTurnPayload>()))
            .ThrowsAsync(new InvalidOperationException("daemon disconnected"));

        var logger = new Mock<ILogger<CancelSessionCommandHandler>>();
        var handler = new CancelSessionCommandHandler(Context, _hub.Object, logger.Object);

        var result = await handler.Handle(
            new CancelSessionCommand(sessionId, "user_requested"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.FinalStatus.Should().Be(AgentSessionStatus.Canceling);

        // Session is still Canceling in the DB — we don't roll back on hub
        // failure because the user-facing intent (cancel requested) is
        // recorded; the janitor handles the orphan.
        var session = await Context.AgentSessions.SingleAsync(s => s.Id == sessionId);
        session.Status.Should().Be(AgentSessionStatus.Canceling);

        // Warning logged so an operator can correlate "user clicked cancel,
        // daemon was offline" with the eventual janitor reap.
        logger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<InvalidOperationException>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task Handle_DefaultReason_IsRespected()
    {
        // The controller defaults a missing/empty reason to "user", but the
        // handler itself takes whatever string the caller passes. Verify the
        // reason flows through unchanged to the SignalR payload — that's the
        // contract the daemon and audit log rely on.
        var runtimeId = Guid.NewGuid();
        var conversationId = await SeedConversation();
        var sessionId = await SeedSession(conversationId, runtimeId, AgentSessionStatus.Running);

        CancelTurnPayload? captured = null;
        _groupClient
            .Setup(c => c.CancelTurn(It.IsAny<CancelTurnPayload>()))
            .Callback<CancelTurnPayload>(p => captured = p)
            .Returns(Task.CompletedTask);

        var handler = BuildHandler();
        await handler.Handle(
            new CancelSessionCommand(sessionId, "preempted_by_urgent"),
            CancellationToken.None);

        captured.Should().NotBeNull();
        captured!.Reason.Should().Be("preempted_by_urgent");

        var session = await Context.AgentSessions.SingleAsync(s => s.Id == sessionId);
        session.CancelReason.Should().Be("preempted_by_urgent");
    }

    // ------------------------------------------------------------------
    // helpers

    private CancelSessionCommandHandler BuildHandler() =>
        new(Context, _hub.Object, NullLogger<CancelSessionCommandHandler>.Instance);

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
            Prompt = "seeded-" + status,
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
