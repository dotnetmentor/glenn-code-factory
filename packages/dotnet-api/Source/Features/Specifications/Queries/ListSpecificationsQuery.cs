using Microsoft.EntityFrameworkCore;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.Specifications.Queries;

/// <summary>
/// List every non-soft-deleted spec for a project, ordered by <c>UpdatedAt
/// DESC</c> so the most recently edited spec is at the top — matches the
/// dominant "what was I working on?" read pattern.
///
/// <para>Returns <see cref="SpecificationSummaryDto"/> (no <c>Content</c>) to
/// keep the list lean. The frontend specs page renders this; the detail page
/// hits <see cref="ReadSpecificationQuery"/> for the body.</para>
///
/// <para><b>Project scope is mandatory.</b> The handler always filters by
/// <see cref="ProjectId"/>; the caller (REST controller via claims, MCP
/// controller via <c>this.ProjectId</c>) provides the project id, never the
/// request body. Defence-in-depth against the framework's forbidden-field
/// strip on the MCP side.</para>
/// </summary>
public record ListSpecificationsQuery(Guid ProjectId)
    : IQuery<Result<List<SpecificationSummaryDto>>>;

public class ListSpecificationsQueryHandler
    : IQueryHandler<ListSpecificationsQuery, Result<List<SpecificationSummaryDto>>>
{
    private readonly ApplicationDbContext _db;

    public ListSpecificationsQueryHandler(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<Result<List<SpecificationSummaryDto>>> Handle(
        ListSpecificationsQuery request,
        CancellationToken cancellationToken)
    {
        // Soft-deleted rows are excluded by the global query filter on
        // Specification. Order by UpdatedAt DESC — most-recent-first matches
        // the frontend list page's UX.
        var rows = await _db.Specifications
            .AsNoTracking()
            .Where(s => s.ProjectId == request.ProjectId)
            .OrderByDescending(s => s.UpdatedAt)
            .Select(s => new SpecificationSummaryDto(
                s.Id,
                s.Slug,
                s.Name,
                s.Status,
                s.CreatedAt,
                s.UpdatedAt))
            .ToListAsync(cancellationToken);

        return Result.Success(rows);
    }
}
