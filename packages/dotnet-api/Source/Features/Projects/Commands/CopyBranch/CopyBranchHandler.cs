using Hangfire;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Source.Features.Cloudflare.Commands;
using Source.Features.BoxManagement;
using Source.Features.BoxManagement.Models;
using Source.Features.RuntimeLifecycle.Provisioning;
using Source.Features.GitHub.Services;
using Source.Features.Projects.Events;
using Source.Features.Projects.Models;
using Source.Features.RuntimeLifecycle.Jobs;
using Source.Features.RuntimeLifecycle.Models;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.Projects.Commands.CopyBranch;

/// <summary>
/// Orchestrates <see cref="CopyBranchCommand"/> end-to-end: validate, reserve a
/// name, pre-flight the source branch on GitHub, create the new ref, fork the
/// source runtime's BOX (disk-level clone), then commit a new <see cref="ProjectBranch"/> +
/// <see cref="ProjectRuntime"/> in a single <c>SaveChangesAsync</c>. The
/// recurring <c>RuntimeProvisionerJob</c> picks the fresh Pending runtime up
/// and walks it to Online — same hand-off shape as <c>CreateProjectHandler</c>.
///
/// <para><b>Compensation.</b> Each side-effecting step (GitHub ref, box fork,
/// DB write) registers a rollback action when it succeeds. If a later step
/// throws, accumulated rollbacks run in reverse order: delete the forked
/// box, then delete the orphan GitHub ref, so the user sees "nothing was
/// changed" with no leftovers. Compensation failures are logged at error level
/// — we do NOT swallow them silently. Ops needs the trail to clean up the rare
/// case where compensation itself fails (e.g. a Box transient 5xx during
/// rollback); leaving a synthetic Failed BoxOperation row in the audit table
/// is the right escape hatch for that.</para>

/// <para><b>Fork freshness.</b> Box forks always take the source's LATEST
/// SNAPSHOT, and snapshots are taken on stop — so a running source box is
/// briefly stopped to snapshot its current disk, forked, then resumed. The
/// source branch sees a few seconds of pause; in exchange the copy carries
/// the source's disk exactly as it is now, not as it was at the last idle
/// suspend. (Two machine starts against the account budget: fork + resume.)</para>
///
/// <para><b>Error shape.</b> Privilege / existence failures use the
/// <see cref="NotFoundPrefix"/> and <see cref="ForbiddenPrefix"/> sentinels so
/// the controller can map them to 404 / 403 without leaking detail. Validation
/// and provider failures bubble up as bare user-readable strings the
/// controller surfaces verbatim in the 400 body.</para>
/// </summary>
public sealed class CopyBranchHandler : ICommandHandler<CopyBranchCommand, Result<CopyBranchResult>>
{
    /// <summary>Sentinel mapped to 404 by the controller — source missing / no access.</summary>
    public const string NotFoundPrefix = "not-found:";

    /// <summary>Sentinel mapped to 403 by the controller — caller is not a workspace member.</summary>
    public const string ForbiddenPrefix = "forbidden:";

    /// <summary>Sentinel mapped to 409 by the controller — source branch hasn't been pushed to GitHub yet.</summary>
    public const string SourceNotPushedPrefix = "source-not-pushed:";

    /// <summary>Sentinel mapped to 422 by the controller — requested branch name conflicts with an existing branch.</summary>
    public const string NameConflictPrefix = "name-conflict:";

    /// <summary>
    /// Sentinel error code returned when the caller passed a <c>CatalogSpecId</c>
    /// that doesn't resolve to a row, or whose row belongs to a different
    /// workspace than the source branch's project. Controller maps to 404 —
    /// existence-safe so a cross-workspace probe can't differentiate "doesn't
    /// exist" from "belongs to someone else".
    /// </summary>
    public const string CatalogSpecNotFoundError = "catalog_spec_not_found";

