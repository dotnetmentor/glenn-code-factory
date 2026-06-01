using Microsoft.EntityFrameworkCore;
using Source.Infrastructure;
using Source.Infrastructure.Workspaces;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.Projects.Queries;

/// <summary>
/// List the projects in the current workspace that are currently in a "detached"
/// state — <c>GithubInstallationId IS NULL</c> — because the workspace
/// disconnected the installation they used to authenticate through. Surfaced
/// on <c>GET /api/workspaces/{slug}/projects/detached</c> so the frontend can
/// render a "needs reconnection" panel grouped by GitHub repo owner.
///
/// <para>Soft-deleted projects are filtered out via the global query filter on
/// <see cref="Source.Features.Projects.Models.Project"/>.</para>
/// </summary>
public record GetDetachedProjectsQuery() : IQuery<Result<IReadOnlyList<DetachedProjectDto>>>;

/// <summary>
/// Minimal projection for the detached-projects panel. The frontend groups by
/// <see cref="GithubRepoOwner"/> so reconnecting a single installation can
/// refresh every row that shares the same owner login in one update.
/// </summary>
public sealed record DetachedProjectDto(
    Guid Id,
    string Name,
    string GithubRepoOwner,
    string GithubRepoName,
    DateTime CreatedAt);

public sealed class GetDetachedProjectsHandler
    : IQueryHandler<GetDetachedProjectsQuery, Result<IReadOnlyList<DetachedProjectDto>>>
{
    private readonly ApplicationDbContext _db;
    private readonly IWorkspaceContext _ctx;

    public GetDetachedProjectsHandler(ApplicationDbContext db, IWorkspaceContext ctx)
    {
        _db = db;
        _ctx = ctx;
    }

    public async Task<Result<IReadOnlyList<DetachedProjectDto>>> Handle(
        GetDetachedProjectsQuery request,
        CancellationToken cancellationToken)
    {
        var workspaceId = _ctx.Id;

        // Ordered by owner then name so the frontend grouping renders in a
        // stable, alphabetised "Owner > Project" tree without a client-side
        // re-sort. The IsDeleted filter is implicit (global query filter on
        // Project), so we only need to express the "detached" predicate here.
        var items = await _db.Projects
            .AsNoTracking()
            .Where(p => p.WorkspaceId == workspaceId
                        && p.GithubInstallationId == null)
            .OrderBy(p => p.GithubRepoOwner)
            .ThenBy(p => p.Name)
            .Select(p => new DetachedProjectDto(
                p.Id,
                p.Name,
                p.GithubRepoOwner,
                p.GithubRepoName,
                p.CreatedAt))
            .ToListAsync(cancellationToken);

        return Result.Success<IReadOnlyList<DetachedProjectDto>>(items);
    }
}
