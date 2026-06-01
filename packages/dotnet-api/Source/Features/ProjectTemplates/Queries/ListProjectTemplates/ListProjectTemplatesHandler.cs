using Microsoft.EntityFrameworkCore;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.ProjectTemplates.Queries.ListProjectTemplates;

/// <summary>
/// Handler for <see cref="ListProjectTemplatesQuery"/>. Tracking-free
/// projection straight to <see cref="ProjectTemplateListItem"/>. Honours
/// <see cref="ListProjectTemplatesQuery.IncludeArchived"/> by lifting the
/// global soft-delete query filter via <c>IgnoreQueryFilters</c>.
/// </summary>
public sealed class ListProjectTemplatesHandler
    : IQueryHandler<ListProjectTemplatesQuery, Result<List<ProjectTemplateListItem>>>
{
    private readonly ApplicationDbContext _db;

    public ListProjectTemplatesHandler(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<Result<List<ProjectTemplateListItem>>> Handle(
        ListProjectTemplatesQuery request,
        CancellationToken cancellationToken)
    {
        // Lift the !IsDeleted query filter when the admin list asks for it;
        // the picker path always uses the filtered view (IncludeArchived=false).
        var query = request.IncludeArchived
            ? _db.ProjectTemplates.IgnoreQueryFilters()
            : _db.ProjectTemplates;

        var rows = await query
            .AsNoTracking()
            .OrderBy(t => t.SortOrder)
            .ThenBy(t => t.Name)
            .Select(t => new ProjectTemplateListItem
            {
                Id = t.Id,
                Slug = t.Slug,
                Name = t.Name,
                Description = t.Description,
                IconKey = t.IconKey,
                SourceRepoOwner = t.SourceRepoOwner,
                SourceRepoName = t.SourceRepoName,
                HasRuntimeSpec = t.RuntimeSpec != null,
                IsActive = t.IsActive,
                IsDefault = t.IsDefault,
                SortOrder = t.SortOrder,
                IsArchived = t.IsDeleted,
                CreatedAt = t.CreatedAt,
                UpdatedAt = t.UpdatedAt,
            })
            .ToListAsync(cancellationToken);

        return Result.Success(rows);
    }
}