    /// <summary>
    /// Sentinel error string returned verbatim when the preview-subdomain pool
    /// is exhausted at branch-claim time. Controller maps to HTTP 409 with
    /// body <c>{ "error": "pool_empty" }</c> — same shape as the create-project
    /// path so the frontend can surface a single recovery message regardless
    /// of which entry point triggered the failure.
    /// </summary>
    public const string PoolEmptyError = "pool_empty";

    /// <summary>
    /// Hard cap on the auto-suffix search. <c>{source}-copy-100</c> is far
    /// beyond any realistic "I just kept clicking copy" pattern; once we hit
    /// the cap we surface a clean error rather than spin forever or wedge the
    /// DB with a name we picked at random.
    /// </summary>
    private const int MaxAutoSuffixAttempts = 100;

    private readonly ApplicationDbContext _db;
    private readonly BoxClient _box;
    private readonly IGithubApiClient _github;
    private readonly IMediator _mediator;
    private readonly IBackgroundJobClient _backgroundJobs;
    private readonly ILogger<CopyBranchHandler> _logger;

    public CopyBranchHandler(
        ApplicationDbContext db,
        BoxClient box,
        IGithubApiClient github,
        IMediator mediator,
        IBackgroundJobClient backgroundJobs,
        ILogger<CopyBranchHandler> logger)
    {
        _db = db;
        _box = box;
        _github = github;
        _mediator = mediator;
        _backgroundJobs = backgroundJobs;
        _logger = logger;
    }

