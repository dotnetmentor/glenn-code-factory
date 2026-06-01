using Source.Features.ProjectKanban.Models;
using Source.Shared.Events;

namespace Source.Features.ProjectKanban.Events;

/// <summary>
/// A new <see cref="ProjectKanbanCardSubtask"/> was added to a card via
/// <see cref="ProjectKanbanCardSubtask.Create"/>. Card 3 of the
/// platform-planning-kanban spec will broadcast this so the card detail UI
/// can append the row in real time.
/// </summary>
public record SubtaskCreated(
    Guid SubtaskId,
    Guid CardId,
    string Title,
    DateTime OccurredAt) : IEntityDomainEvent
{
    public SubtaskCreated(Guid subtaskId, Guid cardId, string title)
        : this(subtaskId, cardId, title, DateTime.UtcNow) { }

    string IEntityDomainEvent.EntityId => SubtaskId.ToString();
    string IEntityDomainEvent.EntityType => "ProjectKanbanCardSubtask";
}
