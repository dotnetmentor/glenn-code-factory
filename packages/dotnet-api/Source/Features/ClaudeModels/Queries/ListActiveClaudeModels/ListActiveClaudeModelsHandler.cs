using Microsoft.EntityFrameworkCore;
using Source.Features.ClaudeModels.Models;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.ClaudeModels.Queries.ListActiveClaudeModels;

/// <summary>
/// Handler for <see cref="ListActiveClaudeModelsQuery"/>. Filtered to
/// <c>IsActive == true</c>; soft-deleted rows are already excluded by the
/// global query filter on <see cref="ClaudeModel"/>.
///
/// <para>Ordering uses <see cref="ClaudeModel.SortOrder"/> ascending so the
/// picker matches the curated catalog order (Opus default first, then Sonnet,
/// Haiku, Fable), with <see cref="ClaudeModel.DisplayName"/> /
/// <see cref="ClaudeModel.Slug"/> as stable tie-breakers.</para>
/// </summary>
public sealed class ListActiveClaudeModelsHandler
    : IQueryHandler<ListActiveClaudeModelsQuery, Result<List<ClaudeModelDto>>>
{
    private readonly ApplicationDbContext _db;

    public ListActiveClaudeModelsHandler(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<Result<List<ClaudeModelDto>>> Handle(
        ListActiveClaudeModelsQuery request,
        CancellationToken cancellationToken)
    {
        var dtos = await _db.ClaudeModels
            .AsNoTracking()
            .Where(m => m.IsActive)
            .OrderBy(m => m.SortOrder)
            .ThenBy(m => m.DisplayName)
            .ThenBy(m => m.Slug)
            .Select(m => new ClaudeModelDto(
                m.Id,
                m.Slug,
                m.DisplayName,
                m.Description,
                m.IsSystemDefault,
                m.SupportsReasoning,
                m.DefaultEffort,
                m.IsActive,
                m.SortOrder))
            .ToListAsync(cancellationToken);

        return Result.Success(dtos);
    }
}
