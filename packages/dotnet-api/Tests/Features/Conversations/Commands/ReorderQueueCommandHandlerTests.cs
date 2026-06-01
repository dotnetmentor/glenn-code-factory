using Api.Tests.Infrastructure;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Source.Features.Conversations.Commands;
using Source.Features.Conversations.Events;
using Source.Features.Conversations.Models;

namespace Api.Tests.Features.Conversations.Commands;

/// <summary>
/// Unit tests for <see cref="ReorderQueueCommandHandler"/> — the command behind
/// <c>PUT /api/runtimes/{runtimeId}/queue/reorder</c>.
///
/// <list type="bullet">
///   <item>Happy path: 3 queued sessions on a runtime, request reorders them
///         into a new permutation; <c>QueuePosition</c> renumbered 1..N.</item>
///   <item>Mismatch — request omits one of the queued ids → failure with the
///         "queue mismatch" sentinel and DB unchanged. The optimistic-
///         concurrency simulation: two clients dragging concurrently bounce
///         the second to a refresh.</item>
///   <item>Mismatch — request includes an extra/unknown id → same failure;
///         no partial mutation.</item>
///   <item>Empty queue + empty request → success with no event raised. A
///         non-empty request against an empty queue is a mismatch.</item>
///   <item>Cross-runtime isolation: queued sessions on a sibling runtime must
///         not be loaded into the comparison set, otherwise reordering one
///         runtime would falsely "miss" sessions on another.</item>
///   <item><see cref="QueueReordered"/> is published exactly once with the
///         requested order and the actor user id.</item>
/// </list>
/// </summary>
public class ReorderQueueCommandHandlerTests : HandlerTestBase
{
    private readonly Mock<IMediator> _mediator = new();

