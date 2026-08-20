using Hangfire;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Source.Features.BoxManagement;
using Source.Features.BoxManagement.Configuration;
using Source.Features.BoxManagement.Models;
using Source.Features.Cloudflare.Models;
using Source.Features.Cloudflare.Services;
using Source.Features.DaemonVersions.Models;
using Source.Features.DaemonVersions.Queries.ResolveDaemonVersion;
using Source.Features.Projects.Models;
using Source.Features.RuntimeLifecycle.Configuration;
using Source.Features.RuntimeLifecycle.Models;
using Source.Features.RuntimeLifecycle.Provisioning;
using Source.Features.RuntimeTemplates.Models;
using Source.Features.RuntimeTokens.Services;
using Source.Features.SystemSettings.Services;
using Source.Infrastructure;

namespace Source.Features.RuntimeLifecycle.Jobs;

/// <summary>
/// Hangfire job that turns <see cref="RuntimeState.Pending"/>
/// <see cref="ProjectRuntime"/> rows into provisioned Box VMs and walks them into
/// <see cref="RuntimeState.Booting"/>.
///
/// <para><b>Box-native provisioning model.</b> A fresh runtime is a FORK of the
/// active golden template box (see the RuntimeTemplates feature): the fork inherits
/// the template's entire prepared disk and receives its identity via per-fork env
/// vars (<c>RUNTIME_ID</c>, <c>GLENN_RUNTIME_TOKEN</c>, <c>MAIN_API_URL</c>, ...),
/// with <c>noEnv</c> isolation so it can never see the platform account's own
/// secrets. There is no separate volume: the box's disk IS the persistence, so a
/// runtime that already has a <see cref="ProjectRuntime.BoxId"/> is REBOOTED
/// (resume + env refresh) rather than re-created, and a size change is a
/// disk-preserving stop → fork-at-new-size → delete-old sequence.</para>
///
/// <para><b>Two entry points</b>:
/// <list type="bullet">
///   <item><see cref="Run(IJobCancellationToken)"/> — recurring sweep every minute.
///         Safety net for Pending rows the ad-hoc enqueue missed.</item>
///   <item><see cref="ProvisionOne(Guid, IJobCancellationToken)"/> — ad-hoc, fired
///         by every insert site so a fresh runtime starts booting in seconds.</item>
/// </list></para>
///
/// <para>This is the only thing in the system that legally moves a Pending runtime
/// into Booting. The next transition (Booting → Bootstrapping) is driven by the
/// <see cref="RuntimeReconcilerJob"/> observing the box come up — Box has no
/// webhooks, so the reconciler owns that edge.</para>
///
/// <para><b>Concurrency / budget.</b> Same shape as before:
/// <see cref="DisableConcurrentExecutionAttribute"/> with a runtime budget shorter
/// than the lock TTL so a hung upstream can never wedge the schedule.</para>
///
/// <para><b>Start-budget awareness.</b> Every fork and resume burns one machine
/// start against Box's account-wide budget (600/hr, 1,500/day). Transient
/// budget/rate errors therefore leave the row Pending for the next sweep instead
/// of marking it Failed — see <see cref="BoxRuntimeProvisioning.IsTransient"/>.</para>
/// </summary>
public class RuntimeProvisionerJob
{
    /// <summary>
    /// Maximum number of Pending runtimes processed per tick. Caps the blast radius
    /// of a misbehaving batch and bounds wall-clock time per Hangfire run — and keeps
    /// a worst-case tick (10 forks/min) far inside Box's 600 starts/hr account budget.
    /// </summary>
    public const int BatchSize = 10;

    private readonly ApplicationDbContext _db;
    private readonly BoxClient _box;
    private readonly IBoxOptionsAccessor _boxOptions;
    private readonly IRuntimeTokenService _runtimeTokenService;
    private readonly IRuntimeOptionsAccessor _runtimeOptions;
    private readonly IMediator _mediator;
    private readonly ISystemSettingsCipher _cipher;
    private readonly CloudflareApiClient _cloudflare;
    private readonly ILogger<RuntimeProvisionerJob> _logger;

