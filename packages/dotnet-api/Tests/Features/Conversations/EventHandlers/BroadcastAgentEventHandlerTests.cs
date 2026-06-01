using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Source.Features.Conversations.EventHandlers;
using Source.Features.Conversations.Events;
using Source.Features.Conversations.Models;
using Source.Features.SignalR.Contracts;
using Source.Features.SignalR.Hubs;
using Source.Infrastructure;

namespace Api.Tests.Features.Conversations.EventHandlers;

/// <summary>
/// Unit tests for <see cref="BroadcastAgentEventHandler"/>. Mocks the typed
/// <see cref="IHubContext{THub, T}"/> end-to-end so we can verify the things
/// the spec cares about:
///
/// <list type="bullet">
///   <item>broadcast is scoped to the correct <c>branch-{id}</c> group — narrowed
///         from the legacy <c>project-{id}</c> group so live AgentEvent ticks
///         don't leak between sibling-branch tabs after CopyBranch;</item>
///   <item>the wire payload carries the full polymorphic
///         <see cref="AgentEventDto"/> snapshot inline so the React client can
///         render without a REST refetch;</item>
///   <item>hub failures are swallowed — broadcast must not poison the domain
///         event dispatcher chain (the AgentEvent row is already committed by
///         <c>EmitEvent</c> and must not roll back).</item>
///
/// <para>Bootstrap mirrors <c>BroadcastRuntimeStateChangedHandlerTests</c>
/// exactly. Same DI shape, same Moq pattern, same assertions style.</para>
/// </summary>
public class BroadcastAgentEventHandlerTests
{
    private readonly Mock<IHubClients<IAgentClient>> _clients = new();
    private readonly Mock<IHubContext<AgentHub, IAgentClient>> _hub = new();
    private readonly Mock<IAgentClient> _groupClient = new();

    public BroadcastAgentEventHandlerTests()
    {
        _hub.SetupGet(h => h.Clients).Returns(_clients.Object);
        _clients.Setup(c => c.Group(It.IsAny<string>())).Returns(_groupClient.Object);
    }

