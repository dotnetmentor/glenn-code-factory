using Source.Features.ProjectKanban.Models;
using Source.Shared.Events;

namespace Source.Features.ProjectKanban.Events;

/// <summary>
/// A new <see cref="ProjectKanbanCard"/> landed on the board (factory branch
/// of <see cref="ProjectKanbanCard.Create"/>). Card 3 of the
/// platform-planning-kanban spec will wire this to a SignalR broadcast so the
/// frontend board refreshes without polling. No handlers in this card — the
/// event is raised, persisted to <c>StoredDomainEvents</c> by the interceptor,
/// and parked for Card 3.
/// </summary>
public record CardCreated(
    Guid CardId,
    Guid ProjectId,
    string Title,
    ProjectKanbanCardStatus Status,
    ProjectKanbanCardPriority Priority,
    ProjectKanbanCardSource Source,
    string? CreatedOnBranch,
    DateTime OccurredAt) : IEntityDomainEvent
{
    public CardCreated(
        Guid cardId,
        Guid projectId,
        string title,
        ProjectKanbanCardStatus status,
        ProjectKanbanCardPriority priority,
        ProjectKanbanCardSource source,
        string? createdOnBranch)
        : this(cardId, projectId, title, status, priority, source, createdOnBranch, DateTime.UtcNow) { }

    string IEntityDomainEvent.EntityId => CardId.ToString();
    string IEntityDomainEvent.EntityType => "ProjectKanbanCard";
}
