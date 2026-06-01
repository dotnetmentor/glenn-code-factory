using Microsoft.EntityFrameworkCore;
using Source.Features.GitHub.Models;
using Source.Features.GitHub.Services;
using Source.Features.GitHub.Services.Dtos;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.Projects.Queries.ListProjectGithubBranches;

/// <summary>
/// Handler for <see cref="ListProjectGithubBranchesQuery"/>. Workflow:
/// <list type="number">
///   <item>Resolve the project + the workspace it belongs to.</item>
///   <item>Gate on workspace membership — non-members and missing rows both collapse to 404
///         (don't leak project existence).</item>
///   <item>Issue the same two GitHub calls as
///         <see cref="GitHub.Queries.ListRepoBranches.ListRepoBranchesHandler"/> —
///         <c>GET /repos/{owner}/{repo}</c> for the default branch and
///         <c>GET /repos/{owner}/{repo}/branches</c> for the list itself.</item>
///   <item>Load every <see cref="Projects.Models.ProjectBranch"/> for the project ONCE
///         (the soft-delete query filter is not configured on <c>ProjectBranch</c> — see
///         the entity comment: "Auditable but NOT soft-deletable"). Build a
///         <c>name → id</c> dictionary so the projection is a single hash lookup per
///         git branch.</item>
///   <item>Project to <see cref="GithubBranchListItemDto"/> and populate
///         <see cref="GithubBranchListItemDto.LinkedSystemBranchId"/> from the dictionary.</item>
/// </list>
///
/// <para><b>Round-trip count.</b> One DB read for the project, one for membership,
/// one for the local branch list, then two GitHub calls — total of 3 DB + 2 HTTP.
/// No N+1.</para>
/// </summary>
public sealed class ListProjectGithubBranchesHandler
    : IQueryHandler<ListProjectGithubBranchesQuery, Result<List<GithubBranchListItemDto>>>
{
    /// <summary>Sentinel mapped to 404 by the controller — project missing / no access.</summary>
    public const string NotFoundPrefix = "not-found:";

    private readonly ApplicationDbContext _db;
    private readonly IGithubApiClient _api;
    private readonly ILogger<ListProjectGithubBranchesHandler> _logger;

    public ListProjectGithubBranchesHandler(
        ApplicationDbContext db,
        IGithubApiClient api,
        ILogger<ListProjectGithubBranchesHandler> logger)
    {
        _db = db;
        _api = api;
        _logger = logger;
    }

    public async Task<Result<List<GithubBranchListItemDto>>> Handle(
        ListProjectGithubBranchesQuery request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.CallerUserId))
        {
            return Result.Failure<List<GithubBranchListItemDto>>($"{NotFoundPrefix} unauthenticated");
        }

        // -------- 1. Resolve project --------
        // Soft-deleted projects are filtered by the global !IsDeleted query
        // filter, so a tombstoned row is "not found" by construction here.
        var project = await _db.Projects
            .AsNoTracking()
            .Where(p => p.Id == request.ProjectId)
            .Select(p => new
            {
                p.WorkspaceId,
                p.GithubRepoOwner,
                p.GithubRepoName,
                p.GithubInstallationId,
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (project is null)
        {
            return Result.Failure<List<GithubBranchListItemDto>>($"{NotFoundPrefix} project not found");
        }

        // -------- 2. Membership gate --------
        var isMember = await _db.WorkspaceMemberships
            .AsNoTracking()
            .AnyAsync(
                m => m.WorkspaceId == project.WorkspaceId && m.UserId == request.CallerUserId,
                cancellationToken);

        if (!isMember)
        {
            return Result.Failure<List<GithubBranchListItemDto>>($"{NotFoundPrefix} project not found");
        }

        // -------- 3. Resolve the installation's long-form GitHub id --------
        // The project row points at the local Guid (nullable — "detached" means
        // the installation was disconnected and the FK got SET NULL); the GitHub
        // API client wants the long-form id, so resolve it once. A detached
        // project, or one whose install row vanished under us, collapses to the
        // same 404 — the user can't do anything productive against GitHub here
        // until the project is reconnected.
        if (project.GithubInstallationId is not { } installationFk)
        {
            return Result.Failure<List<GithubBranchListItemDto>>(
                $"{NotFoundPrefix} project is detached — reconnect its GitHub installation first");
        }

        var installationLongId = await _db.GithubInstallations
            .AsNoTracking()
            .Where(i => i.Id == installationFk)
            .Select(i => (long?)i.InstallationId)
            .SingleOrDefaultAsync(cancellationToken);

        if (installationLongId is null)
        {
            return Result.Failure<List<GithubBranchListItemDto>>(
                $"{NotFoundPrefix} project's GitHub installation is no longer linked");
        }

        // -------- 4. Live GitHub fetch --------
        IReadOnlyList<GithubBranchDto> branches;
        string defaultBranch;
        try
        {
            var repoMeta = await _api.GetRepositoryAsync(
                installationLongId.Value, project.GithubRepoOwner, project.GithubRepoName, cancellationToken);
            defaultBranch = repoMeta.DefaultBranch ?? string.Empty;

            branches = await _api.ListRepositoryBranchesAsync(
                installationLongId.Value, project.GithubRepoOwner, project.GithubRepoName, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(
                ex,
                "GitHub API call failed listing branches for project {ProjectId} ({Owner}/{Repo})",
                request.ProjectId, project.GithubRepoOwner, project.GithubRepoName);
            return Result.Failure<List<GithubBranchListItemDto>>("Failed to list branches from GitHub");
        }

        // -------- 5. Load local system branches once + index by name --------
        // ProjectBranch has no soft-delete column (per the entity contract:
        // "Auditable but NOT soft-deletable"), so no DeletedAt filter is needed.
        var systemBranches = await _db.ProjectBranches
            .AsNoTracking()
            .Where(b => b.ProjectId == request.ProjectId)
            .Select(b => new { b.Id, b.Name })
            .ToListAsync(cancellationToken);

        // Ordinal — branch names are case-sensitive on GitHub. The CopyBranch
        // handler also uses Ordinal for the collision check; keep it consistent.
        var byName = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var b in systemBranches)
        {
            // Defensive: if two rows somehow share a name (shouldn't happen —
            // there's a uniqueness intent at the entity level), the first wins
            // and we log. Don't crash the list endpoint over a data integrity
            // hiccup the user can do nothing about.
            if (!byName.TryAdd(b.Name, b.Id))
            {
                _logger.LogWarning(
                    "ListProjectGithubBranches: duplicate ProjectBranch name {Name} for project {ProjectId}; using first ({Id}).",
                    b.Name, request.ProjectId, byName[b.Name]);
            }
        }

        // -------- 6. Fetch per-branch tip-commit metadata (best-effort) --------
        // GraphQL would give us one round-trip via `refs(...) { node.target.committedDate ... }`,
        // but the codebase has no GraphQL plumbing yet (no Octokit.GraphQL package, no schema
        // codegen). Adding it just for the freshness column is friction we don't need today —
        // so we fall back to batched REST. We fan out `GET /repos/{owner}/{repo}/commits/{sha}`
        // calls with a small concurrency cap so we don't burn through the installation token's
        // rate budget on repos with 100 branches. IGithubApiClient.GetCommitAsync soft-fails to
        // null on any non-success status, so a single bad commit can't sink the whole list.
        //
        // Concurrency cap: 10. GitHub's primary rate limit on installation tokens is 5k/hour,
        // and a 10-wide fan-out for the worst-case 100-branch repo is still only one second of
        // burst. The cap is the secondary-rate-limit safety belt — GitHub flags concurrent
        // bursts above ~100 in-flight calls per user; 10 is comfortably below that.
        const int maxConcurrency = 10;

        // Build the work list once: branch name -> tip sha. Skip rows with no commit pointer
        // (GitHub returns these for in-flight branch creates / empty repos).
        var work = branches
            .Where(b => !string.IsNullOrWhiteSpace(b.Commit?.Sha))
            .Select(b => (Name: b.Name, Sha: b.Commit!.Sha))
            .ToList();

        var commitByBranch = new Dictionary<string, GithubCommitDto?>(StringComparer.Ordinal);
        using (var gate = new SemaphoreSlim(maxConcurrency))
        {
            var tasks = work
                .Select(async pair =>
                {
                    await gate.WaitAsync(cancellationToken);
                    try
                    {
                        try
                        {
                            var commit = await _api.GetCommitAsync(
                                installationLongId.Value,
                                project.GithubRepoOwner,
                                project.GithubRepoName,
                                pair.Sha,
                                cancellationToken);
                            return (pair.Name, Commit: commit);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            // Swallow per-branch failures (HTTP, parse, transient). The branch
                            // row will just render with null commit fields — degraded display,
                            // not a 500. Log at warning so ops sees the failure rate trend.
                            _logger.LogWarning(
                                ex,
                                "ListProjectGithubBranches: commit lookup failed for {Owner}/{Repo}@{Branch} ({Sha}); leaving commit fields null.",
                                project.GithubRepoOwner, project.GithubRepoName, pair.Name, pair.Sha);
                            return (pair.Name, Commit: (GithubCommitDto?)null);
                        }
                    }
                    finally
                    {
                        gate.Release();
                    }
                })
                .ToList();

            var results = await Task.WhenAll(tasks);
            foreach (var (name, commit) in results)
            {
                // Defensive TryAdd — duplicate branch names from GitHub would mean a server
                // bug; the local-side duplicate path already logs above. Last-write-wins on
                // a collision is harmless: both rows point at the same SHA.
                commitByBranch[name] = commit;
            }
        }

        // -------- 7. Project --------
        var items = branches
            .Select(b =>
            {
                commitByBranch.TryGetValue(b.Name, out var commit);

                // Author display preference: the human "name" embedded in the git commit
                // object wins (matches what the user actually typed in `user.name`). Fall
                // back to the GitHub user login resolved from the email when the commit was
                // authored without a configured name (rare but happens with squash-merges
                // / web-UI commits).
                var author = commit?.Commit.Author?.Name;
                if (string.IsNullOrWhiteSpace(author))
                {
                    author = commit?.Author?.Login;
                }

                // Message headline: first line of the commit message. The list endpoint is
                // for UI surface area, not a full commit viewer — anything beyond the first
                // line would just be visually noisy in a row.
                string? message = null;
                var rawMessage = commit?.Commit.Message;
                if (!string.IsNullOrEmpty(rawMessage))
                {
                    var newlineIndex = rawMessage.IndexOf('\n');
                    message = newlineIndex >= 0 ? rawMessage[..newlineIndex] : rawMessage;
                    message = message.Trim();
                    if (string.IsNullOrEmpty(message))
                    {
                        message = null;
                    }
                }

                return new GithubBranchListItemDto(
                    Name: b.Name,
                    IsDefault: !string.IsNullOrEmpty(defaultBranch)
                        && string.Equals(b.Name, defaultBranch, StringComparison.Ordinal),
                    LinkedSystemBranchId: byName.TryGetValue(b.Name, out var sysId) ? sysId : null,
                    LastCommitAt: commit?.Commit.Author?.Date,
                    LastCommitAuthor: string.IsNullOrWhiteSpace(author) ? null : author,
                    LastCommitMessage: message);
            })
            .ToList();

        return Result.Success(items);
    }
}
