using Microsoft.EntityFrameworkCore;
using Source.Features.GitHub.Models;
using Source.Features.GitHub.Services;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.GitHub.Queries.ListInstallationRepos;

/// <summary>
/// Handler for <see cref="ListInstallationReposQuery"/>. Workflow:
/// <list type="number">
///   <item>Resolve the local <c>GithubInstallation</c> row by its DB Guid id.</item>
///   <item>Verify the caller has a <c>WorkspaceMembership</c> on that installation's workspace.
///         Both "row missing" and "caller not a member" collapse to the same 404 outcome —
///         we never confirm the existence of an installation the caller can't see.</item>
///   <item>Mint an installation token and call GitHub <c>GET /installation/repositories</c>
///         (the existing <see cref="IGithubApiClient.ListInstallationRepositoriesAsync"/> already
///         handles auth + pagination up to per_page=100).</item>
///   <item>Project to the controller-shape <see cref="GithubRepoListItemDto"/>.</item>
/// </list>
///
/// <para>No caching. The smoke-test spec is explicit about this; we re-call GitHub on every
/// request.</para>
/// </summary>
public sealed class ListInstallationReposHandler
    : IQueryHandler<ListInstallationReposQuery, Result<List<GithubRepoListItemDto>>>
{
    /// <summary>
    /// Sentinel prefix for "installation not found OR caller is not a member" — the controller
    /// matches on this to return 404 without revealing which of the two it is.
    /// </summary>
    public const string NotFoundPrefix = "notfound:";

    private readonly ApplicationDbContext _db;
    private readonly IGithubApiClient _api;
    private readonly ILogger<ListInstallationReposHandler> _logger;

    public ListInstallationReposHandler(
        ApplicationDbContext db,
        IGithubApiClient api,
        ILogger<ListInstallationReposHandler> logger)
    {
        _db = db;
        _api = api;
        _logger = logger;
    }

    public async Task<Result<List<GithubRepoListItemDto>>> Handle(
        ListInstallationReposQuery request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.CallerUserId))
        {
            return Result.Failure<List<GithubRepoListItemDto>>($"{NotFoundPrefix} caller is not authenticated");
        }

        // Single round-trip: pull only the WorkspaceId + GitHub-side numeric installation id.
        // We don't need the rest of the row here.
        var installation = await _db.GithubInstallations
            .AsNoTracking()
            .Where(i => i.Id == request.InstallationId)
            .Select(i => new { i.WorkspaceId, i.InstallationId })
            .SingleOrDefaultAsync(cancellationToken);

        if (installation is null)
        {
            return Result.Failure<List<GithubRepoListItemDto>>($"{NotFoundPrefix} installation not found");
        }

        var isMember = await _db.WorkspaceMemberships
            .AsNoTracking()
            .AnyAsync(
                m => m.WorkspaceId == installation.WorkspaceId && m.UserId == request.CallerUserId,
                cancellationToken);

        if (!isMember)
        {
            // Same wording as the "row missing" case on purpose — collapses to 404 either way.
            return Result.Failure<List<GithubRepoListItemDto>>($"{NotFoundPrefix} installation not found");
        }

        try
        {
            var repos = await _api.ListInstallationRepositoriesAsync(installation.InstallationId, cancellationToken);

            // Optional cross-reference: when the caller passed a WorkspaceId hint, build a
            // (Owner, Name) -> (ProjectId, ProjectName) index for the workspace's live
            // projects on THIS installation. We deliberately scope by both the workspace AND
            // the installation: a workspace can have multiple installations, and the same
            // (owner, name) under a different installation is a different repo.
            //
            // The Projects DbSet already filters !IsDeleted via a global query filter
            // (Project is ISoftDelete), so we don't need a manual IsDeleted predicate here.
            //
            // OrdinalIgnoreCase: GitHub repo names are case-insensitive on the API side
            // (the same repo can be addressed as "Foo/Bar" or "foo/bar"), and our local
            // projects store whatever case the user clicked through. Index on lower-cased
            // keys so a casing drift doesn't make us miss a real link.
            Dictionary<(string Owner, string Name), (Guid Id, string Name)> linkedProjects = new();
            if (request.WorkspaceId is { } workspaceId)
            {
                var workspaceProjects = await _db.Projects
                    .AsNoTracking()
                    .Where(p => p.WorkspaceId == workspaceId
                                && p.GithubInstallationId == request.InstallationId)
                    .Select(p => new
                    {
                        p.Id,
                        p.Name,
                        p.GithubRepoOwner,
                        p.GithubRepoName,
                    })
                    .ToListAsync(cancellationToken);

                foreach (var p in workspaceProjects)
                {
                    var key = (p.GithubRepoOwner.ToLowerInvariant(), p.GithubRepoName.ToLowerInvariant());
                    // First-write-wins on the unlikely case of two projects pointing at the
                    // same repo within one workspace+installation — the new 409 duplicate
                    // gate we're shipping prevents this from happening on the create path,
                    // but legacy data could already have a pair. The first row is as good as
                    // any for the picker UX.
                    linkedProjects.TryAdd(key, (p.Id, p.Name));
                }
            }

            // Map GitHub's wire shape to our flat DTO. GitHub gives us a nullable default_branch
            // (rare — empty repos with no commits). Surface "" rather than null so the contract
            // is non-null for the frontend.
            var items = repos
                .Select(r =>
                {
                    Guid? linkedId = null;
                    string? linkedName = null;
                    if (linkedProjects.Count > 0)
                    {
                        var key = (r.Owner.Login.ToLowerInvariant(), r.Name.ToLowerInvariant());
                        if (linkedProjects.TryGetValue(key, out var match))
                        {
                            linkedId = match.Id;
                            linkedName = match.Name;
                        }
                    }

                    return new GithubRepoListItemDto(
                        Owner: r.Owner.Login,
                        Name: r.Name,
                        DefaultBranch: r.DefaultBranch ?? string.Empty,
                        Private: r.Private,
                        LinkedProjectId: linkedId,
                        LinkedProjectName: linkedName);
                })
                .ToList();

            return Result.Success(items);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "GitHub API call failed listing repos for installation {InstallationId}", request.InstallationId);
            return Result.Failure<List<GithubRepoListItemDto>>("Failed to list repositories from GitHub");
        }
    }
}