    public RuntimeProvisionerJob(
        ApplicationDbContext db,
        BoxClient box,
        IBoxOptionsAccessor boxOptions,
        IRuntimeTokenService runtimeTokenService,
        IRuntimeOptionsAccessor runtimeOptions,
        IMediator mediator,
        ISystemSettingsCipher cipher,
        CloudflareApiClient cloudflare,
        ILogger<RuntimeProvisionerJob> logger)
    {
        _db = db;
        _box = box;
        _boxOptions = boxOptions;
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
    /// TTL — even if every external call (Box HTTP, EF, SignalR) hangs forever.
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
    /// <see cref="RuntimeState.Pending"/> row so the runtime starts booting in
    /// seconds rather than waiting for the recurring sweep. The per-row CAS at
    /// the head of <see cref="ProvisionAsync"/> makes both paths safe to
    /// converge on the same row.
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
    /// </summary>
    public async Task ProvisionOneCore(Guid runtimeId, CancellationToken ct = default)
    {
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

        // ---- Pre-flight: Box options ----
        if (string.IsNullOrWhiteSpace(_boxOptions.Current.ApiKey))
        {
            await FailOneAsync(
                runtime,
                reason: "provisioner:incomplete_box_config",
                metadata: "Box settings are incomplete. Configure Box:ApiKey in Super Admin → System Settings.",
                ct: ct);
            _logger.LogWarning(
                "RuntimeProvisionerJob.ProvisionOne: Box settings incomplete — failed runtime {RuntimeId}",
                runtimeId);
            return;
        }

        // ---- Pre-flight: Runtime.PublicApiUrl ----
        if (string.IsNullOrWhiteSpace(_runtimeOptions.Current.PublicApiUrl))
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

        // ---- Pre-flight: Active RuntimeTemplate ----
        var template = await GetActiveTemplateAsync(ct);
        if (template is null)
        {
            await FailOneAsync(
                runtime,
                reason: "provisioner:no_active_template",
                metadata: "No active runtime template is registered. Build one with scripts/build-box-template.sh and activate it in Super Admin → Runtime Templates.",
                ct: ct);
            _logger.LogWarning(
                "RuntimeProvisionerJob.ProvisionOne: no Active RuntimeTemplate — failed runtime {RuntimeId}",
                runtimeId);
            return;
        }

        // ---- Pre-flight: an active daemon version MUST exist ----
        // Existence only: the bootstrap script inside the box resolves URL + sha256
        // fresh at boot from the main API, which is what lets a new publish
        // auto-rollout to every existing box on its next daemon restart.
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
            await ProvisionAsync(runtime, template, daemonResolveResult.Value, ct);
            _logger.LogInformation(
                "RuntimeProvisionerJob.ProvisionOne: runtime {RuntimeId} provisioned (Pending → Booting)",
                runtimeId);
        }
        catch (BoxApiException boxEx) when (BoxRuntimeProvisioning.IsTransient(boxEx))
        {
            // Start-budget exhaustion / rate limit / box still starting: leave the
            // row Pending — the recurring sweep retries when the budget frees up.
            _logger.LogWarning(
                boxEx,
                "RuntimeProvisionerJob.ProvisionOne: transient Box error for runtime {RuntimeId} (code={ErrorCode}) — leaving Pending for retry",
                runtimeId, boxEx.ErrorCode);
        }
        catch (BoxApiException boxEx)
        {
            _logger.LogError(
                boxEx,
                "RuntimeProvisionerJob.ProvisionOne: Box API rejected provisioning for runtime {RuntimeId}: status={StatusCode} code={ErrorCode}",
                runtimeId, boxEx.StatusCode, boxEx.ErrorCode);
            await FailFromBoxErrorAsync(runtime, boxEx, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "RuntimeProvisionerJob.ProvisionOne: unexpected error provisioning runtime {RuntimeId} — leaving Pending for safety-net sweep",
                runtimeId);
        }
    }

    /// <summary>
    /// Process one batch of Pending runtimes (recurring sweep).
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

        // -------- Pre-flight: Box options must be present --------
        if (string.IsNullOrWhiteSpace(_boxOptions.Current.ApiKey))
        {
            await FailBatchAsync(
                pending,
                reason: "provisioner:incomplete_box_config",
                metadata: "Box settings are incomplete. Configure Box:ApiKey in Super Admin → System Settings.",
                ct: ct);

            _logger.LogWarning(
                "RuntimeProvisionerJob: Box settings are incomplete — failed {Count} pending runtimes",
                pending.Count);
            return;
        }

        // -------- Pre-flight: Runtime.PublicApiUrl must be present --------
        if (string.IsNullOrWhiteSpace(_runtimeOptions.Current.PublicApiUrl))
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

