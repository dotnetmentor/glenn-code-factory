using Source.Features.Conversations.Models;
using Source.Shared.Events;

namespace Source.Features.Conversations.Events;

/// <summary>
/// Raised when an <see cref="AgentSession"/> is actively dispatched to its
/// runtime — either at create time (no other session was running) or when the
/// dispatcher picks the head of the queue after a previous session terminates
/// (Card 3). The session's <see cref="AgentSession.Status"/> is now
/// <see cref="AgentSessionStatus.Running"/> and its <see cref="QueuePosition"/>
/// is cleared.
///
/// <para>Implements <see cref="IEntityDomainEvent"/> so the
/// <c>DomainEventInterceptor</c> populates <c>EntityId</c> / <c>EntityType</c>
/// on <see cref="StoredDomainEvent"/> for indexed audit lookups.</para>
/// </summary>
public record SessionDispatched(
    Guid SessionId,
    Guid RuntimeId
) : IEntityDomainEvent
{
    public DateTime OccurredAt { get; init; } = DateTime.UtcNow;

    string IEntityDomainEvent.EntityId => SessionId.ToString();
    string IEntityDomainEvent.EntityType => nameof(AgentSession);
}
