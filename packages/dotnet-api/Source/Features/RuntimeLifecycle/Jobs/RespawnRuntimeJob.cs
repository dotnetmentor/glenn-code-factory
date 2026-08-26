using System.Text.Json;
using Hangfire;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Source.Features.BoxManagement;
using Source.Features.BoxManagement.Configuration;
using Source.Features.BoxManagement.Models;
using Source.Features.Cloudflare.Services;
using Source.Features.DaemonVersions.Queries.ResolveDaemonVersion;
using Source.Features.RuntimeEvents.Commands;
using Source.Features.RuntimeEvents.Models;
using Source.Features.RuntimeLifecycle.Configuration;
using Source.Features.RuntimeLifecycle.Models;
using Source.Features.RuntimeLifecycle.Provisioning;
using Source.Features.RuntimeTemplates.Models;
using Source.Features.RuntimeTokens.Services;
using Source.Features.SystemSettings.Services;
using Source.Infrastructure;

namespace Source.Features.RuntimeLifecycle.Jobs;

/// <summary>
/// Delayed Hangfire job that performs the actual recovery flow for a crashed
/// runtime. Scheduled by <c>ScheduleRespawnHandler</c> with a retries-aware
/// backoff; this class does not decide <i>whether</i> to respawn — only how.
///
/// <para><b>Box-native respawn.</b> The old Fly flow destroyed the dead machine
/// and created a replacement on the surviving volume. On Box the box's disk IS
/// the persistence, so the equivalent is a clean VM reboot of the SAME box:
/// stop it if it's still up (wedged VM → fresh snapshot), resume it, then
/// refresh the env file with a freshly-minted runtime JWT and bounce the
/// daemon. Only when the box has vanished entirely do we fork a fresh one from
/// the active template — accepting that the previous disk state is gone.</para>
///
/// <para><b>Idempotency.</b> A pre-flight state check (<c>State == Crashed</c>)
/// makes the job safe to re-run; stop/resume tolerate "already stopped/running"
/// shapes and 404 means "box gone → fork fresh".</para>
/// </summary>
public class RespawnRuntimeJob
{
    private readonly ApplicationDbContext _db;
    private readonly BoxClient _box;
    private readonly IBoxOptionsAccessor _boxOptions;
    private readonly IRuntimeOptionsAccessor _runtimeOptions;
    private readonly IRuntimeTokenService _runtimeTokenService;
    private readonly IMediator _mediator;
    private readonly ISystemSettingsCipher _cipher;
    private readonly CloudflareApiClient _cloudflare;
    private readonly ILogger<RespawnRuntimeJob> _logger;

    public RespawnRuntimeJob(
        ApplicationDbContext db,
        BoxClient box,
        IBoxOptionsAccessor boxOptions,
        IRuntimeOptionsAccessor runtimeOptions,
        IRuntimeTokenService runtimeTokenService,
        IMediator mediator,
        ISystemSettingsCipher cipher,
        CloudflareApiClient cloudflare,
        ILogger<RespawnRuntimeJob> logger)
    {
        _db = db;
        _box = box;
        _boxOptions = boxOptions;
        _runtimeOptions = runtimeOptions;
        _runtimeTokenService = runtimeTokenService;
        _mediator = mediator;
        _cipher = cipher;
        _cloudflare = cloudflare;
        _logger = logger;
    }

