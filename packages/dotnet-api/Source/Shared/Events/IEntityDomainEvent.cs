namespace Source.Shared.Events;

/// <summary>
/// Optional interface for domain events that relate to a specific entity.
/// When implemented, the interceptor populates EntityId and EntityType
/// on StoredDomainEvent for indexed querying.
/// </summary>
public interface IEntityDomainEvent : IDomainEvent
{
    string EntityId { get; }
    string EntityType { get; }
}
