namespace Source.Shared.Events;

/// <summary>
/// Persisted record of property-level entity changes for audit.
/// Automatically written by ChangeTrackingInterceptor in the same transaction
/// as the entity changes — if the save fails, no phantom records are stored.
/// This is Layer 2 of the event architecture (Domain Events are Layer 1).
/// </summary>
public class StoredEntityChange
{
    public Guid Id { get; set; }
    public string EntityType { get; set; } = string.Empty;
    public string EntityId { get; set; } = string.Empty;
    public string Operation { get; set; } = string.Empty; // "Created", "Updated", "Deleted"
    public string ChangedProperties { get; set; } = string.Empty; // JSON array of { Property, OldValue, NewValue }
    public string? UserId { get; set; }
    public DateTime OccurredAt { get; set; }
}
