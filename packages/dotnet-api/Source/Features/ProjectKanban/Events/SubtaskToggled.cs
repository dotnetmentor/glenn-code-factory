using Source.Features.ProjectKanban.Models;
using Source.Shared.Events;

namespace Source.Features.ProjectKanban.Events;

/// <summary>
/// A <see cref="ProjectKanbanCardSubtask"/>'s <c>IsCompleted</c> flag was
/// flipped via <see cref="ProjectKanbanCardSubtask.Toggle"/>. We carry the
/// new state in the payload so a Card-3 SignalR subscriber can update the
/// checklist row without a re-read.
/// </summary>
public record SubtaskToggled(
    Guid SubtaskId,
    Guid CardId,
    bool IsCompleted,
    DateTime OccurredAt) : IEntityDomainEvent
{
    public SubtaskToggled(Guid subtaskId, Guid cardId, bool isCompleted)
        : this(subtaskId, cardId, isCompleted, DateTime.UtcNow) { }

    string IEntityDomainEvent.EntityId => SubtaskId.ToString();
    string IEntityDomainEvent.EntityType => "ProjectKanbanCardSubtask";
}
