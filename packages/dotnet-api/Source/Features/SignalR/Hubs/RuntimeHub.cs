using System.Security.Claims;
using System.Text.Json;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Source.Features.AgentPermissions.Models;
using Source.Features.AgentPermissions.Services;
using Source.Features.Conversations.Events;
using Source.Features.Conversations.Models;
using Source.Features.Conversations.Services;
using Source.Features.GitHub.Services;
using Source.Features.GitOps.Models;
using Source.Features.Health;
using Source.Features.Health.Services;
using Source.Features.Hooks.Models;
using Source.Features.RuntimeBootstrap.Contracts;
using Source.Features.RuntimeBootstrap.Queries;
using Source.Features.RuntimeCuration;
using Source.Features.RuntimeCuration.Commands;
using Source.Features.RuntimeCuration.Models;
using Source.Features.RuntimeEvents.Commands;
using Source.Features.RuntimeEvents.Models;
using Source.Features.RuntimeLifecycle.Commands;
using Source.Features.RuntimeLifecycle.Events;
using Source.Features.RuntimeLifecycle.Models;
using Source.Features.Projects.Services;
using Source.Features.ProjectSecrets.Services;
using Source.Features.RuntimeTokens.Models;
using Source.Features.SignalR.Contracts;
using Source.Features.SignalR.Events;
using Source.Features.SignalR.Services;
using Source.Features.SystemSettings.Services;
using Source.Infrastructure;
using Source.Infrastructure.Extensions;
using Source.Shared;
using TypedSignalR.Client;

namespace Source.Features.SignalR.Hubs;

/// <summary>
/// Daemon-facing hub. One connection per running Machine (per <see cref="ProjectRuntime"/>).
/// Connections are pinned to a single runtime via the <c>rt_runtime</c> claim on
/// the RuntimeToken JWT — a daemon cannot "switch" runtime mid-flight because
/// the claim is signed and bound to one runtime at issuance time.
///
/// <para><b>Auth.</b> JWT bearer via the <see cref="RuntimeTokenAuthenticationDefaults.SchemeName"/>
/// scheme. The token reaches the hub either as a standard
/// <c>Authorization: Bearer ...</c> header (HTTP transports) or as the
/// <c>?access_token=...</c> query string (WebSocket transport — browsers and
/// many WS clients can't send arbitrary headers). The query-string lift is
/// configured in <see cref="AuthenticationExtensions.AddRuntimeTokenAuthScheme"/>.
/// Belt-and-braces: this attribute and the endpoint-level policy on
/// <c>MapHub&lt;RuntimeHub&gt;</c> both enforce the scheme.</para>
/// </summary>
[Authorize(AuthenticationSchemes = RuntimeTokenAuthenticationDefaults.SchemeName)]
public class RuntimeHub : Hub<IRuntimeClient>, IRuntimeHub
{
    private readonly ApplicationDbContext _db;
    private readonly IMediator _mediator;
    private readonly IHubContext<AgentHub, IAgentClient> _agentHub;
    private readonly ITurnDispatcher _turnDispatcher;
    private readonly HealthSnapshotBuffer _healthBuffer;
    private readonly ServiceDownDetector _serviceDownDetector;
    private readonly SecretEncryptionService _encryption;
    private readonly IGithubAppTokenService _githubAppTokens;
    private readonly IAgentPermissionsResolver _agentPermissionsResolver;
    private readonly ISystemSettingsService _systemSettings;
    private readonly IAgentSecretsResolver _agentSecrets;
    private readonly IClock _clock;
    private readonly ILogger<RuntimeHub> _logger;

    public RuntimeHub(
        ApplicationDbContext db,
        IMediator mediator,
        IHubContext<AgentHub, IAgentClient> agentHub,
        ITurnDispatcher turnDispatcher,
        HealthSnapshotBuffer healthBuffer,
        ServiceDownDetector serviceDownDetector,
        SecretEncryptionService encryption,
        IGithubAppTokenService githubAppTokens,
        IAgentPermissionsResolver agentPermissionsResolver,
        ISystemSettingsService systemSettings,
        IAgentSecretsResolver agentSecrets,
        IClock clock,
        ILogger<RuntimeHub> logger)
    {
        _db = db;
        _mediator = mediator;
        _agentHub = agentHub;
        _turnDispatcher = turnDispatcher;
        _healthBuffer = healthBuffer;
        _serviceDownDetector = serviceDownDetector;
        _encryption = encryption;
        _githubAppTokens = githubAppTokens;
        _agentPermissionsResolver = agentPermissionsResolver;
        _systemSettings = systemSettings;
        _agentSecrets = agentSecrets;
        _clock = clock;
        _logger = logger;
    }

    public override async Task OnConnectedAsync()
    {
        // RuntimeToken JWT scheme has already validated the bearer token by the
        // time we get here — the [Authorize] attribute and the endpoint policy
        // both gate this method. We just need to project the verified claim
        // into our own Guid and confirm the ProjectRuntime row still exists.
        var claimRuntimeIdRaw = Context.User?.FindFirstValue(RuntimeTokenClaimNames.RuntimeId);
        if (!Guid.TryParse(claimRuntimeIdRaw, out var runtimeId))
        {
            _logger.LogWarning(
                "RuntimeHub rejected connection {ConnectionId}: missing or unparseable {ClaimName} claim.",
                Context.ConnectionId, RuntimeTokenClaimNames.RuntimeId);
            Context.Abort();
            return;
        }

        // Soft-deleted runtimes are filtered by the global query filter, so the
        // janitor's 30-day window doubles as a kill-switch for stale daemons.
        var runtime = await _db.ProjectRuntimes
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == runtimeId);
        if (runtime is null)
        {
            _logger.LogWarning(
                "RuntimeHub rejected connection {ConnectionId}: runtime {RuntimeId} not found or soft-deleted.",
                Context.ConnectionId, runtimeId);
            Context.Abort();
            return;
        }

        Context.Items["RuntimeId"] = runtime.Id;
        Context.Items["ProjectId"] = runtime.ProjectId;

        await Groups.AddToGroupAsync(Context.ConnectionId, $"runtime-{runtime.Id}");

        await _mediator.Publish(new RuntimeConnected(
            runtime.Id, runtime.ProjectId, Context.ConnectionId, DateTime.UtcNow));

        _logger.LogInformation(
            "RuntimeHub connected. Runtime {RuntimeId}, Project {ProjectId}, Connection {ConnectionId}",
            runtime.Id, runtime.ProjectId, Context.ConnectionId);

        // Bootstrap delivery for hooks + git config. The admin endpoints
        // (PUT /api/admin/runtimes/{id}/hooks, /git/auto-commit, /git/deploy-key)
        // push deltas to live daemons, but a daemon that connects after a write
        // must also see the latest config — the dedicated `getBootstrap` hub
        // method is in a follow-up backlog card, so for this card we lean on
        // UpdateConfig (the same channel hot-applies use) and fire a single
        // one-shot to the connecting caller carrying every known field. When
        // no row exists for the runtime we still send the payload with the
        // matching field as null (or the documented default for AutoCommit)
        // so the daemon can confidently fall back to its built-in baseline —
        // the wire signal is "we know, here is the state", not "we forgot to
        // tell you".
        var hooksJson = await _db.RuntimeHookConfigs
            .AsNoTracking()
            .Where(c => c.RuntimeId == runtime.Id)
            .Select(c => c.Json)
            .FirstOrDefaultAsync();
        var gitConfig = await _db.RuntimeGitConfigs
            .AsNoTracking()
            .Where(c => c.RuntimeId == runtime.Id)
            .Select(c => new { c.AutoCommit, c.DeployKey })
            .FirstOrDefaultAsync();
        // Default-on for AutoCommit when no row exists — matches the entity
        // default and the contract documented on RuntimeGitConfig.AutoCommit.
        var bootstrapAutoCommit = gitConfig?.AutoCommit ?? true;
        var bootstrapDeployKey = gitConfig?.DeployKey;
        try
        {
            await Clients.Caller.UpdateConfig(new ConfigUpdatePayload(
                RuntimeId: runtime.Id,
                Version: "1",
                RuntimeToken: null,
                HooksJson: hooksJson,
                AutoCommit: bootstrapAutoCommit,
                DeployKey: bootstrapDeployKey));
            _logger.LogInformation(
                "RuntimeHub bootstrap UpdateConfig pushed for runtime {RuntimeId} (hooksConfigured={HasHooks}, gitConfigured={HasGit}, autoCommit={AutoCommit}, deployKeyPresent={DeployKey}).",
                runtime.Id,
                hooksJson is not null,
                gitConfig is not null,
                bootstrapAutoCommit,
                bootstrapDeployKey is not null);
        }
        catch (Exception ex)
        {
            // Bootstrap push is best-effort. A failed push only delays the
            // daemon learning about hooks/git config until the next admin
            // write or reconnect — not worth aborting the connection over.
            _logger.LogWarning(ex,
                "RuntimeHub bootstrap UpdateConfig push failed for runtime {RuntimeId}; daemon will pick up config on next admin write or reconnect.",
                runtime.Id);
        }

