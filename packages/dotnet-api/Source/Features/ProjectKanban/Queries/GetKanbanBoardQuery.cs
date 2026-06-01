using Microsoft.EntityFrameworkCore;
using Source.Features.ProjectKanban.Models;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;
using Tapper;

namespace Source.Features.ProjectKanban.Queries;

/// <summary>
/// Read-side projection for the board overview. Returns one row per
/// <see cref="ProjectKanbanCardStatus"/> column with the card count. The board
/// uses this to render the four virtual columns (Backlog / Todo / InProgress /
/// Done) before any cards are loaded; the cards themselves come from
/// <see cref="GetColumnCardsQuery"/> on demand.
/// </summary>
[TranspilationSource]
public record KanbanBoardColumnDto(
    ProjectKanbanCardStatus Status,
    string Name,
    int CardCount);

/// <summary>
/// Card 2 read-side: the four virtual board columns keyed by
/// <see cref="ProjectKanbanCardStatus"/>, each with a count of non-deleted
/// cards in <see cref="ProjectId"/>'s scope. No cards in the response — that
/// belongs to <see cref="GetColumnCardsQuery"/>.
///
/// <para><b>Always returns four columns</b>, even when a column is empty —
/// the board UI needs the structural shape stable across renders. Empty
/// columns have <c>CardCount = 0</c>.</para>
/// </summary>
public record GetKanbanBoardQuery(Guid ProjectId)
    : IQuery<Result<List<KanbanBoardColumnDto>>>;

public class GetKanbanBoardQueryHandler
    : IQueryHandler<GetKanbanBoardQuery, Result<List<KanbanBoardColumnDto>>>
{
    private readonly ApplicationDbContext _db;

    public GetKanbanBoardQueryHandler(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<Result<List<KanbanBoardColumnDto>>> Handle(
        GetKanbanBoardQuery request,
        CancellationToken cancellationToken)
    {
        // One group-by query — EF translates this to a SELECT … GROUP BY …
        // against the filtered (non-deleted) projection.
        var counts = await _db.ProjectKanbanCards
            .AsNoTracking()
            .Where(c => c.ProjectId == request.ProjectId)
            .GroupBy(c => c.Status)
            .Select(g => new { Status = g.Key, CardCount = g.Count() })
            .ToListAsync(cancellationToken);

        var byStatus = counts.ToDictionary(x => x.Status, x => x.CardCount);

        // Stable column order matching the enum's declared ordering — the UI
        // can rely on this and avoid resorting per render.
        var columns = new List<KanbanBoardColumnDto>
        {
            new(ProjectKanbanCardStatus.Backlog,     "Backlog",     byStatus.GetValueOrDefault(ProjectKanbanCardStatus.Backlog)),
            new(ProjectKanbanCardStatus.Todo,        "Todo",        byStatus.GetValueOrDefault(ProjectKanbanCardStatus.Todo)),
            new(ProjectKanbanCardStatus.InProgress,  "In Progress", byStatus.GetValueOrDefault(ProjectKanbanCardStatus.InProgress)),
            new(ProjectKanbanCardStatus.Done,        "Done",        byStatus.GetValueOrDefault(ProjectKanbanCardStatus.Done)),
        };

        return Result.Success(columns);
    }
}
