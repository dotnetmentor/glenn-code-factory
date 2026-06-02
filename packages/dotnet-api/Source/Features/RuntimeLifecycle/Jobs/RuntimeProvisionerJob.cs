using Hangfire;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Source.Features.Cloudflare.Models;
using Source.Features.Cloudflare.Services;
using Source.Features.DaemonVersions.Models;
using Source.Features.DaemonVersions.Queries.ResolveDaemonVersion;
using Source.Features.FlyManagement;
using Source.Features.FlyManagement.Configuration;
using Source.Features.FlyManagement.Models;
using Source.Features.Projects.Models;
using Source.Features.RuntimeImages.Models;
using Source.Features.RuntimeLifecycle.Configuration;
using Source.Features.RuntimeLifecycle.Models;
using Source.Features.RuntimeLifecycle.Provisioning;
using Source.Features.RuntimeTokens.Services;
using Source.Features.SystemSettings.Services;
using Source.Infrastructure;

namespace Source.Features.RuntimeLifecycle.Jobs;

/// <summary>
/// Hangfire job that turns <see cref="RuntimeState.Pending"/>
/// <see cref="ProjectRuntime"/> rows into provisioned Fly resources (volume + machine)
/// and walks them into <see cref="RuntimeState.Booting"/>.
///
/// <para><b>Two entry points</b>:
/// <list type="bullet">
///   <item><see cref="Run(IJobCancellationToken)"/> — recurring sweep every minute
///         via <see cref="RuntimeProvisionerJobRegistration"/>. Acts as a safety
///         net that catches Pending rows the ad-hoc enqueue missed (process killed
///         between transaction commit and enqueue) or legacy rows that pre-date the
///         fast-path.</item>
///   <item><see cref="ProvisionOne(Guid, IJobCancellationToken)"/> — ad-hoc, fired
///         by every insert site (controller create, project create, copy/attach/
///         fork branch, user restart) so a fresh runtime starts booting in seconds
///         rather than waiting up to a minute for the sweep.</item>
/// </list></para>
///
/// <para>This is the first auto-driver of the runtime state graph: the only thing in
/// the system that legally moves a Pending runtime into Booting. The next transition
/// (Booting → Bootstrapping) is driven by the Fly machine-state webhook, which lives
/// in a separate card.</para>
///
/// <para><b>Concurrency.</b> Decorated with <see cref="DisableConcurrentExecutionAttribute"/>
/// so two Hangfire workers can't both claim and provision the same Pending row. The
/// 60-second timeout gives the Fly volume + machine round-trips comfortable headroom
/// before a stuck worker is allowed to be stepped on by a fresh one.</para>
///
/// <para><b>Idempotency.</b> v1 is intentionally simple: if we crash between
/// <c>CreateVolumeAsync</c> and <c>CreateMachineAsync</c>, the next tick will retry the
/// volume create. Fly will respond with a 422 ("name exists"), which we surface as
/// <see cref="FlyApiException"/> and handle by transitioning the runtime to
/// <see cref="RuntimeState.Failed"/>. That's adequate for v1; a smarter
/// idempotency-aware recovery layer can come later if the failure rate justifies it.
/// TODO: revisit once we have production telemetry on volume-create-name-collision rates.</para>
///
/// <para><b>Failure isolation.</b> Each Pending runtime is processed in its own
/// try/catch so a single bad row (or a single Fly failure) cannot poison the rest of
/// the batch.</para>
/// </summary>
public class RuntimeProvisionerJob
{
    /// <summary>
    /// Maximum number of Pending runtimes processed per tick. Caps the blast radius
    /// of a misbehaving batch and bounds wall-clock time per Hangfire run. The job
    /// fires every minute, so 10/min × 60 min = 600/hr — comfortable headroom for
    /// realistic provisioning load.
    /// </summary>
    public const int BatchSize = 10;

    private readonly ApplicationDbContext _db;
    private readonly FlyClient _fly;
    private readonly IFlyOptionsAccessor _flyOptions;
    private readonly IRuntimeTokenService _runtimeTokenService;
    private readonly IRuntimeOptionsAccessor _runtimeOptions;
    private readonly IMediator _mediator;
    private readonly ISystemSettingsCipher _cipher;
    private readonly CloudflareApiClient _cloudflare;
    private readonly ILogger<RuntimeProvisionerJob> _logger;

    public RuntimeProvisionerJob(
        ApplicationDbContext db,
        FlyClient fly,
        IFlyOptionsAccessor flyOptions,
        IRuntimeTokenService runtimeTokenService,
        IRuntimeOptionsAccessor runtimeOptions,
        IMediator mediator,
        ISystemSettingsCipher cipher,
        CloudflareApiClient cloudflare,
        ILogger<RuntimeProvisionerJob> logger)
    {
        _db = db;
        _fly = fly;
        _flyOptions = flyOptions;
        _runtimeTokenService = runtimeTokenService;
        _runtimeOptions = runtimeOptions;
        _mediator = mediator;
        _cipher = cipher;
        _cloudflare = cloudflare;
        _logger = logger;
    }

