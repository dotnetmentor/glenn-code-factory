using Source.Features.Conversations.Models;
using Source.Shared.Events;

namespace Source.Features.Conversations.Events;

/// <summary>
/// Raised when an <see cref="AgentSession"/> transitions from
/// <see cref="AgentSessionStatus.Running"/> to
/// <see cref="AgentSessionStatus.Canceling"/> via
/// <see cref="AgentSession.MarkCanceling"/>. Signals that the user requested
/// cancellation and the server is now waiting for the daemon to confirm with
/// a <c>turn_canceled</c> event before flipping the session to terminal
/// <see cref="AgentSessionStatus.Canceled"/>.
///
/// <para>This is an <b>intermediate</b> signal — the runtime is still
/// occupied draining the in-flight turn until the daemon's terminal event
/// lands. The dispatch-next path therefore listens for
/// <see cref="AgentSessionTerminated"/>, NOT this event; nothing else acts on
/// it today, but it gives the audit trail / event store a row at the moment
/// the user pressed stop, distinct from the eventual terminal Canceled row.</para>
///
/// <para>Implements <see cref="IEntityDomainEvent"/> so the
/// <c>DomainEventInterceptor</c> populates <c>EntityId</c> / <c>EntityType</c>
/// on <see cref="StoredDomainEvent"/> for indexed audit lookups.</para>
/// </summary>
public record SessionCancelRequested(
    Guid SessionId,
    Guid RuntimeId,
    string Reason
) : IEntityDomainEvent
{
    public DateTime OccurredAt { get; init; } = DateTime.UtcNow;

    string IEntityDomainEvent.EntityId => SessionId.ToString();
    string IEntityDomainEvent.EntityType => nameof(AgentSession);
}