    /// <summary>
    /// Bare in-memory DbContext for the handler's workspace-resolution lookup.
    /// We don't seed any Project row, so the additive
    /// <c>workspace-{workspaceId}</c> broadcast short-circuits on a null
    /// workspace and the existing project-group assertions stay untouched.
    /// </summary>
    private static ApplicationDbContext NewDb() => new(
        new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    /// <summary>
    /// Builds a ToolUse DTO with sensible defaults — enough to drive the
    /// polymorphic broadcast through the handler without forcing every caller
    /// to fill the long field list.
    /// </summary>
    private static ToolUseEventDto NewToolUseDto(Guid sessionId, long sequence, DateTime createdAt) =>
        new(
            SessionId: sessionId,
            Sequence: sequence,
            CreatedAt: createdAt,
            CallId: $"call-{sequence}",
            Name: "bash",
            Status: AgentEventToolStatus.Running,
            Args: null,
            Result: null,
            ArgsTruncated: false,
            ResultTruncated: false);

    /// <summary>
    /// Builds an AssistantText DTO with sensible defaults.
    /// </summary>
    private static AssistantTextEventDto NewAssistantTextDto(Guid sessionId, long sequence) =>
        new(
            SessionId: sessionId,
            Sequence: sequence,
            CreatedAt: DateTime.UtcNow,
            Text: "hello");

    /// <summary>
    /// Builds a Status DTO with sensible defaults.
    /// </summary>
    private static StatusEventDto NewStatusDto(Guid sessionId, long sequence) =>
        new(
            SessionId: sessionId,
            Sequence: sequence,
            CreatedAt: DateTime.UtcNow,
            Status: AgentEventRunStatus.Running,
            Message: null);

    [Fact]
    public async Task Handle_broadcasts_to_branch_group_with_correct_payload()
    {
        var sessionId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var occurredAt = new DateTime(2026, 5, 8, 10, 0, 0, DateTimeKind.Utc);
        var dto = NewToolUseDto(sessionId, sequence: 7, createdAt: occurredAt);
        var domainEvent = new AgentEventEmitted(
            ConversationId: conversationId,
            ProjectId: projectId,
            BranchId: branchId,
            Kind: AgentEventKind.ToolUse,
            Event: dto,
            OccurredAt: occurredAt);

        AgentEventNotification? captured = null;
        _groupClient
            .Setup(c => c.AgentEvent(It.IsAny<AgentEventNotification>()))
            .Callback<AgentEventNotification>(p => captured = p)
            .Returns(Task.CompletedTask);

        using var db = NewDb();
        var handler = new BroadcastAgentEventHandler(_hub.Object, db, NullLogger<BroadcastAgentEventHandler>.Instance);

        await handler.Handle(domainEvent, CancellationToken.None);

        // Group selection — exact, branch-scoped (narrowed from project-scoped
        // to prevent cross-branch live-event leak after CopyBranch). The
        // additive workspace-group broadcast is short-circuited because no
        // Project row is seeded (workspace lookup returns null).
        _clients.Verify(c => c.Group($"branch-{branchId}"), Times.Once);
        _clients.Verify(c => c.Group(It.Is<string>(s => s != $"branch-{branchId}")), Times.Never);

        // Single broadcast with the mapped payload.
        _groupClient.Verify(c => c.AgentEvent(It.IsAny<AgentEventNotification>()), Times.Once);
        captured.Should().NotBeNull();
        captured!.ConversationId.Should().Be(conversationId);
        captured.ProjectId.Should().Be(projectId);
        captured.BranchId.Should().Be(branchId);
        // The full typed DTO is embedded — the chat panel can render without a
        // REST refetch.
        captured.Event.Should().BeOfType<ToolUseEventDto>();
        captured.Event.SessionId.Should().Be(sessionId);
        captured.Event.Sequence.Should().Be(7);
        captured.Event.CreatedAt.Should().Be(occurredAt);
    }

    [Fact]
    public async Task Handle_embeds_concrete_subtype_for_polymorphic_dispatch()
    {
        // Wire-stability guarantee: the React client switches on
        // event.eventKind ("assistantText") to narrow the union. The handler
        // must hand the typed DTO through verbatim so the JsonPolymorphic
        // serializer writes the discriminator on the wire.
        var dto = NewAssistantTextDto(Guid.NewGuid(), sequence: 0);
        var domainEvent = new AgentEventEmitted(
            conversationId: Guid.NewGuid(),
            projectId: Guid.NewGuid(),
            branchId: Guid.NewGuid(),
            kind: AgentEventKind.AssistantText,
            @event: dto);

        AgentEventNotification? captured = null;
        _groupClient
            .Setup(c => c.AgentEvent(It.IsAny<AgentEventNotification>()))
            .Callback<AgentEventNotification>(p => captured = p)
            .Returns(Task.CompletedTask);

        using var db = NewDb();
        var handler = new BroadcastAgentEventHandler(_hub.Object, db, NullLogger<BroadcastAgentEventHandler>.Instance);

        await handler.Handle(domainEvent, CancellationToken.None);

        captured.Should().NotBeNull();
        captured!.Event.Should().BeOfType<AssistantTextEventDto>();
        ((AssistantTextEventDto)captured.Event).Text.Should().Be("hello");
    }

    [Fact]
    public async Task Handle_swallows_hub_exception()
    {
        // Critical reliability invariant: SignalR broadcast failures must not
        // propagate. The AgentEvent row was already persisted by EmitEvent,
        // and an exception here would surface back into the domain dispatcher
        // and confuse callers about whether the event itself failed.
        _groupClient
            .Setup(c => c.AgentEvent(It.IsAny<AgentEventNotification>()))
            .ThrowsAsync(new InvalidOperationException("hub is gone"));

        var logger = new Mock<ILogger<BroadcastAgentEventHandler>>();
        using var db = NewDb();
        var handler = new BroadcastAgentEventHandler(_hub.Object, db, logger.Object);

        var dto = NewStatusDto(Guid.NewGuid(), sequence: 3);
        var domainEvent = new AgentEventEmitted(
            conversationId: Guid.NewGuid(),
            projectId: Guid.NewGuid(),
            branchId: Guid.NewGuid(),
            kind: AgentEventKind.Status,
            @event: dto);

        // Act — must not throw.
        var act = async () => await handler.Handle(domainEvent, CancellationToken.None);
        await act.Should().NotThrowAsync();

        // We logged the failure at Warning so an operator can see broadcast loss.
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
    public async Task Handle_each_call_uses_correct_branch_group()
    {
        // Two consecutive Handle invocations with different BranchIds must
        // route to different groups — there must be no shared state between
        // calls that mis-targets the broadcast. Critical for the CopyBranch
        // case where one project owns N branches: each branch's live tick
        // stream must reach only that branch's tabs.
        var branchIdA = Guid.NewGuid();
        var branchIdB = Guid.NewGuid();

        var eventA = new AgentEventEmitted(
            conversationId: Guid.NewGuid(),
            projectId: Guid.NewGuid(),
            branchId: branchIdA,
            kind: AgentEventKind.AssistantText,
            @event: NewAssistantTextDto(Guid.NewGuid(), sequence: 0));
        var eventB = new AgentEventEmitted(
            conversationId: Guid.NewGuid(),
            projectId: Guid.NewGuid(),
            branchId: branchIdB,
            kind: AgentEventKind.AssistantText,
            @event: NewAssistantTextDto(Guid.NewGuid(), sequence: 0));

        _groupClient
            .Setup(c => c.AgentEvent(It.IsAny<AgentEventNotification>()))
            .Returns(Task.CompletedTask);

        using var db = NewDb();
        var handler = new BroadcastAgentEventHandler(_hub.Object, db, NullLogger<BroadcastAgentEventHandler>.Instance);

        await handler.Handle(eventA, CancellationToken.None);
        await handler.Handle(eventB, CancellationToken.None);

        _clients.Verify(c => c.Group($"branch-{branchIdA}"), Times.Once);
        _clients.Verify(c => c.Group($"branch-{branchIdB}"), Times.Once);
        _clients.Verify(c => c.Group(It.Is<string>(s => s != $"branch-{branchIdA}" && s != $"branch-{branchIdB}")), Times.Never);
        _groupClient.Verify(c => c.AgentEvent(It.IsAny<AgentEventNotification>()), Times.Exactly(2));
    }
}
