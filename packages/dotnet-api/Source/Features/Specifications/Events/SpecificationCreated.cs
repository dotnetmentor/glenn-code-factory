using Source.Shared.Events;

namespace Source.Features.Specifications.Events;

/// <summary>
/// A new <see cref="Models.Specification"/> was created (factory method, not the
/// upsert-with-existing branch). Card 3 of the platform-planning-kanban spec
/// will wire this to a SignalR broadcast so the frontend specs list refreshes
/// without polling. No handlers in this card — the event is raised, persisted
/// to <c>StoredDomainEvents</c> by the interceptor, and parked for Card 3.
/// </summary>
public record SpecificationCreated(
    Guid SpecificationId,
    Guid ProjectId,
    string Slug,
    DateTime OccurredAt) : IEntityDomainEvent
{
    public SpecificationCreated(Guid specificationId, Guid projectId, string slug)
        : this(specificationId, projectId, slug, DateTime.UtcNow) { }

    string IEntityDomainEvent.EntityId => SpecificationId.ToString();
    string IEntityDomainEvent.EntityType => "Specification";
}
