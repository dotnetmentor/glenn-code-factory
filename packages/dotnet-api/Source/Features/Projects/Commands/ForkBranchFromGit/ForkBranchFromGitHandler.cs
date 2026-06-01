using Hangfire;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Source.Features.Cloudflare.Commands;
using Source.Features.FlyManagement.Configuration;
using Source.Features.GitHub.Services;
using Source.Features.Projects.Models;
using Source.Features.RuntimeImages.Models;
using Source.Features.RuntimeLifecycle.Jobs;
using Source.Features.RuntimeLifecycle.Models;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.Projects.Commands.ForkBranchFromGit;

/// <summary>
/// Handles <see cref="ForkBranchFromGitCommand"/> — see the command summary for the
/// "fork from git ref" contract. The new <see cref="ProjectRuntime"/> is left in
/// <see cref="RuntimeState.Pending"/> with no <see cref="ProjectRuntime.FlyVolumeId"/>;
/// the recurring <c>RuntimeProvisionerJob</c> creates a fresh volume and the daemon's
/// bootstrap clones the new git ref into it on first start.
///
/// <para><b>Validation order.</b> Mirrors <c>CopyBranchHandler</c> + <c>CreateProjectHandler</c>:
/// <list type="number">
///   <item>Auth + body sanity.</item>
///   <item>Project + workspace membership (404 collapse).</item>
///   <item>Runtime provisioning preconditions (Active image, Fly settings).</item>
///   <item>Local-side name conflict — system branch with the requested new name.</item>
///   <item>GitHub-side validation — source git branch exists; new name not already taken on the remote.</item>
///   <item>Push the new ref. From here on, any failure runs compensations.</item>
///   <item>Insert DB rows + claim a preview subdomain in a single transaction.</item>
/// </list></para>
///
/// <para><b>Failure shape.</b> Sentinel-prefixed error codes the controller maps cleanly:
/// <list type="bullet">
///   <item><see cref="NotFoundPrefix"/> → 404 (project missing / no access).</item>
///   <item><see cref="SourceGitBranchNotFoundError"/> → 404 (the source git branch doesn't exist).</item>
///   <item><see cref="InvalidBranchNameError"/> → 400 (new branch name fails sanitisation).</item>
///   <item><see cref="NameConflictError"/> → 409 (new system-branch name OR new git ref already exists).</item>
///   <item><see cref="PoolEmptyError"/> → 409 (preview-subdomain pool exhausted).</item>
///   <item>everything else → 400 / 503.</item>
/// </list></para>
/// </summary>
public sealed class ForkBranchFromGitHandler
    : ICommandHandler<ForkBranchFromGitCommand, Result<ForkBranchFromGitResult>>
{
    /// <summary>Sentinel mapped to 404 — project missing / no access.</summary>
    public const string NotFoundPrefix = "not-found:";

    /// <summary>Sentinel error code: the source git branch does not exist on the GitHub repo.</summary>
    public const string SourceGitBranchNotFoundError = "source_git_branch_not_found";

    /// <summary>Sentinel error code: the requested new branch name fails sanitisation.</summary>
    public const string InvalidBranchNameError = "invalid_branch_name";

    /// <summary>Sentinel error code: the new branch name conflicts with an existing system branch or remote ref.</summary>
    public const string NameConflictError = "BranchAlreadyLinked";

    /// <summary>Sentinel error code mirroring <c>CreateProjectHandler.PoolEmptyError</c>.</summary>
    public const string PoolEmptyError = "pool_empty";

    /// <summary>
    /// Sentinel mirroring <see cref="CopyBranch.CopyBranchHandler.CatalogSpecNotFoundError"/>.
    /// The caller passed a <c>CatalogSpecId</c> that doesn't resolve, or whose
    /// row belongs to a different workspace than the project. Controller maps
    /// to 404 — existence-safe so a cross-workspace probe can't distinguish.
    /// </summary>
    public const string CatalogSpecNotFoundError = "catalog_spec_not_found";

    private readonly ApplicationDbContext _db;
    private readonly IGithubApiClient _github;
    private readonly IFlyOptionsAccessor _flyOptions;
    private readonly IMediator _mediator;
    private readonly IBackgroundJobClient _backgroundJobs;
    private readonly ILogger<ForkBranchFromGitHandler> _logger;

    public ForkBranchFromGitHandler(
        ApplicationDbContext db,
        IGithubApiClient github,
        IFlyOptionsAccessor flyOptions,
        IMediator mediator,
        IBackgroundJobClient backgroundJobs,
        ILogger<ForkBranchFromGitHandler> logger)
    {
        _db = db;
        _github = github;
        _flyOptions = flyOptions;
        _mediator = mediator;
        _backgroundJobs = backgroundJobs;
        _logger = logger;
    }

    public async Task<Result<ForkBranchFromGitResult>> Handle(
        ForkBranchFromGitCommand request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.CallerUserId))
        {
            return Result.Failure<ForkBranchFromGitResult>($"{NotFoundPrefix} unauthenticated");
        }
        if (string.IsNullOrWhiteSpace(request.SourceGitBranchName))
        {
            return Result.Failure<ForkBranchFromGitResult>("Source git branch name is required");
        }
        if (string.IsNullOrWhiteSpace(request.NewBranchName))
        {
            return Result.Failure<ForkBranchFromGitResult>(InvalidBranchNameError);
        }

        var sourceBranch = request.SourceGitBranchName.Trim();
        var newBranch = request.NewBranchName.Trim();

        // Cheap name sanitisation — same intent as the git refname rules.
        // We only enforce the basics that would crash the GitHub API call: no
        // whitespace, no leading "/", no double slashes, no ".." segments, and
        // no chars from the ASCII-control / ~^:?*[\ set. Anything stricter is
        // policy and would belong on the Project entity.
        if (!IsLikelyValidRefName(newBranch))
        {
            return Result.Failure<ForkBranchFromGitResult>(InvalidBranchNameError);
        }
        if (string.Equals(sourceBranch, newBranch, StringComparison.Ordinal))
        {
            // No-op fork onto self would attempt to create a ref pointing at
            // the same name — collapse to the same name-conflict failure path.
            return Result.Failure<ForkBranchFromGitResult>(NameConflictError);
        }

        // -------- 1. Project --------
        var project = await _db.Projects
            .AsNoTracking()
            .Where(p => p.Id == request.ProjectId)
            .Select(p => new
            {
                p.Id,
                p.WorkspaceId,
                p.GithubRepoOwner,
                p.GithubRepoName,
                p.GithubInstallationId,
                // Per-project runtime spec — snapshotted onto the new
                // ProjectRuntime row so a project default change between
                // fork calls doesn't retro-resize live runtimes.
                p.RuntimeCpuKind,
                p.RuntimeCpus,
                p.RuntimeMemoryMb,
                p.RuntimeVolumeSizeGb,
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (project is null)
        {
            return Result.Failure<ForkBranchFromGitResult>($"{NotFoundPrefix} project not found");
        }

        // -------- 2. Membership gate --------
        var isMember = await _db.WorkspaceMemberships
            .AsNoTracking()
            .AnyAsync(
                m => m.WorkspaceId == project.WorkspaceId && m.UserId == request.CallerUserId,
                cancellationToken);

        if (!isMember)
        {
            return Result.Failure<ForkBranchFromGitResult>($"{NotFoundPrefix} project not found");
        }

        // -------- 3. Pre-flight: runtime provisioning preconditions --------
        var hasActiveImage = await _db.RuntimeImages
            .AnyAsync(i => i.Status == RuntimeImageStatus.Active, cancellationToken);
        if (!hasActiveImage)
        {
            return Result.Failure<ForkBranchFromGitResult>(
                "No active runtime image is registered. Ask an admin to activate one in Super Admin → Runtime Images.");
        }

        var fly = _flyOptions.Current;
        if (string.IsNullOrWhiteSpace(fly.ApiToken) ||
            string.IsNullOrWhiteSpace(fly.OrgSlug) ||
            string.IsNullOrWhiteSpace(fly.AppName))
        {
            return Result.Failure<ForkBranchFromGitResult>(
                "Fly settings are incomplete. Configure them in Super Admin → System Settings.");
        }

        // -------- 4. Local name collision (cheap) --------
        var existingByName = await _db.ProjectBranches
            .AsNoTracking()
            .AnyAsync(
                b => b.ProjectId == project.Id && b.Name == newBranch,
                cancellationToken);

        if (existingByName)
        {
            return Result.Failure<ForkBranchFromGitResult>(NameConflictError);
        }

        // -------- 5. Installation long-id --------
        // Detached projects (FK = NULL after the workspace disconnected the
        // installation) can't fork — there's no credential to drive the
        // CreateRef call against GitHub. Surface a friendly reconnect hint.
        if (project.GithubInstallationId is not { } installationFk)
        {
            return Result.Failure<ForkBranchFromGitResult>(
                $"{NotFoundPrefix} project is detached — reconnect its GitHub installation first");
        }

        var installationLongId = await _db.GithubInstallations
            .AsNoTracking()
            .Where(i => i.Id == installationFk)
            .Select(i => (long?)i.InstallationId)
            .SingleOrDefaultAsync(cancellationToken);

        if (installationLongId is null)
        {
            return Result.Failure<ForkBranchFromGitResult>(
                $"{NotFoundPrefix} project's GitHub installation is no longer linked");
        }

        // -------- 6. Resolve source git branch HEAD SHA --------
        string sourceSha;
        try
        {
            sourceSha = await _github.GetBranchTipShaAsync(
                project.GithubRepoOwner,
                project.GithubRepoName,
                sourceBranch,
                installationLongId.Value,
                cancellationToken);
        }
        catch (SourceBranchNotFoundException)
        {
            return Result.Failure<ForkBranchFromGitResult>(SourceGitBranchNotFoundError);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(
                ex,
                "ForkBranchFromGit: source branch lookup failed for project {ProjectId} branch {Branch}",
                request.ProjectId, sourceBranch);
            return Result.Failure<ForkBranchFromGitResult>("Failed to read source branch from GitHub");
        }

        // -------- 6a. Spec inheritance is now via the project --------
        // Per `project-level-runtime-spec`, runtime spec lives on Project.
        // The new branch's runtime inherits project.Spec on its first
        // bootstrap. The CatalogSpecId / ForceBlankSpec request fields are
        // accepted by the controller for back-compat but no longer mutate
        // anything — there's no per-runtime Spec column to set, and
        // overwriting Project.Spec from a branch-fork would silently mutate
        // every other branch (the surprise the spec is designed to avoid).
        // Per-branch spec override is a noted future extension.

        // -------- 7. Push the new ref --------
        // From this point on, every failure must run compensations.
        var compensations = new List<(string Name, Func<Task> Action)>();
        try
        {
            await _github.CreateBranchRefAsync(
                project.GithubRepoOwner,
                project.GithubRepoName,
                newBranch,
                sourceSha,
                installationLongId.Value,
                cancellationToken);
        }
        catch (BranchAlreadyExistsException)
        {
            return Result.Failure<ForkBranchFromGitResult>(NameConflictError);
        }
        catch (GitHubBranchCreationForbiddenException)
        {
            return Result.Failure<ForkBranchFromGitResult>(
                "GitHub denied creating the branch — check branch protection rules and app permissions.");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(
                ex,
                "ForkBranchFromGit: push of new ref failed for project {ProjectId} new branch {NewBranch}",
                request.ProjectId, newBranch);
            return Result.Failure<ForkBranchFromGitResult>("Failed to create the new branch on GitHub");
        }

        compensations.Add(("DeleteGithubRef", async () =>
        {
            await _github.DeleteBranchRefAsync(
                project.GithubRepoOwner,
                project.GithubRepoName,
                newBranch,
                installationLongId.Value,
                CancellationToken.None);
        }));

        // -------- 8. Build new branch + Pending runtime --------
        var branchRow = new ProjectBranch
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Name = newBranch,
            IsDefault = false,
        };

        var runtimeRow = new ProjectRuntime
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            BranchId = branchRow.Id,
            TenantId = project.WorkspaceId,
            State = RuntimeState.Pending,
            StateChangedAt = DateTime.UtcNow,
            Region = "arn",
            // Runtime SERVICES spec lives on Project, not ProjectRuntime —
            // per `project-level-runtime-spec`. The daemon's GetBootstrap
            // call reads project.Spec on first cold-boot and converges.
            // Runtime MACHINE spec (CPU/RAM/disk) — snapshot the project's
            // current default. See Project.SetRuntimeSpec / Project.RuntimeCpu*.
            CpuKind = project.RuntimeCpuKind,
            Cpus = project.RuntimeCpus,
            MemoryMb = project.RuntimeMemoryMb,
            VolumeSizeGb = project.RuntimeVolumeSizeGb,
        };

        _db.ProjectBranches.Add(branchRow);
        _db.ProjectRuntimes.Add(runtimeRow);

        // -------- 9. Atomic subdomain claim + DB write --------
        const string outcomePoolEmpty = "pool_empty";
        const string outcomeDbUpdate = "db_update";
        const string outcomeOk = "ok";

        var strategy = _db.Database.CreateExecutionStrategy();
        var (outcome, errorDetail) = await strategy.ExecuteAsync<(string Outcome, string? Error)>(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);

            var assignResult = await _mediator.Send(
                new AssignSubdomainToBranchCommand(branchRow.Id),
                cancellationToken);

            if (!assignResult.IsSuccess)
            {
                await tx.RollbackAsync(cancellationToken);
                return (outcomePoolEmpty, assignResult.Error ?? PoolEmptyError);
            }

            try
            {
                await _db.SaveChangesAsync(cancellationToken);
                await tx.CommitAsync(cancellationToken);
                return (outcomeOk, null);
            }
            catch (DbUpdateException ex)
            {
                await tx.RollbackAsync(cancellationToken);
                _logger.LogError(
                    ex,
                    "ForkBranchFromGit: DB write failed for project {ProjectId} new branch {NewBranch}",
                    request.ProjectId, newBranch);
                return (outcomeDbUpdate, null);
            }
        });

        if (outcome == outcomePoolEmpty)
        {
            _logger.LogWarning(
                "ForkBranchFromGit: subdomain pool claim failed for project {ProjectId} new branch {NewBranch}: {Error}; running compensations.",
                request.ProjectId, newBranch, errorDetail);
            await CompensateAsync(compensations);
            return Result.Failure<ForkBranchFromGitResult>(errorDetail ?? PoolEmptyError);
        }
        if (outcome == outcomeDbUpdate)
        {
            _logger.LogWarning(
                "ForkBranchFromGit: running compensations after DB write failure for project {ProjectId} new branch {NewBranch}",
                request.ProjectId, newBranch);
            await CompensateAsync(compensations);
            return Result.Failure<ForkBranchFromGitResult>(NameConflictError);
        }

        _logger.LogInformation(
            "ForkBranchFromGit: project {ProjectId} forked {Source} -> {New} (branch {BranchId} runtime {RuntimeId}) by {UserId}.",
            request.ProjectId, sourceBranch, newBranch, branchRow.Id, runtimeRow.Id, request.CallerUserId);

        // Kick the provisioner immediately for the newly-Pending runtime so the
        // fork starts booting in seconds rather than waiting up to a minute for
        // the recurring sweep. Outside the execution strategy on purpose — we
        // only enqueue once a real commit landed.
        _backgroundJobs.Enqueue<RuntimeProvisionerJob>(
            j => j.ProvisionOne(runtimeRow.Id, JobCancellationToken.Null));

        return Result.Success(new ForkBranchFromGitResult(
            BranchId: branchRow.Id,
            RuntimeId: runtimeRow.Id,
            NewBranchName: branchRow.Name,
            State: runtimeRow.State));
    }

    /// <summary>
    /// Pragmatic git refname sanity check. Rejects names that would crash the
    /// GitHub API call ("refs/heads/<name>") — empty, leading "/", "..", control
    /// characters, and the explicit illegal-char set from git's own rules. Not
    /// trying to be a full git refname validator; the remote will reject
    /// edge-cases we miss with a 422 the caller surfaces as a 409 name conflict.
    /// </summary>
    private static bool IsLikelyValidRefName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (name.Length > 250) return false;                     // git's practical refname limit
        if (name.StartsWith('/') || name.EndsWith('/')) return false;
        if (name.StartsWith('.') || name.EndsWith('.')) return false;
        if (name.EndsWith(".lock", StringComparison.Ordinal)) return false;
        if (name.Contains("//", StringComparison.Ordinal)) return false;
        if (name.Contains("..", StringComparison.Ordinal)) return false;
        if (name.Contains("@{", StringComparison.Ordinal)) return false;
        foreach (var ch in name)
        {
            if (ch < 0x20 || ch == 0x7f) return false;            // ASCII control
            if (ch == ' ' || ch == '~' || ch == '^' || ch == ':' ||
                ch == '?' || ch == '*' || ch == '[' || ch == '\\')
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Run accumulated rollback actions in reverse order. Each compensation
    /// failure is logged at error level but never re-thrown — one failing
    /// rollback should not prevent the rest from running. Mirrors
    /// <c>CopyBranchHandler.CompensateAsync</c>.
    /// </summary>
    private async Task CompensateAsync(List<(string Name, Func<Task> Action)> compensations)
    {
        for (var i = compensations.Count - 1; i >= 0; i--)
        {
            var (name, action) = compensations[i];
            try
            {
                await action();
                _logger.LogInformation("ForkBranchFromGit compensation step {Step} ran successfully.", name);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "ForkBranchFromGit compensation step {Step} FAILED. Manual cleanup may be required.",
                    name);
            }
        }
    }
}