    /// <summary>
    /// Hangfire entry point. Wraps the inner <see cref="Run(CancellationToken)"/>
    /// in a linked <see cref="CancellationTokenSource"/> with a hard 50-second
    /// budget so the job can never hold the
    /// <see cref="DisableConcurrentExecutionAttribute"/> lock past the 60-second
    /// TTL — even if every external call (Fly HTTP, EF, SignalR) hangs forever.
    /// The runtime budget is shorter than the lock TTL on purpose: when the CTS
    /// trips, control returns, Hangfire releases the lock cleanly, and the next
    /// tick acquires on schedule.
    ///
    /// <para><see cref="AutomaticRetry"/> with <c>Attempts = 0</c> stops Hangfire
    /// from auto-requeuing a partially-cancelled run on top of the next scheduled
    /// tick — otherwise a chronic upstream hang would pile cancelled retries
    /// behind the scheduled minutely cron.</para>
    /// </summary>
    [DisableConcurrentExecution(timeoutInSeconds: 60)]
    [AutomaticRetry(Attempts = 0)]
    public async Task Run(IJobCancellationToken hangfireCt)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(hangfireCt.ShutdownToken);
        cts.CancelAfter(TimeSpan.FromSeconds(50));
        await Run(cts.Token);
    }

    /// <summary>
    /// Ad-hoc Hangfire entry point for provisioning a single runtime by id.
    /// Enqueued by every code path that inserts a fresh
    /// <see cref="RuntimeState.Pending"/> row (controller create, project
    /// create, copy/attach/fork branch, user restart) so the runtime starts
    /// booting in seconds rather than waiting up to a minute for the recurring
    /// <see cref="Run(IJobCancellationToken)"/> sweep to pick it up.
    ///
    /// <para>The recurring sweep is retained as a safety net for the rare race
    /// where the row commits but the enqueue does not (process killed between
    /// transaction commit and Hangfire enqueue), and for legacy rows that
    /// existed before this fast-path landed. The per-row CAS at the head of
    /// <see cref="ProvisionAsync"/> (Pending → Booting transition) makes both
    /// paths safe to converge on the same row — whoever loses the race
    /// observes <c>State != Pending</c> at the top of <see cref="ProvisionOneCore"/>
    /// and no-ops.</para>
    ///
    /// <para><b>Budget.</b> Mirrors the batch path's 50-second budget under a
    /// 60-second <see cref="DisableConcurrentExecutionAttribute"/> lock.
    /// Hangfire's default resource name is per-method (not per-argument), so
    /// this lock serialises all ad-hoc provisions across the cluster — same
    /// backpressure semantics as the batch loop's sequential foreach.
    /// Concurrent runs on the SAME row are caught by the Pending-state gate
    /// at the top of <see cref="ProvisionOneCore"/> rather than by the lock.</para>
    ///
    /// <para><b>Retry.</b> <c>AutomaticRetry(Attempts = 0)</c> matches the
    /// recurring path — we lean on the safety-net sweep rather than Hangfire's
    /// implicit retry so a chronic failure shows up as Pending in the dashboard
    /// instead of a retry pile.</para>
    /// </summary>
    [DisableConcurrentExecution(timeoutInSeconds: 60)]
    [AutomaticRetry(Attempts = 0)]
    public async Task ProvisionOne(Guid runtimeId, IJobCancellationToken hangfireCt)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(hangfireCt.ShutdownToken);
        cts.CancelAfter(TimeSpan.FromSeconds(50));
        await ProvisionOneCore(runtimeId, cts.Token);
    }

    /// <summary>
    /// Pure single-runtime provisioning path. Exposed (public) so unit tests can
    /// drive it directly without spinning up the Hangfire infrastructure.
    /// Re-runs the same pre-flight gates the batch path uses, but scoped to a
    /// single row: a misconfigured platform fails just THIS runtime rather than
    /// the whole batch.
    /// </summary>
    public async Task ProvisionOneCore(Guid runtimeId, CancellationToken ct = default)
    {
        // ---- Idempotency gate ----
        // The ad-hoc enqueue races against the recurring sweep, and a single
        // row may have multiple enqueues (controller create + downstream
        // event-driven flow, etc.). The CAS in ProvisionAsync's transition
        // is the real safety net, but bailing early on a non-Pending state
        // keeps the dashboard quiet and saves a round-trip to the Fly preflight.
        var runtime = await _db.ProjectRuntimes
            .FirstOrDefaultAsync(r => r.Id == runtimeId, ct);

        if (runtime is null)
        {
            _logger.LogInformation(
                "RuntimeProvisionerJob.ProvisionOne: runtime {RuntimeId} not found (deleted or never existed) — skipping",
                runtimeId);
            return;
        }

        if (runtime.State != RuntimeState.Pending)
        {
            _logger.LogInformation(
                "RuntimeProvisionerJob.ProvisionOne: runtime {RuntimeId} is in state {State}, not Pending — already handled by another path",
                runtimeId, runtime.State);
            return;
        }

        // ---- Pre-flight: Fly options ----
        // Same surfaces as the batch path, but failure scoped to this row only.
        var flyOptions = _flyOptions.Current;
        if (string.IsNullOrWhiteSpace(flyOptions.ApiToken) ||
            string.IsNullOrWhiteSpace(flyOptions.OrgSlug) ||
            string.IsNullOrWhiteSpace(flyOptions.AppName))
        {
            await FailOneAsync(
                runtime,
                reason: "provisioner:incomplete_fly_config",
                metadata: "Fly settings are incomplete. Configure them in Super Admin → System Settings.",
                ct: ct);
            _logger.LogWarning(
                "RuntimeProvisionerJob.ProvisionOne: Fly settings incomplete — failed runtime {RuntimeId}",
                runtimeId);
            return;
        }

        // ---- Pre-flight: Runtime.PublicApiUrl ----
        var runtimeOptions = _runtimeOptions.Current;
        if (string.IsNullOrWhiteSpace(runtimeOptions.PublicApiUrl))
        {
            await FailOneAsync(
                runtime,
                reason: "provisioner:no_public_api_url",
                metadata: "Runtime:PublicApiUrl is not configured. Set it in Super Admin → System Settings → Runtime. Daemons would otherwise have no MAIN_API_URL to dial back at.",
                ct: ct);
            _logger.LogError(
                "RuntimeProvisionerJob.ProvisionOne: Runtime:PublicApiUrl not configured — failed runtime {RuntimeId}",
                runtimeId);
            return;
        }

        // ---- Pre-flight: Active RuntimeImage ----
        var image = await _db.RuntimeImages
            .Where(i => i.Status == RuntimeImageStatus.Active)
            .OrderByDescending(i => i.BuiltAt)
            .FirstOrDefaultAsync(ct);

        if (image is null)
        {
            await FailOneAsync(
                runtime,
                reason: "provisioner:no_active_image",
                metadata: "No active runtime image is registered. Ask an admin to activate one in Super Admin → Runtime Images.",
                ct: ct);
            _logger.LogWarning(
                "RuntimeProvisionerJob.ProvisionOne: no Active RuntimeImage — failed runtime {RuntimeId}",
                runtimeId);
            return;
        }

        // ---- Pre-flight: an active daemon version MUST exist ----
        // We only check EXISTENCE here. The bootstrap script inside the Fly
        // Machine resolves the URL + sha256 fresh at boot via the main API's
        // resolve endpoints — that's what lets a new publish auto-rollout to
        // every existing Machine on its next restart, with no provisioner
        // re-stamp or Machine re-create needed.
        //
        // We still gate provisioning on existence because spinning a Machine
        // that's guaranteed to crash-loop on bootstrap (no daemon to download)
        // is pure waste. Leave the row Pending; the recurring sweep picks it
        // up once a bundle lands.
        var daemonResolveResult = await _mediator.Send(
            new ResolveDaemonVersionQuery("stable"), ct);

        if (daemonResolveResult.IsFailure)
        {
            _logger.LogWarning(
                "RuntimeProvisionerJob.ProvisionOne: no active daemon version for channel 'stable' — leaving runtime {RuntimeId} Pending for safety-net sweep ({Error})",
                runtimeId, daemonResolveResult.Error);
            return;
        }

        // ---- Provision ----
        try
        {
            await ProvisionAsync(runtime, image, daemonResolveResult.Value, ct);
            _logger.LogInformation(
                "RuntimeProvisionerJob.ProvisionOne: runtime {RuntimeId} provisioned (Pending → Booting)",
                runtimeId);
        }
        catch (FlyApiException flyEx)
        {
            // Same Fly-error → Failed transition as the batch path.
            _logger.LogError(
                flyEx,
                "RuntimeProvisionerJob.ProvisionOne: Fly API rejected provisioning for runtime {RuntimeId}: status={StatusCode} code={ErrorCode}",
                runtimeId, flyEx.StatusCode, flyEx.ErrorCode);

            var reasonCode = flyEx.ErrorCode ?? flyEx.StatusCode.ToString();
            var userMessage = RuntimeFlyProvisioning.FormatUserMessage(flyEx);

            var failResult = runtime.TransitionTo(
                RuntimeState.Failed,
                $"provisioner:fly_error:{reasonCode}",
                "system:provisioner",
                userMessage);

            if (failResult.IsFailure)
            {
                _logger.LogError(
                    "RuntimeProvisionerJob.ProvisionOne could not mark runtime {RuntimeId} Failed after Fly error: {Error}",
                    runtimeId, failResult.Error);
            }
            else
            {
                try
                {
                    await _db.SaveChangesAsync(ct);
                }
                catch (Exception saveEx)
                {
                    _logger.LogError(
                        saveEx,
                        "RuntimeProvisionerJob.ProvisionOne failed to persist Failed transition for runtime {RuntimeId}",
                        runtimeId);
                }
            }
        }
        catch (Exception ex)
        {
            // Same "leave Pending so the sweep retries" semantics as the batch path.
            _logger.LogError(
                ex,
                "RuntimeProvisionerJob.ProvisionOne: unexpected error provisioning runtime {RuntimeId} — leaving Pending for safety-net sweep",
                runtimeId);
        }
    }

    /// <summary>
    /// Single-row analogue of <see cref="FailBatchAsync"/>. Transitions a
    /// runtime to <see cref="RuntimeState.Failed"/> with a structured reason
    /// and metadata, then saves. Used by <see cref="ProvisionOneCore"/>'s
    /// pre-flight gates so misconfiguration on the ad-hoc path produces the
    /// same surface as the batch path (Failed row with reason in the audit
    /// trail, never silently rotting in Pending).
    /// </summary>
    private async Task FailOneAsync(
        ProjectRuntime runtime,
        string reason,
        string metadata,
        CancellationToken ct)
    {
        var failResult = runtime.TransitionTo(
            RuntimeState.Failed,
            reason,
            "system:provisioner",
            metadata);

        if (failResult.IsFailure)
        {
            _logger.LogWarning(
                "RuntimeProvisionerJob.ProvisionOne: could not mark runtime {RuntimeId} Failed ({Reason}): {Error}",
                runtime.Id, reason, failResult.Error);
            return;
        }

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "RuntimeProvisionerJob.ProvisionOne: failed to persist Failed transition for runtime {RuntimeId} ({Reason})",
                runtime.Id, reason);
        }
    }

    /// <summary>
    /// Process one batch of Pending runtimes. Hangfire calls this on the recurring
    /// schedule via the <see cref="Run(IJobCancellationToken)"/> overload, which
    /// applies the cancellation budget. The <see cref="DisableConcurrentExecutionAttribute"/>
    /// on the entry point guards against two workers running this method at the
    /// same time across the cluster.
    /// </summary>
    public async Task Run(CancellationToken ct = default)
    {
        var pending = await _db.ProjectRuntimes
            .Where(r => r.State == RuntimeState.Pending)
            .OrderBy(r => r.CreatedAt)
            .Take(BatchSize)
            .ToListAsync(ct);

        if (pending.Count == 0)
        {
            return;
        }

        // -------- Pre-flight: Fly options must be present --------
        // The CreateProject handler also gates on this, but a row may have been
        // created before settings were cleared, or settings could be partially
        // wiped after the fact. Don't silently early-return when there's
        // work-but-no-config — mark the runtimes Failed with a structured reason
        // so the UI can surface the misconfiguration immediately.
        var flyOptions = _flyOptions.Current;
        if (string.IsNullOrWhiteSpace(flyOptions.ApiToken) ||
            string.IsNullOrWhiteSpace(flyOptions.OrgSlug) ||
            string.IsNullOrWhiteSpace(flyOptions.AppName))
        {
            await FailBatchAsync(
                pending,
                reason: "provisioner:incomplete_fly_config",
                metadata: "Fly settings are incomplete. Configure them in Super Admin → System Settings.",
                ct: ct);

            _logger.LogWarning(
                "RuntimeProvisionerJob: Fly settings are incomplete — failed {Count} pending runtimes",
                pending.Count);
            return;
        }

        // -------- Pre-flight: Runtime.PublicApiUrl must be present --------
        // Without it the daemon has no MAIN_API_URL to dial back at, so the
        // machine would spin forever without ever talking to us. Fail fast
        // with a structured reason rather than producing zombie runtimes.
        var runtimeOptions = _runtimeOptions.Current;
        if (string.IsNullOrWhiteSpace(runtimeOptions.PublicApiUrl))
        {
            await FailBatchAsync(
                pending,
                reason: "provisioner:no_public_api_url",
                metadata: "Runtime:PublicApiUrl is not configured. Set it in Super Admin → System Settings → Runtime. Daemons would otherwise have no MAIN_API_URL to dial back at.",
                ct: ct);

            _logger.LogError(
                "RuntimeProvisionerJob: Runtime:PublicApiUrl is not configured — failed {Count} pending runtimes",
                pending.Count);
            return;
        }

        // Resolve the latest Active image once per batch — every runtime in the batch
        // gets the same image. If a new Active image lands mid-batch the next tick
        // will pick it up.
        var image = await _db.RuntimeImages
            .Where(i => i.Status == RuntimeImageStatus.Active)
            .OrderByDescending(i => i.BuiltAt)
            .FirstOrDefaultAsync(ct);

        if (image is null)
        {
            // Fail-fast: a Pending runtime with no Active image will never make
            // progress. Mark them all Failed with a structured reason rather
            // than letting them rot in Pending forever.
            await FailBatchAsync(
                pending,
                reason: "provisioner:no_active_image",
                metadata: "No active runtime image is registered. Ask an admin to activate one in Super Admin → Runtime Images.",
                ct: ct);

            _logger.LogWarning(
                "RuntimeProvisionerJob: no Active RuntimeImage — failed {Count} pending runtimes",
                pending.Count);
            return;
        }

        // Existence check (NOT URL stamping): verify a daemon bundle exists.
        // bundle has been published. The bootstrap script inside the Machine
        // resolves URL + sha256 fresh at boot from the main API's resolve
        // endpoints, which is what lets a new publish auto-rollout to every
        // existing Machine on its next restart — no provisioner re-stamp or
        // Machine re-create needed.
        //
        // We still gate the batch on existence because spinning Machines that
        // are guaranteed to crash-loop on bootstrap (nothing to download) is
        // pure waste. Leave the rows Pending; the next tick picks them up once
        // a bundle lands. (Contrast with no-active-image, which is a clear
        // misconfiguration we surface immediately by marking the rows Failed.)
        var daemonResolveResult = await _mediator.Send(
            new ResolveDaemonVersionQuery("stable"), ct);

        if (daemonResolveResult.IsFailure)
        {
            _logger.LogWarning(
                "RuntimeProvisionerJob: no active daemon version for channel 'stable' — leaving {Count} runtimes Pending until one is published ({Error})",
                pending.Count, daemonResolveResult.Error);
            return;
        }

        var succeeded = 0;
        var failed = 0;

        foreach (var runtime in pending)
        {
            try
            {
                await ProvisionAsync(runtime, image, daemonResolveResult.Value, ct);
                succeeded++;
            }
            catch (FlyApiException flyEx)
            {
                failed++;
                _logger.LogError(
                    flyEx,
                    "RuntimeProvisionerJob: Fly API rejected provisioning for runtime {RuntimeId}: status={StatusCode} code={ErrorCode}",
                    runtime.Id, flyEx.StatusCode, flyEx.ErrorCode);

                // Transition to Failed with a structured reason and a (bounded) snippet
                // of the Fly response body for forensic context. The transition itself
                // is on a fresh save outside the failed Fly call, so the audit row
                // lands cleanly.
                var reasonCode = flyEx.ErrorCode ?? flyEx.StatusCode.ToString();
                var userMessage = RuntimeFlyProvisioning.FormatUserMessage(flyEx);

                var failResult = runtime.TransitionTo(
                    RuntimeState.Failed,
                    $"provisioner:fly_error:{reasonCode}",
                    "system:provisioner",
                    userMessage);

                if (failResult.IsFailure)
                {
                    // Defensive: Pending → Failed is a legal edge in the state graph
                    // (added specifically for this provisioner-error path). If the
                    // transition is rejected here it usually means the runtime moved
                    // out of Pending in parallel with our run — log and continue.
                    _logger.LogError(
                        "RuntimeProvisionerJob could not mark runtime {RuntimeId} Failed after Fly error: {Error}",
                        runtime.Id, failResult.Error);
                }
                else
                {
                    try
                    {
                        await _db.SaveChangesAsync(ct);
                    }
                    catch (Exception saveEx)
                    {
                        _logger.LogError(
                            saveEx,
                            "RuntimeProvisionerJob failed to persist Failed transition for runtime {RuntimeId}",
                            runtime.Id);
                    }
                }
            }
            catch (Exception ex)
            {
                failed++;
                _logger.LogError(
                    ex,
                    "RuntimeProvisionerJob: unexpected error provisioning runtime {RuntimeId} — leaving Pending for retry",
                    runtime.Id);
                // Deliberately do NOT transition: the next tick will pick the row up again.
            }
        }

        _logger.LogInformation(
            "RuntimeProvisionerJob processed {Total} runtimes, {Succeeded} succeeded, {Failed} failed",
            pending.Count, succeeded, failed);
    }

    /// <summary>
    /// Mark every runtime in <paramref name="batch"/> Failed with the same structured
    /// reason and metadata, then persist in a single SaveChanges. Used by the pre-flight
    /// gates (no Active image, incomplete Fly config) so a misconfigured platform never
    /// leaves a runtime stuck in Pending.
    ///
    /// <para>Skips rows where the Pending → Failed transition is illegal (someone moved
    /// them in parallel) — those are logged but not fatal to the batch.</para>
    /// </summary>
    private async Task FailBatchAsync(
        List<ProjectRuntime> batch,
        string reason,
        string metadata,
        CancellationToken ct)
    {
        foreach (var runtime in batch)
        {
            var failResult = runtime.TransitionTo(
                RuntimeState.Failed,
                reason,
                "system:provisioner",
                metadata);

            if (failResult.IsFailure)
            {
                _logger.LogWarning(
                    "RuntimeProvisionerJob: could not mark runtime {RuntimeId} Failed ({Reason}): {Error}",
                    runtime.Id, reason, failResult.Error);
            }
        }

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "RuntimeProvisionerJob: failed to persist batch Failed transitions ({Reason})",
                reason);
        }
    }

    /// <summary>
    /// Provision a single Pending runtime: create its Fly volume, create its Fly machine,
    /// stamp the resulting ids + image digest on the runtime, and transition it to
    /// <see cref="RuntimeState.Booting"/>. Any exception bubbles to the caller, which
    /// owns batch-level error handling.
    ///
    private async Task ProvisionAsync(
        ProjectRuntime runtime,
        RuntimeImage image,
        DaemonVersionDto daemon,
        CancellationToken ct)
    {
        // ---- 1. Volume ----
        // Skip the volume create when one is already stamped on the runtime
        // row. Two flows land here:
        //   * Copy Branch (CopyBranchHandler) forks the source volume up
        //     front and writes the resulting FlyVolumeId to the row before
        //     the runtime enters this job's queue.
        //   * User-triggered restart from Failed (ProjectRuntime.Restart)
        //     walks Failed → Pending with FlyVolumeId still attached so the
        //     user's working data survives the restart.
        // Calling CreateVolume again would either duplicate (and bill twice)
        // or 422 on name collision — both wrong. The "FlyVolumeId is null →
        // fresh create" path is the legacy onboarding path; the "FlyVolumeId
        // already set → reuse" path is Copy Branch / Restart and any future
        // fork-style flow.
        string volumeId;
        if (string.IsNullOrWhiteSpace(runtime.FlyVolumeId))
        {
            var volumeReq = new CreateVolumeRequest(
                Name: RuntimeFlyProvisioning.BuildVolumeName(runtime.Id),
                Region: runtime.Region,
                SizeGb: runtime.VolumeSizeGb,
                Encrypted: true);

            var volume = await _fly.CreateVolumeAsync(volumeReq, runtimeId: runtime.Id, ct: ct);
            runtime.FlyVolumeId = volume.Id;
            volumeId = volume.Id;
        }
        else
        {
            // Pre-existing volume (Copy Branch fork OR user restart from
            // Failed). Use it as-is — Fly already has it provisioned in the
            // same region the runtime row's `Region` column points at.
            volumeId = runtime.FlyVolumeId;
            _logger.LogInformation(
                "RuntimeProvisionerJob: runtime {RuntimeId} already has FlyVolumeId {VolumeId} — skipping volume create.",
                runtime.Id, volumeId);

            // ---- 1.5. Best-effort destroy of any stale machine ----
            //
            // A restart from Failed (ProjectRuntime.Restart) intentionally
            // keeps FlyMachineId pointing at the dead Fly machine so we can
            // tear it down here before booting a replacement on the same
            // volume. Without this the orphaned machine lingers on Fly's
            // side and Fly will refuse to attach the volume to two machines.
            //
            // Mirrors RespawnRuntimeJob.Run's destroy-then-create pattern —
            // force: true because the dead machine may still register as
            // "started" with Fly (412 Precondition Failed without force),
            // and 404 = "already gone" is a non-error continuation. Other
            // Fly errors propagate to the outer batch-level catch which
            // marks the runtime Failed.
            if (!string.IsNullOrEmpty(runtime.FlyMachineId))
            {
                var staleMachineId = runtime.FlyMachineId;
                try
                {
                    await _fly.DestroyMachineAsync(staleMachineId, force: true, runtimeId: runtime.Id, ct: ct);
                    _logger.LogInformation(
                        "RuntimeProvisionerJob: force-destroyed stale machine {MachineId} for runtime {RuntimeId} before reuse-volume boot.",
                        staleMachineId, runtime.Id);
                }
                catch (FlyApiException ex) when (ex.StatusCode == 404)
                {
                    _logger.LogInformation(
                        "RuntimeProvisionerJob: stale machine {MachineId} already gone (404) for runtime {RuntimeId}, continuing with new boot.",
                        staleMachineId, runtime.Id);
                }
                // Clear the stale id so the create-machine block below assigns
                // the fresh one cleanly (and so a partial failure between here
                // and machine-create doesn't leave us re-destroying on retry).
                runtime.FlyMachineId = null;
            }
        }

        // ---- 1.5. RuntimeToken ----
        // Mint the JWT the daemon will use to authenticate back to us. The
        // RuntimeTokenIssue audit row is written in the same DbContext as the
        // rest of the provisioner's state, so a Fly-create failure leaves
        // *both* the runtime row's intermediate state and the issuance row
        // intact — both are caught by the next tick (which re-mints) and the
        // orphaned token simply expires in 7 days. This is "audit before
        // issuance" per the spec: we never want a Machine running with an
        // unrecorded token, so the mint MUST happen before the Fly machine
        // create call.
        var mintResult = await _runtimeTokenService.MintAsync(new MintTokenRequest(
            RuntimeId: runtime.Id,
            ProjectId: runtime.ProjectId,
            BranchId: null,         // single branch per runtime today
            TenantId: runtime.TenantId,
            Scope: "runtime"
        ), ct);

        if (mintResult.IsFailure)
        {
            // Tenant-less runtime row, or some other refusal from the token
            // service. We can't safely proceed: a Machine running with no
            // (or worse, an undertyped) JWT defeats tenancy isolation. Mark
            // the runtime Failed with a structured reason so the operator
            // can see why and the next provisioner tick won't retry it.
            _logger.LogError(
                "RuntimeProvisionerJob: refusing to provision runtime {RuntimeId} — token mint rejected: {Error}",
                runtime.Id, mintResult.Error);

            var failResult = runtime.TransitionTo(
                RuntimeState.Failed,
                "provisioner:mint_rejected",
                "system:provisioner",
                mintResult.Error);

            if (failResult.IsSuccess)
            {
                await _db.SaveChangesAsync(ct);
            }
            return;
        }

        var minted = mintResult.Value;

        // ---- 1.6. Cloudflare preview-tunnel env (Phase 4) ----
        // If the branch has an Assigned subdomain in the pool, we stamp the
        // three env vars the daemon needs to start `cloudflared` and route
        // traffic to the user's dev server:
        //   * TUNNEL_TOKEN — decrypted from the pool row's encrypted token
        //   * PREVIEW_PORT — the project's per-project preview port (default 5173)
        //   * PREVIEW_HOSTNAME — full FQDN, useful for logs / debug
        //
        // Legacy branches created before Phase 3 don't have a SubdomainAssignment
        // bound to them. That's fine — we skip all three env vars and the daemon
        // simply never starts cloudflared. No tunnel, no preview, but the
        // runtime still boots cleanly. (Logged at info so an operator can spot
        // the legacy population.)
        var subdomain = await _db.SubdomainAssignments
            .Where(s => s.AssignedBranchId == runtime.BranchId
                        && s.Status == SubdomainStatus.Assigned)
            .FirstOrDefaultAsync(ct);

        var previewPort = await _db.Projects
            .Where(p => p.Id == runtime.ProjectId)
            .Select(p => (int?)p.PreviewPort)
            .FirstOrDefaultAsync(ct) ?? Project.DefaultPreviewPort;

        // ---- 2. Machine ----
        if (!string.IsNullOrWhiteSpace(runtime.FlyMachineId))
        {
            _logger.LogInformation(
                "RuntimeProvisionerJob: runtime {RuntimeId} already has FlyMachineId {MachineId} — resuming Pending → Booting.",
                runtime.Id, runtime.FlyMachineId);

            var resumeTransition = runtime.TransitionTo(
                RuntimeState.Booting,
                "provisioner:resumed_existing_machine",
                "system:provisioner");

            if (resumeTransition.IsFailure)
            {
                _logger.LogError(
                    "RuntimeProvisionerJob: Pending -> Booting resume rejected for runtime {RuntimeId}: {Error}",
                    runtime.Id, resumeTransition.Error);
                return;
            }

            await _db.SaveChangesAsync(ct);
            return;
        }

        var env = new Dictionary<string, string>
        {
            ["RUNTIME_ID"] = runtime.Id.ToString(),
            ["GLENN_RUNTIME_TOKEN"] = minted.Token,
            // MAIN_API_URL is BOTH the daemon's callback URL AND the URL the
            // (new) bootstrap script uses to RESOLVE the daemon + agent-natives
            // bundles at boot.
            ["MAIN_API_URL"] = _runtimeOptions.Current.PublicApiUrl,
            // DEFENSIVE STAMP — backwards-compat with already-deployed runtime
            // images that pre-date commit fc8e81d. The NEW bootstrap-daemon.sh
            // (post-fc8e81d) ignores these env vars and re-resolves the bundles
            // from the main API at boot for hot-reload semantics. But OLDER
            // images still on Fly were built from the OLD bootstrap-daemon.sh
            // which hard-requires these six env vars and crash-loops without
            // them. Stamping them costs nothing on new images and unblocks any
            // old image still in production. Remove this once every runtime
            // image in production has been rebuilt past fc8e81d.
            ["DAEMON_VERSION"] = daemon.Version,
            ["DAEMON_BUNDLE_URL"] = daemon.DownloadUrl,
            ["DAEMON_BUNDLE_SHA256"] = daemon.Sha256,
        };

        if (subdomain is not null)
        {
            env["TUNNEL_TOKEN"] = _cipher.Decrypt(subdomain.TunnelToken);
            env["PREVIEW_PORT"] = previewPort.ToString();
            env["PREVIEW_HOSTNAME"] = subdomain.Hostname;

            // Defensive Cloudflare ingress reconciliation.
            //
            // Belt-and-braces for the port-mismatch class of bug:
            //   - AssignSubdomainToBranch already pushes the PUT at claim time
            //     for the happy path, but rows assigned before that fix shipped
            //     are still pointing at pool placeholder 5173.
            //   - A network blip during the claim-time PUT also leaves a row
            //     drifted (we deliberately don't fail the claim on Cloudflare
            //     errors — see ReconcileCloudflareIngressAsync).
            //
            // Doing it here in the provisioner is the safety net: every machine
            // boot pays one extra Cloudflare PUT (idempotent — Cloudflare's
            // configurations endpoint is PUT-replace, no diff needed) and we
            // guarantee the tunnel routes to the right port by the time the
            // machine accepts traffic. Skipped for the default port, same
            // reasoning as the claim-time path: pool placeholder already
            // matches, no API round-trip needed.
            if (previewPort != Project.DefaultPreviewPort)
            {
                try
                {
                    await _cloudflare.AddPublicHostnameAsync(
                        subdomain.TunnelId,
                        subdomain.Hostname,
                        previewPort,
                        ct);
                    _logger.LogInformation(
                        "RuntimeProvisionerJob: reconciled tunnel {TunnelId} ingress to localhost:{PreviewPort} for runtime {RuntimeId}",
                        subdomain.TunnelId, previewPort, runtime.Id);
                }
                catch (Exception ex)
                {
                    // Best-effort. If Cloudflare is down right now we still want
                    // the machine to boot — the daemon will heartbeat and serve
                    // its app on the right port, even if the tunnel briefly
                    // routes wrong. UpdateProjectPreviewPort's fan-out (or the
                    // next provisioner tick on a different runtime) will catch
                    // up. Don't fail the boot for a stale tunnel config.
                    _logger.LogWarning(
                        ex,
                        "RuntimeProvisionerJob: Cloudflare ingress PUT failed for tunnel {TunnelId} (runtime {RuntimeId}, port {PreviewPort}). Proceeding with boot; tunnel may briefly route to placeholder port.",
                        subdomain.TunnelId, runtime.Id, previewPort);
                }
            }
        }
        else
        {
            _logger.LogInformation(
                "RuntimeProvisionerJob: runtime {RuntimeId} (branch {BranchId}) has no assigned subdomain — skipping preview-tunnel env vars (legacy or pre-Phase-3 branch).",
                runtime.Id, runtime.BranchId);
        }

        var machineReq = new CreateMachineRequest(
            Name: RuntimeFlyProvisioning.BuildMachineName(runtime.Id),
            Region: runtime.Region,
            Config: new MachineConfig(
                Image: $"{image.Registry}:{image.Tag}",
                Env: env,
                // Snapshotted spec on the runtime row drives Fly machine sizing.
                // Defaults (shared/1/2048) match the historical MachineGuest()
                // tuple so legacy runtimes boot exactly as before; per-project
                // override flows in via ProjectRuntime.CpuKind/Cpus/MemoryMb,
                // populated at row creation from Project.RuntimeCpu*. The
                // PersistRootfs="always" invariant stays — see CreateMachineRequest.cs.
                Guest: new MachineGuest(
                    CpuKind: runtime.CpuKind,
                    Cpus: runtime.Cpus,
                    MemoryMb: runtime.MemoryMb,
                    PersistRootfs: "always"),
                Mounts: new List<MachineMount>
                {
                    new(Volume: volumeId, Path: "/data"),
                }));

        var machine = await RuntimeFlyProvisioning.CreateOrAdoptMachineAsync(
            _fly, _db, runtime, machineReq, ct);
        runtime.FlyMachineId = machine.Id;
        runtime.ImageDigest = image.Digest;

        // ---- 3. Transition Pending → Booting ----
        var transitionResult = runtime.TransitionTo(
            RuntimeState.Booting,
            "provisioner:created",
            "system:provisioner");

        if (transitionResult.IsFailure)
        {
            // Persist the Fly ids so the next tick can resume instead of
            // re-creating a machine with the same deterministic name.
            await _db.SaveChangesAsync(ct);
            _logger.LogError(
                "RuntimeProvisionerJob: Pending -> Booting transition rejected for runtime {RuntimeId}: {Error}",
                runtime.Id, transitionResult.Error);
            return;
        }

        await _db.SaveChangesAsync(ct);
    }
}