    [Fact]
    public async Task Handle_ReordersThreeSessions_RenumbersOneBased()
    {
        var runtimeId = Guid.NewGuid();
        var conversationId = await SeedConversation();
        var a = await SeedQueued(conversationId, runtimeId, position: 1);
        var b = await SeedQueued(conversationId, runtimeId, position: 2);
        var c = await SeedQueued(conversationId, runtimeId, position: 3);

        var handler = BuildHandler();
        // Reorder to [C, A, B] — drag C to the head.
        var newOrder = new List<Guid> { c, a, b };

        var result = await handler.Handle(
            new ReorderQueueCommand(runtimeId, newOrder, "user-1"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.NewOrder.Should().Equal(newOrder);

        // 1-based renumber matches the requested order.
        var byId = await Context.AgentSessions
            .Where(s => s.RuntimeId == runtimeId)
            .ToDictionaryAsync(s => s.Id, s => s.QueuePosition);
        byId[c].Should().Be(1);
        byId[a].Should().Be(2);
        byId[b].Should().Be(3);
    }

    [Fact]
    public async Task Handle_RequestMissingId_ReturnsMismatchFailure_DbUnchanged()
    {
        var runtimeId = Guid.NewGuid();
        var conversationId = await SeedConversation();
        var a = await SeedQueued(conversationId, runtimeId, position: 1);
        var b = await SeedQueued(conversationId, runtimeId, position: 2);
        var c = await SeedQueued(conversationId, runtimeId, position: 3);

        var handler = BuildHandler();
        // Omit `b` — only 2 of the 3 queued ids supplied.
        var newOrder = new List<Guid> { c, a };

        var result = await handler.Handle(
            new ReorderQueueCommand(runtimeId, newOrder, "user-1"),
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("queue mismatch");

        // Original positions intact — no partial renumber, no torn write.
        var positions = await Context.AgentSessions
            .Where(s => s.RuntimeId == runtimeId)
            .ToDictionaryAsync(s => s.Id, s => s.QueuePosition);
        positions[a].Should().Be(1);
        positions[b].Should().Be(2);
        positions[c].Should().Be(3);

        // No event published on a rejected reorder.
        _mediator.Verify(
            m => m.Publish(It.IsAny<QueueReordered>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Handle_RequestExtraUnknownId_ReturnsMismatchFailure_DbUnchanged()
    {
        var runtimeId = Guid.NewGuid();
        var conversationId = await SeedConversation();
        var a = await SeedQueued(conversationId, runtimeId, position: 1);
        var b = await SeedQueued(conversationId, runtimeId, position: 2);

        var handler = BuildHandler();
        // Include a completely unknown id — set membership mismatch.
        var ghost = Guid.NewGuid();
        var newOrder = new List<Guid> { b, a, ghost };

        var result = await handler.Handle(
            new ReorderQueueCommand(runtimeId, newOrder, "user-1"),
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("queue mismatch");

        var positions = await Context.AgentSessions
            .Where(s => s.RuntimeId == runtimeId)
            .ToDictionaryAsync(s => s.Id, s => s.QueuePosition);
        positions[a].Should().Be(1);
        positions[b].Should().Be(2);

        _mediator.Verify(
            m => m.Publish(It.IsAny<QueueReordered>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Handle_DuplicateIdsInRequest_ReturnsMismatchFailure()
    {
        // The hash-set comparison would otherwise collapse duplicates and
        // pass — explicit count check guards against [a, a] reordering only
        // one of two queued sessions.
        var runtimeId = Guid.NewGuid();
        var conversationId = await SeedConversation();
        var a = await SeedQueued(conversationId, runtimeId, position: 1);
        var b = await SeedQueued(conversationId, runtimeId, position: 2);

        var handler = BuildHandler();
        var newOrder = new List<Guid> { a, a };

        var result = await handler.Handle(
            new ReorderQueueCommand(runtimeId, newOrder, "user-1"),
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("queue mismatch");

        // DB intact.
        var positions = await Context.AgentSessions
            .Where(s => s.RuntimeId == runtimeId)
            .ToDictionaryAsync(s => s.Id, s => s.QueuePosition);
        positions[a].Should().Be(1);
        positions[b].Should().Be(2);
    }

    [Fact]
    public async Task Handle_EmptyQueue_EmptyRequest_ReturnsSuccess_NoEvent()
    {
        var runtimeId = Guid.NewGuid();

        var handler = BuildHandler();
        var result = await handler.Handle(
            new ReorderQueueCommand(runtimeId, new List<Guid>(), "user-1"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.NewOrder.Should().BeEmpty();

        // Empty reorder is a no-op — no event row in the audit log.
        _mediator.Verify(
            m => m.Publish(It.IsAny<QueueReordered>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Handle_EmptyQueue_NonEmptyRequest_ReturnsMismatch()
    {
        var runtimeId = Guid.NewGuid();
        var conversationId = await SeedConversation();
        // Seed a session that is NOT queued (Running) — must not be counted
        // by the handler when the queue is "empty".
        await SeedSession(conversationId, runtimeId, AgentSessionStatus.Running, queuePosition: null);

        var handler = BuildHandler();
        var result = await handler.Handle(
            new ReorderQueueCommand(runtimeId, new List<Guid> { Guid.NewGuid() }, "user-1"),
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("queue mismatch");
    }

    [Fact]
    public async Task Handle_OnlyAffectsRequestedRuntime()
    {
        // A queued session on a sibling runtime must not be loaded into the
        // comparison set — otherwise reordering runtime A would falsely
        // include B's queued sessions and either explode the cardinality
        // check or, worse, renumber across runtimes.
        var runtimeA = Guid.NewGuid();
        var runtimeB = Guid.NewGuid();
        var conversationId = await SeedConversation();

        var a1 = await SeedQueued(conversationId, runtimeA, position: 1);
        var a2 = await SeedQueued(conversationId, runtimeA, position: 2);
        var b1 = await SeedQueued(conversationId, runtimeB, position: 1);

        var handler = BuildHandler();
        var result = await handler.Handle(
            new ReorderQueueCommand(runtimeA, new List<Guid> { a2, a1 }, "user-1"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        var byId = await Context.AgentSessions.ToDictionaryAsync(s => s.Id, s => s.QueuePosition);
        byId[a2].Should().Be(1);
        byId[a1].Should().Be(2);
        byId[b1].Should().Be(1, "sibling runtime's queue must be untouched.");
    }

    [Fact]
    public async Task Handle_IgnoresNonPendingAndNullQueuePosition()
    {
        // Running / Succeeded / Canceled sessions on the runtime must not be
        // counted as queued — same for any defensive null QueuePosition row.
        var runtimeId = Guid.NewGuid();
        var conversationId = await SeedConversation();

        var queued1 = await SeedQueued(conversationId, runtimeId, position: 1);
        var queued2 = await SeedQueued(conversationId, runtimeId, position: 2);

        // Noise that must not be picked up.
        await SeedSession(conversationId, runtimeId, AgentSessionStatus.Running, queuePosition: null);
        await SeedSession(conversationId, runtimeId, AgentSessionStatus.Succeeded, queuePosition: null);
        await SeedSession(conversationId, runtimeId, AgentSessionStatus.Canceled, queuePosition: null);
        // Pending but with null QueuePosition — transient state during dispatch;
        // not part of "the queue" for reordering purposes.
        await SeedSession(conversationId, runtimeId, AgentSessionStatus.Pending, queuePosition: null);

        var handler = BuildHandler();
        var result = await handler.Handle(
            new ReorderQueueCommand(runtimeId, new List<Guid> { queued2, queued1 }, "user-1"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        var positions = await Context.AgentSessions
            .Where(s => s.Id == queued1 || s.Id == queued2)
            .ToDictionaryAsync(s => s.Id, s => s.QueuePosition);
        positions[queued2].Should().Be(1);
        positions[queued1].Should().Be(2);
    }

    [Fact]
    public async Task Handle_PublishesQueueReorderedOnce_WithCorrectPayload()
    {
        var runtimeId = Guid.NewGuid();
        var conversationId = await SeedConversation();
        var a = await SeedQueued(conversationId, runtimeId, position: 1);
        var b = await SeedQueued(conversationId, runtimeId, position: 2);

        QueueReordered? captured = null;
        _mediator
            .Setup(m => m.Publish(It.IsAny<QueueReordered>(), It.IsAny<CancellationToken>()))
            .Callback<object, CancellationToken>((evt, _) => captured = (QueueReordered)evt)
            .Returns(Task.CompletedTask);

        var handler = BuildHandler();
        var newOrder = new List<Guid> { b, a };
        await handler.Handle(
            new ReorderQueueCommand(runtimeId, newOrder, "user-42"),
            CancellationToken.None);

        _mediator.Verify(
            m => m.Publish(It.IsAny<QueueReordered>(), It.IsAny<CancellationToken>()),
            Times.Once);

        captured.Should().NotBeNull();
        captured!.RuntimeId.Should().Be(runtimeId);
        captured.NewOrder.Should().Equal(newOrder);
        captured.ActorUserId.Should().Be("user-42");
    }

    // ------------------------------------------------------------------
    // helpers

    private ReorderQueueCommandHandler BuildHandler() =>
        new(Context, _mediator.Object, NullLogger<ReorderQueueCommandHandler>.Instance);

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

    private async Task<Guid> SeedQueued(Guid conversationId, Guid runtimeId, int position) =>
        await SeedSession(conversationId, runtimeId, AgentSessionStatus.Pending, queuePosition: position);

    private async Task<Guid> SeedSession(
        Guid conversationId,
        Guid runtimeId,
        AgentSessionStatus status,
        int? queuePosition)
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