    /// <summary>
    /// Hangfire entry point. Re-validates the pre-conditions (runtime exists and is
    /// still <see cref="RuntimeState.Crashed"/>), reboots or re-forks the box, bumps
    /// <see cref="ProjectRuntime.RespawnRetries"/>, and walks the runtime back to
    /// <see cref="RuntimeState.Booting"/>.
    /// </summary>
    [DisableConcurrentExecution(timeoutInSeconds: 120)]
    public async Task Run(Guid runtimeId, CancellationToken ct = default)
    {
        var runtime = await _db.ProjectRuntimes
            .FirstOrDefaultAsync(r => r.Id == runtimeId, ct);

        if (runtime is null)
        {
            _logger.LogInformation(
                "Respawn: runtime {RuntimeId} no longer exists, skipping",
                runtimeId);
            return;
        }

        if (runtime.State != RuntimeState.Crashed)
        {
            // The state moved on between scheduling and now — operator delete,
            // manual reset, or a parallel respawn already completed. No-op.
            _logger.LogInformation(
                "Respawn: runtime {RuntimeId} is in state {State} (no longer Crashed), skipping",
                runtimeId, runtime.State);
            return;
        }

        // Pre-flight: Runtime.PublicApiUrl must be configured — the daemon has no
        // MAIN_API_URL to dial back at otherwise.
        var publicApiUrl = _runtimeOptions.Current.PublicApiUrl;
        if (string.IsNullOrWhiteSpace(publicApiUrl))
        {
            _logger.LogError(
                "Respawn: refusing to respawn runtime {RuntimeId} — Runtime:PublicApiUrl is not configured",
                runtimeId);

            var failResult = runtime.TransitionTo(
                RuntimeState.Failed,
                "provisioner:no_public_api_url",
                "respawn-job",
                "Runtime:PublicApiUrl is not configured. Daemons would have no MAIN_API_URL to dial back at.");

            if (failResult.IsSuccess)
            {
                await _db.SaveChangesAsync(ct);
            }
            return;
        }

        // ---- 0.5. Emit the RuntimeRespawnTriggered observability event ----
        // Stable marker so the super-admin drawer can correlate "box got rebooted
        // at HH:MM" with the timeline. Attempt number attributed BEFORE the
        // increment (retry #1 emits 1, not 0).
        var lastFailure = await _db.RuntimeStateEvents
            .AsNoTracking()
            .Where(e => e.RuntimeId == runtimeId && e.ToState == RuntimeState.Crashed)
            .OrderByDescending(e => e.CreatedAt)
            .Select(e => new { e.Reason, e.Metadata })
            .FirstOrDefaultAsync(ct);

        var secondsSinceLastHeartbeat = runtime.LastHeartbeatAt.HasValue
            ? (long)(DateTime.UtcNow - runtime.LastHeartbeatAt.Value).TotalSeconds
            : (long?)null;

        await EmitRespawnTriggeredAsync(
            runtimeId: runtimeId,
            attemptNumber: runtime.RespawnRetries + 1,
            lastFailureReason: lastFailure?.Reason,
            lastFailureMessage: lastFailure?.Metadata,
            secondsSinceLastHeartbeat: secondsSinceLastHeartbeat,
            ct: ct);

        // ---- 1. Resolve the daemon bundle (existence gate + env stamps) ----
        var daemonResolveResult = await _mediator.Send(
            new ResolveDaemonVersionQuery("stable"), ct);
        if (daemonResolveResult.IsFailure)
        {
            _logger.LogWarning(
                "Respawn: no active daemon version for channel 'stable' — leaving runtime {RuntimeId} Crashed (will retry next Hangfire attempt): {Error}",
                runtimeId, daemonResolveResult.Error);
            // Throw so Hangfire retries — recoverable as soon as a bundle is published.
            throw new InvalidOperationException(
                $"No active daemon version: {daemonResolveResult.Error}");
        }
        var daemon = daemonResolveResult.Value;

        // ---- 2. Mint a fresh runtime JWT ----
        // The original JWT is bound to the previous boot; daemons re-read env on
        // restart, so the rebooted VM gets its own. Same audit-before-issuance
        // contract as the provisioner.
        var mintResult = await _runtimeTokenService.MintAsync(new MintTokenRequest(
            RuntimeId: runtime.Id,
            ProjectId: runtime.ProjectId,
            BranchId: null,
            TenantId: runtime.TenantId,
            Scope: "runtime"
        ), ct);
        if (mintResult.IsFailure)
        {
            _logger.LogError(
                "Respawn: refusing to respawn runtime {RuntimeId} — token mint rejected: {Error}",
                runtimeId, mintResult.Error);
            var failResult = runtime.TransitionTo(
                RuntimeState.Failed,
                "respawn:mint_rejected",
                "respawn-job",
                mintResult.Error);
            if (failResult.IsSuccess)
            {
                await _db.SaveChangesAsync(ct);
            }
            return;
        }

        // ---- 3. Shared env contract (identical to the provisioner's) ----
        var env = await BoxRuntimeProvisioning.BuildRuntimeEnvAsync(
            _db, _cipher, _cloudflare, publicApiUrl,
            runtime, daemon, mintResult.Value.Token, _logger, ct);

        // ---- 4. Reboot the existing box, or fork fresh when it's gone ----
        var oldBoxId = runtime.BoxId;
        BoxVm? existing = null;
        if (!string.IsNullOrEmpty(runtime.BoxId))
        {
            try
            {
                existing = await _box.GetBoxAsync(runtime.BoxId, ct);
            }
            catch (BoxApiException ex) when (ex.StatusCode == 404)
            {
                _logger.LogWarning(
                    "Respawn: box {BoxId} for runtime {RuntimeId} no longer exists — forking a fresh one from the template (previous disk state is lost).",
                    runtime.BoxId, runtimeId);
                runtime.BoxId = null;
            }
        }

        if (existing is not null)
        {
            // A crashed runtime's box may be up-but-wedged (daemon dead, VM sick)
            // or already archived. Stop-if-up gives us the Box-destroy equivalent:
            // a clean VM boot from a fresh snapshot — with the disk intact.
            if (BoxStates.IsUp(existing.State) || BoxStates.IsError(existing.State))
            {
                try
                {
                    await _box.StopBoxAsync(
                        existing.Id,
                        runtimeId: runtimeId,
                        idempotencyKey: $"respawn-stop:{runtimeId:D}:{runtime.RespawnRetries}",
                        ct: ct);
                }
                catch (BoxApiException ex) when (ex.IsRetriableStartup)
                {
                    // Mid-transition — let Hangfire's retry take the next swing.
                    throw;
                }
            }

            // Pass the fresh env + TTL directly in the resume body (the contract
            // supports it) so the rebooted daemon can see the new JWT even if the
            // commands-based env-file refresh below never lands. The env-file
            // refresh stays as belt and braces — systemd reads the env file.
            await _box.ResumeBoxAsync(
                existing.Id,
                new ResumeBoxRequest(
                    Env: env,
                    TtlSeconds: _boxOptions.Current.DefaultTtlSeconds),
                runtimeId: runtimeId,
                idempotencyKey: $"respawn-resume:{runtimeId:D}:{runtime.RespawnRetries}",
                ct: ct);

            // Re-arm the TTL guardrail on the freshly-resumed box.
            try
            {
                await _box.SetTtlAsync(existing.Id, _boxOptions.Current.DefaultTtlSeconds, runtimeId, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Respawn: TTL re-arm failed for box {BoxId} (runtime {RuntimeId}); BoxTtlExtenderJob will catch up.",
                    existing.Id, runtimeId);
            }
        }
        else
        {
            // Box gone → fork fresh from the active template, same as first provision.
            var template = await _db.RuntimeTemplates
                .Where(t => t.Status == RuntimeTemplateStatus.Active)
                .OrderByDescending(t => t.BuiltAt)
                .FirstOrDefaultAsync(ct);

            if (template is null)
            {
                _logger.LogError(
                    "Respawn: no Active RuntimeTemplate — cannot re-fork runtime {RuntimeId}", runtimeId);
                var failResult = runtime.TransitionTo(
                    RuntimeState.Failed,
                    "respawn:no_active_template",
                    "respawn-job",
                    "No active runtime template is registered. Build one with scripts/build-box-template.sh and activate it in Super Admin → Runtime Templates.");
                if (failResult.IsSuccess)
                {
                    await _db.SaveChangesAsync(ct);
                }
                return;
            }

            // No name on the fork body (per the contract) — ForkOrAdoptAsync
            // PATCHes the deterministic rt-{id} name onto the fork afterwards.
            var forkReq = new ForkBoxRequest(
                Type: BoxTypeMapper.FromSpec(runtime.Cpus, runtime.MemoryMb),
                Env: env,
                NoEnv: true,
                TtlSeconds: _boxOptions.Current.DefaultTtlSeconds);

            var forked = await BoxRuntimeProvisioning.ForkOrAdoptAsync(
                _box, _db, runtime, template.BoxId, forkReq,
                BoxRuntimeProvisioning.BuildBoxName(runtime.Id), _logger, ct);

            runtime.BoxId = forked.Id;
            runtime.TemplateBoxId = template.BoxId;
        }

        // ---- 5. Bump retries + transition Crashed → Booting ----
        runtime.RespawnRetries += 1;

        var metadata = JsonSerializer.Serialize(new
        {
            oldBoxId,
            newBoxId = runtime.BoxId,
            rebooted = existing is not null,
            retries = runtime.RespawnRetries,
        });

        var transition = runtime.TransitionTo(
            RuntimeState.Booting,
            "respawn:rebooted",
            "respawn-job",
            metadata);

        if (transition.IsFailure)
        {
            // Persist the box id so a redelivered job can resume instead of
            // colliding on the deterministic box name.
            await _db.SaveChangesAsync(ct);
            _logger.LogError(
                "Respawn: TransitionTo Booting failed for runtime {RuntimeId}: {Error}",
                runtimeId, transition.Error);
            return;
        }

        await _db.SaveChangesAsync(ct);

        // ---- 6. Env refresh on the rebooted box (fresh JWT) ----
        // Only the reboot path needs this — a fresh fork got the env at fork time.
        // Best-effort within the job window: if the box isn't up in time the daemon
        // boots with the old env; an expired token then surfaces as a failed
        // SignalR connect and the watcher schedules the next respawn.
        if (existing is not null)
        {
            try
            {
                var up = await BoxRuntimeProvisioning.WaitForBoxUpAsync(
                    _box, existing.Id, BoxRuntimeProvisioning.DefaultUpTimeout, ct);

                if (up is not null && BoxStates.IsUp(up.State))
                {
                    await BoxRuntimeProvisioning.RefreshEnvAndRestartDaemonAsync(
                        _box, existing.Id, env, runtimeId, ct);
                }
                else
                {
                    _logger.LogWarning(
                        "Respawn: box {BoxId} (runtime {RuntimeId}) did not come up within the env-refresh window (last state: {State}).",
                        existing.Id, runtimeId, up?.State ?? "unknown");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Respawn: env refresh failed for box {BoxId} (runtime {RuntimeId}); daemon boots with previous env.",
                    existing.Id, runtimeId);
            }
        }

        _logger.LogInformation(
            "Respawn: runtime {RuntimeId} now Booting on box {BoxId} (retry #{Retries}, rebooted={Rebooted})",
            runtimeId, runtime.BoxId, runtime.RespawnRetries, existing is not null);
    }

    /// <summary>
    /// Best-effort RuntimeRespawnTriggered emit. Logs and swallows on failure —
    /// observability must never abort a respawn.
    /// </summary>
    private async Task EmitRespawnTriggeredAsync(
        Guid runtimeId,
        int attemptNumber,
        string? lastFailureReason,
        string? lastFailureMessage,
        long? secondsSinceLastHeartbeat,
        CancellationToken ct)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                attemptNumber,
                lastFailureReason,
                lastFailureMessage,
                secondsSinceLastHeartbeat,
            });

            await _mediator.Send(
                new RecordRuntimeEventCommand(
                    RuntimeId: runtimeId,
                    Type: RuntimeEventTypes.RuntimeRespawnTriggered,
                    Severity: RuntimeEventSeverity.Warn,
                    Timestamp: DateTime.UtcNow,
                    DurationMs: null,
                    Payload: payload),
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Respawn: RuntimeRespawnTriggered emit failed for runtime {RuntimeId}; continuing with respawn.",
                runtimeId);
        }
    }
}
