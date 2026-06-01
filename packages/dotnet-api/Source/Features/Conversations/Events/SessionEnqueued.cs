using Source.Features.Conversations.Models;
using Source.Shared.Events;

namespace Source.Features.Conversations.Events;

/// <summary>
/// Raised when a freshly-created <see cref="AgentSession"/> is queued behind an
/// already-running session on the same runtime instead of being dispatched
/// immediately. The session stays in <see cref="AgentSessionStatus.Pending"/>
/// with a non-null <see cref="QueuePosition"/>; the dispatcher will pick it up
/// when the running session reaches a terminal state (Card 3).
///
/// <para>Implements <see cref="IEntityDomainEvent"/> so the
/// <c>DomainEventInterceptor</c> populates <c>EntityId</c> / <c>EntityType</c>
/// on <see cref="StoredDomainEvent"/> for indexed audit lookups.</para>
/// </summary>
public record SessionEnqueued(
    Guid SessionId,
    Guid RuntimeId,
    int QueuePosition
) : IEntityDomainEvent
{
    public DateTime OccurredAt { get; init; } = DateTime.UtcNow;

    string IEntityDomainEvent.EntityId => SessionId.ToString();
    string IEntityDomainEvent.EntityType => nameof(AgentSession);
}
