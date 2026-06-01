using Microsoft.EntityFrameworkCore;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.Specifications.Queries;

/// <summary>
/// Fetch a single spec by <c>(ProjectId, Slug)</c>. Filters by <b>both</b> so
/// even a stale slug from another project resolves as <c>"not_found"</c> rather
/// than leaking the existence of specs in other tenants — same uniform-404
/// stance as <see cref="Source.Features.ProjectKanban.Queries.GetProjectKanbanCardQuery"/>.
///
/// <para>Returns the full <see cref="SpecificationDto"/> including the markdown
/// body. The frontend spec-detail page and the MCP <c>read_specification</c>
/// tool both consume this.</para>
/// </summary>
public record ReadSpecificationQuery(Guid ProjectId, string Slug)
    : IQuery<Result<SpecificationDto>>;

public class ReadSpecificationQueryHandler
    : IQueryHandler<ReadSpecificationQuery, Result<SpecificationDto>>
{
    private readonly ApplicationDbContext _db;

    public ReadSpecificationQueryHandler(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<Result<SpecificationDto>> Handle(
        ReadSpecificationQuery request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Slug))
        {
            return Result.Failure<SpecificationDto>("not_found");
        }

        // Normalise slug match to lowercase — the entity stores it normalised,
        // so a caller passing "My-Spec" still resolves the row.
        var slug = request.Slug.Trim().ToLowerInvariant();

        // The global query filter excludes soft-deleted rows. Project filter
        // is the defence-in-depth gate that mirrors the kanban convention.
        var spec = await _db.Specifications
            .AsNoTracking()
            .FirstOrDefaultAsync(
                s => s.ProjectId == request.ProjectId && s.Slug == slug,
                cancellationToken);

        if (spec is null)
        {
            return Result.Failure<SpecificationDto>("not_found");
        }

        return Result.Success(new SpecificationDto(
            spec.Id,
            spec.Slug,
            spec.Name,
            spec.Content,
            spec.Status,
            spec.CreatedAt,
            spec.UpdatedAt));
    }
}
