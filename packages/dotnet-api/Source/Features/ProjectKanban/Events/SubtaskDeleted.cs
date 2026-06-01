using Source.Features.ProjectKanban.Models;
using Source.Shared.Events;

namespace Source.Features.ProjectKanban.Events;

/// <summary>
/// A <see cref="ProjectKanbanCardSubtask"/> was soft-deleted via
/// <see cref="ProjectKanbanCardSubtask.MarkDeleted"/>. The row stays in the
/// table (the global query filter hides it from default queries); subscribers
/// should remove the checklist row from the rendered card.
/// </summary>
public record SubtaskDeleted(
    Guid SubtaskId,
    Guid CardId,
    DateTime OccurredAt) : IEntityDomainEvent
{
    public SubtaskDeleted(Guid subtaskId, Guid cardId)
        : this(subtaskId, cardId, DateTime.UtcNow) { }

    string IEntityDomainEvent.EntityId => SubtaskId.ToString();
    string IEntityDomainEvent.EntityType => "ProjectKanbanCardSubtask";
}
