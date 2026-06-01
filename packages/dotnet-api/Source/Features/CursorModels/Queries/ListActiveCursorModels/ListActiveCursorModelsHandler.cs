using Microsoft.EntityFrameworkCore;
using Source.Features.CursorModels.Models;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.CursorModels.Queries.ListActiveCursorModels;

/// <summary>
/// Handler for <see cref="ListActiveCursorModelsQuery"/>. Filtered to
/// <c>IsActive == true</c>; soft-deleted rows are already excluded by the
/// global query filter on <see cref="CursorModel"/>.
///
/// <para>Ordering uses <see cref="CursorModel.SortOrder"/> ascending so the
/// picker matches the SDK's natural ordering (<c>default</c> first, then
/// <c>composer-2.5</c>, <c>composer-2</c>, etc.), with <see cref="CursorModel.DisplayName"/>
/// as a stable tie-breaker.</para>
/// </summary>
public sealed class ListActiveCursorModelsHandler
    : IQueryHandler<ListActiveCursorModelsQuery, Result<List<CursorModelDto>>>
{
    private readonly ApplicationDbContext _db;

    public ListActiveCursorModelsHandler(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<Result<List<CursorModelDto>>> Handle(
        ListActiveCursorModelsQuery request,
        CancellationToken cancellationToken)
    {
        // Materialise first — jsonb List<T> columns deserialise client-side
        // via Npgsql's dynamic JSON, so projecting inside the SQL translation
        // would need a server-side selector EF can't emit. Cheap: the catalog
        // is <30 rows.
        var rows = await _db.CursorModels
            .AsNoTracking()
            .Where(m => m.IsActive)
            .OrderBy(m => m.SortOrder)
            .ThenBy(m => m.DisplayName)
            .ThenBy(m => m.Slug)
            .ToListAsync(cancellationToken);

        var dtos = rows
            .Select(m => new CursorModelDto(
                m.Id,
                m.Slug,
                m.DisplayName,
                m.Description,
                m.IsActive,
                m.Aliases,
                m.Parameters,
                m.Variants,
                m.SortOrder))
            .ToList();

        return Result.Success(dtos);
    }
}
