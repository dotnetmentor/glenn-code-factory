using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Source.Features.GitOps.Models;
using Source.Features.Hooks.Models;
using Source.Features.ProjectSecrets.Services;
using Source.Features.RuntimeBootstrap.Contracts;
using Source.Features.RuntimeBootstrap.Models;
using Source.Features.RuntimeCuration;
using Source.Features.RuntimeLifecycle.Configuration;
using Source.Features.RuntimeLifecycle.Models;
using Source.Features.RuntimePresets.Contracts;
using Source.Features.RuntimePresets.Services;
using Source.Infrastructure;
using Source.Shared;
using Source.Shared.CQRS;
using Source.Shared.Results;

// Disambiguate the wire-record `McpServer` (Name/Url/Scope) from the catalog
// entity `McpServer` (Name/Version/DefaultEnabled). The handler reads the
// catalog table and projects each row onto the wire record.
using BootstrapMcp = Source.Features.RuntimeBootstrap.Contracts.McpServer;
using McpCatalogEntry = Source.Features.Mcp.Models.McpServer;

namespace Source.Features.RuntimeBootstrap.Queries;

/// <summary>
/// Single-shot bootstrap fetch for a daemon. Invoked from
/// <see cref="SignalR.Hubs.RuntimeHub.GetBootstrap"/> the first time a daemon
/// connects to <c>/hubs/runtime</c>; the daemon walks its own state machine off
/// the returned <see cref="BootstrapPayloadV2"/> (install bash, services[],
/// setup bash, env vars, hooks, MCPs, repo).
///
/// <list type="bullet">
///   <item>Targets the runtime by id — the hub reads it off the verified
///         <c>rt_runtime</c> JWT claim, so the cross-runtime check is already
///         done by the time we run.</item>
///   <item>MCP <see cref="BootstrapMcp.Url"/> values are composed from the
///         <c>Runtime:PublicApiUrl</c> SystemSetting (via
///         <see cref="IRuntimeOptionsAccessor"/>) — NOT from the inbound
///         request's Host header. The daemon runs on a separate Fly VM and
///         <c>localhost:5338</c> (what Cloudflare forwards as the upstream Host)
///         is unreachable from there; we must dial the public canonical URL.
///         If the setting is unset/unparseable we log a warning and emit a
///         clearly-broken URL so the failure is loud rather than silently
///         hanging on a localhost handshake.</item>
///   <item>Sub-sources are tolerant of absence: a Phase-1 runtime with no spec,
///         a brand-new project with no secrets, a runtime predating the hooks /
///         git-config admin write — every field has a documented "missing"
///         state (empty list, null, default-on AutoCommit) and we never fail
///         the whole bootstrap because one is empty. <c>Repo</c> is left null
///         when no clone URL is determinable; the daemon then skips
///         CloningRepo entirely (Scene 5 of the runtime-bootstrap spec).</item>
///   <item>Writes one append-only <see cref="BootstrapRun"/> row per call so
///         operators can answer "did the daemon last fetch its bundle, and if
///         so when?" without parsing logs. <c>FinalStage</c> records
///         <see cref="BootstrapStage.Fetching"/> — we're handing back the
///         bundle, not running it; the daemon's later acks (out of scope for
///         this card) update the row to Ready / failure stages.</item>
/// </list>
///
/// <para><b>Failure mode.</b> The runtime row missing or soft-deleted is the
/// only hard error: the JWT claim picks a runtime that has since been torn
/// down. We surface a Result.Failure so the hub method can throw <c>HubException</c>
/// and the daemon can decide whether to retry or abort. Decryption failures on
/// individual secrets do <i>not</i> nuke the whole bundle — the secret is
/// skipped + logged and the rest of the bundle ships. A daemon with a partial
/// env file is more useful than one with no boot at all.</para>
/// </summary>
public record GetBootstrapQuery(
    Guid RuntimeId) : IQuery<Result<BootstrapPayloadV2>>;