        // Resolve the active template once per batch — every runtime in the batch
        // forks the same template. If a new Active template lands mid-batch the
        // next tick will pick it up.
        var template = await GetActiveTemplateAsync(ct);
        if (template is null)
        {
            await FailBatchAsync(
                pending,
                reason: "provisioner:no_active_template",
                metadata: "No active runtime template is registered. Build one with scripts/build-box-template.sh and activate it in Super Admin → Runtime Templates.",
                ct: ct);

            _logger.LogWarning(
                "RuntimeProvisionerJob: no Active RuntimeTemplate — failed {Count} pending runtimes",
                pending.Count);
            return;
        }

        // Existence check for the daemon bundle — see ProvisionOneCore.
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
                await ProvisionAsync(runtime, template, daemonResolveResult.Value, ct);
                succeeded++;
            }
            catch (BoxApiException boxEx) when (BoxRuntimeProvisioning.IsTransient(boxEx))
            {
                failed++;
                _logger.LogWarning(
                    boxEx,
                    "RuntimeProvisionerJob: transient Box error for runtime {RuntimeId} (code={ErrorCode}) — leaving Pending for retry",
                    runtime.Id, boxEx.ErrorCode);
            }
            catch (BoxApiException boxEx)
            {
                failed++;
                _logger.LogError(
                    boxEx,
                    "RuntimeProvisionerJob: Box API rejected provisioning for runtime {RuntimeId}: status={StatusCode} code={ErrorCode}",
                    runtime.Id, boxEx.StatusCode, boxEx.ErrorCode);
                await FailFromBoxErrorAsync(runtime, boxEx, ct);
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
    /// Provision a single Pending runtime. Three shapes, all ending in
    /// <see cref="RuntimeState.Booting"/>:
    ///
    /// <list type="number">
    ///   <item><b>Fresh fork</b> (no <see cref="ProjectRuntime.BoxId"/>): fork the
    ///         active template with per-fork env + noEnv + TTL; stamp the new box id.</item>
    ///   <item><b>Reboot</b> (BoxId set, size unchanged): resume the archived box (or
    ///         use it in place if it's already up), then refresh the env file and
    ///         bounce the daemon so it picks up the freshly-minted JWT.</item>
    ///   <item><b>Disk-preserving resize</b> (BoxId set, size tier changed): stop the
    ///         box to snapshot it, fork the SNAPSHOT at the new size with fresh env,
    ///         delete the old box, stamp the new box id.</item>
    /// </list>
    /// </summary>
    private async Task ProvisionAsync(
        ProjectRuntime runtime,
        RuntimeTemplate template,
        DaemonVersionDto daemon,
        CancellationToken ct)
    {
        // ---- 1. RuntimeToken ----
        // Mint the JWT the daemon will use to authenticate back to us. "Audit before
        // issuance": the mint MUST happen before any box call so we never have a VM
        // running with an unrecorded token. A Box failure after this point just
        // leaves an orphaned token that expires in 7 days.
        var mintResult = await _runtimeTokenService.MintAsync(new MintTokenRequest(
            RuntimeId: runtime.Id,
            ProjectId: runtime.ProjectId,
            BranchId: null,         // single branch per runtime today
            TenantId: runtime.TenantId,
            Scope: "runtime"
        ), ct);

        if (mintResult.IsFailure)
        {
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

        var env = await BuildRuntimeEnvAsync(runtime, daemon, mintResult.Value.Token, ct);
        var desiredSize = BoxSizeMapper.FromSpec(runtime.Cpus, runtime.MemoryMb);

        // ---- 2. Existing box? Reboot or resize in place ----
        if (!string.IsNullOrWhiteSpace(runtime.BoxId))
        {
            BoxVm? existing = null;
            try
            {
                existing = await _box.GetBoxAsync(runtime.BoxId, ct);
            }
            catch (BoxApiException ex) when (ex.StatusCode == 404)
            {
                _logger.LogWarning(
                    "RuntimeProvisionerJob: runtime {RuntimeId} points at box {BoxId} which no longer exists — falling back to a fresh fork (previous disk state is lost).",
                    runtime.Id, runtime.BoxId);
                runtime.BoxId = null;
            }

            if (existing is not null)
            {
                var sizeChanged = !string.IsNullOrEmpty(existing.Size)
                    && !string.Equals(existing.Size, desiredSize, StringComparison.OrdinalIgnoreCase);

                if (sizeChanged)
                {
                    await ResizeViaForkAsync(runtime, existing, desiredSize, env, ct);
                }
                else
                {
                    await RebootExistingBoxAsync(runtime, existing, env, ct);
                }
                return;
            }
        }

        // ---- 3. Fresh fork from the template ----
        var forkReq = new ForkBoxRequest(
            Name: BoxRuntimeProvisioning.BuildBoxName(runtime.Id),
            Size: desiredSize,
            Env: env,
            NoEnv: true,
            TtlSeconds: _boxOptions.Current.DefaultTtlSeconds);

        var forked = await BoxRuntimeProvisioning.ForkOrAdoptAsync(
            _box, _db, runtime, template.BoxId, forkReq, ct);

        runtime.BoxId = forked.Id;
        runtime.TemplateBoxId = template.BoxId;
        if (!string.IsNullOrEmpty(forked.Region))
        {
            runtime.Region = forked.Region;
        }

        // ---- 4. Transition Pending → Booting ----
        var transitionResult = runtime.TransitionTo(
            RuntimeState.Booting,
            "provisioner:forked_from_template",
            "system:provisioner");

        if (transitionResult.IsFailure)
        {
            // Persist the box id so the next tick can resume instead of
            // re-forking a box with the same deterministic name.
            await _db.SaveChangesAsync(ct);
            _logger.LogError(
                "RuntimeProvisionerJob: Pending -> Booting transition rejected for runtime {RuntimeId}: {Error}",
                runtime.Id, transitionResult.Error);
            return;
        }

        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Reboot path: the runtime's box still exists and its size matches. Resume it
    /// if archived, walk to Booting, then refresh the env file (fresh JWT!) and
    /// bounce the daemon. The env refresh is best-effort within the job budget —
    /// if it can't land, the daemon comes up with its old env; an expired token
    /// then shows up as a failed SignalR connect, the HeartbeatWatcher crashes the
    /// runtime, and the next respawn retries the whole sequence.
    /// </summary>
    private async Task RebootExistingBoxAsync(
        ProjectRuntime runtime,
        BoxVm existing,
        Dictionary<string, string> env,
        CancellationToken ct)
    {
        if (BoxStatus.IsArchived(existing.Status) || BoxStatus.IsError(existing.Status))
        {
            await _box.ResumeBoxAsync(
                existing.Id,
                runtimeId: runtime.Id,
                idempotencyKey: $"resume-box:{runtime.Id:D}",
                ct: ct);
        }

        var transition = runtime.TransitionTo(
            RuntimeState.Booting,
            "provisioner:rebooted_existing_box",
            "system:provisioner");

        if (transition.IsFailure)
        {
            _logger.LogError(
                "RuntimeProvisionerJob: Pending -> Booting reboot transition rejected for runtime {RuntimeId}: {Error}",
                runtime.Id, transition.Error);
            return;
        }

        await _db.SaveChangesAsync(ct);

        // Re-arm the TTL guardrail — an archived box's TTL may have lapsed.
        try
        {
            await _box.SetTtlAsync(existing.Id, _boxOptions.Current.DefaultTtlSeconds, runtime.Id, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "RuntimeProvisionerJob: TTL re-arm failed for box {BoxId} (runtime {RuntimeId}); BoxTtlExtenderJob will catch up.",
                existing.Id, runtime.Id);
        }

        try
        {
            var up = await BoxRuntimeProvisioning.WaitForBoxUpAsync(
                _box, existing.Id, BoxRuntimeProvisioning.DefaultUpTimeout, ct);

            if (up is not null && BoxStatus.IsUp(up.Status))
            {
                await BoxRuntimeProvisioning.RefreshEnvAndRestartDaemonAsync(
                    _box, existing.Id, env, runtime.Id, ct);
            }
            else
            {
                _logger.LogWarning(
                    "RuntimeProvisionerJob: box {BoxId} (runtime {RuntimeId}) did not come up within the env-refresh window (last status: {Status}); daemon will boot with its previous env.",
                    existing.Id, runtime.Id, up?.Status ?? "unknown");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "RuntimeProvisionerJob: env refresh failed for box {BoxId} (runtime {RuntimeId}); daemon will boot with its previous env.",
                existing.Id, runtime.Id);
        }
    }

    /// <summary>
    /// Disk-preserving resize: stop the current box (Box snapshots the disk on
    /// stop), fork the snapshot at the new size tier with fresh env, delete the
    /// old box, stamp the new id. The user's working data rides the snapshot into
    /// the new box.
    /// </summary>
    private async Task ResizeViaForkAsync(
        ProjectRuntime runtime,
        BoxVm existing,
        string desiredSize,
        Dictionary<string, string> env,
        CancellationToken ct)
    {
        if (!BoxStatus.IsArchived(existing.Status))
        {
            try
            {
                await _box.StopBoxAsync(
                    existing.Id,
                    runtimeId: runtime.Id,
                    idempotencyKey: $"resize-stop:{runtime.Id:D}",
                    ct: ct);
            }
            catch (BoxApiException ex) when (ex.IsRetriableStartup)
            {
                // Box mid-transition — let the next tick retry the whole resize.
                throw;
            }
        }

        var oldBoxId = existing.Id;
        var forkReq = new ForkBoxRequest(
            Name: BoxRuntimeProvisioning.BuildBoxName(runtime.Id),
            Size: desiredSize,
            Env: env,
            NoEnv: true,
            TtlSeconds: _boxOptions.Current.DefaultTtlSeconds);

        var replacement = await _box.ForkBoxAsync(
            oldBoxId,
            forkReq,
            idempotencyKey: $"resize-fork:{runtime.Id:D}:{desiredSize}",
            runtimeId: runtime.Id,
            ct: ct);

        runtime.BoxId = replacement.Id;

        var transition = runtime.TransitionTo(
            RuntimeState.Booting,
            "provisioner:resized_via_fork",
            "system:provisioner",
            $"{{\"oldBoxId\":\"{oldBoxId}\",\"newBoxId\":\"{replacement.Id}\",\"size\":\"{desiredSize}\"}}");

        if (transition.IsFailure)
        {
            await _db.SaveChangesAsync(ct);
            _logger.LogError(
                "RuntimeProvisionerJob: resize transition rejected for runtime {RuntimeId}: {Error}",
                runtime.Id, transition.Error);
            return;
        }

        await _db.SaveChangesAsync(ct);

        // Old box is now redundant — its disk lives on in the fork. Best-effort
        // delete; an orphan is caught by the TTL guardrail + admin cleanup anyway.
        try
        {
            await _box.DeleteBoxAsync(oldBoxId, runtimeId: runtime.Id, ct: ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "RuntimeProvisionerJob: could not delete old box {BoxId} after resize of runtime {RuntimeId}; it will archive itself at TTL and can be removed in Box Cleanup.",
                oldBoxId, runtime.Id);
        }
    }

    /// <summary>
    /// Delegates to <see cref="BoxRuntimeProvisioning.BuildRuntimeEnvAsync"/> — the
    /// env contract is shared with <see cref="RespawnRuntimeJob"/> so the two can
    /// never diverge.
    /// </summary>
    private Task<Dictionary<string, string>> BuildRuntimeEnvAsync(
        ProjectRuntime runtime,
        DaemonVersionDto daemon,
        string runtimeToken,
        CancellationToken ct) =>
        BoxRuntimeProvisioning.BuildRuntimeEnvAsync(
            _db, _cipher, _cloudflare, _runtimeOptions.Current.PublicApiUrl,
            runtime, daemon, runtimeToken, _logger, ct);

    /// <summary>Newest Active template — the default fork source.</summary>
    private Task<RuntimeTemplate?> GetActiveTemplateAsync(CancellationToken ct) =>
        _db.RuntimeTemplates
            .Where(t => t.Status == RuntimeTemplateStatus.Active)
            .OrderByDescending(t => t.BuiltAt)
            .FirstOrDefaultAsync(ct);

    /// <summary>Mark one runtime Failed after a non-transient Box error, mirroring the old Fly-error path.</summary>
    private async Task FailFromBoxErrorAsync(
        ProjectRuntime runtime,
        BoxApiException boxEx,
        CancellationToken ct)
    {
        var reasonCode = boxEx.ErrorCode ?? boxEx.StatusCode.ToString();
        var userMessage = BoxRuntimeProvisioning.FormatUserMessage(boxEx);

        var failResult = runtime.TransitionTo(
            RuntimeState.Failed,
            $"provisioner:box_error:{reasonCode}",
            "system:provisioner",
            userMessage);

        if (failResult.IsFailure)
        {
            _logger.LogError(
                "RuntimeProvisionerJob could not mark runtime {RuntimeId} Failed after Box error: {Error}",
                runtime.Id, failResult.Error);
            return;
        }

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

    /// <summary>
    /// Single-row analogue of <see cref="FailBatchAsync"/> for the pre-flight gates
    /// so misconfiguration on the ad-hoc path produces the same surface as the batch
    /// path (Failed row with reason in the audit trail, never rotting in Pending).
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
    /// Mark every runtime in <paramref name="batch"/> Failed with the same structured
    /// reason and metadata, then persist in a single SaveChanges. Used by the pre-flight
    /// gates so a misconfigured platform never leaves a runtime stuck in Pending.
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
}
