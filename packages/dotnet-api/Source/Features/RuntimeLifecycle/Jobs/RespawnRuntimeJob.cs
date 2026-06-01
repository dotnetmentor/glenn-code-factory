using System.Text.Json;
using Hangfire;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Source.Features.Cloudflare.Models;
using Source.Features.Cloudflare.Services;
using Source.Features.DaemonVersions.Queries.ResolveDaemonVersion;
using Source.Features.FlyManagement;
using Source.Features.FlyManagement.Models;
using Source.Features.Projects.Models;
using Source.Features.RuntimeEvents.Commands;
using Source.Features.RuntimeEvents.Models;
using Source.Features.RuntimeImages.Models;
using Source.Features.RuntimeLifecycle.Configuration;
using Source.Features.RuntimeLifecycle.Models;
using Source.Features.RuntimeTokens.Services;
using Source.Features.SystemSettings.Services;
using Source.Infrastructure;

namespace Source.Features.RuntimeLifecycle.Jobs;

/// <summary>
/// Delayed Hangfire job that performs the actual destroy + create flow for a
/// crashed runtime. Scheduled by <c>ScheduleRespawnHandler</c> with a
/// retries-aware backoff; this class does not decide <i>whether</i> to respawn
/// — only how.
///
/// <para><b>Concurrency.</b> <see cref="DisableConcurrentExecutionAttribute"/>
/// keyed on the underlying job identity prevents two workers running the same
/// scheduled job at once. The 120-second timeout covers a Fly destroy + create
/// round trip with comfortable headroom.</para>
///
/// <para><b>Idempotency.</b> A pre-flight state check (<c>State == Crashed</c>)
/// makes the job safe to re-run: if the state already moved on (manual reset,
/// operator delete, an earlier successful respawn) we no-op. The destroy path
/// tolerates Fly's 404 ("already gone") because a redelivery of the schedule
/// could find the previous machine torn down already.</para>
/// </summary>
public class RespawnRuntimeJob
{
    private readonly ApplicationDbContext _db;
    private readonly FlyClient _fly;
    private readonly IRuntimeOptionsAccessor _runtimeOptions;
    private readonly IRuntimeTokenService _runtimeTokenService;
    private readonly IMediator _mediator;
    private readonly ISystemSettingsCipher _cipher;
    private readonly CloudflareApiClient _cloudflare;
    private readonly ILogger<RespawnRuntimeJob> _logger;

    public RespawnRuntimeJob(
        ApplicationDbContext db,
        FlyClient fly,
        IRuntimeOptionsAccessor runtimeOptions,
        IRuntimeTokenService runtimeTokenService,
        IMediator mediator,
        ISystemSettingsCipher cipher,
        CloudflareApiClient cloudflare,
        ILogger<RespawnRuntimeJob> logger)
    {
        _db = db;
        _fly = fly;
        _runtimeOptions = runtimeOptions;
        _runtimeTokenService = runtimeTokenService;
        _mediator = mediator;
        _cipher = cipher;
        _cloudflare = cloudflare;
        _logger = logger;
    }

    /// <summary>
    /// Hangfire entry point. Re-validates the pre-conditions (runtime exists
    /// and is still <see cref="RuntimeState.Crashed"/>), tears down the old
    /// Fly machine on a best-effort basis, creates a replacement on the same
    /// volume, bumps <see cref="ProjectRuntime.RespawnRetries"/>, and walks
    /// the runtime back to <see cref="RuntimeState.Booting"/>.
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

        if (string.IsNullOrEmpty(runtime.ImageDigest))
        {
            // We can't boot a machine without the OCI image digest the runtime
            // was originally provisioned from. Throw so Hangfire records a
            // failure — fixing this requires operator intervention (likely a
            // manual reset back to Pending so the provisioner can pick it up).
            throw new InvalidOperationException(
                $"Runtime {runtimeId} has no ImageDigest, cannot respawn");
        }

