using Source.Features.Conversations.Models;
using Source.Shared.Events;

namespace Source.Features.Conversations.Events;

/// <summary>
/// Raised when an <see cref="AgentSession"/> is refused by the daemon because
/// another turn is still in flight on the same runtime — the daemon-side
/// single-turn invariant. The session has just been flipped to
/// <see cref="AgentSessionStatus.Failed"/> with
/// <see cref="AgentSession.CancelReason"/> = <c>"daemon_refused_concurrent"</c>
/// and <see cref="AgentSession.CompletedAt"/> stamped.
///
/// <para>Distinct from <see cref="AgentSessionTerminated"/>: refusal is the
/// pre-execution case (the daemon never picked the session up), so the
/// dispatch-next pipeline doesn't need to react to this — the UI does, via the
/// <c>RuntimeHub.TurnRefused</c> fan-out. We still raise a domain event so the
/// audit trail captures the rejection in <c>StoredDomainEvents</c>.</para>
///
/// <para>Implements <see cref="IEntityDomainEvent"/> so the
/// <c>DomainEventInterceptor</c> populates <c>EntityId</c> / <c>EntityType</c>
/// on <see cref="StoredDomainEvent"/> for indexed audit lookups.</para>
/// </summary>
public record SessionRefused(
    Guid SessionId,
    string Reason
) : IEntityDomainEvent
{
    public DateTime OccurredAt { get; init; } = DateTime.UtcNow;

    string IEntityDomainEvent.EntityId => SessionId.ToString();
    string IEntityDomainEvent.EntityType => nameof(AgentSession);
}
