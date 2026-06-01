using Microsoft.EntityFrameworkCore;
using Source.Features.ProjectKanban.Models;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.ProjectKanban.Queries;

/// <summary>
/// List every (non-soft-deleted) kanban card for a project, optionally filtered
/// by <see cref="Status"/>. Order is stable: <c>(Status ASC, Position ASC)</c>
/// — same shape the dominant index on the entity supports.
///
/// <para><b>Lean list shape.</b> Returns <see cref="ProjectKanbanCardListItemDto"/>
/// (not the full <see cref="ProjectKanbanCardDto"/>): each row carries the
/// subtask count + completed-count so the board can render the "2/5" badge,
/// but never the subtasks themselves. Drilling into a card detail goes through
/// <see cref="GetProjectKanbanCardQuery"/> which eagerly loads them.</para>
///
/// <para><b>Project scope is mandatory.</b> The handler always filters by
/// <see cref="ProjectId"/>. The kanban MCP controller passes its claims-derived
/// <c>this.ProjectId</c> here; the daemon can never widen the query past its
/// own project, even if the framework's forbidden-field strip somehow let
/// through a malicious payload — defence in depth.</para>
/// </summary>
public record ListProjectKanbanCardsQuery(
    Guid ProjectId,
    ProjectKanbanCardStatus? Status) : IQuery<Result<List<ProjectKanbanCardListItemDto>>>;

public class ListProjectKanbanCardsQueryHandler
    : IQueryHandler<ListProjectKanbanCardsQuery, Result<List<ProjectKanbanCardListItemDto>>>
{
    private readonly ApplicationDbContext _db;

    public ListProjectKanbanCardsQueryHandler(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<Result<List<ProjectKanbanCardListItemDto>>> Handle(
        ListProjectKanbanCardsQuery request,
        CancellationToken cancellationToken)
    {
        var query = _db.ProjectKanbanCards
            .AsNoTracking()
            .Where(c => c.ProjectId == request.ProjectId);

        if (request.Status.HasValue)
        {
            var status = request.Status.Value;
            query = query.Where(c => c.Status == status);
        }

        // Project subtask counts in the same query via a correlated subquery
        // (EF translates this to a SQL subselect — one round trip total). The
        // !IsDeleted filter on subtasks mirrors the global query filter so
        // soft-deleted checklist items aren't counted.
        var rows = await query
            .OrderBy(c => c.Status)
            .ThenBy(c => c.Position)
            .Select(c => new ProjectKanbanCardListItemDto(
                c.Id,
                c.ProjectId,
                c.Title,
                c.Description,
                c.Status,
                c.Position,
                c.Priority,
                c.DueDate,
                c.Source,
                c.CreatedOnBranch,
                c.CreatedAt,
                c.UpdatedAt,
                _db.ProjectKanbanCardSubtasks.Count(s => s.ProjectKanbanCardId == c.Id),
                _db.ProjectKanbanCardSubtasks.Count(s => s.ProjectKanbanCardId == c.Id && s.IsCompleted)))
            .ToListAsync(cancellationToken);

        return Result.Success(rows);
    }
}