        // Pre-flight: Runtime.PublicApiUrl must be configured. Without it the
        // daemon would have no MAIN_API_URL to dial back at, and the new
        // machine would spin without ever talking to us. Mark the runtime
        // Failed with a structured reason so the operator can fix the config.
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
                "Runtime:PublicApiUrl is not configured in appsettings. Daemons would have no MAIN_API_URL to dial back at.");

            if (failResult.IsSuccess)
            {
                await _db.SaveChangesAsync(ct);
            }
            return;
        }

        var oldMachineId = runtime.FlyMachineId;

        // ---- 0.5. Emit the RuntimeRespawnTriggered observability event ----
        //
        // Audit item A11 / card task 7: the super-admin drawer needs a stable
        // marker each time we attempt a respawn so the operator can correlate
        // "machine N got swapped at HH:MM" with the timeline. We attribute the
        // attempt number BEFORE incrementing the column (so retry #1 emits 1,
        // not 0); the "last failure" context is the most recent transition
        // INTO Crashed for this runtime — its Reason/Metadata strings are the
        // structured error code + human message ScheduleRespawnHandler stored
        // on its way in here. Heartbeat lag is observability colour: how stale
        // the last reading was when we triggered the respawn.
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

        // ---- 1. Best-effort destroy of the old machine ----
        //
        // We pass force: true because by the time we're respawning, the runtime
        // is already in Crashed state and we explicitly want to scrap whatever
        // VM is associated with it. Without force, Fly returns
        //   412 Precondition Failed
        // for any machine that is currently `started` (i.e. has not been
        // gracefully stopped first), which is the common case here — a
        // bootstrap loop or a runaway daemon keeps the machine "running" from
        // Fly's perspective even though the runtime is broken. Hangfire would
        // then retry the destroy forever, and the respawn never gets to step 2.
        if (!string.IsNullOrEmpty(oldMachineId))
        {
            try
            {
                await _fly.DestroyMachineAsync(oldMachineId, force: true, runtimeId: runtimeId, ct: ct);
                _logger.LogInformation(
                    "Respawn: force-destroyed old machine {MachineId} for runtime {RuntimeId}",
                    oldMachineId, runtimeId);
            }
            catch (FlyApiException ex) when (ex.StatusCode == 404)
            {
                // Already gone — Fly cleaned it up, or a redelivered schedule
                // ran us twice. Either way we proceed with the create.
                _logger.LogInformation(
                    "Respawn: old machine {MachineId} already gone (404), continuing",
                    oldMachineId);
            }
            // Other Fly exceptions propagate — Hangfire will retry the job.
        }

        // ---- 2. Resolve the image to boot from ----
        //
        // <see cref="ProjectRuntime.ImageDigest"/> is stored as just
        // `sha256:<hex>` — a bare digest, no registry or repository. Passing
        // that as the Fly machine image triggers
        //   400 Bad Request — manifest unknown
        // because Fly resolves bare names against `docker-hub-mirror.fly.io/
        // library/`. Mirror the provisioner: re-resolve the currently active
        // <see cref="RuntimeImage"/> and use its
        // `{Registry}:{Tag}` form. (We deliberately do not pin to the
        // runtime's original image — respawning is a "use the current platform
        // image" operation; if an operator just promoted a new image to
        // Active, respawned VMs should land on it.)
        var image = await _db.RuntimeImages
            .Where(i => i.Status == RuntimeImageStatus.Active)
            .OrderByDescending(i => i.BuiltAt)
            .FirstOrDefaultAsync(ct);

        if (image is null)
        {
            _logger.LogError(
                "Respawn: no Active RuntimeImage — cannot respawn runtime {RuntimeId}", runtimeId);
            var failResult = runtime.TransitionTo(
                RuntimeState.Failed,
                "respawn:no_active_image",
                "respawn-job",
                "No active runtime image is registered. Ask an admin to activate one in Super Admin → Runtime Images.");
            if (failResult.IsSuccess)
            {
                await _db.SaveChangesAsync(ct);
            }
            return;
        }

        // ---- 2.5. Resolve the daemon bundle for cold-boot fetch ----
        //
        // Same shape as the provisioner: the runtime image only ships the
        // bootstrap script, which uses these three env vars to download +
        // verify the daemon tarball before exec'ing it. Without them the new
        // VM would come up running supervisord but with no daemon to start.
        var daemonResolveResult = await _mediator.Send(
            new ResolveDaemonVersionQuery("stable"), ct);
        if (daemonResolveResult.IsFailure)
        {
            _logger.LogWarning(
                "Respawn: no active daemon version for channel 'stable' — leaving runtime {RuntimeId} Crashed (will retry next Hangfire attempt): {Error}",
                runtimeId, daemonResolveResult.Error);
            // Throw so Hangfire retries — this is recoverable as soon as
            // an operator publishes a bundle.
            throw new InvalidOperationException(
                $"No active daemon version: {daemonResolveResult.Error}");
        }
        var daemon = daemonResolveResult.Value;

        // ---- 2.7. Subdomain + preview-port lookup ----
        //
        // Mirrors RuntimeProvisionerJob §1.6. Branches assigned through Phase
        // 3+ paths (CreateProject / CopyBranch / ForkBranchFromGit /
        // AttachGitBranch) have a SubdomainAssignment bound to them; legacy
        // pre-Phase-3 branches don't, and we skip the tunnel env trio for
        // those (daemon never starts cloudflared, runtime still boots).
        var subdomain = await _db.SubdomainAssignments
            .Where(s => s.AssignedBranchId == runtime.BranchId
                        && s.Status == SubdomainStatus.Assigned)
            .FirstOrDefaultAsync(ct);

        var previewPort = await _db.Projects
            .Where(p => p.Id == runtime.ProjectId)
            .Select(p => (int?)p.PreviewPort)
            .FirstOrDefaultAsync(ct) ?? Project.DefaultPreviewPort;

        // ---- 2.6. Mint a fresh runtime JWT ----
        //
        // The original JWT is bound to the old machine's lifetime — daemons
        // re-mint on cold-boot, so the respawned VM needs its own. Same
        // audit-before-issuance contract as the provisioner.
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
        var minted = mintResult.Value;

        // ---- 3. Build env + create the replacement machine on the same volume ----
        //
        // The env block is intentionally a 1:1 mirror of RuntimeProvisionerJob's
        // — same keys, same comments, same semantics. The whole point of this
        // change is that "respawn" and "first provision" must produce equivalent
        // machines, so the env shapes have to converge.
        var env = new Dictionary<string, string>
        {
            ["RUNTIME_ID"] = runtime.Id.ToString(),
            ["GLENN_RUNTIME_TOKEN"] = minted.Token,
            ["MAIN_API_URL"] = publicApiUrl,
            ["DAEMON_VERSION"] = daemon.Version,
            ["DAEMON_BUNDLE_URL"] = daemon.DownloadUrl,
            ["DAEMON_BUNDLE_SHA256"] = daemon.Sha256,
        };

        if (subdomain is not null)
        {
            env["TUNNEL_TOKEN"] = _cipher.Decrypt(subdomain.TunnelToken);
            env["PREVIEW_PORT"] = previewPort.ToString();
            env["PREVIEW_HOSTNAME"] = subdomain.Hostname;

            // Defensive Cloudflare ingress reconciliation on every respawn.
            // Same belt-and-braces logic as the provisioner: claim-time PUT
            // (AssignSubdomainToBranch) covers the happy path, this is the
            // catch-up for rows assigned pre-fix or rows that drifted because
            // the user changed PreviewPort while the runtime was crashed and
            // UpdateProjectPreviewPort's fan-out skipped them. Idempotent on
            // Cloudflare's side; one PUT per respawn.
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
                        "Respawn: reconciled tunnel {TunnelId} ingress to localhost:{PreviewPort} for runtime {RuntimeId}",
                        subdomain.TunnelId, previewPort, runtimeId);
                }
                catch (Exception ex)
                {
                    // Best-effort. Don't fail the respawn — the machine will
                    // boot and the daemon will heartbeat; UpdateProjectPreviewPort
                    // or the next respawn will reconcile.
                    _logger.LogWarning(
                        ex,
                        "Respawn: Cloudflare ingress PUT failed for tunnel {TunnelId} (runtime {RuntimeId}, port {PreviewPort}). Proceeding with boot; tunnel may briefly route to placeholder port.",
                        subdomain.TunnelId, runtimeId, previewPort);
                }
            }
        }
        else
        {
            _logger.LogInformation(
                "Respawn: runtime {RuntimeId} (branch {BranchId}) has no assigned subdomain — skipping preview-tunnel env vars (legacy or pre-Phase-3 branch).",
                runtimeId, runtime.BranchId);
        }

        var machineReq = new CreateMachineRequest(
            // Fly machine names: lowercase alphanumeric + underscores, max 30 chars.
            // Guid "N" format = 32 hex chars; prefix "rt_" + 27 chars = 30 total.
            Name: $"rt_{runtime.Id:N}".Substring(0, 30),
            Region: runtime.Region,
            Config: new MachineConfig(
                Image: $"{image.Registry}:{image.Tag}",
                Env: env,
                // Respawn boots the *same* runtime row on a new machine; reuse
                // the spec snapshot from when the runtime was first created so
                // a respawn never accidentally drifts to a different size. If
                // the user wants new specs, they edit the project and the next
                // ProjectRuntime row (new branch / new project) picks them up.
                Guest: new MachineGuest(
                    CpuKind: runtime.CpuKind,
                    Cpus: runtime.Cpus,
                    MemoryMb: runtime.MemoryMb,
                    PersistRootfs: "always"),
                Mounts: !string.IsNullOrEmpty(runtime.FlyVolumeId)
                    ? new List<MachineMount> { new(Volume: runtime.FlyVolumeId, Path: "/data") }
                    : new List<MachineMount>()));

        var newMachine = await _fly.CreateMachineAsync(machineReq, runtimeId: runtimeId, ct: ct);

        // ---- 4. Persist the new machine id, refresh image digest, bump retries, transition ----
        runtime.FlyMachineId = newMachine.Id;
        // Keep the runtime row's recorded image digest in sync with what we
        // actually booted — otherwise a future respawn that DOES try to use
        // `runtime.ImageDigest` (or any operator dashboard) would lie about
        // the running image. Mirrors the provisioner's bookkeeping.
        runtime.ImageDigest = $"{image.Registry}:{image.Tag}";
        runtime.RespawnRetries += 1;

        var metadata = JsonSerializer.Serialize(new
        {
            oldMachineId,
            newMachineId = newMachine.Id,
            retries = runtime.RespawnRetries,
        });

        var transition = runtime.TransitionTo(
            RuntimeState.Booting,
            "respawn:created",
            "respawn-job",
            metadata);

        if (transition.IsFailure)
        {
            // Defensive: Crashed -> Booting is a legal edge in the state graph.
            // If this fires, somebody changed the state machine. Log and don't
            // save — the audit row would lie about what happened.
            _logger.LogError(
                "Respawn: TransitionTo Booting failed for runtime {RuntimeId}: {Error}",
                runtimeId, transition.Error);
            return;
        }

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Respawn: runtime {RuntimeId} now Booting on machine {MachineId} (retry #{Retries})",
            runtimeId, newMachine.Id, runtime.RespawnRetries);
    }

    /// <summary>
    /// Best-effort RuntimeRespawnTriggered emit. Logs and swallows on failure
    /// — same observability-not-load-bearing principle as
    /// <see cref="RecordRuntimeEventCommandHandler"/>: a respawn attempt must
    /// not be aborted because the event store choked. The drawer falls back
    /// to RuntimeStateEvent rows ("Crashed → Booting") if this event is missing.
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
