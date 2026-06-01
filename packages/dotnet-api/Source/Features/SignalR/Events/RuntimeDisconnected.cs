using Source.Shared.Events;

namespace Source.Features.SignalR.Events;

/// <summary>
/// Raised by <see cref="Hubs.RuntimeHub"/> when a daemon's SignalR connection
/// goes away — either gracefully or via a transport-level abort. The
/// <see cref="ExceptionMessage"/> carries the exception summary when the drop
/// was abnormal (so the supervisor can distinguish "daemon shut down cleanly"
/// from "daemon crashed mid-turn").
/// </summary>
public record RuntimeDisconnected(
    Guid RuntimeId,
    Guid ProjectId,
    string ConnectionId,
    DateTime DisconnectedAt,
    string? ExceptionMessage
) : IDomainEvent
{
    DateTime IDomainEvent.OccurredAt => DisconnectedAt;
}
