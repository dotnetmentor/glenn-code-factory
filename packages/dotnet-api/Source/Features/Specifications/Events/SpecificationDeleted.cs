using Source.Shared.Events;

namespace Source.Features.Specifications.Events;

/// <summary>
/// A <see cref="Models.Specification"/> was soft-deleted via
/// <see cref="Models.Specification.MarkDeleted"/>. The row stays in the table
/// (the global query filter hides it from default queries); re-creating a spec
/// with the same <c>(ProjectId, Slug)</c> is permitted because the unique index
/// is filtered to <c>IsDeleted = false</c>.
/// </summary>
public record SpecificationDeleted(
    Guid SpecificationId,
    Guid ProjectId,
    string Slug,
    DateTime OccurredAt) : IEntityDomainEvent
{
    public SpecificationDeleted(Guid specificationId, Guid projectId, string slug)
        : this(specificationId, projectId, slug, DateTime.UtcNow) { }

    string IEntityDomainEvent.EntityId => SpecificationId.ToString();
    string IEntityDomainEvent.EntityType => "Specification";
}
