using Source.Shared.Events;

namespace Source.Features.SignalR.Events;

/// <summary>
/// Raised by <see cref="Hubs.RuntimeHub"/> when a daemon successfully connects
/// and is bound to a runtime row. Lifecycle decisions (move Suspended runtimes
/// to Online once their daemon shows up, refresh heartbeat watermark, etc.)
/// react to this event in subsequent cards.
/// </summary>
public record RuntimeConnected(
    Guid RuntimeId,
    Guid ProjectId,
    string ConnectionId,
    DateTime ConnectedAt
) : IDomainEvent
{
    DateTime IDomainEvent.OccurredAt => ConnectedAt;
}
