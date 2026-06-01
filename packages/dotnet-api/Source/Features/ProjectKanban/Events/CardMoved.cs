using Source.Features.ProjectKanban.Models;
using Source.Shared.Events;

namespace Source.Features.ProjectKanban.Events;

/// <summary>
/// A <see cref="ProjectKanbanCard"/> was moved to a new status / position
/// via <see cref="ProjectKanbanCard.Move"/>. Payload-rich on purpose — the
/// move is the most interesting event for observers (re-render the board), so
/// we carry both before- and after-state so subscribers don't need a re-fetch
/// to display "moved from Todo to InProgress" UX.
/// </summary>
public record CardMoved(
    Guid CardId,
    Guid ProjectId,
    ProjectKanbanCardStatus OldStatus,
    int OldPosition,
    ProjectKanbanCardStatus NewStatus,
    int NewPosition,
    DateTime OccurredAt) : IEntityDomainEvent
{
    public CardMoved(
        Guid cardId,
        Guid projectId,
        ProjectKanbanCardStatus oldStatus,
        int oldPosition,
        ProjectKanbanCardStatus newStatus,
        int newPosition)
        : this(cardId, projectId, oldStatus, oldPosition, newStatus, newPosition, DateTime.UtcNow) { }

    string IEntityDomainEvent.EntityId => CardId.ToString();
    string IEntityDomainEvent.EntityType => "ProjectKanbanCard";
}
