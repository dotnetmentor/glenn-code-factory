using Source.Features.ProjectKanban.Models;
using Source.Shared.Events;

namespace Source.Features.ProjectKanban.Events;

/// <summary>
/// A <see cref="ProjectKanbanCard"/>'s editable metadata changed via
/// <see cref="ProjectKanbanCard.UpdateMetadata"/>. Payload-light on purpose —
/// subscribers re-fetch the card detail (or update what they have via a
/// known-id refresh); we don't want to teach every observer how to merge
/// individual field diffs.
/// </summary>
public record CardUpdated(
    Guid CardId,
    Guid ProjectId,
    DateTime OccurredAt) : IEntityDomainEvent
{
    public CardUpdated(Guid cardId, Guid projectId)
        : this(cardId, projectId, DateTime.UtcNow) { }

    string IEntityDomainEvent.EntityId => CardId.ToString();
    string IEntityDomainEvent.EntityType => "ProjectKanbanCard";
}
