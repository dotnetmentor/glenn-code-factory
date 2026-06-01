using Source.Features.Conversations.Models;
using Source.Shared.Events;

namespace Source.Features.Conversations.Events;

/// <summary>
/// Raised when a <see cref="Conversation"/>'s <see cref="Conversation.Title"/>
/// changes — whether via an explicit user rename (<see cref="Conversation.Rename"/>)
/// or via the one-shot auto-retitle heuristic that fires off the first
/// <see cref="AgentEventType.AssistantText"/> chunk (<see cref="Conversation.AutoRetitle"/>).
///
/// <para>Both paths drop <see cref="Conversation.IsAutoTitled"/> to <c>false</c>
/// so subsequent auto-retitle attempts are short-circuited; this event lets the
/// SignalR fan-out push the new title to other connected tabs / clients live,
/// so an automatic retitle (or a rename from another browser session) shows up
/// without a manual refresh or page reload.</para>
///
/// <para>Implements <see cref="IEntityDomainEvent"/> so the
/// <c>DomainEventInterceptor</c> populates <c>EntityId</c> / <c>EntityType</c>
/// on <see cref="StoredDomainEvent"/> for indexed audit lookups (the entity is
/// the <see cref="Conversation"/> whose title changed).</para>
/// </summary>
public record ConversationRenamed(
    Guid ConversationId,
    Guid ProjectId,
    Guid BranchId,
    string Title,
    bool IsAutoTitled,
    DateTime OccurredAt
) : IEntityDomainEvent
{
    /// <summary>Convenience ctor that stamps OccurredAt with the current UTC clock.</summary>
    public ConversationRenamed(
        Guid conversationId,
        Guid projectId,
        Guid branchId,
        string title,
        bool isAutoTitled)
        : this(conversationId, projectId, branchId, title, isAutoTitled, DateTime.UtcNow) { }

    string IEntityDomainEvent.EntityId => ConversationId.ToString();
    string IEntityDomainEvent.EntityType => nameof(Conversation);
}
