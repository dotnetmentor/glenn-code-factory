using Microsoft.EntityFrameworkCore;
using Source.Infrastructure;
using Source.Infrastructure.Workspaces;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.GitHub.Queries;

/// <summary>
/// List the repositories visible to the current workspace through any of its installations.
/// Optionally filter to a single installation via <see cref="InstallationId"/>.
/// Endpoint: <c>GET /api/workspaces/{slug}/github/repositories</c>.
/// </summary>
public record GetGithubRepositoriesQuery(Guid? InstallationId = null)
    : IQuery<Result<IReadOnlyList<GithubRepositoryListItem>>>;

public sealed record GithubRepositoryListItem
{
    public required Guid Id { get; init; }
    public required Guid InstallationId { get; init; }
    public required long GithubRepoId { get; init; }
    public required string Owner { get; init; }
    public required string Name { get; init; }
    public required string FullName { get; init; }
    public required bool Private { get; init; }
    public string? DefaultBranch { get; init; }
    public DateTime? LastSyncedAt { get; init; }
}

public sealed class GetGithubRepositoriesHandler
    : IQueryHandler<GetGithubRepositoriesQuery, Result<IReadOnlyList<GithubRepositoryListItem>>>
{
    private readonly ApplicationDbContext _db;
    private readonly IWorkspaceContext _ctx;

    public GetGithubRepositoriesHandler(ApplicationDbContext db, IWorkspaceContext ctx)
    {
        _db = db;
        _ctx = ctx;
    }

    public async Task<Result<IReadOnlyList<GithubRepositoryListItem>>> Handle(
        GetGithubRepositoriesQuery request,
        CancellationToken cancellationToken)
    {
        var workspaceId = _ctx.Id;

        var query = _db.GithubRepositories
            .AsNoTracking()
            .Where(r => r.Installation!.WorkspaceId == workspaceId);

        if (request.InstallationId is { } id)
        {
            query = query.Where(r => r.GithubInstallationId == id);
        }

        var items = await query
            .OrderBy(r => r.FullName)
            .Select(r => new GithubRepositoryListItem
            {
                Id = r.Id,
                InstallationId = r.GithubInstallationId,
                GithubRepoId = r.GithubRepoId,
                Owner = r.Owner,
                Name = r.Name,
                FullName = r.FullName,
                Private = r.Private,
                DefaultBranch = r.DefaultBranch,
                LastSyncedAt = r.LastSyncedAt,
            })
            .ToListAsync(cancellationToken);

        return Result.Success<IReadOnlyList<GithubRepositoryListItem>>(items);
    }
}
