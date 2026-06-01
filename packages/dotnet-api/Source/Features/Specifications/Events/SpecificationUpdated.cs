using Source.Shared.Events;

namespace Source.Features.Specifications.Events;

/// <summary>
/// An existing <see cref="Models.Specification"/>'s name and/or content was
/// changed via <see cref="Models.Specification.UpdateContent"/> (this is the
/// "upsert hits an existing row" branch of <c>SaveSpecificationCommand</c>).
/// Card 3 of the platform-planning-kanban spec will subscribe to this for the
/// SignalR broadcast that refreshes the spec detail page in real time.
/// </summary>
public record SpecificationUpdated(
    Guid SpecificationId,
    Guid ProjectId,
    string Slug,
    DateTime OccurredAt) : IEntityDomainEvent
{
    public SpecificationUpdated(Guid specificationId, Guid projectId, string slug)
        : this(specificationId, projectId, slug, DateTime.UtcNow) { }

    string IEntityDomainEvent.EntityId => SpecificationId.ToString();
    string IEntityDomainEvent.EntityType => "Specification";
}
