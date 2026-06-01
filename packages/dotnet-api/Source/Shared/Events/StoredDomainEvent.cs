namespace Source.Shared.Events;

/// <summary>
/// Persisted record of a domain event for traceability and audit.
/// Automatically written by DomainEventInterceptor in the same transaction
/// as the entity changes — if the save fails, no phantom events are stored.
/// </summary>
public class StoredDomainEvent
{
    public Guid Id { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
    public string? EntityId { get; set; }
    public string? EntityType { get; set; }
    public string? UserId { get; set; }
    public DateTime OccurredAt { get; set; }
}
