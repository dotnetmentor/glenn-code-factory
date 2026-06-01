using Hangfire;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Source.Features.Cloudflare.Commands;
using Source.Features.GitHub.Services;
using Source.Features.Projects.Models;
using Source.Features.RuntimeImages.Models;
using Source.Features.RuntimeLifecycle.Jobs;
using Source.Features.RuntimeLifecycle.Models;
using Source.Features.FlyManagement.Configuration;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.Projects.Commands.AttachGitBranch;

/// <summary>
/// Handles <see cref="AttachGitBranchCommand"/> — see the command summary for the
/// "slow-path branch attach" contract. The new <see cref="ProjectRuntime"/> is left in
/// <see cref="RuntimeState.Pending"/> with no <see cref="ProjectRuntime.FlyVolumeId"/>;
/// the recurring <c>RuntimeProvisionerJob</c> creates a fresh volume and the daemon
/// bootstrap clones the git branch into it. Same hand-off shape as
/// <c>CreateProjectHandler</c>.
///
/// <para><b>Differences vs CopyBranchHandler.</b> No GitHub ref is created (the source
/// ref already exists by construction) and no Fly volume is forked (there's no source
/// volume to fork from — we clone fresh from git). The validation order, transaction
/// shape and pool-claim flow all mirror Copy Branch / Create Project so the three entry
/// points behave identically from the frontend's perspective.</para>
///
/// <para><b>Failure shape.</b> Sentinel-prefixed errors so the controller can map cleanly:
/// <list type="bullet">
///   <item><see cref="NotFoundPrefix"/> → 404 (project missing or caller not a member).</item>
///   <item><see cref="GitBranchNotFoundError"/> → 404 (git branch doesn't exist on the repo).</item>
///   <item><see cref="AlreadyLinkedError"/> → 409 (a non-deleted ProjectBranch with this name already exists).</item>
///   <item><see cref="PoolEmptyError"/> → 409 (preview-subdomain pool exhausted).</item>
///   <item>everything else → 400 / 503 ("nothing was changed").</item>
/// </list></para>
/// </summary>
public sealed class AttachGitBranchHandler
    : ICommandHandler<AttachGitBranchCommand, Result<AttachGitBranchResult>>
{
    /// <summary>Sentinel mapped to 404 by the controller — project missing / no access.</summary>
    public const string NotFoundPrefix = "not-found:";

    /// <summary>Sentinel error code: the requested git branch does not exist on the GitHub repo.</summary>
    public const string GitBranchNotFoundError = "git_branch_not_found";

    /// <summary>Sentinel error code: a live <see cref="ProjectBranch"/> with this name already exists for the project.</summary>
    public const string AlreadyLinkedError = "BranchAlreadyLinked";

    /// <summary>Sentinel error code mirroring <c>CreateProjectHandler.PoolEmptyError</c>.</summary>
    public const string PoolEmptyError = "pool_empty";

    private readonly ApplicationDbContext _db;
    private readonly IGithubApiClient _github;
    private readonly IFlyOptionsAccessor _flyOptions;
    private readonly IMediator _mediator;
    private readonly IBackgroundJobClient _backgroundJobs;
    private readonly ILogger<AttachGitBranchHandler> _logger;

    public AttachGitBranchHandler(
        ApplicationDbContext db,
        IGithubApiClient github,
        IFlyOptionsAccessor flyOptions,
        IMediator mediator,
        IBackgroundJobClient backgroundJobs,
        ILogger<AttachGitBranchHandler> logger)
    {
        _db = db;
        _github = github;
        _flyOptions = flyOptions;
        _mediator = mediator;
        _backgroundJobs = backgroundJobs;
        _logger = logger;
    }

    public async Task<Result<AttachGitBranchResult>> Handle(
        AttachGitBranchCommand request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.CallerUserId))
        {
            return Result.Failure<AttachGitBranchResult>($"{NotFoundPrefix} unauthenticated");
        }
        if (string.IsNullOrWhiteSpace(request.GitBranchName))
        {
            return Result.Failure<AttachGitBranchResult>("Git branch name is required");
        }

        var gitBranchName = request.GitBranchName.Trim();

        // -------- 1. Resolve project + workspace + repo coordinates --------
        // Soft-deleted projects are excluded by the global query filter.
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
                // attach calls doesn't retro-resize live runtimes.
                p.RuntimeCpuKind,
                p.RuntimeCpus,
                p.RuntimeMemoryMb,
                p.RuntimeVolumeSizeGb,
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (project is null)
        {
            return Result.Failure<AttachGitBranchResult>($"{NotFoundPrefix} project not found");
        }

        // -------- 2. Membership gate --------
        var isMember = await _db.WorkspaceMemberships
            .AsNoTracking()
            .AnyAsync(
                m => m.WorkspaceId == project.WorkspaceId && m.UserId == request.CallerUserId,
                cancellationToken);

        if (!isMember)
        {
            // Same 404 collapse — don't leak existence.
            return Result.Failure<AttachGitBranchResult>($"{NotFoundPrefix} project not found");
        }

        // -------- 3. Pre-flight: runtime provisioning preconditions --------
        // Fail fast if no Active runtime image or Fly settings are misconfigured —
        // same belt-and-braces guards as CreateProjectHandler so a misconfigured
        // platform can never leave us with a Pending runtime that never advances.
        var hasActiveImage = await _db.RuntimeImages
            .AnyAsync(i => i.Status == RuntimeImageStatus.Active, cancellationToken);
        if (!hasActiveImage)
        {
            return Result.Failure<AttachGitBranchResult>(
                "No active runtime image is registered. Ask an admin to activate one in Super Admin → Runtime Images.");
        }

        var fly = _flyOptions.Current;
        if (string.IsNullOrWhiteSpace(fly.ApiToken) ||
            string.IsNullOrWhiteSpace(fly.OrgSlug) ||
            string.IsNullOrWhiteSpace(fly.AppName))
        {
            return Result.Failure<AttachGitBranchResult>(
                "Fly settings are incomplete. Configure them in Super Admin → System Settings.");
        }

        // -------- 4. Reject duplicate system branch BEFORE touching GitHub --------
        // Cheaper than the network round-trip and removes a Phase A failure
        // mode from the GitHub-lookup path. ProjectBranch has no soft-delete
        // column ("Auditable but NOT soft-deletable" per the entity comment)
        // so a simple Any check is the live state.
        var alreadyLinked = await _db.ProjectBranches
            .AsNoTracking()
            .AnyAsync(
                b => b.ProjectId == project.Id && b.Name == gitBranchName,
                cancellationToken);

        if (alreadyLinked)
        {
            return Result.Failure<AttachGitBranchResult>(AlreadyLinkedError);
        }

        // -------- 5. Resolve installation long-id and verify the git branch exists --------
        // Detached projects (FK = NULL after the workspace disconnected the
        // installation) can't attach a fresh branch — bail with a clear message
        // so the UI can prompt the user to reconnect first.
        if (project.GithubInstallationId is not { } installationFk)
        {
            return Result.Failure<AttachGitBranchResult>(
                $"{NotFoundPrefix} project is detached — reconnect its GitHub installation first");
        }

        var installationLongId = await _db.GithubInstallations
            .AsNoTracking()
            .Where(i => i.Id == installationFk)
            .Select(i => (long?)i.InstallationId)
            .SingleOrDefaultAsync(cancellationToken);

        if (installationLongId is null)
        {
            return Result.Failure<AttachGitBranchResult>(
                $"{NotFoundPrefix} project's GitHub installation is no longer linked");
        }

        try
        {
            // We don't actually need the SHA here (the daemon clones by branch
            // name on bootstrap), but the call doubles as a fast "does this
            // branch actually exist on the remote?" gate before we create any
            // DB rows or claim any pool resources.
            _ = await _github.GetBranchTipShaAsync(
                project.GithubRepoOwner,
                project.GithubRepoName,
                gitBranchName,
                installationLongId.Value,
                cancellationToken);
        }
        catch (SourceBranchNotFoundException)
        {
            return Result.Failure<AttachGitBranchResult>(GitBranchNotFoundError);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(
                ex,
                "AttachGitBranch: GitHub branch lookup failed for project {ProjectId} branch {Branch}",
                request.ProjectId, gitBranchName);
            return Result.Failure<AttachGitBranchResult>("Failed to verify branch on GitHub");
        }

        // -------- 6. Build new branch + Pending runtime --------
        // FlyVolumeId is intentionally null — the provisioner job will create a
        // fresh volume and the daemon's bootstrap will clone the git branch into
        // it on first start. Same shape as the CreateProject onboarding path.
        var newBranch = new ProjectBranch
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Name = gitBranchName,
            IsDefault = false,
        };

        var newRuntime = new ProjectRuntime
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            BranchId = newBranch.Id,
            // TenantId carries the workspace boundary all the way to the daemon's
            // rt_tenant JWT claim — critical for tenancy isolation. Matches the
            // CreateProject default.
            TenantId = project.WorkspaceId,
            State = RuntimeState.Pending,
            StateChangedAt = DateTime.UtcNow,
            Region = "arn",
            // Snapshot the project's runtime spec (CPU/RAM/disk) — see
            // Project.SetRuntimeSpec / Project.RuntimeCpu*.
            CpuKind = project.RuntimeCpuKind,
            Cpus = project.RuntimeCpus,
            MemoryMb = project.RuntimeMemoryMb,
            VolumeSizeGb = project.RuntimeVolumeSizeGb,
        };

        _db.ProjectBranches.Add(newBranch);
        _db.ProjectRuntimes.Add(newRuntime);

        // -------- 7. Atomic subdomain claim + DB write --------
        // Same execution-strategy + ambient-tx dance as CreateProjectHandler.
        // The retrying execution strategy forbids user-initiated transactions
        // outside its ExecuteAsync wrapper, so we route the entire tx body
        // through CreateExecutionStrategy().ExecuteAsync.
        const string outcomePoolEmpty = "pool_empty";
        const string outcomeDbUpdate = "db_update";
        const string outcomeOk = "ok";

        var strategy = _db.Database.CreateExecutionStrategy();
        var (outcome, errorDetail) = await strategy.ExecuteAsync<(string Outcome, string? Error)>(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);

            var assignResult = await _mediator.Send(
                new AssignSubdomainToBranchCommand(newBranch.Id),
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
                    "AttachGitBranch: DB write failed for project {ProjectId} branch {Branch}",
                    request.ProjectId, gitBranchName);
                return (outcomeDbUpdate, null);
            }
        });

        if (outcome == outcomePoolEmpty)
        {
            _logger.LogWarning(
                "AttachGitBranch: subdomain pool claim failed for project {ProjectId} branch {Branch}: {Error}",
                request.ProjectId, gitBranchName, errorDetail);
            return Result.Failure<AttachGitBranchResult>(errorDetail ?? PoolEmptyError);
        }
        if (outcome == outcomeDbUpdate)
        {
            // Likely a uniqueness race (two concurrent attaches for the same
            // branch name landing the same instant). Surface as the same
            // 409 "already linked" code the up-front check uses so the frontend
            // can treat both paths identically.
            return Result.Failure<AttachGitBranchResult>(AlreadyLinkedError);
        }

        // Kick the provisioner immediately for the newly-Pending runtime so the
        // branch starts booting in seconds rather than waiting up to a minute
        // for the recurring sweep. Outside the execution strategy on purpose —
        // we only enqueue once a real commit landed.
        _backgroundJobs.Enqueue<RuntimeProvisionerJob>(
            j => j.ProvisionOne(newRuntime.Id, JobCancellationToken.Null));

        _logger.LogInformation(
            "AttachGitBranch: project {ProjectId} branch {Branch} attached as system branch {BranchId} (runtime {RuntimeId}) by {UserId}.",
            request.ProjectId, gitBranchName, newBranch.Id, newRuntime.Id, request.CallerUserId);

        return Result.Success(new AttachGitBranchResult(
            BranchId: newBranch.Id,
            RuntimeId: newRuntime.Id,
            State: newRuntime.State));
    }
}