        await base.OnConnectedAsync();
    }

    /// <summary>
    /// Daemon-to-server heartbeat. Bumps <see cref="ProjectRuntime.LastHeartbeatAt"/>
    /// to the server clock so the staleness detector (heartbeat-respawn spec)
    /// has a fresh "last seen" timestamp to read.
    ///
    /// <para><b>Hot path.</b> Fires every few seconds for every connected daemon
    /// — potentially thousands of concurrent runtimes. Deliberately:</para>
    /// <list type="bullet">
    ///   <item>does not raise a domain event (no fan-out, no event-store row per beat);</item>
    ///   <item>does not validate the payload beyond the auth-by-context check
    ///         performed at connect-time;</item>
    ///   <item>logs at <see cref="LogLevel.Trace"/> on the happy path —
    ///         heartbeat success is one of the noisiest events in the system.</item>
    /// </list>
    ///
    /// <para><b>Auth.</b> The runtime id is read from <c>Context.Items["RuntimeId"]</c>,
    /// which <see cref="OnConnectedAsync"/> populates <i>only</i> if the
    /// connection passed the RuntimeToken JWT gate (and the runtime row was
    /// found). If the entry is missing, this is a connection that should
    /// never have made it past handshake — log and return rather than throw,
    /// so a bug in the hub or a malformed daemon cannot crash the connection
    /// (and through retry/reconnect, take out the hub instance).</para>
    ///
    /// <para><b>Server clock wins.</b> <see cref="HeartbeatPayload.EmittedAt"/>
    /// is preserved on the wire for telemetry but is not used here — see the
    /// payload doc for why.</para>
    /// </summary>
    public async Task Heartbeat(HeartbeatPayload payload)
    {
        // Auth by Context.Items: OnConnectedAsync stashes RuntimeId only on a
        // verified handshake. A missing entry means either we somehow let an
        // un-verified connection through, or the hub instance state is broken.
        // Either way, do not throw — silent no-op + warning is the safer
        // choice for a method that runs thousands of times per second.
        if (!Context.Items.TryGetValue("RuntimeId", out var rt) || rt is not Guid runtimeId)
        {
            _logger.LogWarning(
                "RuntimeHub.Heartbeat called on connection {ConnectionId} with no RuntimeId in Context.Items; ignoring.",
                Context.ConnectionId);
            return;
        }

        // Default query — soft-deleted runtimes are filtered by the global
        // query filter on ProjectRuntime, so a janitor-marked or hard-deleted
        // runtime simply won't be found and we drop the beat. The 30-day
        // soft-delete window doubles as the kill-switch.
        var runtime = await _db.ProjectRuntimes
            .FirstOrDefaultAsync(r => r.Id == runtimeId);
        if (runtime is null)
        {
            _logger.LogWarning(
                "RuntimeHub.Heartbeat for runtime {RuntimeId} on connection {ConnectionId}: runtime not found or soft-deleted; ignoring.",
                runtimeId, Context.ConnectionId);
            return;
        }

        // Server clock — see method doc + payload doc. Daemon-emitted
        // EmittedAt is intentionally ignored for the LastHeartbeatAt update.
        var receivedAt = _clock.UtcNow;
        runtime.LastHeartbeatAt = receivedAt;

        // Daemon's authoritative "the run I am executing right now is X, or null
        // if idle". Persisted every beat so ReconcileStaleSessionsJob can reap
        // sessions stuck Running/Canceling that the daemon is provably no longer
        // driving (lost terminal event). Overwrite unconditionally — a null here
        // means the daemon is genuinely idle, which is exactly the signal we want.
        runtime.ActiveSessionId = payload.ActiveSessionId;

        // runtime-observability-super-admin — persist the latest disk sample
        // and sysstats snapshot from the heartbeat. Both are optional on the
        // wire (older daemons don't ship them, mid-boot daemons may not have
        // sampled yet). Only update when present so a heartbeat that drops the
        // sysstats key doesn't overwrite the prior good snapshot with null.
        if (payload.Disk is not null)
        {
            runtime.LastDiskUsedBytes = payload.Disk.UsedBytes;
            runtime.LastDiskTotalBytes = payload.Disk.TotalBytes;
            // payload.Disk.SampledAt is the daemon's wall clock — preserved for
            // the drawer's "sampled X seconds ago" hint. UTC by convention; the
            // daemon emits ISO-8601 UTC and SignalR's JSON protocol parses to
            // DateTime with Kind=Utc.
            runtime.LastDiskSampledAt = payload.Disk.SampledAt;
        }
        if (!string.IsNullOrEmpty(payload.SysstatsSnapshotJson))
        {
            runtime.LastSysstatsSnapshot = payload.SysstatsSnapshotJson;
        }

        // Phase D — append the health snapshot to the in-memory rolling buffer
        // before SaveChanges. The buffer is process-local; if SaveChanges
        // throws (transient DB blip), the heartbeat itself is lost — but the
        // buffer is non-persistent and best-effort, so missing one row in the
        // window is acceptable. Order picked so the detector below sees the
        // freshest data when it computes outage windows.
        var snapshot = new HealthSnapshot(
            ReceivedAt: receivedAt,
            CpuPct: payload.CpuPercent,
            MemUsedMb: payload.MemoryUsedMb,
            DiskUsedPct: payload.DiskUsedPct,
            SupervisedServicesUp: payload.SupervisedServicesUp ?? Array.Empty<string>(),
            ActiveSessionId: payload.ActiveSessionId);
        _healthBuffer.Append(runtimeId, snapshot);

        await _db.SaveChangesAsync();

        // Phase D Card 2 — service-down detection. Compares the spec's required
        // services against payload.SupervisedServicesUp; published events drive
        // RestartService dispatch in DispatchRestartServiceHandler. Skipped
        // silently when the daemon hasn't yet reported services (null
        // SupervisedServicesUp on the wire) — older daemons / a still-booting
        // daemon shouldn't trip the detector.
        //
        // Direct _mediator.Publish (not interceptor-driven) because these are
        // transient detection events, not entity state transitions — there's
        // no row that "changed" carrying them, and we don't need them in the
        // StoredDomainEvents audit table. Same dispatch end-state as the
        // interceptor path, just without the persistence half.
        if (payload.SupervisedServicesUp is not null)
        {
            // Spec lives on Project, not ProjectRuntime — see
            // `project-level-runtime-spec`. Cheap projection: only the spec
            // JSON column, no entity tracking, no fan-out load.
            var projectSpec = await _db.Projects
                .AsNoTracking()
                .Where(p => p.Id == runtime.ProjectId)
                .Select(p => p.Spec)
                .FirstOrDefaultAsync();

            var events = _serviceDownDetector.Detect(
                runtimeId,
                projectSpec,
                payload.SupervisedServicesUp,
                receivedAt);
            foreach (var evt in events)
            {
                await _mediator.Publish(evt);
            }
        }

        _logger.LogTrace(
            "RuntimeHub.Heartbeat applied for runtime {RuntimeId} (daemon {DaemonVersion}, cpu={CpuPercent}, memMb={MemoryUsedMb}, diskPct={DiskUsedPct}, svcUp={SvcCount}, activeSession={ActiveSessionId}).",
            runtimeId,
            payload.DaemonVersion,
            payload.CpuPercent,
            payload.MemoryUsedMb,
            payload.DiskUsedPct,
            payload.SupervisedServicesUp?.Count ?? -1,
            payload.ActiveSessionId);
    }

    /// <summary>
    /// One-shot bootstrap fetch: the daemon calls this exactly once per boot and
    /// receives the full <see cref="BootstrapPayloadV2"/> (runtime spec, env vars,
    /// hooks config, MCP catalog, repo + deploy key) in a single hub roundtrip.
    /// Replaces the multi-controller dance the daemon would otherwise have to
    /// orchestrate (<c>/bootstrap-env</c>, <c>/bootstrap-mcp-config</c>, …) — one
    /// authenticated call gets everything it needs to walk its install / clone /
    /// startup state machine.
    ///
    /// <para><b>Auth.</b> The connection-level <c>RuntimeId</c> stash is the
    /// source of truth, identical to <see cref="Heartbeat"/>. Missing it means
    /// the connection slipped past the handshake gate — log + throw <see cref="HubException"/>
    /// because unlike Heartbeat, the daemon's boot path actively waits on this
    /// response and a silent drop would hang the boot.</para>
    ///
    /// <para><b>MCP URL composition.</b> The handler reads the public API base
    /// from the <c>Runtime:PublicApiUrl</c> SystemSetting (via
    /// <see cref="Source.Features.RuntimeLifecycle.Configuration.IRuntimeOptionsAccessor"/>);
    /// no scheme/host plumbing from the hub. The previous approach used the
    /// inbound request's Host header but Cloudflare forwards that as
    /// <c>localhost:5338</c>, which the daemon (running on a separate Fly VM)
    /// cannot reach — it would hang on the MCP handshake.</para>
    ///
    /// <para><b>Failures.</b> A missing / soft-deleted runtime row is the only
    /// hard error and is surfaced as <see cref="HubException"/> — the daemon's
    /// boot logic decides retry vs abort. Every sub-source (secrets, MCPs,
    /// hooks, repo) tolerates absence: empty list / null with a logged warning,
    /// never a thrown exception. See <see cref="GetBootstrapQueryHandler"/>
    /// for the per-sub-source semantics.</para>
    /// </summary>
    public async Task<BootstrapPayloadV2> GetBootstrap()
    {
        var runtimeId = ResolveRuntimeIdFromContext(nameof(GetBootstrap));
        if (runtimeId is null)
        {
            // Unlike Heartbeat (silent drop), the daemon awaits this response
            // — surface the failure so the boot path can see + handle it.
            throw new HubException("missing runtime claim");
        }

        // MCP URL composition now lives in the handler and reads
        // Runtime:PublicApiUrl from SystemSettings — see the xmldoc above for
        // why the inbound Host header is unusable (Cloudflare forwards it as
        // localhost:5338, which the Fly daemon can't reach).
        var result = await _mediator.Send(new GetBootstrapQuery(runtimeId.Value));
        if (!result.IsSuccess)
        {
            // Surface the handler's reason verbatim — the daemon logs this and
            // the operator sees it in next-boot diagnostics.
            throw new HubException(result.Error ?? "bootstrap failed");
        }
        return result.Value!;
    }

    /// <summary>
    /// Daemon-invoked BYOK fetch — returns the Anthropic API key and Claude
    /// Code OAuth token the daemon should use for the next prompt, with a
    /// per-project → host-env fall-back chain. Per-prompt fetch (rather than
    /// bake into bootstrap) so a freshly-rotated key takes effect on the
    /// daemon's next turn without a respawn.
    ///
    /// <list type="number">
    ///   <item>Resolve the project from the connection's runtime claim
    ///         (<c>Context.Items["ProjectId"]</c>, populated in
    ///         <see cref="OnConnectedAsync"/> from the <c>rt_runtime</c>
    ///         JWT claim).</item>
    ///   <item>Read the project row (<c>AsNoTracking</c>) for the encrypted
    ///         envelope columns; decrypt via
    ///         <see cref="SecretEncryptionService"/> when set.</item>
    ///   <item>Fall back to <c>ANTHROPIC_API_KEY</c> /
    ///         <c>CLAUDE_CODE_OAUTH_TOKEN</c> from the host environment when
    ///         the project column is null. (No <c>SystemSettings</c> stage —
    ///         no Anthropic-flavoured keys are registered in the catalog
    ///         today; if that lands later it slots in between the project
    ///         column and the env var.)</item>
    /// </list>
    ///
    /// <para><b>Auth.</b> Connection-level <c>RuntimeId</c> stash is the
    /// source of truth (same contract as <see cref="GetBootstrap"/>);
    /// missing it means the connection bypassed the handshake gate — throw
    /// <see cref="HubException"/> so the daemon's awaiting promise rejects
    /// rather than hangs.</para>
    ///
    /// <para><b>Logging hygiene.</b> We log the project id and BOOLEAN
    /// presence flags for each secret, never the values themselves.
    /// Decryption failures escape as <see cref="System.Security.Cryptography.CryptographicException"/>
    /// — the daemon sees a generic invocation failure on its end, the server
    /// logs the underlying reason, and the operator sees it via the
    /// SystemSettings tooling for the master key.</para>
    /// </summary>
    public async Task<AgentSecretsDto> GetSecrets()
    {
        var runtimeId = ResolveRuntimeIdFromContext(nameof(GetSecrets));
        if (runtimeId is null)
        {
            // Daemon awaits this — a silent drop would hang the next turn.
            throw new HubException("missing runtime claim");
        }

        // ProjectId was stashed in Context.Items at connect time alongside
        // RuntimeId. If the connection slipped past with a runtime claim but
        // no resolved project, that's a "shouldn't happen" — we still tolerate
        // it by walking the DB rather than throwing, so a stale Items bag
        // doesn't break the BYOK path.
        Guid projectId;
        if (Context.Items.TryGetValue("ProjectId", out var pj) && pj is Guid stashed)
        {
            projectId = stashed;
        }
        else
        {
            var resolved = await _db.ProjectRuntimes
                .AsNoTracking()
                .Where(r => r.Id == runtimeId.Value)
                .Select(r => (Guid?)r.ProjectId)
                .FirstOrDefaultAsync();
            if (resolved is null)
            {
                _logger.LogWarning(
                    "RuntimeHub.GetSecrets: runtime {RuntimeId} has no resolvable project — returning empty secrets envelope.",
                    runtimeId.Value);
                return new AgentSecretsDto(CursorApiKey: null);
            }
            projectId = resolved.Value;
        }

        var cursorKey = await _agentSecrets.ResolveCursorApiKeyAsync(
            projectId,
            Context.ConnectionAborted);

        _logger.LogInformation(
            "RuntimeHub.GetSecrets: project {ProjectId} runtime {RuntimeId} issued secrets (hasCursor={HasCursor}).",
            projectId,
            runtimeId.Value,
            cursorKey is not null);

        return new AgentSecretsDto(CursorApiKey: cursorKey);
    }

    /// <summary>
    /// Daemon-invoked: fetch the effective <see cref="AgentPermissionsConfig"/>
    /// for the project this connection's runtime is pinned to. Resolution is
    /// "project override or system defaults" — never merged — and is delegated
    /// entirely to <see cref="IAgentPermissionsResolver"/>. The daemon calls
    /// this once at the start of every turn and caches the result for that
    /// turn's lifetime, so mid-turn config edits don't create inconsistent
    /// decisions inside a single tool sequence (see the agent-sdk-permissions
    /// spec's "per-turn fetch" architectural note).
    ///
    /// <para><b>Auth.</b> Same shape as <see cref="GetSecrets"/>: the
    /// connection-level <c>RuntimeId</c> stash is the source of truth, and the
    /// pinned <c>ProjectId</c> is read from <c>Context.Items</c> with a DB
    /// fall-back for the "shouldn't happen" path where the items bag is stale.
    /// A daemon cannot ask for another project's config — the runtime claim
    /// fully determines which project's permissions are returned.</para>
    ///
    /// <para><b>Failure modes</b> raise <see cref="HubException"/> so the
    /// daemon's awaiting promise rejects rather than hangs:
    /// <list type="bullet">
    ///   <item>missing runtime claim — handshake bypassed;</item>
    ///   <item>project not found — soft-deleted between connect and call.</item>
    /// </list>
    /// </para>
    ///
    /// <para><b>Logging hygiene.</b> Project id, runtime id, the resolved
    /// permission mode, and list sizes are safe to log — they describe the
    /// guardrail surface, not user data. Individual tool-pattern strings are
    /// also non-sensitive (they're the same strings the super-admin types into
    /// the settings UI).</para>
    /// </summary>
    public async Task<AgentPermissionsConfig> GetAgentPermissions()
    {
        var runtimeId = ResolveRuntimeIdFromContext(nameof(GetAgentPermissions));
        if (runtimeId is null)
        {
            // Daemon awaits this — a silent drop would hang the next turn.
            throw new HubException("missing runtime claim");
        }

        // Same Context.Items → DB fall-back contract as GetSecrets / GetRepoAccessToken.
        Guid projectId;
        if (Context.Items.TryGetValue("ProjectId", out var pj) && pj is Guid stashed)
        {
            projectId = stashed;
        }
        else
        {
            var resolved = await _db.ProjectRuntimes
                .AsNoTracking()
                .Where(r => r.Id == runtimeId.Value)
                .Select(r => (Guid?)r.ProjectId)
                .FirstOrDefaultAsync();
            if (resolved is null)
            {
                _logger.LogWarning(
                    "RuntimeHub.GetAgentPermissions: runtime {RuntimeId} has no resolvable project.",
                    runtimeId.Value);
                throw new HubException("project not found");
            }
            projectId = resolved.Value;
        }

        var config = await _agentPermissionsResolver.ResolveForProjectAsync(
            projectId,
            Context.ConnectionAborted);

        _logger.LogInformation(
            "RuntimeHub.GetAgentPermissions: project {ProjectId} runtime {RuntimeId} resolved " +
            "(mode={PermissionMode}, skip={AllowDangerouslySkip}, allowed={AllowedCount}, " +
            "disallowed={DisallowedCount}, additionalDirs={AdditionalDirsCount}).",
            projectId,
            runtimeId.Value,
            config.PermissionMode,
            config.AllowDangerouslySkipPermissions,
            config.AllowedTools.Count,
            config.DisallowedTools.Count,
            config.AdditionalDirectories.Count);

        return config;
    }

    /// <summary>
    /// Daemon-invoked: mint a fresh GitHub-App installation token scoped to
    /// the project's repository, returned alongside its UTC expiry timestamp.
    /// The daemon uses the token as the basic-auth password for the HTTPS
    /// clone (and any subsequent <c>git fetch</c> / <c>push</c> within the
    /// token window).
    ///
    /// <para><b>Auth model.</b> Same shape as <see cref="GetSecrets"/>: the
    /// connection's pinned <c>RuntimeId</c> (and the derived <c>ProjectId</c>)
    /// are the only inputs the server trusts. <paramref name="repoFullName"/>
    /// is verified against the GithubRepository row that the project resolves
    /// to — a daemon cannot ask for an arbitrary repo's token by passing a
    /// different <c>owner/name</c>, even if that repo lives in the same
    /// installation.</para>
    ///
    /// <para><b>Failure modes</b> (all raise <see cref="HubException"/> so the
    /// daemon's awaiting promise rejects rather than hangs):
    /// <list type="bullet">
    ///   <item>missing runtime claim — handshake bypassed;</item>
    ///   <item>project not found — soft-deleted between connect and call;</item>
    ///   <item>no GithubRepository row linking the project's installation +
    ///         owner/name pair — admin hasn't synced repositories yet;</item>
    ///   <item>repo mismatch — daemon asked for a repo that isn't this
    ///         project's repo (logged as a warning; tampering signal).</item>
    /// </list>
    /// </para>
    ///
    /// <para><b>Logging hygiene.</b> Project id, runtime id, repo full name
    /// and expiry are safe to log. The token itself is NEVER logged.</para>
    /// </summary>
    public async Task<RepoAccessToken> GetRepoAccessToken(string repoFullName)
    {
        var runtimeId = ResolveRuntimeIdFromContext(nameof(GetRepoAccessToken));
        if (runtimeId is null)
        {
            throw new HubException("missing runtime claim");
        }

        Guid projectId;
        if (Context.Items.TryGetValue("ProjectId", out var pj) && pj is Guid stashed)
        {
            projectId = stashed;
        }
        else
        {
            var resolved = await _db.ProjectRuntimes
                .AsNoTracking()
                .Where(r => r.Id == runtimeId.Value)
                .Select(r => (Guid?)r.ProjectId)
                .FirstOrDefaultAsync();
            if (resolved is null)
            {
                throw new HubException("project not found");
            }
            projectId = resolved.Value;
        }

        var project = await _db.Projects
            .AsNoTracking()
            .Where(p => p.Id == projectId)
            .Select(p => new
            {
                p.GithubInstallationId,
                p.GithubRepoOwner,
                p.GithubRepoName,
            })
            .FirstOrDefaultAsync();
        if (project is null)
        {
            throw new HubException("project not found");
        }

        // Detached project — the workspace disconnected the installation and
        // the FK got SET NULL. There's no installation to mint a token against;
        // surface this so the daemon stops asking until reconnect.
        if (project.GithubInstallationId is not { } projectInstallationId)
        {
            throw new HubException("project is detached — reconnect its GitHub installation first");
        }

        var installation = await _db.GithubInstallations
            .AsNoTracking()
            .Where(i => i.Id == projectInstallationId)
            .Select(i => new { i.InstallationId })
            .FirstOrDefaultAsync();
        if (installation is null)
        {
            throw new HubException("installation not found");
        }

        // Source of truth for "what repo is this project bound to" is the
        // Project row itself (GithubRepoOwner + GithubRepoName), not the
        // webhook-maintained GithubRepositories cache. The cache can be stale
        // or missing for a project that legitimately exists on GitHub —
        // depending on it here used to throw "repository not found" and the
        // daemon would silently retry the clone five times before giving up.
        if (string.IsNullOrWhiteSpace(project.GithubRepoOwner)
            || string.IsNullOrWhiteSpace(project.GithubRepoName))
        {
            _logger.LogWarning(
                "RuntimeHub.GetRepoAccessToken: project {ProjectId} has empty owner/name (owner='{Owner}', name='{Name}'); cannot mint a token.",
                projectId, project.GithubRepoOwner, project.GithubRepoName);
            throw new HubException("project repository coordinates are missing");
        }

        var boundFullName = $"{project.GithubRepoOwner}/{project.GithubRepoName}";

        // Cross-check: the daemon must ask for the same repo this connection's
        // project is bound to. Mismatch is a tampering signal — log loudly.
        if (!string.Equals(repoFullName, boundFullName, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "RuntimeHub.GetRepoAccessToken: runtime {RuntimeId} project {ProjectId} requested repo " +
                "{RequestedRepo} but is bound to {BoundRepo}; rejecting.",
                runtimeId.Value, projectId, repoFullName, boundFullName);
            throw new HubException("repo not authorized for this runtime");
        }

        var scoped = await _githubAppTokens.MintScopedTokenByNameAsync(
            installation.InstallationId,
            boundFullName,
            Context.ConnectionAborted);

        _logger.LogInformation(
            "RuntimeHub.GetRepoAccessToken: minted token for project {ProjectId} runtime {RuntimeId} repo {RepoFullName} (expiresAt={ExpiresAt:O}).",
            projectId, runtimeId.Value, boundFullName, scoped.ExpiresAt);

        return new RepoAccessToken(scoped.Token, scoped.ExpiresAt);
    }

    /// <summary>
    /// Inner step of <see cref="GetSecrets"/>: walk the per-project envelope
    /// → SystemSettings (optional) → host env-var fall-back chain for one
    /// secret slot. Returns the resolved plaintext or null if no source
    /// supplied a value.
    ///
    /// <para><paramref name="systemSettingKey"/>, when supplied, slots in
    /// between the per-project envelope and the host env var — same shape as
    /// the OpenCode Zen key's three-tier resolution chain (project envelope
    /// → SystemSettings <c>OPENCODE_ZEN_API_KEY</c> → env <c>OPENCODE_ZEN_API_KEY</c>).
    /// The Anthropic + OAuth slots do not register a system-wide key in the
    /// catalog today and pass <c>null</c> here, mirroring the original behavior.</para>
    /// </summary>
    private async Task<string?> ResolveSecretAsync(
        string? envelope,
        Guid projectId,
        string envVarName,
        string? systemSettingKey = null)
    {
        if (!string.IsNullOrEmpty(envelope))
        {
            try
            {
                var (ciphertext, nonce, dekVersion) = ProjectByokEnvelope.Unpack(envelope);
                return await _encryption.DecryptAsync(projectId, ciphertext, nonce, dekVersion, default);
            }
            catch (Exception ex)
            {
                // Don't leak the value or the raw exception to the daemon;
                // log enough server-side that an operator can correlate to
                // the project row. Fall through to env-var so a corrupt row
                // doesn't take the whole runtime down.
                _logger.LogError(ex,
                    "RuntimeHub.GetSecrets: failed to unpack/decrypt envelope for project {ProjectId} (env-var={EnvVar}); falling back.",
                    projectId, envVarName);
            }
        }

        // SystemSettings tier — only consulted when the caller registers a key
        // in the catalog. Today only the OpenCode Zen key uses this stage.
        if (!string.IsNullOrEmpty(systemSettingKey))
        {
            try
            {
                var fromSystem = _systemSettings.Get(systemSettingKey);
                if (!string.IsNullOrEmpty(fromSystem))
                {
                    return fromSystem;
                }
            }
            catch (Exception ex)
            {
                // SystemSettings unreachable (boot ordering, cache miss inside a
                // disposed scope) shouldn't break the daemon's BYOK fetch; just
                // fall through to the env var.
                _logger.LogWarning(ex,
                    "RuntimeHub.GetSecrets: SystemSettings lookup failed for key {SystemSettingKey} (project {ProjectId}); falling back to env var.",
                    systemSettingKey, projectId);
            }
        }

        var fromEnv = Environment.GetEnvironmentVariable(envVarName);
        return string.IsNullOrEmpty(fromEnv) ? null : fromEnv;
    }

    /// <summary>
    /// Daemon-to-server signal: bootstrap finished and the daemon is ready to
    /// accept turns. Drives the <c>Bootstrapping → Online</c> transition (the
    /// only legal "the daemon is alive" edge per
    /// <see cref="RuntimeLifecycle.RuntimeStateMachine"/>). Idempotent — a
    /// double-call from a daemon that crash-replayed its boot trampoline
    /// becomes a logged no-op rather than an illegal-transition error.
    ///
    /// <para><b>Auth.</b> Same contract as <see cref="Heartbeat"/>: the
    /// connection-level <c>RuntimeId</c> stash is the source of truth; missing
    /// it means the connection somehow bypassed the handshake gate — log +
    /// throw <see cref="HubException"/> because the daemon awaits the ack.</para>
    /// </summary>
    public async Task RuntimeReady()
    {
        var runtimeId = ResolveRuntimeIdFromContext(nameof(RuntimeReady));
        if (runtimeId is null)
        {
            throw new HubException("missing runtime claim");
        }

        var result = await _mediator.Send(new RuntimeReadyCommand(runtimeId.Value));
        if (!result.IsSuccess)
        {
            // The handler logs the underlying reason; we surface it back to the
            // daemon so it can decide whether to retry. Failure paths today are
            // (1) runtime not found, (2) illegal transition (state != Bootstrapping
            // and != Online — Online is the idempotent success path).
            throw new HubException(result.Error ?? "runtime ready failed");
        }
    }

    /// <summary>
    /// Daemon-to-server error report. Persists a <c>RuntimeErrorReport</c> row
    /// for operator triage — boot failures, hook crashes, sandbox refusals,
    /// and any other "the daemon survived but something went wrong" signal the
    /// daemon decides is worth recording. Append-only audit, no state-machine
    /// implications: a runtime can keep running after reporting an error, the
    /// row is for diagnostics only.
    ///
    /// <para><b>Auth.</b> Connection-level <c>RuntimeId</c> stash, same contract
    /// as <see cref="Heartbeat"/>. Unlike <see cref="GetBootstrap"/> /
    /// <see cref="RuntimeReady"/>, this is fire-and-forget: the daemon
    /// doesn't block on the ack, so a missing-context drop is silent (mirrors
    /// the audit-fan-out methods like <see cref="HookStarted"/>).</para>
    /// </summary>
    public async Task ReportError(ErrorReportPayload payload)
    {
        var runtimeId = ResolveRuntimeIdFromContext(nameof(ReportError));
        if (runtimeId is null) return;

        var result = await _mediator.Send(new ReportRuntimeErrorCommand(runtimeId.Value, payload));
        if (!result.IsSuccess)
        {
            _logger.LogWarning(
                "RuntimeHub.ReportError: ReportRuntimeError failed for runtime {RuntimeId} (category={Category}): {Error}",
                runtimeId.Value, payload.Category, result.Error);
        }
    }

    /// <summary>
    /// Daemon-to-server disk-pressure transition (Phase D Card 3). Persists a
    /// <c>RuntimeDiskPressureEvent</c> audit row and fans the event out to the
    /// <c>project-{ProjectId}</c> SignalR group so the project dashboard can
    /// surface the warning without polling.
    ///
    /// <para><b>Auth.</b> Connection-level <c>RuntimeId</c> claim, same
    /// contract as <see cref="Heartbeat"/>. Fire-and-forget — daemon doesn't
    /// block on the ack and a missing-context drop is silent (mirrors
    /// <see cref="ReportError"/>).</para>
    /// </summary>
    public async Task ReportDiskPressure(DiskPressurePayload payload)
    {
        var runtimeId = ResolveRuntimeIdFromContext(nameof(ReportDiskPressure));
        if (runtimeId is null) return;

        var result = await _mediator.Send(
            new Health.Commands.RecordDiskPressureCommand(runtimeId.Value, payload));
        if (!result.IsSuccess)
        {
            _logger.LogWarning(
                "RuntimeHub.ReportDiskPressure: RecordDiskPressure failed for runtime {RuntimeId} (level={Level}): {Error}",
                runtimeId.Value, payload.Level, result.Error);
        }
    }

    /// <summary>
    /// Daemon-to-server agent event push. The daemon's <i>primary</i> inbound
    /// channel: every <see cref="AgentEvent"/> it produces while running a
    /// session lands here. The hub:
    ///
    /// <list type="number">
    ///   <item>Loads the target <see cref="AgentSession"/> + parent
    ///         <c>Conversation</c> (one round-trip; conversation is needed for
    ///         <c>ProjectId</c> and the denormalized counters).</item>
    ///   <item>Computes the next per-session monotonic <c>Sequence</c>. The
    ///         <c>(SessionId, Sequence)</c> composite PK on
    ///         <see cref="AgentEvent"/> is the safety net — concurrent inserts
    ///         on the same session would race, but the spec is per-session
    ///         linear (one daemon owns one session at a time), so a true
    ///         collision is the pathological case we drop and let the daemon
    ///         resend.</item>
    ///   <item>Inserts the <see cref="AgentEvent"/> row with the server's UTC
    ///         clock as <c>CreatedAt</c> (daemon's <see cref="EmitEventPayload.EmittedAt"/>
    ///         is intentionally not used for ordering — server clock wins, just
    ///         like <see cref="Heartbeat"/>).</item>
    ///   <item>Captures <see cref="AgentSession.ClaudeSessionId"/> on the first
    ///         payload that contains a recognized property — any of
    ///         <c>sdkSessionId</c> (the daemon's <c>system:init</c> event, our
    ///         expected source), <c>newClaudeSessionId</c> (the daemon's
    ///         <c>TurnCompleted</c> envelope, safety net), or
    ///         <c>claudeSessionId</c> (canonical legacy name, kept for tests +
    ///         any future producer that uses it). Heuristic peek via
    ///         <see cref="JsonDocument"/>; if the daemon is sloppy and the
    ///         data isn't valid JSON we silently move on — the audit row is
    ///         still written.</item>
    ///   <item>Walks the session status state machine on terminal events:
    ///         TurnStarted → Running, TurnCompleted → Succeeded,
    ///         TurnFailed → Failed (with optional <c>reason</c> extracted from
    ///         the JSON), TurnCanceled → Canceled. Non-terminal events leave
    ///         status alone.</item>
    ///   <item>Bumps <c>Conversation.LastActivityAt</c> + <c>EventCount</c>
    ///         (denormalized counters for the conversation list view).</item>
    ///   <item>Raises <see cref="AgentEventEmitted"/> on the session entity so
    ///         the <c>DomainEventInterceptor</c> + auto-publish pipeline fans
    ///         it to the broadcast handler after <c>SaveChangesAsync</c> commits.</item>
    /// </list>
    ///
    /// <para><b>Auth.</b> Same contract as <see cref="Heartbeat"/>: missing
    /// <c>RuntimeId</c> in <c>Context.Items</c> means the connection somehow
    /// bypassed the handshake gate — log + drop, never throw. An unknown
    /// <see cref="EmitEventPayload.SessionId"/> is also a soft drop because the
    /// daemon may be replaying after a restart and the session may have been
    /// deleted; we don't want a stale daemon to crash the hub.</para>
    ///
    /// <para><b>One <c>SaveChangesAsync</c> per call.</b> Insert the event,
    /// mutate the session, mutate the conversation, raise the domain event,
    /// commit once. If the rare composite-PK collision fires we log and drop
    /// rather than retry — the daemon owns the resend semantics.</para>
    /// </summary>
    public async Task EmitEvent(EmitEventPayload payload)
    {
        // Auth by Context.Items — same contract as Heartbeat. A missing
        // RuntimeId means we somehow let an un-verified connection through;
        // log and drop, never throw.
        if (!Context.Items.TryGetValue("RuntimeId", out var rt) || rt is not Guid runtimeId)
        {
            _logger.LogWarning(
                "RuntimeHub.EmitEvent called on connection {ConnectionId} with no RuntimeId in Context.Items; ignoring.",
                Context.ConnectionId);
            return;
        }

        // Bootstrap / shutdown broadcast. The daemon reuses EmitEvent as a
        // best-effort runtime-scope channel for progress events that don't
        // belong to any AgentSession yet (see daemon's BootstrapOrchestrator
        // / ShutdownCoordinator — they pass an empty/null sessionId by
        // design). There's no session to attach the event to, so we
        // short-circuit before the DB round-trip. A future runtime-scope hub
        // method will replace this overload; until then, the broadcast
        // doubles as a readiness signal: payloads carrying
        // {"type":"runtime_ready"} or {"type":"bootstrap_completed"} drive the
        // Bootstrapping → Online transition for daemons that haven't (yet)
        // invoked the typed RuntimeReady hub method. Other runtime-scope
        // events (e.g. bootstrap_progress, bootstrap_stage_completed) are
        // soft-dropped — they're observability-only.
        if (payload.SessionId is not Guid sessionId)
        {
            await TryHandleRuntimeScopeBroadcastAsync(runtimeId, payload);
            return;
        }

        // Single round-trip: pull session + parent conversation. Conversation
        // is needed for ProjectId on the domain event AND for the denormalized
        // counters we bump below.
        var session = await _db.AgentSessions
            .Include(s => s.Conversation)
            .FirstOrDefaultAsync(s => s.Id == sessionId);
        if (session is null)
        {
            _logger.LogWarning(
                "RuntimeHub.EmitEvent for runtime {RuntimeId} referenced unknown session {SessionId}; ignoring.",
                runtimeId, sessionId);
            return;
        }

        // Compute next sequence atomically-ish. With the per-session linearity
        // the spec guarantees, this read-then-write is safe. The composite PK
        // on (SessionId, Sequence) catches the pathological concurrent-insert
        // case below — we don't retry, we drop and let the daemon resend.
        var maxSeq = await _db.AgentEvents
            .Where(e => e.SessionId == sessionId)
            .MaxAsync(e => (long?)e.Sequence);
        var nextSeq = (maxSeq ?? -1L) + 1L;

        var nowUtc = DateTime.UtcNow;
        // Cursor-native row: copy first-class fields straight from the payload
        // onto AgentEvent. The daemon populates the per-kind cluster relevant
        // to its Kind; nullable columns left null on irrelevant clusters.
        var newEvent = new AgentEvent
        {
            SessionId = sessionId,
            Sequence = nextSeq,
            Kind = payload.Kind,
            // AgentEvent is deliberately NOT IAuditable (rows are immutable
            // once written; UpdatedAt would be meaningless), so the auto-stamp
            // interceptor doesn't touch it. We set CreatedAt explicitly here.
            CreatedAt = nowUtc,
            // Per-kind first-class columns (cursor-native-chat-ux card 3).
            Text = payload.Text,
            ThinkingDurationMs = payload.ThinkingDurationMs,
            CallId = payload.ToolCallId,
            ToolName = payload.ToolName,
            ToolStatus = payload.ToolStatus,
            Args = payload.ToolArgs,
            Result = payload.ToolResult,
            ArgsTruncated = payload.ToolArgsTruncated,
            ResultTruncated = payload.ToolResultTruncated,
            RunStatus = payload.RunStatus,
            StatusMessage = payload.StatusMessage,
            TaskId = payload.TaskId,
            TaskTitle = payload.TaskTitle,
        };
        _db.AgentEvents.Add(newEvent);

        // Cursor agent id capture. The daemon emits the SDK-assigned agent id
        // on the first system:init frame which lands as the AgentEvent's
        // CreatedAt-stamped row; the daemon also passes the captured id back
        // as `newCursorAgentId` / `newAgentId` inside the legacy EventData
        // JSON envelope on the terminal TurnCompleted payload. Read both
        // surfaces tolerantly so we capture the id regardless of which
        // emission lands first / whether the daemon has migrated to the typed
        // wire yet.
        if (!string.IsNullOrWhiteSpace(payload.EventData) && payload.EventData != "{}")
        {
            try
            {
                using var doc = JsonDocument.Parse(payload.EventData);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    if (doc.RootElement.TryGetProperty("newCursorAgentId", out var prop) &&
                        prop.ValueKind == JsonValueKind.String)
                    {
                        session.AgentId = prop.GetString();
                    }
                    else if (doc.RootElement.TryGetProperty("newAgentId", out prop) &&
                             prop.ValueKind == JsonValueKind.String)
                    {
                        session.AgentId = prop.GetString();
                    }
                }
            }
            catch (JsonException)
            {
                // Malformed JSON on the legacy envelope must never block the audit row.
            }
        }

        // Status state machine — driven off the typed RunStatus column on the
        // payload (cursor-native-chat-ux card 3). The session lifecycle
        // (Pending → Running → Succeeded/Failed/Canceled) flips on each
        // terminal Cursor status frame; non-Status kinds (Text, ToolUse, ...)
        // leave session.Status alone.
        if (payload.Kind == AgentEventKind.Status && payload.RunStatus is { } runStatus)
        {
            switch (runStatus)
            {
                case AgentEventRunStatus.Running:
                    if (session.Status == AgentSessionStatus.Pending)
                    {
                        session.Status = AgentSessionStatus.Running;
                        session.StartedAt = nowUtc;
                    }
                    break;
                case AgentEventRunStatus.Finished:
                    if (session.Status != AgentSessionStatus.Succeeded)
                    {
                        session.Succeed();
                    }
                    break;
                case AgentEventRunStatus.Error:
                    session.Fail(payload.StatusMessage);
                    await TryRecoverStaleAgentIdAsync(session, payload.StatusMessage);
                    break;
                case AgentEventRunStatus.Cancelled:
                    session.MarkCanceled(reason: null);
                    break;
                case AgentEventRunStatus.Expired:
                    session.Fail("expired");
                    break;
                case AgentEventRunStatus.Creating:
                    // Informational — leave session.Status alone.
                    break;
            }
        }

        // Per-turn run result — the daemon ships the aggregate piggybacked on
        // the terminal Status payload. Upsert keyed by session id so a
        // duplicate emit (rare; daemon resend) wins idempotently.
        if (payload.RunResult is { } runResult)
        {
            var existing = await _db.RunResults
                .FirstOrDefaultAsync(r => r.SessionId == sessionId);
            var artifactsJson = JsonSerializer.Serialize(
                runResult.Artifacts,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (existing is null)
            {
                _db.RunResults.Add(new RunResult
                {
                    SessionId = sessionId,
                    DurationMs = runResult.DurationMs,
                    Model = runResult.Model,
                    GitBranch = runResult.GitBranch,
                    GitPrUrl = runResult.GitPrUrl,
                    ArtifactsJson = artifactsJson,
                    CreatedAt = nowUtc,
                });
            }
            else
            {
                existing.DurationMs = runResult.DurationMs;
                existing.Model = runResult.Model;
                existing.GitBranch = runResult.GitBranch;
                existing.GitPrUrl = runResult.GitPrUrl;
                existing.ArtifactsJson = artifactsJson;
            }
        }

        // Denormalized counters on the parent conversation — kept in sync here
        // so the conversation-list query doesn't need to join through events.
        session.Conversation.LastActivityAt = nowUtc;
        session.Conversation.EventCount += 1;

        // Build the typed event projection so downstream broadcast handlers
        // can fan out the full payload without re-loading the row.
        var dto = BuildAgentEventDto(newEvent);

        // Raise the domain event from the session. The DomainEventInterceptor
        // collects + persists + publishes after SaveChanges — the broadcast
        // handler listens for this and pushes to web clients. Every EmitEvent
        // raises this regardless of Kind — the broadcast handler decides what
        // (if anything) to surface to the UI.
        session.RecordEventEmitted(new AgentEventEmitted(
            ConversationId: session.ConversationId,
            ProjectId: session.Conversation.ProjectId,
            BranchId: session.Conversation.BranchId,
            Kind: payload.Kind,
            Event: dto,
            OccurredAt: nowUtc));

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (
            ex.InnerException?.Message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase) == true ||
            ex.InnerException?.Message.Contains("unique constraint", StringComparison.OrdinalIgnoreCase) == true)
        {
            // Rare: concurrent inserts on the same session collided on
            // (SessionId, Sequence). Per spec the daemon owns one session at
            // a time so this should never happen — but if it does, log and
            // drop. The daemon's resend logic will retry the emit.
            _logger.LogWarning(ex,
                "RuntimeHub.EmitEvent: sequence collision on session {SessionId} at seq {Sequence}; dropping. Daemon should resend.",
                sessionId, nextSeq);
        }
    }

    /// <summary>
    /// Project a freshly-inserted <see cref="AgentEvent"/> row into the
    /// discriminated <see cref="AgentEventDto"/> wire shape. Mirrors the
    /// projection in <c>ConversationsController.GetEvents</c> — kept in two
    /// places because the hub's broadcast path doesn't want a controller
    /// dependency and the controller doesn't want a hub dependency.
    /// </summary>
    private static AgentEventDto BuildAgentEventDto(AgentEvent e)
    {
        return e.Kind switch
        {
            AgentEventKind.PromptReceived => new PromptReceivedEventDto(
                e.SessionId, e.Sequence, e.CreatedAt, e.Text ?? string.Empty),
            AgentEventKind.AssistantText => new AssistantTextEventDto(
                e.SessionId, e.Sequence, e.CreatedAt, e.Text ?? string.Empty),
            AgentEventKind.Thinking => new ThinkingEventDto(
                e.SessionId, e.Sequence, e.CreatedAt, e.Text ?? string.Empty, e.ThinkingDurationMs),
            AgentEventKind.ToolUse => new ToolUseEventDto(
                e.SessionId,
                e.Sequence,
                e.CreatedAt,
                CallId: e.CallId ?? string.Empty,
                Name: e.ToolName ?? string.Empty,
                Status: e.ToolStatus ?? AgentEventToolStatus.Running,
                Args: e.Args,
                Result: e.Result,
                ArgsTruncated: e.ArgsTruncated ?? false,
                ResultTruncated: e.ResultTruncated ?? false),
            AgentEventKind.Status => new StatusEventDto(
                e.SessionId,
                e.Sequence,
                e.CreatedAt,
                Status: e.RunStatus ?? AgentEventRunStatus.Creating,
                Message: e.StatusMessage),
            AgentEventKind.Task => new TaskEventDto(
                e.SessionId,
                e.Sequence,
                e.CreatedAt,
                TaskId: e.TaskId,
                Title: e.TaskTitle),
            _ => throw new InvalidOperationException(
                $"Unknown AgentEventKind {e.Kind} on session {e.SessionId} seq {e.Sequence}"),
        };
    }

    private async Task TryRecoverStaleAgentIdAsync(AgentSession session, string? errorString)
    {
        if (errorString is null) return;
        if (!errorString.Contains("agent not found", StringComparison.OrdinalIgnoreCase)
            && !errorString.Contains("No conversation found with session ID", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (_db.Database.ProviderName == "Microsoft.EntityFrameworkCore.InMemory")
        {
            var stale = await _db.AgentSessions
                .Where(s => s.ConversationId == session.ConversationId && s.AgentId != null)
                .ToListAsync();
            foreach (var s in stale)
            {
                s.AgentId = null;
            }
        }
        else
        {
            await _db.AgentSessions
                .Where(s => s.ConversationId == session.ConversationId && s.AgentId != null)
                .ExecuteUpdateAsync(setters => setters.SetProperty(s => s.AgentId, (string?)null), CancellationToken.None);
        }
        _logger.LogWarning(
            "Cleared stale AgentId on conversation {ConversationId} after sdk_error from session {SessionId}.",
            session.ConversationId, session.Id);
    }

    /// <summary>
    /// Inspect a runtime-scope (sessionId-less) <see cref="EmitEvent"/> payload
    /// and, when its <see cref="EmitEventPayload.EventData"/> JSON announces
    /// runtime readiness — <c>{"type":"runtime_ready"}</c> or
    /// <c>{"type":"bootstrap_completed"}</c> — drive the same
    /// <c>Bootstrapping → Online</c> transition the typed
    /// <see cref="RuntimeReady"/> hub method would. This is the
    /// belt-and-braces path: published daemons that bypass the typed
    /// <c>RuntimeReady</c> invoke (or whose typed-call ack is dropped on a
    /// flaky transport) still succeed at flipping state because their
    /// telemetry-grade "I'm done bootstrapping" broadcasts double as the
    /// readiness signal.
    ///
    /// <para>Failures are logged at warn but never thrown — the daemon
    /// fire-and-forgets these broadcasts and we don't want to break the hub
    /// connection on a transient DB hiccup. Idempotency is delegated to
    /// <see cref="RuntimeReadyCommand"/> itself, which short-circuits when the
    /// runtime is already <see cref="RuntimeState.Online"/>.</para>
    /// </summary>
    private async Task TryHandleRuntimeScopeBroadcastAsync(Guid runtimeId, EmitEventPayload payload)
    {
        // Cheap fast path — only AssistantText is the carrier shape used today
        // (see daemon's BootstrapOrchestrator.#emitRuntimeEvent /
        // SignalRClient.reportBootstrapProgress). Other event kinds have no
        // runtime-scope semantics; soft-drop them to avoid the JSON parse cost.
        if (payload.Kind != AgentEventKind.AssistantText)
        {
            _logger.LogDebug(
                "RuntimeHub.EmitEvent for runtime {RuntimeId} arrived without a SessionId and Kind={Kind}; ignoring (no runtime-scope handler).",
                runtimeId, payload.Kind);
            return;
        }

        string? broadcastType = null;
        try
        {
            using var doc = JsonDocument.Parse(payload.EventData);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("type", out var typeProp) &&
                typeProp.ValueKind == JsonValueKind.String)
            {
                broadcastType = typeProp.GetString();
            }
        }
        catch (JsonException)
        {
            // Malformed payload — treat as unknown and soft-drop, same as we do
            // on the session path.
        }

        // Both runtime_ready and bootstrap_completed mean "the daemon finished
        // booting"; the daemon emits stage progress as bootstrap_progress /
        // bootstrap_stage_completed (which we deliberately ignore here — those
        // are observability-only) and then fires bootstrap_completed once
        // every stage including ReportReadyStage has succeeded. Treating them
        // as synonyms is safe: RuntimeReadyCommand is idempotent, so a daemon
        // that emits both back-to-back gets one transition + one no-op.
        if (broadcastType is "runtime_ready" or "bootstrap_completed")
        {
            var result = await _mediator.Send(new RuntimeReadyCommand(runtimeId));
            if (result.IsSuccess)
            {
                _logger.LogInformation(
                    "RuntimeHub.EmitEvent: runtime {RuntimeId} bootstrap broadcast '{BroadcastType}' drove Booting/Bootstrapping -> Online (or was idempotent no-op).",
                    runtimeId, broadcastType);
            }
            else
            {
                // Fire-and-forget: log + swallow. The daemon's resend loop
                // (re-emitting bootstrap_completed on every reconnect, per
                // BootstrapOrchestrator) gives us another chance.
                _logger.LogWarning(
                    "RuntimeHub.EmitEvent: runtime {RuntimeId} bootstrap broadcast '{BroadcastType}' failed to transition: {Error}",
                    runtimeId, broadcastType, result.Error);
            }
            return;
        }

        _logger.LogDebug(
            "RuntimeHub.EmitEvent for runtime {RuntimeId} arrived without a SessionId (broadcast type='{BroadcastType}'); ignoring.",
            runtimeId, broadcastType ?? "<unknown>");
    }

    /// <summary>
    /// Daemon-to-server per-message cost report (legacy Claude path). The
    /// deployed daemon fires this from its <c>onRawMessage</c> hook once per
    /// assistant message that carries a <c>usage</c> block. Each call brings
    /// the incremental tokens for one message + a derived USD cost; the hub
    /// accumulates them onto <see cref="AgentSession"/> so that at session
    /// end the row carries the per-turn total without a final aggregation
    /// pass.
    ///
    /// <para><b>Accumulation, not replacement.</b> If a turn produces N
    /// assistant messages this method is invoked N times for the same
    /// <paramref name="sessionId"/>. We add to the existing column values
    /// rather than overwrite — that's what makes the running total at
    /// session end correct. A SQL-level <c>UPDATE … SET = COALESCE() + @v</c>
    /// keeps the round-trip down to one statement and dodges the
    /// load-mutate-save race.</para>
    ///
    /// <para><b>Auth.</b> Connection-level <c>RuntimeId</c> claim, same
    /// contract as <see cref="Heartbeat"/>. <paramref name="containerId"/>
    /// is observability-only — we don't trust the daemon's self-reported
    /// container id for authorization; the runtime binding off
    /// <c>Context.Items["RuntimeId"]</c> is the gate. Missing-context is a
    /// silent drop, matching every other fire-and-forget telemetry method.
    /// </para>
    ///
    /// <para><b>Soft drop on unknown session.</b> The daemon's resend loop
    /// can fire after the session row was deleted (rare, but: session
    /// cleanup, branch nuke, etc.). Log + return rather than throwing — a
    /// pristine cost report should never break the hub connection.</para>
    ///
    /// <para><b>Coexists with <see cref="EmitEvent"/>.</b> The newer
    /// <c>/workspace/packages/daemon</c> path folds cost into the
    /// <c>TurnCompleted</c> event's JSON payload and is parsed by
    /// <c>RecordSessionCostHandler</c> after the terminal event commits.
    /// Both paths target the same <see cref="AgentSession"/> columns; only
    /// one of them stamps for a given turn (the legacy daemon never emits
    /// cost inside <c>TurnCompleted</c>, the new daemon does not call this
    /// method). They will not collide.</para>
    /// </summary>
    public async Task ReportSessionCost(string containerId, Guid sessionId, ReportSessionCostPayload payload)
    {
        var runtimeId = ResolveRuntimeIdFromContext(nameof(ReportSessionCost));
        if (runtimeId is null) return;

        // Single-statement accumulate. ExecuteUpdateAsync skips change
        // tracking + interceptors and emits a bare UPDATE — fast path,
        // zero allocations, and no race between "read existing total" and
        // "write new total" if two messages land near-simultaneously.
        var rowsAffected = await _db.AgentSessions
            .Where(s => s.Id == sessionId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(s => s.TotalCostUsd,
                    s => (s.TotalCostUsd ?? 0m) + payload.TotalCostUsd)
                .SetProperty(s => s.InputTokens,
                    s => (s.InputTokens ?? 0) + payload.InputTokens)
                .SetProperty(s => s.OutputTokens,
                    s => (s.OutputTokens ?? 0) + payload.OutputTokens)
                .SetProperty(s => s.CacheWriteTokens,
                    s => (s.CacheWriteTokens ?? 0) + payload.CacheCreationTokens)
                .SetProperty(s => s.CacheReadTokens,
                    s => (s.CacheReadTokens ?? 0) + payload.CacheReadTokens));

        if (rowsAffected == 0)
        {
            _logger.LogWarning(
                "RuntimeHub.ReportSessionCost for runtime {RuntimeId} container {ContainerId} referenced unknown session {SessionId}; ignoring (totalCostUsd={TotalCostUsd}).",
                runtimeId.Value, containerId, sessionId, payload.TotalCostUsd);
            return;
        }

        _logger.LogDebug(
            "RuntimeHub.ReportSessionCost: accumulated cost for session {SessionId} (runtime {RuntimeId}): +${TotalCostUsd:F6} (in={InputTokens}, out={OutputTokens}, cacheRead={CacheReadTokens}, cacheWrite={CacheWriteTokens}).",
            sessionId, runtimeId.Value, payload.TotalCostUsd, payload.InputTokens, payload.OutputTokens, payload.CacheReadTokens, payload.CacheCreationTokens);
    }

    /// <summary>
    /// Daemon-to-server: the daemon refused a <c>StartTurn</c> because another
    /// turn is still in flight on the same runtime. The single-turn invariant
    /// lives on the daemon side (<c>TurnRunner.start()</c>); when it trips,
    /// the daemon sends this payload up so the server can:
    ///
    /// <list type="number">
    ///   <item>Cross-check the runtime claim — the rejected session must
    ///         belong to a conversation whose project is owned by the calling
    ///         daemon's runtime. Mismatch is a hard <see cref="HubException"/>,
    ///         mirroring the hook hub methods.</item>
    ///   <item>Flip the rejected <see cref="AgentSession"/> to
    ///         <see cref="AgentSessionStatus.Failed"/> via the rich-entity
    ///         <see cref="AgentSession.MarkRefused"/> method, which stamps
    ///         <c>CancelReason="daemon_refused_concurrent"</c>,
    ///         <c>CompletedAt=UtcNow</c>, and raises
    ///         <see cref="SessionRefused"/> for the audit trail.</item>
    ///   <item>Fan out the same payload to the project's UI clients via
    ///         <see cref="IAgentClient.TurnRefused"/> so the chat panel can
    ///         surface the refusal without polling.</item>
    /// </list>
    ///
    /// <para><b>Idempotency.</b> A rejected session that is already terminal
    /// (Succeeded / Failed / Canceled) is a no-op on persistence — the
    /// rich-entity method swallows the second call so we don't double-raise
    /// <see cref="SessionRefused"/>. The fan-out is also skipped on the
    /// already-terminal path; replay-on-reconnect is out of scope here.</para>
    ///
    /// <para><b>Auth.</b> Two-layer claim check, mirroring <see cref="EmitEvent"/>:
    /// the connection-level <c>RuntimeId</c> must be present, and the runtime
    /// resolved from the rejected session's project must equal it. A daemon
    /// claiming a peer's session is a hard error — different from the silent
    /// <c>log+return</c> on a missing context entry.</para>
    /// </summary>
    public async Task TurnRefused(TurnRefusedPayload payload)
    {
        var runtimeId = ResolveRuntimeIdFromContext(nameof(TurnRefused));
        if (runtimeId is null) return;

        // Single round-trip: pull session + parent conversation. We need the
        // conversation for ProjectId on the runtime cross-check and for the
        // fan-out group resolution.
        var session = await _db.AgentSessions
            .Include(s => s.Conversation)
            .FirstOrDefaultAsync(s => s.Id == payload.SessionId);
        if (session is null)
        {
            // Unknown session — daemon may be replaying after a server restart
            // that lost state, or referencing a phantom id. Drop silently.
            _logger.LogWarning(
                "RuntimeHub.TurnRefused for runtime {RuntimeId} referenced unknown session {SessionId}; ignoring.",
                runtimeId.Value, payload.SessionId);
            return;
        }

        // Cross-check: the rejected session's project must be owned by the
        // calling runtime. Hard fail on mismatch — a daemon claiming a peer's
        // session is exactly the case the per-method claim check guards against.
        var sessionRuntimeId = await _db.ProjectRuntimes
            .Where(r => r.ProjectId == session.Conversation.ProjectId)
            .Select(r => (Guid?)r.Id)
            .FirstOrDefaultAsync();
        if (sessionRuntimeId is null || sessionRuntimeId.Value != runtimeId.Value)
        {
            throw new HubException("runtime claim mismatch");
        }

        // Idempotent terminal flip via the rich-entity method. Already-terminal
        // sessions return Success without raising SessionRefused — we still
        // skip the fan-out below because the UI already saw the rejection on
        // the first call.
        var wasTerminalBefore = session.Status is AgentSessionStatus.Succeeded
            or AgentSessionStatus.Failed
            or AgentSessionStatus.Canceled;
        session.MarkRefused("daemon_refused_concurrent");
        await _db.SaveChangesAsync();

        if (wasTerminalBefore)
        {
            _logger.LogInformation(
                "RuntimeHub.TurnRefused: session {SessionId} was already terminal; skipping fan-out.",
                payload.SessionId);
            return;
        }

        try
        {
            await _agentHub.Clients
                .Group($"project-{session.Conversation.ProjectId}")
                .TurnRefused(payload);
        }
        catch (Exception ex)
        {
            // Fan-out failure does NOT roll back the terminal flip — the
            // session row is the durable record, the UI relay is best-effort.
            _logger.LogWarning(ex,
                "RuntimeHub.TurnRefused: broadcast failed for session {SessionId} (project {ProjectId}); persistence is unaffected.",
                payload.SessionId, session.Conversation.ProjectId);
        }
    }

    /// <summary>
    /// Daemon-to-server ack for an <see cref="ApplyRuntimeSpecDeltaPayload"/>
    /// push. The daemon ran the additive mise + supervisord work for a given
    /// <see cref="RuntimeProposal"/> and is now reporting success or failure.
    /// We project the connection's <c>rt_runtime</c> + <c>rt_project</c> claims
    /// out of <see cref="HubCallerContext.User"/> (the same JWT that gated
    /// <see cref="OnConnectedAsync"/>) and dispatch
    /// <see cref="RecordApplyResultCommand"/> through MediatR — the handler
    /// owns the Status flip + project-group fan-out.
    ///
    /// <para><b>Fire-and-forget.</b> The daemon doesn't await any response;
    /// we don't need to return anything. A logged warning on the rare handler
    /// failure is enough — the proposal row stays in its previous state and
    /// the daemon can retry the ack.</para>
    ///
    /// <para><b>Auth.</b> Claims-based, not <c>Context.Items</c>-based — this
    /// method needs the project id too (the ack handler cross-checks both),
    /// and the <c>rt_project</c> claim is the source of truth on the JWT.
    /// Missing / unparseable claims are a silent log+drop because this is a
    /// hot path and the connection-level <see cref="OnConnectedAsync"/>
    /// already gated the JWT itself.</para>
    /// </summary>
    public async Task RuntimeSpecDeltaApplied(RuntimeSpecDeltaApplyResultPayload payload)
    {
        var runtimeIdRaw = Context.User?.FindFirstValue(RuntimeTokenClaimNames.RuntimeId);
        var projectIdRaw = Context.User?.FindFirstValue(RuntimeTokenClaimNames.ProjectId);
        if (!Guid.TryParse(runtimeIdRaw, out var runtimeId) ||
            !Guid.TryParse(projectIdRaw, out var projectId))
        {
            _logger.LogWarning(
                "RuntimeHub.RuntimeSpecDeltaApplied called on connection {ConnectionId} without valid rt_runtime / rt_project claims; ignoring.",
                Context.ConnectionId);
            return;
        }

        var result = await _mediator.Send(new RecordApplyResultCommand(runtimeId, projectId, payload));
        if (!result.IsSuccess)
        {
            _logger.LogWarning(
                "RuntimeHub.RuntimeSpecDeltaApplied: RecordApplyResult failed for proposal {ProposalId} (runtime {RuntimeId}, project {ProjectId}): {Error}",
                payload.ProposalId, runtimeId, projectId, result.Error);
        }
    }

    /// <summary>
    /// Daemon-to-server: a hook process has begun. Inserts a fresh
    /// <see cref="HookExecution"/> row (the lifecycle / completion fields stay
    /// blank until <see cref="HookCompleted"/> or <see cref="HookConfigError"/>
    /// fills them in) and fans the start out to the project's UI clients so the
    /// chat panel can render the "running…" affordance.
    ///
    /// <para><b>Auth.</b> Two-layer claim check, mirroring <see cref="Heartbeat"/>
    /// + <see cref="EmitEvent"/>: the connection-level RuntimeId stash from
    /// <see cref="OnConnectedAsync"/> must be present (else the connection
    /// somehow bypassed the handshake gate — log + drop), and the daemon's
    /// claimed <see cref="HookStartedPayload.RuntimeId"/> must match it (a
    /// daemon may only report hooks for its own runtime — mismatch is a hard
    /// <see cref="HubException"/>, not a silent drop, so a buggy or hostile
    /// daemon surfaces immediately on the JS console).</para>
    ///
    /// <para><b>Idempotency.</b> A daemon retry on the same
    /// <see cref="HookStartedPayload.ExecutionId"/> must not duplicate the row
    /// or throw a unique-key error — we existence-check first. The fan-out is
    /// still emitted on retry so a tab that connected after the original
    /// HookStarted catches up via the live channel; replay-on-reconnect is
    /// out of scope for this card.</para>
    ///
    /// <para><b>Group convention.</b> Fan-out targets <c>project-{ProjectId}</c>
    /// on <see cref="AgentHub"/> via <see cref="IHubContext{THub, T}"/> — the
    /// same group the existing <c>BroadcastAgentEventHandler</c> and
    /// <c>BroadcastRuntimeStateChangedHandler</c> use, so any browser tab
    /// already subscribed to the project sees hook events without an extra
    /// join. Resolved by reading <c>ProjectRuntime.ProjectId</c> off the
    /// runtime row — one DB hit, no caching.</para>
    /// </summary>
    public async Task HookStarted(HookStartedPayload payload)
    {
        var runtimeId = ResolveRuntimeIdFromContext(nameof(HookStarted));
        if (runtimeId is null) return;
        if (runtimeId.Value != payload.RuntimeId)
        {
            // Hard fail — hub layer assumes a daemon only ever reports for its
            // own runtime. Different from the silent log+return on a missing
            // Context.Items entry: that's a "shouldn't happen, defense-in-depth"
            // case; this is a "daemon is lying" case and the JS console should
            // see it.
            throw new HubException("runtime claim mismatch");
        }

        // Idempotent insert — a daemon retry on the same ExecutionId must not
        // duplicate the row or throw a unique-key error. Existence check first
        // (option (b) from the brief): one extra read, but cleaner than catching
        // DbUpdateException and reasoning about which exception variant fired.
        var alreadyStarted = await _db.HookExecutions
            .AnyAsync(h => h.Id == payload.ExecutionId);

        var projectId = await _db.ProjectRuntimes
            .Where(r => r.Id == payload.RuntimeId)
            .Select(r => (Guid?)r.ProjectId)
            .FirstOrDefaultAsync();
        if (projectId is null)
        {
            _logger.LogWarning(
                "RuntimeHub.HookStarted: runtime {RuntimeId} not found (or soft-deleted); dropping execution {ExecutionId}.",
                payload.RuntimeId, payload.ExecutionId);
            return;
        }

        if (!alreadyStarted)
        {
            var execution = new HookExecution
            {
                Id = payload.ExecutionId,
                RuntimeId = payload.RuntimeId,
                ConversationId = payload.ConversationId,
                TurnId = payload.TurnId,
                HookPoint = payload.HookPoint,
                HookName = payload.HookName,
                Cmd = payload.Cmd,
                FeedbackMode = payload.FeedbackMode,
                StartedAt = payload.StartedAt,
                OutputTail = string.Empty,
                OutputHash = string.Empty,
            };
            _db.HookExecutions.Add(execution);
            await _db.SaveChangesAsync();
        }

        try
        {
            await _agentHub.Clients
                .Group($"project-{projectId.Value}")
                .HookStarted(payload);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "RuntimeHub.HookStarted: broadcast failed for execution {ExecutionId} (project {ProjectId}); persistence is unaffected.",
                payload.ExecutionId, projectId.Value);
        }
    }

    /// <summary>
    /// Daemon-to-server: a single stdout line streamed live from a still-running
    /// hook. <i>Not persisted</i> — pure live UX. The 16 KiB tail is captured
    /// once on <see cref="HookCompleted"/>, so streaming every line into the
    /// row would just churn the same bytes.
    ///
    /// <para><b>Soft drop on unknown execution.</b> If we receive progress for
    /// an execution we have no record of (daemon retry after a server restart
    /// that lost the in-memory subscription, replayed packet, or just a buggy
    /// daemon), drop silently — there's no row to attach to and no UX value
    /// in surfacing the orphan.</para>
    /// </summary>
    public async Task HookProgress(HookProgressPayload payload)
    {
        var runtimeId = ResolveRuntimeIdFromContext(nameof(HookProgress));
        if (runtimeId is null) return;
        if (runtimeId.Value != payload.RuntimeId)
        {
            throw new HubException("runtime claim mismatch");
        }

        // Look up project via the existing HookExecution row — saves the
        // ProjectRuntime hop on every progress line. Drop silently if the row
        // is gone (best-effort live UX, not an audit channel).
        var projectId = await _db.HookExecutions
            .Where(h => h.Id == payload.ExecutionId && h.RuntimeId == payload.RuntimeId)
            .Join(
                _db.ProjectRuntimes,
                h => h.RuntimeId,
                r => r.Id,
                (h, r) => (Guid?)r.ProjectId)
            .FirstOrDefaultAsync();
        if (projectId is null)
        {
            _logger.LogDebug(
                "RuntimeHub.HookProgress: unknown execution {ExecutionId} on runtime {RuntimeId}; dropping line {LineIndex}.",
                payload.ExecutionId, payload.RuntimeId, payload.LineIndex);
            return;
        }

        try
        {
            await _agentHub.Clients
                .Group($"project-{projectId.Value}")
                .HookProgress(payload);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "RuntimeHub.HookProgress: broadcast failed for execution {ExecutionId} (project {ProjectId}); dropping line.",
                payload.ExecutionId, projectId.Value);
        }
    }

    /// <summary>
    /// Daemon-to-server: the hook process exited (any exit code, but the
    /// runner itself ran to completion). Fills in the end-of-run fields on
    /// the matching <see cref="HookExecution"/>. The row is then immutable.
    ///
    /// <para><b>Idempotency.</b> A second HookCompleted for an
    /// already-closed row is a no-op (we just re-fan-out so late tabs catch up).
    /// Closed = <c>EndedAt</c> is non-null.</para>
    ///
    /// <para><b>Claim check via the row.</b> We load the row first and validate
    /// <c>row.RuntimeId == claim</c> rather than trusting
    /// <c>payload.RuntimeId</c> — the audit row already encodes which runtime
    /// owns the execution and is the authoritative source.</para>
    /// </summary>
    public async Task HookCompleted(HookCompletedPayload payload)
    {
        var runtimeId = ResolveRuntimeIdFromContext(nameof(HookCompleted));
        if (runtimeId is null) return;
        if (runtimeId.Value != payload.RuntimeId)
        {
            throw new HubException("runtime claim mismatch");
        }

        var execution = await _db.HookExecutions
            .FirstOrDefaultAsync(h => h.Id == payload.ExecutionId);
        if (execution is null)
        {
            _logger.LogWarning(
                "RuntimeHub.HookCompleted: unknown execution {ExecutionId} on runtime {RuntimeId}; dropping.",
                payload.ExecutionId, payload.RuntimeId);
            return;
        }
        if (execution.RuntimeId != runtimeId.Value)
        {
            // Audit row says a different runtime owns this execution — a daemon
            // claiming a peer's row is a hard error.
            throw new HubException("runtime claim mismatch");
        }

        var projectId = await _db.ProjectRuntimes
            .Where(r => r.Id == execution.RuntimeId)
            .Select(r => (Guid?)r.ProjectId)
            .FirstOrDefaultAsync();
        if (projectId is null)
        {
            _logger.LogWarning(
                "RuntimeHub.HookCompleted: runtime {RuntimeId} for execution {ExecutionId} is gone; persisting close but skipping fan-out.",
                execution.RuntimeId, payload.ExecutionId);
        }

        // Idempotent close — only update if the row hasn't already been closed.
        if (execution.EndedAt is null)
        {
            execution.EndedAt = payload.EndedAt;
            execution.ExitCode = payload.ExitCode;
            execution.DurationMs = payload.DurationMs;
            execution.OutputTail = payload.OutputTail ?? string.Empty;
            execution.OutputHash = payload.OutputHash ?? string.Empty;
            // WasConfigError stays false here — that's the HookConfigError path.
            await _db.SaveChangesAsync();
        }

        if (projectId is not null)
        {
            try
            {
                await _agentHub.Clients
                    .Group($"project-{projectId.Value}")
                    .HookCompleted(payload);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "RuntimeHub.HookCompleted: broadcast failed for execution {ExecutionId} (project {ProjectId}); persistence is unaffected.",
                    payload.ExecutionId, projectId.Value);
            }
        }
    }

    /// <summary>
    /// Daemon-to-server: the hook could not run at all — command not found,
    /// malformed config, sandbox refusal, etc. Persists by closing the row
    /// with <see cref="HookExecution.WasConfigError"/> set so operators can
    /// filter "ran and failed" vs "couldn't run". Same idempotency contract
    /// as <see cref="HookCompleted"/>.
    /// </summary>
    public async Task HookConfigError(HookConfigErrorPayload payload)
    {
        var runtimeId = ResolveRuntimeIdFromContext(nameof(HookConfigError));
        if (runtimeId is null) return;
        if (runtimeId.Value != payload.RuntimeId)
        {
            throw new HubException("runtime claim mismatch");
        }

        var execution = await _db.HookExecutions
            .FirstOrDefaultAsync(h => h.Id == payload.ExecutionId);
        if (execution is null)
        {
            _logger.LogWarning(
                "RuntimeHub.HookConfigError: unknown execution {ExecutionId} on runtime {RuntimeId}; dropping.",
                payload.ExecutionId, payload.RuntimeId);
            return;
        }
        if (execution.RuntimeId != runtimeId.Value)
        {
            throw new HubException("runtime claim mismatch");
        }

        var projectId = await _db.ProjectRuntimes
            .Where(r => r.Id == execution.RuntimeId)
            .Select(r => (Guid?)r.ProjectId)
            .FirstOrDefaultAsync();
        if (projectId is null)
        {
            _logger.LogWarning(
                "RuntimeHub.HookConfigError: runtime {RuntimeId} for execution {ExecutionId} is gone; persisting close but skipping fan-out.",
                execution.RuntimeId, payload.ExecutionId);
        }

        if (execution.EndedAt is null)
        {
            execution.EndedAt = payload.EndedAt;
            execution.ExitCode = null;
            execution.DurationMs = (int)(payload.EndedAt - execution.StartedAt).TotalMilliseconds;
            execution.OutputTail = payload.OutputTail ?? string.Empty;
            execution.OutputHash = string.Empty;
            execution.WasConfigError = true;
            await _db.SaveChangesAsync();
        }

        if (projectId is not null)
        {
            try
            {
                await _agentHub.Clients
                    .Group($"project-{projectId.Value}")
                    .HookConfigError(payload);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "RuntimeHub.HookConfigError: broadcast failed for execution {ExecutionId} (project {ProjectId}); persistence is unaffected.",
                    payload.ExecutionId, projectId.Value);
            }
        }
    }

    /// <summary>
    /// Daemon-to-server <i>relay-only</i>: the daemon has started another turn
    /// to recover from a failing hook. UX hint for the chat panel; no
    /// persistence. The actual continuation flow (turn creation, prompt
    /// composition, etc.) ships in a follow-up card.
    /// </summary>
    public async Task HookSelfHealStarted(HookSelfHealStartedPayload payload)
    {
        var runtimeId = ResolveRuntimeIdFromContext(nameof(HookSelfHealStarted));
        if (runtimeId is null) return;
        if (runtimeId.Value != payload.RuntimeId)
        {
            throw new HubException("runtime claim mismatch");
        }

        var projectId = await _db.ProjectRuntimes
            .Where(r => r.Id == payload.RuntimeId)
            .Select(r => (Guid?)r.ProjectId)
            .FirstOrDefaultAsync();
        if (projectId is null)
        {
            _logger.LogWarning(
                "RuntimeHub.HookSelfHealStarted: runtime {RuntimeId} not found; dropping relay.",
                payload.RuntimeId);
            return;
        }

        try
        {
            await _agentHub.Clients
                .Group($"project-{projectId.Value}")
                .HookSelfHealStarted(payload);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "RuntimeHub.HookSelfHealStarted: broadcast failed for runtime {RuntimeId} (project {ProjectId}).",
                payload.RuntimeId, projectId.Value);
        }
    }

    /// <summary>
    /// Daemon-to-server <i>relay-only</i>: the self-heal retry budget is
    /// exhausted. UX hint surfaced to the chat panel so the user knows to
    /// step in. No persistence.
    /// </summary>
    public async Task HookSelfHealMaxedOut(HookSelfHealMaxedOutPayload payload)
    {
        var runtimeId = ResolveRuntimeIdFromContext(nameof(HookSelfHealMaxedOut));
        if (runtimeId is null) return;
        if (runtimeId.Value != payload.RuntimeId)
        {
            throw new HubException("runtime claim mismatch");
        }

        var projectId = await _db.ProjectRuntimes
            .Where(r => r.Id == payload.RuntimeId)
            .Select(r => (Guid?)r.ProjectId)
            .FirstOrDefaultAsync();
        if (projectId is null)
        {
            _logger.LogWarning(
                "RuntimeHub.HookSelfHealMaxedOut: runtime {RuntimeId} not found; dropping relay.",
                payload.RuntimeId);
            return;
        }

        try
        {
            await _agentHub.Clients
                .Group($"project-{projectId.Value}")
                .HookSelfHealMaxedOut(payload);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "RuntimeHub.HookSelfHealMaxedOut: broadcast failed for runtime {RuntimeId} (project {ProjectId}).",
                payload.RuntimeId, projectId.Value);
        }
    }

    /// <summary>
    /// Daemon-to-server: request the server approve and dispatch a self-heal
    /// continuation after an <c>afterPrompt</c> hook reported a recoverable
    /// failure. The server is the budget authority — it owns the per-turn cap
    /// (<c>SelfHealAttempts</c> on <see cref="AgentSession"/>) and the
    /// database, so the daemon cannot decide unilaterally to retry; it has
    /// to ask, and the answer is synchronous.
    ///
    /// <para><b>Decision flow.</b></para>
    /// <list type="number">
    ///   <item>Auth: connection-level claim must match
    ///         <c>payload.RuntimeId</c>; mismatch returns
    ///         <c>runtimeMismatch</c>. <i>Never</i> throws — the daemon
    ///         expects a structured response and uses
    ///         <see cref="RequestSelfHealContinuationResponse.RejectionReason"/>
    ///         to branch its UX, which is harder to do with a generic
    ///         <c>HubException</c>.</item>
    ///   <item>Liveness: the failing session must still be in
    ///         <see cref="AgentSessionStatus.Running"/> with no
    ///         <c>CompletedAt</c>. A user cancel, an explicit failure, or a
    ///         missing row all collapse into <c>turnNotRunning</c>.</item>
    ///   <item>Budget: <c>SelfHealAttempts &gt;= 3</c> means we've hit the
    ///         cap. Flip the session to <see cref="AgentSessionStatus.Failed"/>
    ///         (so subsequent UI / API queries see "this turn is done"), fan
    ///         out <see cref="HookSelfHealMaxedOutPayload"/> for the chat
    ///         panel, and return <c>maxedOut</c>.</item>
    ///   <item>Approve: bump the counter, save, then hand off to the shared
    ///         <see cref="ITurnDispatcher"/> with the daemon's feedback prompt
    ///         + the failing turn's <c>ClaudeSessionId</c>. The dispatch path
    ///         is byte-identical to the user-typed-prompt path; the only
    ///         difference is the prompt source.</item>
    /// </list>
    ///
    /// <para><b>Why not raise a domain event instead.</b> The daemon needs the
    /// new turn id back synchronously so it can subscribe its hook chain to the
    /// fresh session before any <c>EmitEvent</c> lands. A domain-event +
    /// async-dispatch round-trip would race against the daemon's first
    /// <c>EmitEvent</c> on the new turn.</para>
    /// </summary>
    public async Task<RequestSelfHealContinuationResponse> RequestSelfHealContinuation(
        RequestSelfHealContinuationPayload payload)
    {
        // Connection-level claim — same gate as the other hook methods, but
        // we return a structured response instead of throwing on mismatch.
        // The daemon switches on RejectionReason, so a HubException would just
        // become a generic invocation failure on its end.
        var claimRuntimeId = ResolveRuntimeIdFromContext(nameof(RequestSelfHealContinuation));
        if (claimRuntimeId is null || claimRuntimeId.Value != payload.RuntimeId)
        {
            return new RequestSelfHealContinuationResponse(false, null, "runtimeMismatch");
        }

        // Load the failing session. Include the conversation because the
        // dispatcher needs the project id (resolved via Conversation.ProjectId)
        // and we want a single round-trip.
        var session = await _db.AgentSessions
            .Include(s => s.Conversation)
            .FirstOrDefaultAsync(s => s.Id == payload.TurnId);
        if (session is null
            || session.Status != AgentSessionStatus.Running
            || session.CompletedAt is not null)
        {
            // Missing row, terminal state, or both. The daemon's view of
            // "still running" can drift from ours if a user cancel or a
            // TurnFailed landed between the hook firing and this request.
            return new RequestSelfHealContinuationResponse(false, null, "turnNotRunning");
        }

        // Budget check. The cap is the per-turn counter on the session row,
        // not a per-conversation or per-runtime aggregate. A fresh prompt from
        // the user always starts at 0; we only ever bump on the originating
        // turn, so a user that types "fix it again" gets a fresh budget.
        if (session.SelfHealAttempts >= 3)
        {
            // Flip the session to Failed via the rich-entity method so the
            // UI stops pretending it's still in flight AND so the dispatch-
            // next handler picks the next queued session on the same runtime
            // (Card 3). CompletedAt is stamped inside Fail().
            session.Fail(session.FailureReason ?? "self_heal_maxed_out");
            await _db.SaveChangesAsync();

            // Best-effort UI fan-out — the daemon also gets the structured
            // rejection back, but the chat panel needs its own signal so the
            // user sees "we tried 3 times and gave up" without polling.
            var maxedOutProjectId = await _db.ProjectRuntimes
                .Where(r => r.Id == payload.RuntimeId)
                .Select(r => (Guid?)r.ProjectId)
                .FirstOrDefaultAsync();
            if (maxedOutProjectId is not null)
            {
                try
                {
                    await _agentHub.Clients
                        .Group($"project-{maxedOutProjectId.Value}")
                        .HookSelfHealMaxedOut(new HookSelfHealMaxedOutPayload(
                            payload.RuntimeId,
                            payload.ConversationId,
                            payload.TurnId,
                            payload.Iteration));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "RuntimeHub.RequestSelfHealContinuation: maxed-out fan-out failed for runtime {RuntimeId} (project {ProjectId}); persistence is unaffected.",
                        payload.RuntimeId, maxedOutProjectId.Value);
                }
            }

            return new RequestSelfHealContinuationResponse(false, null, "maxedOut");
        }

        // Approve. Bump the counter under the same change-tracker as the
        // session load so the SaveChanges below picks it up. The new turn
        // dispatch happens AFTER this save so the bumped counter is visible
        // to any concurrent reads while the new turn is in flight.
        session.SelfHealAttempts += 1;
        await _db.SaveChangesAsync();

        // Hand off to the shared dispatcher. Same path AgentHub.SubmitPrompt
        // takes for user-typed prompts — identical session-create + audit-row
        // + counter-bump + StartTurn ordering. EventOriginUserId is null
        // because this is daemon-driven, not user-driven; the audit JSON
        // simply omits the field.
        var dispatch = await _turnDispatcher.DispatchTurnAsync(new DispatchTurnArgs(
            ConversationId: payload.ConversationId,
            ProjectId: session.Conversation.ProjectId,
            BranchId: session.Conversation.BranchId,
            Prompt: payload.FeedbackPrompt,
            AgentId: payload.AgentId ?? session.AgentId,
            EventOriginUserId: null));

        // Best-effort UI heads-up. The daemon also emits its own
        // HookSelfHealStarted relay, but routing through the hub here means
        // the chat panel sees the new turn id even if the daemon's relay
        // arrives first or never lands.
        var startedProjectId = await _db.ProjectRuntimes
            .Where(r => r.Id == payload.RuntimeId)
            .Select(r => (Guid?)r.ProjectId)
            .FirstOrDefaultAsync();
        if (startedProjectId is not null)
        {
            try
            {
                await _agentHub.Clients
                    .Group($"project-{startedProjectId.Value}")
                    .HookSelfHealStarted(new HookSelfHealStartedPayload(
                        payload.RuntimeId,
                        payload.ConversationId,
                        payload.TurnId,
                        dispatch.SessionId,
                        payload.Iteration));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "RuntimeHub.RequestSelfHealContinuation: started fan-out failed for runtime {RuntimeId} (project {ProjectId}); persistence is unaffected.",
                    payload.RuntimeId, startedProjectId.Value);
            }
        }

        _logger.LogInformation(
            "RuntimeHub.RequestSelfHealContinuation: approved self-heal for turn {TurnId} (hook {HookName}, attempt {Attempt}); new turn {NewTurnId}.",
            payload.TurnId, payload.HookName, session.SelfHealAttempts, dispatch.SessionId);

        return new RequestSelfHealContinuationResponse(true, dispatch.SessionId, null);
    }

    /// <summary>
    /// Daemon-to-server: a git operation has begun. Inserts a fresh
    /// <see cref="GitOperation"/> row (the lifecycle / completion fields stay
    /// blank until <see cref="GitOperationCompleted"/> fills them in) and fans
    /// the start out to the project's UI clients so the git activity strip can
    /// render a "running…" affordance.
    ///
    /// <para><b>Auth.</b> Connection-level claim only — the payload doesn't
    /// carry a runtime id (the daemon already authenticated as that runtime
    /// at handshake time, and the audit row gets the runtime id from the
    /// connection, not the wire).</para>
    ///
    /// <para><b>Idempotency.</b> A daemon retry on the same
    /// <see cref="GitOperationStartedPayload.OperationId"/> must not duplicate
    /// the row or throw a unique-key error — we existence-check first. The
    /// fan-out is still emitted on retry so a tab that connected after the
    /// original GitOperationStarted catches up via the live channel.</para>
    /// </summary>
    public async Task GitOperationStarted(GitOperationStartedPayload payload)
    {
        var runtimeId = ResolveRuntimeIdFromContext(nameof(GitOperationStarted));
        if (runtimeId is null) return;

        var projectId = await _db.ProjectRuntimes
            .Where(r => r.Id == runtimeId.Value)
            .Select(r => (Guid?)r.ProjectId)
            .FirstOrDefaultAsync();
        if (projectId is null)
        {
            _logger.LogWarning(
                "RuntimeHub.GitOperationStarted: runtime {RuntimeId} not found (or soft-deleted); dropping operation {OperationId}.",
                runtimeId.Value, payload.OperationId);
            return;
        }

        // Idempotent insert — a daemon retry on the same OperationId must not
        // duplicate the row. Existence check first, same pattern as HookStarted.
        var alreadyStarted = await _db.GitOperations
            .AnyAsync(g => g.Id == payload.OperationId);

        if (!alreadyStarted)
        {
            // The destructive set is the daemon's contract on the wire — we
            // mirror it server-side so the audit row tells the truth even if
            // the daemon forgets to set the flag at the source.
            var wasDestructive = payload.OpType is GitOpType.Reset
                or GitOpType.ForcePush
                or GitOpType.BranchDelete;

            var op = new GitOperation
            {
                Id = payload.OperationId,
                RuntimeId = runtimeId.Value,
                ConversationId = payload.ConversationId,
                TurnId = payload.TurnId,
                OpType = payload.OpType,
                CommandLine = payload.CommandLine,
                StartedAt = DateTime.UtcNow,
                OutputTail = string.Empty,
                OutputHash = string.Empty,
                WasDestructive = wasDestructive,
            };
            _db.GitOperations.Add(op);
            await _db.SaveChangesAsync();
        }

        try
        {
            await _agentHub.Clients
                .Group($"project-{projectId.Value}")
                .GitOperationStarted(payload);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "RuntimeHub.GitOperationStarted: broadcast failed for operation {OperationId} (project {ProjectId}); persistence is unaffected.",
                payload.OperationId, projectId.Value);
        }
    }

    /// <summary>
    /// Daemon-to-server: a git operation exited. Closes the matching
    /// <see cref="GitOperation"/> row (sets <c>EndedAt</c>, <c>ExitCode</c>,
    /// <c>DurationMs</c>, <c>OutputTail</c>, <c>OutputHash</c>) and fans the
    /// completion out for the UI. Idempotent — a second completion for an
    /// already-closed row is a no-op on persistence; the fan-out still fires
    /// so late tabs catch up.
    /// </summary>
    public async Task GitOperationCompleted(GitOperationCompletedPayload payload)
    {
        var runtimeId = ResolveRuntimeIdFromContext(nameof(GitOperationCompleted));
        if (runtimeId is null) return;

        var op = await _db.GitOperations
            .FirstOrDefaultAsync(g => g.Id == payload.OperationId);
        if (op is null)
        {
            _logger.LogWarning(
                "RuntimeHub.GitOperationCompleted: unknown operation {OperationId} on runtime {RuntimeId}; dropping.",
                payload.OperationId, runtimeId.Value);
            return;
        }
        if (op.RuntimeId != runtimeId.Value)
        {
            // Audit row says a different runtime owns this operation — a daemon
            // claiming a peer's row is a hard error, same contract as
            // HookCompleted.
            throw new HubException("runtime claim mismatch");
        }

        var projectId = await _db.ProjectRuntimes
            .Where(r => r.Id == op.RuntimeId)
            .Select(r => (Guid?)r.ProjectId)
            .FirstOrDefaultAsync();
        if (projectId is null)
        {
            _logger.LogWarning(
                "RuntimeHub.GitOperationCompleted: runtime {RuntimeId} for operation {OperationId} is gone; persisting close but skipping fan-out.",
                op.RuntimeId, payload.OperationId);
        }

        // Idempotent close — only update if the row hasn't already been closed.
        if (op.EndedAt is null)
        {
            op.EndedAt = DateTime.UtcNow;
            op.ExitCode = payload.ExitCode;
            op.DurationMs = payload.DurationMs;
            op.OutputTail = payload.OutputTail ?? string.Empty;
            op.OutputHash = payload.OutputHash ?? string.Empty;
            await _db.SaveChangesAsync();
        }

        if (projectId is not null)
        {
            try
            {
                await _agentHub.Clients
                    .Group($"project-{projectId.Value}")
                    .GitOperationCompleted(payload);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "RuntimeHub.GitOperationCompleted: broadcast failed for operation {OperationId} (project {ProjectId}); persistence is unaffected.",
                    payload.OperationId, projectId.Value);
            }
        }
    }

    /// <summary>
    /// Daemon-to-server: a commit landed. Fan-out only — the underlying
    /// <see cref="GitOperation"/> row (with <see cref="GitOpType.Commit"/>) is
    /// the audit; this method just gives the chat panel and commit history a
    /// high-level signal to refresh without parsing every git op.
    /// </summary>
    public async Task CommitMade(CommitMadePayload payload)
    {
        var runtimeId = ResolveRuntimeIdFromContext(nameof(CommitMade));
        if (runtimeId is null) return;

        var projectId = await _db.ProjectRuntimes
            .Where(r => r.Id == runtimeId.Value)
            .Select(r => (Guid?)r.ProjectId)
            .FirstOrDefaultAsync();
        if (projectId is null)
        {
            _logger.LogWarning(
                "RuntimeHub.CommitMade: runtime {RuntimeId} not found; dropping commit {CommitSha}.",
                runtimeId.Value, payload.CommitSha);
            return;
        }

        try
        {
            await _agentHub.Clients
                .Group($"project-{projectId.Value}")
                .CommitMade(payload);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "RuntimeHub.CommitMade: broadcast failed for commit {CommitSha} (project {ProjectId}).",
                payload.CommitSha, projectId.Value);
        }
    }

    /// <summary>
    /// Daemon-to-server: a <c>git push</c> failed. Fan-out only — the matching
    /// <see cref="GitOperation"/> row already records the failure exit code
    /// and full output tail. This relay surfaces a coarse reason so the UI
    /// can branch (auth banner vs. retry vs. conflict resolution) without
    /// parsing the output.
    /// </summary>
    public async Task GitPushFailed(GitPushFailedPayload payload)
    {
        var runtimeId = ResolveRuntimeIdFromContext(nameof(GitPushFailed));
        if (runtimeId is null) return;

        var projectId = await _db.ProjectRuntimes
            .Where(r => r.Id == runtimeId.Value)
            .Select(r => (Guid?)r.ProjectId)
            .FirstOrDefaultAsync();
        if (projectId is null)
        {
            _logger.LogWarning(
                "RuntimeHub.GitPushFailed: runtime {RuntimeId} not found; dropping push failure for branch {Branch}.",
                runtimeId.Value, payload.Branch);
            return;
        }

        try
        {
            await _agentHub.Clients
                .Group($"project-{projectId.Value}")
                .GitPushFailed(payload);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "RuntimeHub.GitPushFailed: broadcast failed for branch {Branch} (project {ProjectId}).",
                payload.Branch, projectId.Value);
        }
    }

    /// <summary>
    /// Daemon-to-server: a <c>git push</c> succeeded. Fan-out only — the
    /// matching <see cref="GitOperation"/> row already records the success;
    /// this relay surfaces a positive "we are now synced with the remote"
    /// signal so the UI can clear an out-of-sync banner without polling.
    /// </summary>
    public async Task GitPushSucceeded(GitPushSucceededPayload payload)
    {
        var runtimeId = ResolveRuntimeIdFromContext(nameof(GitPushSucceeded));
        if (runtimeId is null) return;

        var projectId = await _db.ProjectRuntimes
            .Where(r => r.Id == runtimeId.Value)
            .Select(r => (Guid?)r.ProjectId)
            .FirstOrDefaultAsync();
        if (projectId is null)
        {
            _logger.LogWarning(
                "RuntimeHub.GitPushSucceeded: runtime {RuntimeId} not found; dropping push success for branch {Branch}.",
                runtimeId.Value, payload.Branch);
            return;
        }

        try
        {
            await _agentHub.Clients
                .Group($"project-{projectId.Value}")
                .GitPushSucceeded(payload);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "RuntimeHub.GitPushSucceeded: broadcast failed for branch {Branch} (project {ProjectId}).",
                payload.Branch, projectId.Value);
        }
    }

    /// <summary>
    /// Daemon-to-server: a <c>git commit</c> attempt failed (missing identity,
    /// hook rejection, lock contention, runner timeout). Fan-out only — the
    /// matching <see cref="GitOperation"/> row already records the failure exit
    /// code + full output tail. This relay surfaces a coarse reason so the UI
    /// can render an "out of sync" banner with a readable hint.
    /// </summary>
    public async Task CommitFailed(CommitFailedPayload payload)
    {
        var runtimeId = ResolveRuntimeIdFromContext(nameof(CommitFailed));
        if (runtimeId is null) return;

        var projectId = await _db.ProjectRuntimes
            .Where(r => r.Id == runtimeId.Value)
            .Select(r => (Guid?)r.ProjectId)
            .FirstOrDefaultAsync();
        if (projectId is null)
        {
            _logger.LogWarning(
                "RuntimeHub.CommitFailed: runtime {RuntimeId} not found; dropping commit failure on branch {Branch} (reason={Reason}).",
                runtimeId.Value, payload.Branch, payload.Reason);
            return;
        }

        try
        {
            await _agentHub.Clients
                .Group($"project-{projectId.Value}")
                .CommitFailed(payload);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "RuntimeHub.CommitFailed: broadcast failed for branch {Branch} (project {ProjectId}, reason={Reason}).",
                payload.Branch, projectId.Value, payload.Reason);
        }
    }

    /// <summary>
    /// Daemon-to-server: a merge produced conflicts. Fan-out only — the
    /// working tree is the source of truth; this relay just lets the UI open
    /// a conflict resolution flow without polling.
    /// </summary>
    public async Task MergeConflict(MergeConflictPayload payload)
    {
        var runtimeId = ResolveRuntimeIdFromContext(nameof(MergeConflict));
        if (runtimeId is null) return;

        var projectId = await _db.ProjectRuntimes
            .Where(r => r.Id == runtimeId.Value)
            .Select(r => (Guid?)r.ProjectId)
            .FirstOrDefaultAsync();
        if (projectId is null)
        {
            _logger.LogWarning(
                "RuntimeHub.MergeConflict: runtime {RuntimeId} not found; dropping conflict on {Source} -> {Target}.",
                runtimeId.Value, payload.SourceBranch, payload.TargetBranch);
            return;
        }

        try
        {
            await _agentHub.Clients
                .Group($"project-{projectId.Value}")
                .MergeConflict(payload);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "RuntimeHub.MergeConflict: broadcast failed for {Source} -> {Target} (project {ProjectId}).",
                payload.SourceBranch, payload.TargetBranch, projectId.Value);
        }
    }

    /// <summary>
    /// Daemon-to-server: request an approval id for a destructive git op
    /// (reset, force-push, branch-delete). The hub mints a fresh
    /// <see cref="Guid"/>, persists a stub <see cref="GitOperation"/> row
    /// tagged with the new approval id (so the audit trail captures the
    /// intent regardless of whether the user approves), fans out
    /// <see cref="IAgentClient.DestructiveGitOpRequested"/> for the UI to
    /// surface the prompt, and returns the id synchronously to the daemon.
    ///
    /// <para>The actual approve/reject decision flow ships in a follow-up
    /// card — this card just mints the id and persists the intent. The stub
    /// row's <c>EndedAt</c> stays null (it's "in flight" until either the
    /// daemon reports completion, or the approval is denied and the daemon
    /// closes it from its side).</para>
    /// </summary>
    public async Task<RequestDestructiveGitOpResponse> RequestDestructiveGitOp(
        RequestDestructiveGitOpPayload payload)
    {
        var runtimeId = ResolveRuntimeIdFromContext(nameof(RequestDestructiveGitOp));
        if (runtimeId is null)
        {
            // Synchronous request, but we still throw rather than returning a
            // poison id — the daemon should never invoke this without a valid
            // claim, and a thrown HubException surfaces the bug clearly.
            throw new HubException("missing runtime claim");
        }

        var projectId = await _db.ProjectRuntimes
            .Where(r => r.Id == runtimeId.Value)
            .Select(r => (Guid?)r.ProjectId)
            .FirstOrDefaultAsync();
        if (projectId is null)
        {
            throw new HubException("runtime not found");
        }

        var approvalId = Guid.NewGuid();

        // Stub audit row. EndedAt stays null — the row is "in flight" until
        // the daemon reports completion or the approval is denied. We set
        // WasDestructive=true unconditionally because that's the whole point
        // of this request path.
        var stub = new GitOperation
        {
            Id = Guid.NewGuid(),
            RuntimeId = runtimeId.Value,
            OpType = payload.OpType,
            CommandLine = $"git {payload.OpType} {payload.Args}",
            StartedAt = DateTime.UtcNow,
            EndedAt = null,
            OutputTail = string.Empty,
            OutputHash = string.Empty,
            WasDestructive = true,
            ApprovalId = approvalId,
        };
        _db.GitOperations.Add(stub);
        await _db.SaveChangesAsync();

        try
        {
            await _agentHub.Clients
                .Group($"project-{projectId.Value}")
                .DestructiveGitOpRequested(approvalId, payload);
        }
        catch (Exception ex)
        {
            // Fan-out failure does NOT roll back the stub — the audit row is
            // the durable record; the UI relay is best-effort. Operators can
            // still see the pending approval via the audit query.
            _logger.LogWarning(ex,
                "RuntimeHub.RequestDestructiveGitOp: fan-out failed for approval {ApprovalId} (project {ProjectId}); audit row is persisted.",
                approvalId, projectId.Value);
        }

        return new RequestDestructiveGitOpResponse(approvalId);
    }

    /// <summary>
    /// Daemon-to-server: the agent SDK's <c>canUseTool</c> callback fired and
    /// the daemon needs a human decision. Fan-out only — the resolver / project-
    /// permissions entity / approval UX are owned by separate cards. Same shape
    /// as <see cref="CommitFailed"/> / <see cref="GitPushSucceeded"/>: pull the
    /// runtime id off the connection context, resolve the parent project, and
    /// broadcast onto <c>project-{projectId}</c> so every open tab on the
    /// project sees the request. The eventual <c>AgentHub.ResolvePermission</c>
    /// echoes <see cref="PermissionRequestedPayload.ToolUseId"/> back through
    /// <see cref="IRuntimeClient.PermissionResolved"/>.
    /// </summary>
    public async Task PermissionRequested(PermissionRequestedPayload payload)
    {
        var runtimeId = ResolveRuntimeIdFromContext(nameof(PermissionRequested));
        if (runtimeId is null) return;

        var projectId = await _db.ProjectRuntimes
            .Where(r => r.Id == runtimeId.Value)
            .Select(r => (Guid?)r.ProjectId)
            .FirstOrDefaultAsync();
        if (projectId is null)
        {
            _logger.LogWarning(
                "RuntimeHub.PermissionRequested: runtime {RuntimeId} not found; dropping request for tool {ToolName} (toolUseId={ToolUseId}).",
                runtimeId.Value, payload.ToolName, payload.ToolUseId);
            return;
        }

        try
        {
            await _agentHub.Clients
                .Group($"project-{projectId.Value}")
                .PermissionRequested(payload);
        }
        catch (Exception ex)
        {
            // Fan-out failure is non-fatal — the daemon will eventually time
            // out its canUseTool wait and resolve as a deny. Logging is enough.
            _logger.LogWarning(ex,
                "RuntimeHub.PermissionRequested: broadcast failed for tool {ToolName} (project {ProjectId}, toolUseId={ToolUseId}).",
                payload.ToolName, projectId.Value, payload.ToolUseId);
        }
    }

    /// <summary>
    /// Daemon-to-server: append a single structured <see cref="RuntimeEvent"/>
    /// to the event store, then broadcast it to subscribed frontends via the
    /// <c>runtime-events:{runtimeId}</c> group. The hub is the single
    /// daemon-side entry point for V2 runtime events — install snippets,
    /// supervised service lifecycle, setup commands, spec delta apply /
    /// fail, etc. (see <see cref="RuntimeEventTypes"/>).
    ///
    /// <list type="number">
    ///   <item>Resolve <see cref="ProjectRuntime.Id"/> from the connection's
    ///         signed <c>rt_runtime</c> claim. The daemon does not (and
    ///         cannot) supply this on the wire — a daemon authenticated as
    ///         runtime A cannot emit events into runtime B.</item>
    ///   <item>Parse the wire <c>Severity</c> string into the
    ///         <see cref="RuntimeEventSeverity"/> enum. Unknown values map
    ///         to <see cref="RuntimeEventSeverity.Info"/> + a warn log —
    ///         the daemon can introduce new severities ahead of the server
    ///         without us dropping the event entirely.</item>
    ///   <item>Dispatch <see cref="RecordRuntimeEventCommand"/>. The command
    ///         handler enforces the per-runtime rolling FIFO cap and is
    ///         best-effort — a persistence failure logs + returns
    ///         Result.Failure rather than throwing.</item>
    ///   <item>On success, fan the event out to
    ///         <c>runtime-events:{runtimeId}</c> via
    ///         <see cref="IAgentClient.RuntimeEventReceived"/>. The
    ///         broadcast is intentionally separate from persistence:
    ///         persistence is the source of truth (REST endpoint reads
    ///         it back), the broadcast is the live-tail UX.</item>
    /// </list>
    ///
    /// <para><b>Best-effort.</b> Both persistence and fan-out are
    /// fire-and-forget from the daemon's perspective: a hub-side failure
    /// only delays observability, it must not break a working runtime. We
    /// log on every failure path but never throw <see cref="HubException"/>
    /// — the daemon does not await this method's return value to make
    /// progress (mirrors <see cref="ReportError"/> / <see cref="ReportDiskPressure"/>).</para>
    ///
    /// <para><b>Group naming.</b> <c>runtime-events:{runtimeId}</c> is
    /// deliberately scoped per-runtime (not per-project) because the
    /// drawer's Timeline is always for one runtime — a project with a
    /// history of two runtimes opens the drawer on the active one, and
    /// events from the dead one must not bleed in.</para>
    /// </summary>
    public async Task RecordRuntimeEvent(RuntimeEventPayloadDto payload)
    {
        var runtimeId = ResolveRuntimeIdFromContext(nameof(RecordRuntimeEvent));
        if (runtimeId is null) return;

        // Parse severity from the wire string into the typed enum. Unknown
        // values: log + default to Info so a daemon that adds a new severity
        // ahead of the server doesn't drop the event entirely. Persistence's
        // string conversion would also accept it via the enum's underlying
        // mapping, but we want a typed value to flow into the command.
        if (!Enum.TryParse<RuntimeEventSeverity>(payload.Severity, ignoreCase: true, out var severity))
        {
            _logger.LogWarning(
                "RuntimeHub.RecordRuntimeEvent: unrecognised Severity '{Severity}' for runtime {RuntimeId} type {Type}; defaulting to Info.",
                payload.Severity, runtimeId.Value, payload.Type);
            severity = RuntimeEventSeverity.Info;
        }

        // The persistence handler is best-effort — it catches its own
        // exceptions and returns Result.Failure. We dispatch and inspect
        // the result rather than relying on exception propagation.
        var result = await _mediator.Send(new RecordRuntimeEventCommand(
            RuntimeId: runtimeId.Value,
            Type: payload.Type,
            Severity: severity,
            Timestamp: payload.Timestamp,
            DurationMs: payload.DurationMs,
            Payload: payload.Payload));

        if (!result.IsSuccess)
        {
            _logger.LogWarning(
                "RuntimeHub.RecordRuntimeEvent: persistence failed for runtime {RuntimeId} type {Type}: {Error}. Skipping broadcast.",
                runtimeId.Value, payload.Type, result.Error);
            return;
        }

        // Broadcast to the per-runtime drawer group. The notification's Id
        // is generated server-side at insert; we re-mint here using a
        // fresh Guid because the command handler does not surface its
        // insert id. The persisted row is the source of truth for the
        // REST endpoint — the notification is purely a live-tail
        // signal and the React side reconciles on reconnect via REST.
        var notification = new RuntimeEventNotification(
            Id: Guid.NewGuid(),
            RuntimeId: runtimeId.Value,
            Type: payload.Type,
            Severity: severity.ToString(),
            Timestamp: payload.Timestamp,
            DurationMs: payload.DurationMs,
            Payload: payload.Payload);

        try
        {
            await _agentHub.Clients
                .Group($"runtime-events:{runtimeId.Value}")
                .RuntimeEventReceived(notification);
        }
        catch (Exception ex)
        {
            // Broadcast failure is non-fatal — the event is persisted, the
            // drawer's next REST refresh will pick it up. Mirror the same
            // pattern as PermissionRequested.
            _logger.LogWarning(ex,
                "RuntimeHub.RecordRuntimeEvent: broadcast failed for runtime {RuntimeId} type {Type}; row is persisted.",
                runtimeId.Value, payload.Type);
        }
    }

    /// <summary>
    /// Daemon-to-server: persist this runtime's spec-health
    /// (self-healing-runtime-specs, card B1).
    ///
    /// <list type="number">
    ///   <item>Resolve <see cref="ProjectRuntime.Id"/> from the connection's
    ///         signed <c>rt_runtime</c> claim. The daemon does not (and cannot)
    ///         supply this on the wire — a daemon authenticated as runtime A
    ///         cannot flip runtime B's health.</item>
    ///   <item>Parse the wire <c>Health</c> string into the
    ///         <see cref="RuntimeSpecHealth"/> enum. Unknown values map to
    ///         <see cref="RuntimeSpecHealth.Unknown"/> + a warn log — the daemon
    ///         can introduce a new level ahead of the server without us dropping
    ///         the report.</item>
    ///   <item>Persist via a single targeted <see cref="EntityFrameworkQueryableExtensions"/>
    ///         <c>ExecuteUpdateAsync</c> on the runtime row — no entity load, and
    ///         it deliberately does not re-stamp <c>UpdatedAt</c> (a health report
    ///         is not a lifecycle mutation; it must not perturb the heartbeat /
    ///         drift audit clocks).</item>
    /// </list>
    ///
    /// <para><b>Best-effort.</b> Mirrors <see cref="RecordRuntimeEvent"/> /
    /// <see cref="ReportError"/>: every failure path logs and returns; the method
    /// never throws <see cref="HubException"/>. The daemon does not await this to
    /// make progress — a hub-side blip only delays the degraded banner, it must
    /// not break a working runtime.</para>
    ///
    /// <para><b>Boot-issue details are not persisted here.</b>
    /// <see cref="ReportSpecHealthPayload.Issues"/> rides along for completeness,
    /// but the durable per-issue trail is the separately-emitted
    /// <c>SpecDegraded</c> / <c>InstallFailed</c> / <c>ServiceCrashed</c>
    /// <c>RuntimeEvents</c> — this row only carries the roll-up health flag.</para>
    /// </summary>
    public async Task ReportSpecHealth(ReportSpecHealthPayload payload)
    {
        var runtimeId = ResolveRuntimeIdFromContext(nameof(ReportSpecHealth));
        if (runtimeId is null) return;

        // Parse the wire string into the typed enum. Unknown values: log +
        // default to Unknown so a daemon that adds a health level ahead of the
        // server doesn't drop the report entirely.
        if (!Enum.TryParse<RuntimeSpecHealth>(payload.Health, ignoreCase: true, out var health))
        {
            _logger.LogWarning(
                "RuntimeHub.ReportSpecHealth: unrecognised Health '{Health}' for runtime {RuntimeId}; defaulting to Unknown.",
                payload.Health, runtimeId.Value);
            health = RuntimeSpecHealth.Unknown;
        }

        try
        {
            // Targeted update — no entity load, don't touch UpdatedAt (a health
            // report is not a lifecycle mutation and must not perturb the
            // heartbeat / drift audit clocks). The global soft-delete filter
            // applies, so a janitor-marked runtime is silently skipped (0 rows).
            var rows = await _db.ProjectRuntimes
                .Where(r => r.Id == runtimeId.Value)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.SpecHealth, health));

            if (rows == 0)
            {
                _logger.LogWarning(
                    "RuntimeHub.ReportSpecHealth: no runtime row updated for {RuntimeId} (deleted or missing); health '{Health}' dropped.",
                    runtimeId.Value, health);
            }
        }
        catch (Exception ex)
        {
            // Best-effort: log + swallow. A failed health write only delays the
            // degraded banner; the daemon does not block on this method's return.
            _logger.LogWarning(ex,
                "RuntimeHub.ReportSpecHealth: persistence failed for runtime {RuntimeId} health {Health}.",
                runtimeId.Value, health);
        }
    }

    /// <summary>
    /// Daemon-to-server: a single line from a supervised service the daemon's
    /// <c>LogTailer</c> is currently tailing. The hub resolves the owning
    /// <see cref="ProjectRuntime.Id"/> from the connection's signed
    /// <c>rt_runtime</c> claim, then broadcasts the line to the
    /// <c>service-logs:{runtimeId}:{serviceName}</c> SignalR group via
    /// <see cref="IAgentClient.ServiceLogLine"/>.
    ///
    /// <para><b>Not persisted.</b> Mirrors the runtime-spec-v2 contract:
    /// "raw logs live on disk and are tailed on demand." We don't even touch
    /// the database — straight pass-through fan-out so the live "Logs" tab
    /// renders immediately. The supervisord-rotated file on disk is the only
    /// durable copy.</para>
    ///
    /// <para><b>Best-effort.</b> A broadcast failure (no subscribers, transport
    /// blip) is non-fatal — the daemon does not await this method's return
    /// value, and a dropped line just doesn't appear in the open Logs tab. We
    /// log on failure but never throw <see cref="HubException"/>; the daemon
    /// must not block its tail pipeline waiting on a single ack.</para>
    /// </summary>
    public async Task ServiceLogLine(ServiceLogLineDto payload)
    {
        var runtimeId = ResolveRuntimeIdFromContext(nameof(ServiceLogLine));
        if (runtimeId is null) return;

        // Trivial validation — a daemon shipping a blank service name has a
        // bug we'd rather log than silently forward. Empty Line is *valid*
        // (supervisord can emit a blank line) so we leave that alone.
        if (string.IsNullOrWhiteSpace(payload.ServiceName))
        {
            _logger.LogWarning(
                "RuntimeHub.ServiceLogLine: empty ServiceName from runtime {RuntimeId}; dropping line.",
                runtimeId.Value);
            return;
        }

        var notification = new ServiceLogLineNotification(
            RuntimeId: runtimeId.Value,
            ServiceName: payload.ServiceName,
            Line: payload.Line,
            Timestamp: payload.Timestamp);

        try
        {
            await _agentHub.Clients
                .Group($"service-logs:{runtimeId.Value}:{payload.ServiceName}")
                .ServiceLogLine(notification);
        }
        catch (Exception ex)
        {
            // Fan-out failure: log + swallow. The daemon's tail pipeline keeps
            // going; the dropped line just doesn't reach a subscriber.
            _logger.LogWarning(ex,
                "RuntimeHub.ServiceLogLine: broadcast failed for runtime {RuntimeId} service {ServiceName}.",
                runtimeId.Value, payload.ServiceName);
        }
    }

    /// <summary>
    /// runtime-observability-super-admin — daemon-to-server: a single line of
    /// stdout/stderr from the daemon's own log files. Mirrors
    /// <see cref="ServiceLogLine"/> shape (resolve runtime id from the signed
    /// context claim, broadcast to the daemon-logs group, swallow fan-out
    /// failures) — different group, different notification record, otherwise
    /// identical pass-through.
    /// </summary>
    public async Task DaemonLogLine(DaemonLogLineDto payload)
    {
        var runtimeId = ResolveRuntimeIdFromContext(nameof(DaemonLogLine));
        if (runtimeId is null) return;

        // Stream defaults to "stdout" when the daemon doesn't supply one —
        // older daemons may not have learned the field yet. Empty Line is
        // valid (the daemon's own log can carry blank lines).
        var stream = string.IsNullOrEmpty(payload.Stream) ? "stdout" : payload.Stream;

        var notification = new DaemonLogLineNotification(
            RuntimeId: runtimeId.Value,
            Stream: stream,
            Line: payload.Line,
            Timestamp: payload.Timestamp);

        try
        {
            await _agentHub.Clients
                .Group($"daemon-logs:{runtimeId.Value}")
                .DaemonLogLineReceived(notification);
        }
        catch (Exception ex)
        {
            // Fan-out failure: log + swallow. Same contract as ServiceLogLine —
            // the daemon's tail pipeline keeps going; the dropped line just
            // doesn't reach a subscriber.
            _logger.LogWarning(ex,
                "RuntimeHub.DaemonLogLine: broadcast failed for runtime {RuntimeId}.",
                runtimeId.Value);
        }
    }

    /// <summary>
    /// Daemon-to-server live supervisord snapshot push. Pure pass-through:
    /// resolve <c>RuntimeId</c> from the signed connection claim, then fan the
    /// payload out to the <c>runtime-events:{runtimeId}</c> group via
    /// <see cref="IAgentClient.LiveSupervisordSnapshotReceived"/>. Not
    /// persisted — consumers cache the latest snapshot in-memory and replace
    /// on every push. Same broadcast group already used for
    /// <see cref="RecordRuntimeEvent"/> so the drawer only needs one
    /// subscription per runtime.
    /// </summary>
    public async Task PushLiveSupervisordSnapshot(LiveSupervisordSnapshotPayload payload)
    {
        var runtimeId = ResolveRuntimeIdFromContext(nameof(PushLiveSupervisordSnapshot));
        if (runtimeId is null) return;

        var notification = new LiveSupervisordSnapshotNotification(
            RuntimeId: runtimeId.Value,
            SampledAt: payload.SampledAt,
            Processes: payload.Processes);

        try
        {
            await _agentHub.Clients
                .Group($"runtime-events:{runtimeId.Value}")
                .LiveSupervisordSnapshotReceived(notification);
        }
        catch (Exception ex)
        {
            // Fan-out failure: log + swallow. Same contract as ServiceLogLine —
            // the daemon's polling pipeline keeps going; this tick's snapshot
            // just doesn't reach subscribers.
            _logger.LogWarning(ex,
                "RuntimeHub.PushLiveSupervisordSnapshot: broadcast failed for runtime {RuntimeId}.",
                runtimeId.Value);
        }
    }

    /// <summary>
    /// chat-file-attachments — daemon-to-server ack that an attachment push
    /// finished. Idempotently stamps <c>Attachment.StagedAt</c> on success and
    /// fans an <c>AttachmentStateChanged</c> notification out to the
    /// conversation's branch group so the composer chip flips its UI state.
    /// </summary>
    public async Task ReportAttachmentStaged(Guid attachmentId, bool success, string? error)
    {
        var runtimeId = ResolveRuntimeIdFromContext(nameof(ReportAttachmentStaged));
        if (runtimeId is null) return;

        // Pull attachment + parent conversation in one query — we need the
        // conversation's BranchId for both the runtime cross-check and the
        // fan-out group resolution.
        var attachment = await _db.Attachments
            .Include(a => a.Conversation)
            .FirstOrDefaultAsync(a => a.Id == attachmentId);
        if (attachment is null)
        {
            // Unknown / deleted attachment — drop silently. The daemon's
            // staging pipeline may emit a late ack after a row was removed
            // out from under it; not worth a HubException over.
            _logger.LogWarning(
                "RuntimeHub.ReportAttachmentStaged: runtime {RuntimeId} acked unknown attachment {AttachmentId} (success={Success}); ignoring.",
                runtimeId.Value, attachmentId, success);
            return;
        }

        // Cross-check: the attachment's parent conversation's branch must
        // resolve to a runtime owned by the calling daemon. Same hard-fail
        // contract as RuntimeSpecDeltaApplied / TurnRefused — a daemon
        // claiming a peer's attachment is the case the per-method claim
        // guards against.
        var attachmentRuntimeId = await _db.ProjectRuntimes
            .Where(r => r.BranchId == attachment.Conversation.BranchId)
            .Select(r => (Guid?)r.Id)
            .FirstOrDefaultAsync();
        if (attachmentRuntimeId is null || attachmentRuntimeId.Value != runtimeId.Value)
        {
            throw new HubException("runtime claim mismatch");
        }

        if (success)
        {
            // Idempotent stamp — only update if StagedAt hasn't already been
            // set. A re-ack after a previous success is a no-op on
            // persistence; we still re-broadcast below so a late tab catches
            // up.
            if (attachment.StagedAt is null)
            {
                attachment.StagedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync();
                _logger.LogInformation(
                    "RuntimeHub.ReportAttachmentStaged: marked attachment {AttachmentId} staged (runtime {RuntimeId}, conversation {ConversationId}).",
                    attachmentId, runtimeId.Value, attachment.ConversationId);
            }
            else
            {
                _logger.LogInformation(
                    "RuntimeHub.ReportAttachmentStaged: attachment {AttachmentId} already staged; skipping stamp (re-broadcasting).",
                    attachmentId);
            }
        }
        else
        {
            // Failure path — leave StagedAt null so SubmitPrompt validation
            // keeps rejecting the chip. The error string is the daemon's
            // human-readable reason; we log it with attachment context but
            // don't persist (no column for it in v1).
            _logger.LogWarning(
                "RuntimeHub.ReportAttachmentStaged: daemon failed to stage attachment {AttachmentId} (runtime {RuntimeId}, conversation {ConversationId}): {Error}",
                attachmentId, runtimeId.Value, attachment.ConversationId, error ?? "<no detail>");
        }

        // Fan-out to the conversation's branch group. The chip in the
        // composer listens on this notification and flips its UI state.
        var notification = new AttachmentStateChangedPayload(
            AttachmentId: attachment.Id,
            ConversationId: attachment.ConversationId,
            BranchId: attachment.Conversation.BranchId,
            State: success ? "Ready" : "Failed",
            Error: success ? null : error);

        try
        {
            await _agentHub.Clients
                .Group($"branch-{attachment.Conversation.BranchId}")
                .AttachmentStateChanged(notification);
        }
        catch (Exception ex)
        {
            // Fan-out failure does NOT roll back the StagedAt stamp — the
            // attachment row is the durable record, the UI relay is
            // best-effort. The chip will still re-render on the next refetch
            // of the attachment.
            _logger.LogWarning(ex,
                "RuntimeHub.ReportAttachmentStaged: broadcast failed for attachment {AttachmentId} (branch {BranchId}); persistence is unaffected.",
                attachmentId, attachment.Conversation.BranchId);
        }
    }

    /// <summary>
    /// Pulls the runtime id stashed on <see cref="HubCallerContext.Items"/> by
    /// <see cref="OnConnectedAsync"/>. Same contract as <see cref="Heartbeat"/>:
    /// a missing entry means the connection somehow bypassed the handshake
    /// gate — log a warning and return <c>null</c>; never throw, because a
    /// thrown <see cref="HubException"/> here would surface as a generic
    /// invocation failure on a hot path.
    ///
    /// <para>The per-method "claim equals payload.RuntimeId" cross-check is
    /// performed by the caller — that one DOES throw, because a daemon claiming
    /// a peer's runtime is a hard error worth seeing on the JS console.</para>
    /// </summary>
    private Guid? ResolveRuntimeIdFromContext(string methodName)
    {
        if (Context.Items.TryGetValue("RuntimeId", out var rt) && rt is Guid runtimeId)
        {
            return runtimeId;
        }

        _logger.LogWarning(
            "RuntimeHub.{MethodName} called on connection {ConnectionId} with no RuntimeId in Context.Items; ignoring.",
            methodName, Context.ConnectionId);
        return null;
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        // Items may be missing RuntimeId/ProjectId entirely if the connection
        // was rejected pre-handshake (missing/bogus claim, or unknown runtime).
        // Use TryGetValue — indexing into a Dictionary throws KeyNotFoundException
        // on miss.
        if (Context.Items.TryGetValue("RuntimeId", out var rt) && rt is Guid runtimeId &&
            Context.Items.TryGetValue("ProjectId", out var pj) && pj is Guid projectId)
        {
            await _mediator.Publish(new RuntimeDisconnected(
                runtimeId, projectId, Context.ConnectionId, DateTime.UtcNow, exception?.Message));
        }

        _logger.LogInformation(exception,
            "RuntimeHub disconnected. Connection {ConnectionId}", Context.ConnectionId);

        await base.OnDisconnectedAsync(exception);
    }
}
