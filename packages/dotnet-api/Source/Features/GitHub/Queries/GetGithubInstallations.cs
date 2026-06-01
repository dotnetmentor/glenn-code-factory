using Microsoft.EntityFrameworkCore;
using Source.Infrastructure;
using Source.Infrastructure.Workspaces;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.GitHub.Queries;

/// <summary>
/// List the GitHub App installations linked to the current workspace.
/// Endpoint: <c>GET /api/workspaces/{slug}/github/installations</c>.
/// </summary>
public record GetGithubInstallationsQuery : IQuery<Result<IReadOnlyList<GithubInstallationListItem>>>;

public sealed record GithubInstallationListItem
{
    public required Guid Id { get; init; }
    public required long InstallationId { get; init; }
    public required string AccountLogin { get; init; }
    public required string AccountType { get; init; }
    public string? AccountAvatarUrl { get; init; }
    public required bool Suspended { get; init; }
    public required int RepoCount { get; init; }
    public required DateTime CreatedAt { get; init; }
}

public sealed class GetGithubInstallationsHandler
    : IQueryHandler<GetGithubInstallationsQuery, Result<IReadOnlyList<GithubInstallationListItem>>>
{
    private readonly ApplicationDbContext _db;
    private readonly IWorkspaceContext _ctx;

    public GetGithubInstallationsHandler(ApplicationDbContext db, IWorkspaceContext ctx)
    {
        _db = db;
        _ctx = ctx;
    }

    public async Task<Result<IReadOnlyList<GithubInstallationListItem>>> Handle(
        GetGithubInstallationsQuery request,
        CancellationToken cancellationToken)
    {
        var workspaceId = _ctx.Id;

        var items = await _db.GithubInstallations
            .AsNoTracking()
            .Where(i => i.WorkspaceId == workspaceId)
            .OrderByDescending(i => i.CreatedAt)
            .Select(i => new GithubInstallationListItem
            {
                Id = i.Id,
                InstallationId = i.InstallationId,
                AccountLogin = i.AccountLogin,
                AccountType = i.AccountType,
                AccountAvatarUrl = i.AccountAvatarUrl,
                Suspended = i.Suspended,
                RepoCount = i.Repositories.Count,
                CreatedAt = i.CreatedAt,
            })
            .ToListAsync(cancellationToken);

        return Result.Success<IReadOnlyList<GithubInstallationListItem>>(items);
    }
}
