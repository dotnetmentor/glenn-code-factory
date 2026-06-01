using Microsoft.EntityFrameworkCore;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.ProjectTemplates.Queries.GetProjectTemplate;

/// <summary>
/// Handler for <see cref="GetProjectTemplateQuery"/>. Single tracking-free
/// projection to <see cref="ProjectTemplateDetail"/>. Tombstoned rows are
/// hidden by the global query filter — callers that need archived rows visible
/// (e.g. a "restore" flow on the admin edit screen) should lift the filter at
/// that call site.
/// </summary>
public sealed class GetProjectTemplateHandler
    : IQueryHandler<GetProjectTemplateQuery, Result<ProjectTemplateDetail>>
{
    public const string NotFoundError = "template_not_found";

    private readonly ApplicationDbContext _db;

    public GetProjectTemplateHandler(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<Result<ProjectTemplateDetail>> Handle(
        GetProjectTemplateQuery request,
        CancellationToken cancellationToken)
    {
        // Admin edit screen needs to see archived rows so the operator can
        // inspect (and eventually restore) them. The picker never hits this
        // endpoint, so always lift the soft-delete filter here.
        var row = await _db.ProjectTemplates
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(t => t.Id == request.TemplateId)
            .Select(t => new ProjectTemplateDetail
            {
                Id = t.Id,
                Slug = t.Slug,
                Name = t.Name,
                Description = t.Description,
                IconKey = t.IconKey,
                SourceRepoOwner = t.SourceRepoOwner,
                SourceRepoName = t.SourceRepoName,
                RuntimeSpec = t.RuntimeSpec,
                IsActive = t.IsActive,
                IsDefault = t.IsDefault,
                SortOrder = t.SortOrder,
                IsArchived = t.IsDeleted,
                CreatedAt = t.CreatedAt,
                UpdatedAt = t.UpdatedAt,
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (row is null)
        {
            return Result.Failure<ProjectTemplateDetail>(NotFoundError);
        }

        return Result.Success(row);
    }
}
