using Source.Features.Conversations.Models;
using Source.Shared.Events;

namespace Source.Features.Conversations.Events;

/// <summary>
/// Raised when an <see cref="AgentSession"/> transitions into any terminal
/// state — <see cref="AgentSessionStatus.Succeeded"/>,
/// <see cref="AgentSessionStatus.Failed"/>, or
/// <see cref="AgentSessionStatus.Canceled"/>. The session's
/// <see cref="AgentSession.CompletedAt"/> is now stamped and its
/// <see cref="AgentSession.QueuePosition"/> is cleared.
///
/// <para>This is the <b>single unified terminal signal</b> the dispatch-next
/// path (Card 3) listens for; intermediate transitions like Running → Canceling
/// do NOT raise this event because the runtime is still occupied draining the
/// in-flight turn. Only when the daemon finally emits <c>turn_canceled</c> /
/// <c>turn_completed</c> / <c>turn_failed</c> and the session lands in a
/// terminal state does the runtime free up and the next queued session become
/// eligible for dispatch.</para>
///
/// <para>Implements <see cref="IEntityDomainEvent"/> so the
/// <c>DomainEventInterceptor</c> populates <c>EntityId</c> / <c>EntityType</c>
/// on <see cref="StoredDomainEvent"/> for indexed audit lookups.</para>
/// </summary>
public record AgentSessionTerminated(
    Guid SessionId,
    Guid RuntimeId,
    AgentSessionStatus FinalStatus,
    string? Reason
) : IEntityDomainEvent
{
    public DateTime OccurredAt { get; init; } = DateTime.UtcNow;

    string IEntityDomainEvent.EntityId => SessionId.ToString();
    string IEntityDomainEvent.EntityType => nameof(AgentSession);
}