public class GetBootstrapQueryHandler
    : IQueryHandler<GetBootstrapQuery, Result<BootstrapPayloadV2>>
{
    private readonly ApplicationDbContext _db;
    private readonly SecretEncryptionService _encryption;
    private readonly IClock _clock;
    private readonly IRuntimeOptionsAccessor _runtimeOptions;
    private readonly IPresetExpander _expander;
    private readonly ILogger<GetBootstrapQueryHandler> _logger;

    public GetBootstrapQueryHandler(
        ApplicationDbContext db,
        SecretEncryptionService encryption,
        IClock clock,
        IRuntimeOptionsAccessor runtimeOptions,
        IPresetExpander expander,
        ILogger<GetBootstrapQueryHandler> logger)
    {
        _db = db;
        _encryption = encryption;
        _clock = clock;
        _runtimeOptions = runtimeOptions;
        _expander = expander;
        _logger = logger;
    }

    public async Task<Result<BootstrapPayloadV2>> Handle(
        GetBootstrapQuery request,
        CancellationToken cancellationToken)
    {
        // The hub already verified the rt_runtime claim and the connection's
        // OnConnectedAsync confirmed the row exists; we re-read here because
        // the connection-time runtime may have been soft-deleted between
        // connect and getBootstrap. Tracked read so we can mutate Spec /
        // RespawnRetries from the daemon-ready ack later — for this query we
        // only project, but the small cost is dwarfed by the I/O.
        var runtime = await _db.ProjectRuntimes
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == request.RuntimeId, cancellationToken);
        if (runtime is null)
        {
            return Result.Failure<BootstrapPayloadV2>(
                $"Runtime {request.RuntimeId} not found or soft-deleted.");
        }

        // Spec now lives on Project (per the `project-level-runtime-spec`
        // spec). Read the project's spec — this is how lazy propagation works:
        // every cold-boot / wake / respawn picks up whatever the project's
        // current spec is, regardless of when the proposal that wrote it was
        // approved or which runtime originated it.
        var project = await _db.Projects
            .AsNoTracking()
            .Where(p => p.Id == runtime.ProjectId)
            .Select(p => new { p.Spec, p.SpecVersion })
            .FirstOrDefaultAsync(cancellationToken);

        var runtimeSpec = await ProjectRuntimeSpecAsync(project?.Spec, runtime.Id, cancellationToken);
        var envVars = await LoadEnvVarsAsync(runtime.ProjectId, cancellationToken);
        var hooks = await LoadHooksAsync(runtime.Id, cancellationToken);
        var mcps = await LoadMcpServersAsync(cancellationToken);
        var repo = await LoadRepoAsync(runtime.Id, cancellationToken);

        var modelSlug = await _db.Projects
            .AsNoTracking()
            .Where(p => p.Id == runtime.ProjectId)
            .Select(p => p.Model != null && p.Model.IsActive ? p.Model.Slug : null)
            .FirstOrDefaultAsync(cancellationToken);

        var payload = new BootstrapPayloadV2(
            Version: BootstrapPayloadVersions.Latest,
            RuntimeSpec: runtimeSpec,
            EnvVars: envVars,
            Hooks: hooks,
            Mcps: mcps,
            Repo: repo,
            ModelSlug: modelSlug);

        // Append-only audit row. Mirrors the BootstrapEnvController pattern of
        // recording one row per delivery, NOT per env-var: this is a single
        // bundle delivery. FinalStage = Fetching because we're handing the
        // bundle to the daemon — Ready is owned by the daemon's runtime-ready
        // ack (Card 2). Success is true: the *fetch* succeeded; downstream
        // failure overrides via a Failed row written by ReportError later.
        var startedAt = _clock.UtcNow;
        _db.BootstrapRuns.Add(new BootstrapRun
        {
            Id = Guid.NewGuid(),
            RuntimeId = runtime.Id,
            StartedAt = startedAt,
            EndedAt = startedAt,
            FinalStage = BootstrapStage.Fetching,
            Success = true,
            ErrorReason = null,
            DaemonVersion = null,
            ImageDigest = runtime.ImageDigest,
            BootstrapVersion = BootstrapPayloadVersions.Latest,
        });
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "GetBootstrap: delivered bundle to runtime {RuntimeId} (project {ProjectId}, envCount={EnvCount}, mcpCount={McpCount}, repo={HasRepo}, hooks={HasHooks}, model={ModelSlug}).",
            runtime.Id, runtime.ProjectId, envVars.Count, mcps.Count, repo is not null, hooks is not null, modelSlug ?? "(none)");

        return Result.Success(payload);
    }

    // ------------------------------------------------------------------
    // Sub-source loaders. Each one returns a "documented empty" state on
    // missing input rather than throwing — see class doc.
    // ------------------------------------------------------------------

    /// <summary>
    /// Project the project's persisted spec jsonb into the V2 wire shape the
    /// daemon consumes. The project's spec is now V3 (preset-based, the
    /// user/agent authoring surface); the daemon still consumes V2, so we run
    /// the spec through <see cref="IPresetExpander"/> here to produce the V2
    /// the daemon expects.
    ///
    /// <para>An empty / null spec (Phase-1 projects that never had a spec
    /// installed) collapses to an empty <see cref="RuntimeSpecV2"/> so the
    /// daemon skips install / services / setup stages cleanly. A
    /// non-parseable body or an expander failure (preset missing, parameter
    /// type mismatch, etc.) also collapses to empty + a warning log — we
    /// never fail bootstrap because of a malformed spec; the daemon boots
    /// with no services rather than not at all.</para>
    /// </summary>
    private async Task<RuntimeSpecV2> ProjectRuntimeSpecAsync(string? specJson, Guid runtimeId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(specJson))
        {
            return new RuntimeSpecV2();
        }

        var parsed = RuntimeSpecV3.TryParse(specJson);
        if (parsed is null)
        {
            _logger.LogWarning(
                "GetBootstrap.ProjectRuntimeSpec: runtime {RuntimeId} project Spec body did not parse as V3; emitting empty V2 spec.",
                runtimeId);
            return new RuntimeSpecV2();
        }

        var expanded = await _expander.ExpandAsync(parsed, ct);
        if (expanded.IsFailure)
        {
            _logger.LogWarning(
                "GetBootstrap.ProjectRuntimeSpec: runtime {RuntimeId} V3→V2 expansion failed ({Error}); emitting empty V2 spec.",
                runtimeId, expanded.Error);
            return new RuntimeSpecV2();
        }
        return expanded.Value;
    }

    /// <summary>
    /// Decrypt every (non-soft-deleted) project secret. Per-row decrypt
    /// failures are logged + skipped — see class doc. Empty list is a valid
    /// outcome: a brand-new project with no secrets returns [], not an error.
    /// </summary>
    private async Task<List<EnvVar>> LoadEnvVarsAsync(Guid projectId, CancellationToken ct)
    {
        var secrets = await _db.ProjectSecrets
            .Where(s => s.ProjectId == projectId)
            .OrderBy(s => s.Key)
            .ToListAsync(ct);

        var entries = new List<EnvVar>(secrets.Count);
        foreach (var secret in secrets)
        {
            try
            {
                var plaintext = await _encryption.DecryptAsync(
                    projectId,
                    secret.Ciphertext,
                    secret.Nonce,
                    secret.DekVersion,
                    ct);
                entries.Add(new EnvVar(secret.Key, plaintext));
            }
            catch (Exception ex)
            {
                // Skip + log: a corrupted DEK / wrong nonce on one row should
                // not nuke the entire bundle. The daemon writes whatever lands
                // and the operator sees the gap in next-boot diagnostics.
                _logger.LogWarning(ex,
                    "GetBootstrap: failed to decrypt secret {SecretId} for project {ProjectId}; skipping.",
                    secret.Id, projectId);
            }
        }
        return entries;
    }

    /// <summary>
    /// Translate the persisted hooks-json (opaque to the server, owned by the
    /// daemon's hooks-runner spec) into the typed wire <see cref="HooksConfig"/>.
    /// Until the daemon-hooks-runner spec finalises the schema, the wire record
    /// is intentionally empty; the daemon receives "config exists, here it is"
    /// vs "no config row at all" via <c>HooksConfig?</c> being null. Anything
    /// the daemon needs beyond the empty record lives in the <c>UpdateConfig</c>
    /// channel from <c>OnConnectedAsync</c>, which already ships the raw json.
    /// </summary>
    private async Task<HooksConfig?> LoadHooksAsync(Guid runtimeId, CancellationToken ct)
    {
        var hasConfig = await _db.RuntimeHookConfigs
            .AsNoTracking()
            .AnyAsync(c => c.RuntimeId == runtimeId, ct);
        return hasConfig ? new HooksConfig() : null;
    }

    /// <summary>
    /// Catalog-level enabled MCP servers, projected onto the wire record. The
    /// scope token is empty for now — issuance lives in the future
    /// mcp-scoping spec; we transport whatever's in scope today (i.e. nothing)
    /// so adding it later doesn't require a payload-version bump.
    ///
    /// <para>URLs are composed from <c>Runtime:PublicApiUrl</c> (live-editable
    /// SystemSetting) — daemons live on Fly machines and can't dial
    /// <c>localhost:5338</c>, which is what Cloudflare forwards in the upstream
    /// Host header. If the setting is missing/unparseable we emit
    /// <c>about:blank/...</c> so the daemon fails loudly rather than silently
    /// hanging on a localhost MCP handshake.</para>
    /// </summary>
    private async Task<List<BootstrapMcp>> LoadMcpServersAsync(CancellationToken ct)
    {
        var servers = await _db.McpServers
            .AsNoTracking()
            .Where(s => s.DefaultEnabled)
            .OrderBy(s => s.Name)
            .ToListAsync(ct);

        var publicApiUrl = _runtimeOptions.Current.PublicApiUrl;
        Uri? publicApi = null;
        if (string.IsNullOrWhiteSpace(publicApiUrl)
            || !Uri.TryCreate(publicApiUrl, UriKind.Absolute, out publicApi))
        {
            // Fail loud, not silent. A blank/garbage PublicApiUrl in production
            // is an operator misconfiguration; we'd rather the daemon log "can't
            // reach about:blank/..." than hang forever on a localhost handshake.
            _logger.LogWarning(
                "GetBootstrap: Runtime:PublicApiUrl is missing or unparseable ('{PublicApiUrl}'); MCP URLs will be emitted as about:blank so the daemon fails loudly. Set the SystemSetting to the canonical public API hostname.",
                publicApiUrl);
            publicApi = null;
        }

        return servers
            .Select(s => new BootstrapMcp(
                Name: s.Name,
                Url: ComposeMcpUrl(publicApi, s),
                Scope: string.Empty))
            .ToList();
    }

    // The /api/ prefix is mandatory: the production Cloudflare tunnel ingress
    // rule forwards only /api/* to upstream Kestrel. A bare /mcp/* URL 404s at
    // the edge before the daemon's request reaches the backend, and the SDK
    // (per MCP Streamable HTTP §3.4) silently emits an empty tools array on
    // an unreachable server. See KanbanMcpController route doc for the spec
    // pointer (mcp-streamable-http-transport, Card faf5297f).
    private static string ComposeMcpUrl(Uri? publicApi, McpCatalogEntry s)
        => publicApi is null
            ? $"about:blank/api/mcp/{s.Name}/{s.Version}"
            : $"{publicApi.Scheme}://{publicApi.Authority}/api/mcp/{s.Name}/{s.Version}";

    /// <summary>
    /// Resolve the runtime's clone target straight from the
    /// <c>ProjectRuntime → ProjectBranch + Project</c> chain. The wire payload
    /// carries only the HTTPS clone URL + branch name; the daemon fetches a
    /// short-lived, repo-scoped installation token via the
    /// <c>GetRepoAccessToken</c> hub method just before <c>git clone</c>, so no
    /// long-lived credential rides this contract.
    ///
    /// <para><b>Why we trust <c>Project.GithubRepoOwner</c>/<c>GithubRepoName</c>
    /// directly instead of joining <c>GithubRepositories</c>.</b> The repo cache
    /// is maintained by the install callback + <c>installation_repositories</c>
    /// webhook, but <c>CreateProject</c> never seeds a row — and a dropped/missed
    /// webhook leaves the cache permanently out of sync with what the Project
    /// already knows. The join then silently returns nothing → the daemon
    /// short-circuits the clone → users see an empty <c>/data/project/repo</c>
    /// with no error anywhere (the failure mode that brought us here).
    /// The Project columns are the source of truth at creation time and are
    /// what every other code path (clone-token issuance, branch listing,
    /// webhook routing) already uses; the cache is incidental for this lookup.</para>
    ///
    /// <para>Returns null only for genuinely detached projects: the
    /// installation FK is NULL (the project's installation was disconnected)
    /// or the owner/name strings are empty. Null is the documented
    /// "skip the clone stage" signal — not an error.</para>
    /// </summary>
    private async Task<RepoConfig?> LoadRepoAsync(Guid runtimeId, CancellationToken ct)
    {
        // Two-table read: runtime → branch (for the branch name) → project
        // (for the installation + owner/name triple). Detached projects
        // (GithubInstallationId is null) fall through to the post-query null
        // check below — the documented "skip the clone stage" signal.
        var data = await (from rt in _db.ProjectRuntimes.AsNoTracking()
                          where rt.Id == runtimeId
                          join br in _db.ProjectBranches.AsNoTracking() on rt.BranchId equals br.Id
                          join p in _db.Projects.AsNoTracking() on rt.ProjectId equals p.Id
                          select new
                          {
                              p.GithubInstallationId,
                              p.GithubRepoOwner,
                              p.GithubRepoName,
                              BranchName = br.Name,
                          })
                         .FirstOrDefaultAsync(ct);

        if (data is null)
        {
            // The runtime / branch / project chain should always resolve here —
            // the runtime existence check at the top of Handle already passed.
            // Defensive: log and skip the clone if for any reason it doesn't.
            _logger.LogWarning(
                "GetBootstrap.LoadRepo: runtime {RuntimeId} branch/project chain missing — clone skipped.",
                runtimeId);
            return null;
        }

        // Detached project (installation disconnected from workspace) — there's
        // no live credential path to clone with, so we deliberately skip the
        // clone stage and let the daemon boot without a working tree.
        if (data.GithubInstallationId is null)
        {
            _logger.LogInformation(
                "GetBootstrap.LoadRepo: runtime {RuntimeId} project is detached (GithubInstallationId is null) — clone skipped.",
                runtimeId);
            return null;
        }

        // Defensive: CreateProject enforces non-empty owner + name, but a hand-
        // edited row or a future code path could leave them blank. Without an
        // owner/name we can't form a clone URL — skip rather than ship a
        // malformed "https://github.com//.git".
        if (string.IsNullOrWhiteSpace(data.GithubRepoOwner)
            || string.IsNullOrWhiteSpace(data.GithubRepoName))
        {
            _logger.LogWarning(
                "GetBootstrap.LoadRepo: runtime {RuntimeId} project has empty owner/name (owner='{Owner}', name='{Name}') — clone skipped.",
                runtimeId, data.GithubRepoOwner, data.GithubRepoName);
            return null;
        }

        var fullName = $"{data.GithubRepoOwner}/{data.GithubRepoName}";
        return new RepoConfig(
            Url: $"https://github.com/{fullName}.git",
            Branch: data.BranchName,
            DeployKey: null);
    }
}
