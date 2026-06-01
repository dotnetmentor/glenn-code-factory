using Microsoft.EntityFrameworkCore;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.ProjectKanban.Queries;

/// <summary>
/// Fetch a single kanban card by id, including its (non-deleted) subtasks in
/// position order. Filters by <b>both</b> <see cref="ProjectId"/> and
/// <see cref="CardId"/> as defence-in-depth — even if a stale card id leaks
/// across tenants, the project filter prevents any information disclosure.
///
/// <para><b>Cross-project lookup returns <c>"not_found"</c>, not
/// <c>"forbidden"</c>.</b> Distinguishing the two would let a probing caller
/// confirm the existence of cards in other projects by observing a 403 vs 404.
/// We answer "no such card" uniformly.</para>
///
/// <para><b>Subtasks are eagerly loaded</b> because every UI surface that hits
/// this query is the card-detail view, where the checklist is always visible.
/// The query filter on <see cref="Models.ProjectKanbanCardSubtask"/> hides
/// soft-deleted rows automatically.</para>
/// </summary>
public record GetProjectKanbanCardQuery(
    Guid ProjectId,
    Guid CardId) : IQuery<Result<ProjectKanbanCardDto>>;

public class GetProjectKanbanCardQueryHandler
    : IQueryHandler<GetProjectKanbanCardQuery, Result<ProjectKanbanCardDto>>
{
    private readonly ApplicationDbContext _db;

    public GetProjectKanbanCardQueryHandler(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<Result<ProjectKanbanCardDto>> Handle(
        GetProjectKanbanCardQuery request,
        CancellationToken cancellationToken)
    {
        // Soft-deleted rows are excluded by the global query filter on
        // ProjectKanbanCard. We still match on ProjectId to avoid leaking the
        // existence of cards in other projects via a 200 vs 404 timing
        // difference.
        var card = await _db.ProjectKanbanCards
            .AsNoTracking()
            .FirstOrDefaultAsync(
                c => c.Id == request.CardId && c.ProjectId == request.ProjectId,
                cancellationToken);

        if (card is null)
        {
            return Result.Failure<ProjectKanbanCardDto>("not_found");
        }

        var subtasks = await _db.ProjectKanbanCardSubtasks
            .AsNoTracking()
            .Where(s => s.ProjectKanbanCardId == card.Id)
            .OrderBy(s => s.Position)
            .Select(s => new ProjectKanbanCardSubtaskDto(
                s.Id, s.ProjectKanbanCardId, s.Title, s.IsCompleted, s.Position))
            .ToListAsync(cancellationToken);

        return Result.Success(new ProjectKanbanCardDto(
            card.Id,
            card.ProjectId,
            card.Title,
            card.Description,
            card.Status,
            card.Position,
            card.Priority,
            card.DueDate,
            card.Source,
            card.CreatedOnBranch,
            card.CreatedAt,
            card.UpdatedAt,
            subtasks));
    }
}
