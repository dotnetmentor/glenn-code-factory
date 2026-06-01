using Microsoft.EntityFrameworkCore;
using Source.Features.GitHub.Models;
using Source.Features.GitHub.Services;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.GitHub.Queries.ListRepoBranches;

/// <summary>
/// Handler for <see cref="ListRepoBranchesQuery"/>. Workflow mirrors
/// <see cref="ListInstallationRepos.ListInstallationReposHandler"/>:
/// <list type="number">
///   <item>Resolve installation row, collapse "missing" + "non-member" to a single 404 result.</item>
///   <item>Read the repo once (<c>GET /repos/{owner}/{repo}</c>) for the default branch name.</item>
///   <item>List branches (<c>GET /repos/{owner}/{repo}/branches</c>, paginated).</item>
///   <item>Project to the flat <see cref="GithubBranchListItemDto"/>; mark the default-branch row.</item>
/// </list>
///
/// <para>Why two calls instead of accepting a hint from the client? The repo lookup also
/// validates that the installation actually has access to the repo — passing through a forged
/// (owner, repo) pair would simply 404 from GitHub, which we surface as a 502-ish failure
/// rather than letting the client probe access via timing.</para>
/// </summary>
public sealed class ListRepoBranchesHandler
    : IQueryHandler<ListRepoBranchesQuery, Result<List<GithubBranchListItemDto>>>
{
    /// <summary>Reuse the same sentinel as the repo-list handler so the controller can map both with one prefix check.</summary>
    public const string NotFoundPrefix = "notfound:";

    private readonly ApplicationDbContext _db;
    private readonly IGithubApiClient _api;
    private readonly ILogger<ListRepoBranchesHandler> _logger;

    public ListRepoBranchesHandler(
        ApplicationDbContext db,
        IGithubApiClient api,
        ILogger<ListRepoBranchesHandler> logger)
    {
        _db = db;
        _api = api;
        _logger = logger;
    }

    public async Task<Result<List<GithubBranchListItemDto>>> Handle(
        ListRepoBranchesQuery request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.CallerUserId))
        {
            return Result.Failure<List<GithubBranchListItemDto>>($"{NotFoundPrefix} caller is not authenticated");
        }
        if (string.IsNullOrWhiteSpace(request.Owner) || string.IsNullOrWhiteSpace(request.Repo))
        {
            return Result.Failure<List<GithubBranchListItemDto>>("owner and repo are required");
        }

        var installation = await _db.GithubInstallations
            .AsNoTracking()
            .Where(i => i.Id == request.InstallationId)
            .Select(i => new { i.WorkspaceId, i.InstallationId })
            .SingleOrDefaultAsync(cancellationToken);

        if (installation is null)
        {
            return Result.Failure<List<GithubBranchListItemDto>>($"{NotFoundPrefix} installation not found");
        }

        var isMember = await _db.WorkspaceMemberships
            .AsNoTracking()
            .AnyAsync(
                m => m.WorkspaceId == installation.WorkspaceId && m.UserId == request.CallerUserId,
                cancellationToken);

        if (!isMember)
        {
            return Result.Failure<List<GithubBranchListItemDto>>($"{NotFoundPrefix} installation not found");
        }

        try
        {
            // Step 1: get the default branch name. We need this BEFORE the branch list so we can
            // flag the matching entry — and it doubles as a "does the installation actually
            // have access to this repo?" gate (GitHub will 404 if not).
            var repoMeta = await _api.GetRepositoryAsync(installation.InstallationId, request.Owner, request.Repo, cancellationToken);
            var defaultBranch = repoMeta.DefaultBranch ?? string.Empty;

            // Step 2: list branches. Existing client method handles auth + the per_page=100 cap.
            var branches = await _api.ListRepositoryBranchesAsync(installation.InstallationId, request.Owner, request.Repo, cancellationToken);

            var items = branches
                .Select(b => new GithubBranchListItemDto(
                    Name: b.Name,
                    IsDefault: !string.IsNullOrEmpty(defaultBranch)
                        && string.Equals(b.Name, defaultBranch, StringComparison.Ordinal)))
                .ToList();

            return Result.Success(items);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "GitHub API call failed listing branches for installation {InstallationId} repo {Owner}/{Repo}",
                request.InstallationId, request.Owner, request.Repo);
            return Result.Failure<List<GithubBranchListItemDto>>("Failed to list branches from GitHub");
        }
    }
}
