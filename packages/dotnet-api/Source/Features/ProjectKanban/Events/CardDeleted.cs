using Source.Features.ProjectKanban.Models;
using Source.Shared.Events;

namespace Source.Features.ProjectKanban.Events;

/// <summary>
/// A <see cref="ProjectKanbanCard"/> was soft-deleted via
/// <see cref="ProjectKanbanCard.MarkDeleted"/>. The row stays in the table
/// (the global query filter hides it from default queries) — subscribers
/// should treat the card as gone from the board.
/// </summary>
public record CardDeleted(
    Guid CardId,
    Guid ProjectId,
    DateTime OccurredAt) : IEntityDomainEvent
{
    public CardDeleted(Guid cardId, Guid projectId)
        : this(cardId, projectId, DateTime.UtcNow) { }

    string IEntityDomainEvent.EntityId => CardId.ToString();
    string IEntityDomainEvent.EntityType => "ProjectKanbanCard";
}