    public async Task<Result<CopyBranchResult>> Handle(CopyBranchCommand request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.CallerUserId))
        {
            return Result.Failure<CopyBranchResult>($"{NotFoundPrefix} unauthenticated");
        }

        // -------- 1. Load source state --------
        // Pull the source branch with its project + installation + the pinned
        // runtime in one go. We need:
        //   - the project for tenancy + repo coordinates,
        //   - the installation for the long-form GitHub installation id,
        //   - the runtime for the source box / region / size to fork.
        var source = await _db.ProjectBranches
            .Include(b => b.Project)
                .ThenInclude(p => p.GithubInstallation)
            .Include(b => b.Runtimes)
            .FirstOrDefaultAsync(b => b.Id == request.SourceBranchId, cancellationToken);

        if (source is null)
        {
            return Result.Failure<CopyBranchResult>($"{NotFoundPrefix} Source branch not found");
        }

        // A branch always has at least one runtime (created alongside it); pick
        // the most recent one if there are multiple (future-proofing — today
        // it's 1:1). If none exists we treat the source as not copyable yet —
        // there's literally nothing to fork.
        var sourceRuntime = source.Runtimes
            .OrderByDescending(r => r.CreatedAt)
            .FirstOrDefault();

        if (sourceRuntime is null || string.IsNullOrWhiteSpace(sourceRuntime.BoxId))
        {
            return Result.Failure<CopyBranchResult>($"{NotFoundPrefix} Source branch has no provisioned runtime to fork");
        }

        // -------- 2. Authorize — caller must be a workspace member --------
        // Same shape as CreateProjectHandler: membership check on the project's
        // workspace, with a forbidden sentinel that the controller maps to 403.
        var isMember = await _db.WorkspaceMemberships
            .AsNoTracking()
            .AnyAsync(
                m => m.WorkspaceId == source.Project.WorkspaceId && m.UserId == request.CallerUserId,
                cancellationToken);

        if (!isMember)
        {
            return Result.Failure<CopyBranchResult>(
                $"{ForbiddenPrefix} caller is not a member of the project's workspace");
        }

        // -------- 3. Resolve the new branch name --------
        // Either honour the caller's explicit choice (and reject collisions) or
        // auto-suffix from {source.Name}-copy. We load every existing branch
        // name for this project up-front so the auto-suffix search is a single
        // LINQ pass, not N+1 round trips.
        var existingNames = await _db.ProjectBranches
            .AsNoTracking()
            .Where(b => b.ProjectId == source.ProjectId)
            .Select(b => b.Name)
            .ToListAsync(cancellationToken);
        var existingNamesSet = new HashSet<string>(existingNames, StringComparer.Ordinal);

        string newBranchName;
        if (!string.IsNullOrWhiteSpace(request.NewBranchName))
        {
            newBranchName = request.NewBranchName.Trim();
            if (existingNamesSet.Contains(newBranchName))
            {
                return Result.Failure<CopyBranchResult>(
                    $"{NameConflictPrefix} A branch with that name already exists.");
            }
        }
        else
        {
            var resolved = ResolveAutoSuffixName(source.Name, existingNamesSet);
            if (resolved is null)
            {
                return Result.Failure<CopyBranchResult>(
                    $"Couldn't find an unused -copy suffix after {MaxAutoSuffixAttempts} tries. Pick a name explicitly.");
            }
            newBranchName = resolved;
        }

        // -------- 4. Pre-flight: source branch must exist on GitHub --------
        // We do this BEFORE forking the box so a "didn't push yet" user
        // sees an instant, actionable error with zero billing impact. The
        // returned SHA is what we'll point the new GitHub ref at in step 5.
        // A detached project (installation disconnected from the workspace,
        // FK SET NULL) has no installation to mint tokens against — bail with
        // a friendly reconnect hint before we spend any time / Box starts.
        var installation = source.Project.GithubInstallation;
        if (installation is null)
        {
            return Result.Failure<CopyBranchResult>(
                $"{NotFoundPrefix} project is detached — reconnect its GitHub installation first");
        }
        var owner = source.Project.GithubRepoOwner;
        var repo = source.Project.GithubRepoName;

        string sourceTipSha;
        try
        {
            sourceTipSha = await _github.GetBranchTipShaAsync(
                owner, repo, source.Name, installation.InstallationId, cancellationToken);
        }
        catch (SourceBranchNotFoundException)
        {
            return Result.Failure<CopyBranchResult>(
                $"{SourceNotPushedPrefix} The source branch hasn't been pushed to GitHub yet. Push it first, then try again.");
        }

        // Compensation stack. Each entry is "what to do if a LATER step fails."
        // We push after a step succeeds and pop in reverse on rollback. Using
        // a List<Func<...>> instead of e.g. Stack<> just so we can log the
        // sequence clearly when it runs.
        var compensations = new List<(string Name, Func<Task> Action)>();

        // -------- 5. Create the new GitHub ref --------
        // From this point on, every failure must run the compensation stack.
        try
        {
            await _github.CreateBranchRefAsync(
                owner, repo, newBranchName, sourceTipSha, installation.InstallationId, cancellationToken);
        }
        catch (BranchAlreadyExistsException)
        {
            return Result.Failure<CopyBranchResult>(
                $"{NameConflictPrefix} That branch already exists on GitHub. Pick a different name.");
        }
        catch (GitHubBranchCreationForbiddenException)
        {
            return Result.Failure<CopyBranchResult>(
                "GitHub denied creating the branch — check branch protection rules and app permissions.");
        }

        compensations.Add(("DeleteGithubRef", async () =>
        {
            await _github.DeleteBranchRefAsync(
                owner, repo, newBranchName, installation.InstallationId, CancellationToken.None);
        }));

        // -------- 6. Fork the source box --------
        // Generate the new runtime id up-front so the forked box carries the
        // same `rt-{RuntimeId:N}` naming convention the provisioner uses for
        // fresh forks. One rule, not two, for anyone staring at the Box console.
        var newRuntimeId = Guid.NewGuid();
        var newBranchId = Guid.NewGuid();

        // Snapshot-freshness dance: forks take the LATEST snapshot, and
        // snapshots happen on stop. If the source box is up we stop it first
        // (fresh snapshot of the user's current disk), fork, then resume it.
        // An archived source already has a current snapshot — fork directly.
        var sourceWasUp = false;
        try
        {
            var sourceBox = await _box.GetBoxAsync(sourceRuntime.BoxId!, cancellationToken);
            sourceWasUp = BoxStatus.IsUp(sourceBox.Status);
        }
        catch (BoxApiException ex) when (ex.StatusCode == 404)
        {
            await CompensateAsync(compensations, cancellationToken);
            return Result.Failure<CopyBranchResult>(
                $"{NotFoundPrefix} Source branch's box no longer exists on Box.");
        }

        if (sourceWasUp)
        {
            try
            {
                await _box.StopBoxAsync(
                    sourceRuntime.BoxId!,
                    runtimeId: sourceRuntime.Id,
                    idempotencyKey: $"copy-snapshot:{newRuntimeId:N}",
                    ct: cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "CopyBranch: pre-fork stop of source box {BoxId} failed for branch {SourceBranchId}; running compensations.",
                    sourceRuntime.BoxId, source.Id);
                await CompensateAsync(compensations, cancellationToken);
                return Result.Failure<CopyBranchResult>(
                    "Couldn't snapshot the source runtime for copying. Nothing was changed.");
            }
        }

        BoxVm forkedBox;
        try
        {
            // Partial env on purpose: the runtime JWT doesn't exist yet (the
            // provisioner mints it), so the fork gets identity-only env and
            // noEnv isolation. The provisioner's existing-box path then
            // refreshes the full env (JWT, tunnel trio) and bounces the daemon
            // on its first tick — enqueued right below.
            forkedBox = await _box.ForkBoxAsync(
                sourceRuntime.BoxId!,
                new ForkBoxRequest(
                    Name: BoxRuntimeProvisioning.BuildBoxName(newRuntimeId),
                    Size: BoxSizeMapper.FromSpec(sourceRuntime.Cpus, sourceRuntime.MemoryMb),
                    Env: new Dictionary<string, string> { ["RUNTIME_ID"] = newRuntimeId.ToString() },
                    NoEnv: true),
                idempotencyKey: $"copyBranch:{newRuntimeId:N}",
                ct: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "CopyBranch: box fork failed for source branch {SourceBranchId}; running compensations.",
                source.Id);
            await ResumeSourceBestEffortAsync(sourceRuntime, sourceWasUp);
            await CompensateAsync(compensations, cancellationToken);
            return Result.Failure<CopyBranchResult>(
                "Couldn't fork the runtime box. Nothing was changed.");
        }

        // Give the user their source branch back — both the success path (here)
        // and the fork-failure path above resume it.
        await ResumeSourceBestEffortAsync(sourceRuntime, sourceWasUp);

        compensations.Add(("DeleteForkedBox", async () =>
        {
            await _box.DeleteBoxAsync(forkedBox.Id, runtimeId: newRuntimeId, ct: CancellationToken.None);
        }));

        // -------- 7. Spec inheritance is now via the project --------
        // Per `project-level-runtime-spec`, runtime spec lives on Project, so
        // a branch copy under the same project automatically inherits the
        // project's spec via bootstrap. The CatalogSpecId / ForceBlankSpec
        // request fields are accepted by the controller for back-compat but
        // are no-ops on the runtime row: it has no Spec column to set, and
        // overwriting Project.Spec from a branch copy would silently mutate
        // every other branch's runtime on its next bootstrap (which is exactly
        // the surprise the spec is designed to avoid).
        //
        // If the catalog-pick / blank-on-copy semantics become important again
        // a future per-branch-spec-override extension is noted in the spec
        // (`ProjectRuntime.SpecOverride` with null-fallback). Until then,
        // copy = inherit project spec.

        // -------- 8. Insert DB rows (single SaveChanges = single transaction) --------
        var newBranch = new ProjectBranch
        {
            Id = newBranchId,
            ProjectId = source.ProjectId,
            Name = newBranchName,
            IsDefault = false,
            // CreatedAt / UpdatedAt are stamped by the DbContext interceptor.
        };

        var newRuntime = new ProjectRuntime
        {
            Id = newRuntimeId,
            ProjectId = source.ProjectId,
            BranchId = newBranchId,
            // Carry tenancy from the source — the workspace boundary follows
            // the project, mirroring CreateProjectHandler.
            TenantId = sourceRuntime.TenantId,
            State = RuntimeState.Pending,
            StateChangedAt = DateTime.UtcNow,
            Region = sourceRuntime.Region,
            // Runtime SERVICES spec lives on Project per `project-level-runtime-spec` —
            // the runtime row no longer carries Spec / SpecVersion. The new branch
            // picks up project.Spec on its first bootstrap.
            // Runtime MACHINE spec (CPU class, vCPU count, RAM, volume size) —
            // always carried from the source runtime, never from the catalog
            // and never blanked. The source runtime row is the source-of-truth,
            // not the project's current default, so a project default change
            // between the source's birth and the copy does not retro-resize
            // the new branch.
            VolumeSizeGb = sourceRuntime.VolumeSizeGb,
            CpuKind = sourceRuntime.CpuKind,
            Cpus = sourceRuntime.Cpus,
            MemoryMb = sourceRuntime.MemoryMb,
            // The forked box already exists — stamp it now so the provisioner's
            // first tick takes the existing-box path: it mints the runtime JWT,
            // refreshes the env file inside the box, and bounces the daemon.
            BoxId = forkedBox.Id,
            TemplateBoxId = sourceRuntime.TemplateBoxId,
        };

        _db.ProjectBranches.Add(newBranch);
        _db.ProjectRuntimes.Add(newRuntime);

        // -------- 9. Raise BranchCopied — observed after SaveChanges commits --------
        newBranch.RaiseBranchCopied(source.Id, newRuntimeId, forkedBox.Id);

        // -------- 10. Claim a preview subdomain + commit, all in one transaction --------
        // cloudflare-tunnel-preview Phase 3: copying a branch creates a new
        // branch row, which must claim its own subdomain just like a freshly
        // created branch. Wrap the claim + SaveChanges in an explicit
        // transaction so the new branch row and the assigned subdomain row
        // land together (or roll back together). The assignment handler
        // participates in the ambient tx and defers its SaveChanges to ours.
        //
        // Execution-strategy note: Npgsql's retrying execution strategy
        // (enabled in DatabaseExtensions.cs with EnableRetryOnFailure) forbids
        // user-initiated transactions outside its ExecuteAsync wrapper. We
        // route the whole tx body through CreateExecutionStrategy() /
        // ExecuteAsync so the strategy controls the retry boundary. We keep
        // compensations (forked-box delete + GitHub ref delete) OUTSIDE the
        // strategy so they run exactly once, never replayed on transient
        // retries — provider rollbacks shouldn't be re-issued.
        //
        // pool_empty here is recoverable from the user's POV ("admin batch-
        // creates more, retry copy") so we run compensations to give back the
        // forked box + GitHub ref that we already provisioned upstream, then
        // surface pool_empty verbatim. Controller maps to 409.
        const string outcomePoolEmpty = "pool_empty";
        const string outcomeWriteFailed = "write_failed";
        const string outcomeOk = "ok";

        var strategy = _db.Database.CreateExecutionStrategy();
        var (outcome, errorDetail) = await strategy.ExecuteAsync<(string Outcome, string? Error)>(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);

            var assignResult = await _mediator.Send(
                new AssignSubdomainToBranchCommand(newBranchId),
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
            catch (Exception ex)
            {
                await tx.RollbackAsync(cancellationToken);
                _logger.LogError(
                    ex,
                    "CopyBranch: DB write failed after box fork + GitHub ref creation for source {SourceBranchId}.",
                    source.Id);
                return (outcomeWriteFailed, null);
            }
        });

        if (outcome == outcomePoolEmpty)
        {
            _logger.LogWarning(
                "CopyBranch: subdomain claim failed for source branch {SourceBranchId}: {Error}; running compensations.",
                source.Id, errorDetail);
            await CompensateAsync(compensations, cancellationToken);
            return Result.Failure<CopyBranchResult>(errorDetail ?? PoolEmptyError);
        }
        if (outcome == outcomeWriteFailed)
        {
            _logger.LogWarning(
                "CopyBranch: running compensations after DB write failure for source branch {SourceBranchId}.",
                source.Id);
            await CompensateAsync(compensations, cancellationToken);
            return Result.Failure<CopyBranchResult>(
                "Couldn't finish creating the new branch. Nothing was changed.");
        }

        _logger.LogInformation(
            "CopyBranch: source {SourceBranchId} -> new {NewBranchId} (runtime {NewRuntimeId}, box {BoxId}) by {UserId}.",
            source.Id, newBranch.Id, newRuntime.Id, forkedBox.Id, request.CallerUserId);

        // Kick the provisioner immediately for the newly-Pending runtime — the
        // recurring sweep stays in place as a safety net for the rare race
        // where the row commits but this enqueue doesn't (process killed
        // between strategy.Commit and Enqueue). Outside the execution strategy
        // on purpose: the strategy may retry on transient DB faults, and we
        // only want to enqueue once a real commit landed.
        _backgroundJobs.Enqueue<RuntimeProvisionerJob>(
            j => j.ProvisionOne(newRuntime.Id, JobCancellationToken.Null));

        return Result.Success(new CopyBranchResult(
            NewBranchId: newBranch.Id,
            NewRuntimeId: newRuntime.Id,
            NewBranchName: newBranch.Name));
    }

    /// <summary>
    /// Resolve a fresh branch name by appending <c>-copy</c>, <c>-copy-2</c>,
    /// <c>-copy-3</c>, … until one is not in <paramref name="existing"/>.
    /// Returns <c>null</c> if every candidate up to <see cref="MaxAutoSuffixAttempts"/>
    /// is taken — caller surfaces a clean validation error rather than picking
    /// a name at random. Pure / side-effect-free so it's trivially testable.
    /// </summary>
    private static string? ResolveAutoSuffixName(string sourceName, HashSet<string> existing)
    {
        // First attempt is the plain "{source}-copy"; subsequent attempts add
        // a numeric tail. The scene-3 example in the spec calls for exactly
        // this progression so the user's mental model is consistent.
        for (var attempt = 1; attempt <= MaxAutoSuffixAttempts; attempt++)
        {
            var candidate = attempt == 1
                ? $"{sourceName}-copy"
                : $"{sourceName}-copy-{attempt}";
            if (!existing.Contains(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    /// <summary>
    /// Resume the source box after the pre-fork snapshot stop, when it was up
    /// to begin with. Best-effort — the user can always reconnect (wake-on-
    /// connect) if this races or fails; we never fail the copy over it.
    /// </summary>
    private async Task ResumeSourceBestEffortAsync(ProjectRuntime sourceRuntime, bool sourceWasUp)
    {
        if (!sourceWasUp || string.IsNullOrEmpty(sourceRuntime.BoxId))
        {
            return;
        }

        try
        {
            await _box.ResumeBoxAsync(
                sourceRuntime.BoxId,
                runtimeId: sourceRuntime.Id,
                ct: CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "CopyBranch: could not resume source box {BoxId} after snapshot stop; wake-on-connect will recover it.",
                sourceRuntime.BoxId);
        }
    }

    /// <summary>
    /// Run accumulated rollback actions in reverse order. We deliberately do
    /// NOT swallow exceptions silently — each compensation failure is logged
    /// at error level with enough context for ops to find and tear down the
    /// orphan by hand. The compensation loop itself does not throw: one
    /// failing rollback should not stop the rest from running (e.g. a box
    /// rollback failing should not prevent the GitHub ref rollback).
    /// </summary>
    private async Task CompensateAsync(
        List<(string Name, Func<Task> Action)> compensations,
        CancellationToken ct)
    {
        for (var i = compensations.Count - 1; i >= 0; i--)
        {
            var (name, action) = compensations[i];
            try
            {
                await action();
                _logger.LogInformation("CopyBranch compensation step {Step} ran successfully.", name);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "CopyBranch compensation step {Step} FAILED. Manual cleanup may be required.",
                    name);
            }
        }
    }
}
