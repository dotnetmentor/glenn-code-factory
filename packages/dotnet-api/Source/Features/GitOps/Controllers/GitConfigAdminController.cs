using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Source.Features.GitOps.Models;
using Source.Features.SignalR.Contracts;
using Source.Features.SignalR.Hubs;
using Source.Infrastructure;
using Source.Infrastructure.AuthorizationModels;

namespace Source.Features.GitOps.Controllers;

/// <summary>
/// Operator-only HTTP surface for editing a runtime's <see cref="RuntimeGitConfig"/>.
/// Backs the admin UI / CLI for the daemon-git-ops spec: an operator toggles
/// auto-commit or installs an SSH deploy key, we upsert the row and push the
/// new bytes to the live daemon over SignalR via
/// <see cref="IRuntimeClient.UpdateConfig"/>.
///
/// <para><b>Why no MediatR.</b> Same pragmatic reasoning as
/// <see cref="Hooks.Controllers.HookConfigAdminController"/> — a thin upsert
/// over a single row plus a SignalR push. Wrapping it in a command would add
/// four files per endpoint without changing the behavior.</para>
///
/// <para><b>Authorization.</b> <see cref="RoleConstants.SuperAdmin"/>, matching
/// every other admin surface. TenantAdmin is too broad — a deploy key gives
/// the daemon push access to whatever upstream repo the operator pointed at.</para>
///
/// <para><b>Versioning.</b> <c>ConfigUpdatePayload.Version</c> stays "1" here —
/// the daemon doesn't act on it yet (parses tolerantly, ignores unknown), and
/// no other call site has bumped it. When the daemon starts caring about
/// monotonic deltas (follow-up card), this controller will move to the same
/// shared counter as the hook + token rotation paths.</para>
/// </summary>
[ApiController]
[Route("api/admin/runtimes/{id:guid}/git")]
[Authorize(Roles = RoleConstants.SuperAdmin)]
[Tags("GitOpsAdmin")]
public class GitConfigAdminController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    private readonly IHubContext<RuntimeHub, IRuntimeClient> _runtimeHub;
    private readonly ILogger<GitConfigAdminController> _logger;

    /// <summary>
    /// Minimal SSH private-key sniff. Full validation (key parses, key matches
    /// host fingerprint, etc.) is the daemon's job at first push — server-side
    /// over-validation would only ratchet the deploy story tighter without
    /// blocking real misuse, since a SuperAdmin can already write whatever
    /// they like to the daemon's keyring.
    /// </summary>
    private static readonly Regex PrivateKeyHeader = new(
        @"^-----BEGIN (?:OPENSSH|RSA|DSA|EC|ED25519) PRIVATE KEY-----",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline);

    public GitConfigAdminController(
        ApplicationDbContext db,
        IHubContext<RuntimeHub, IRuntimeClient> runtimeHub,
        ILogger<GitConfigAdminController> logger)
    {
        _db = db;
        _runtimeHub = runtimeHub;
        _logger = logger;
    }

    /// <summary>
    /// Toggle the runtime's auto-commit behaviour. Upsert + live push.
    /// </summary>
    [HttpPut("auto-commit")]
    [ProducesResponseType(typeof(UpdatedGitConfigResponse), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(401)]
    [ProducesResponseType(403)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<UpdatedGitConfigResponse>> SetAutoCommit(
        Guid id,
        [FromBody] SetAutoCommitRequest request,
        CancellationToken ct)
    {
        // Existence check — soft-deleted rows are filtered by the global
        // ProjectRuntime query filter, so a janitor-marked runtime falls
        // through to 404 just like a hard-missing one. Same kill-switch
        // story as HookConfigAdminController.
        var runtimeExists = await _db.ProjectRuntimes
            .AsNoTracking()
            .AnyAsync(r => r.Id == id, ct);
        if (!runtimeExists)
        {
            return NotFound();
        }

        var config = await UpsertAsync(id, ct);
        config.AutoCommit = request.Enabled;
        await _db.SaveChangesAsync(ct);

        await PushUpdateConfigAsync(id, autoCommit: request.Enabled, deployKey: null);

        _logger.LogInformation(
            "GitConfigAdminController: set AutoCommit={AutoCommit} for runtime {RuntimeId} (configId={ConfigId}).",
            request.Enabled, id, config.Id);

        return Ok(new UpdatedGitConfigResponse(id, "1"));
    }

    /// <summary>
    /// Install (or replace) the runtime's SSH deploy key. Upsert + live push.
    /// <b>The response intentionally does not echo the private key</b> — leaving
    /// it out of the audit log is the cheapest "key is write-only" affordance.
    /// </summary>
    [HttpPut("deploy-key")]
    [ProducesResponseType(typeof(UpdatedGitConfigResponse), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(401)]
    [ProducesResponseType(403)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<UpdatedGitConfigResponse>> SetDeployKey(
        Guid id,
        [FromBody] SetDeployKeyRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.PrivateKey))
        {
            return BadRequest(new { error = "privateKey is required" });
        }
        if (!PrivateKeyHeader.IsMatch(request.PrivateKey))
        {
            return BadRequest(new
            {
                error = "privateKey does not look like an OpenSSH/RSA/DSA/EC/ED25519 private key " +
                        "(missing -----BEGIN ... PRIVATE KEY----- header)",
            });
        }

        var runtimeExists = await _db.ProjectRuntimes
            .AsNoTracking()
            .AnyAsync(r => r.Id == id, ct);
        if (!runtimeExists)
        {
            return NotFound();
        }

        var config = await UpsertAsync(id, ct);
        config.DeployKey = request.PrivateKey;
        config.DeployKeyHostKey = request.HostKey;
        await _db.SaveChangesAsync(ct);

        await PushUpdateConfigAsync(id, autoCommit: null, deployKey: request.PrivateKey);

        // Deliberately do NOT log the key bytes (the PrivateKeyRedactor would
        // catch a leak anyway, but redundancy is cheap).
        _logger.LogInformation(
            "GitConfigAdminController: deploy key installed for runtime {RuntimeId} (configId={ConfigId}, hostKeyPresent={HasHostKey}, keyLength={KeyLength}).",
            id, config.Id, request.HostKey is not null, request.PrivateKey.Length);

        return Ok(new UpdatedGitConfigResponse(id, "1"));
    }

    /// <summary>
    /// Loads the existing row by RuntimeId or creates a fresh one with
    /// defaults. Caller mutates the returned instance and calls
    /// SaveChangesAsync. Centralises the upsert so the two endpoints
    /// can't drift on the default values.
    /// </summary>
    private async Task<RuntimeGitConfig> UpsertAsync(Guid runtimeId, CancellationToken ct)
    {
        var config = await _db.RuntimeGitConfigs
            .FirstOrDefaultAsync(c => c.RuntimeId == runtimeId, ct);
        if (config is null)
        {
            config = new RuntimeGitConfig
            {
                RuntimeId = runtimeId,
                // AutoCommit defaults to true on the entity; an upsert into a
                // fresh row inherits that default. The two endpoints overwrite
                // the field they care about and leave the other untouched.
            };
            _db.RuntimeGitConfigs.Add(config);
        }
        return config;
    }

    /// <summary>
    /// Best-effort live push to the runtime group. A daemon that's offline at
    /// write time will pick up the same fields via the bootstrap delivery in
    /// <see cref="RuntimeHub.OnConnectedAsync"/> on its next reconnect, so a
    /// failed fan-out doesn't compromise eventual consistency.
    /// </summary>
    private async Task PushUpdateConfigAsync(Guid runtimeId, bool? autoCommit, string? deployKey)
    {
        try
        {
            await _runtimeHub.Clients
                .Group($"runtime-{runtimeId}")
                .UpdateConfig(new ConfigUpdatePayload(
                    RuntimeId: runtimeId,
                    Version: "1",
                    RuntimeToken: null,
                    HooksJson: null,
                    AutoCommit: autoCommit,
                    DeployKey: deployKey));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "GitConfigAdminController: UpdateConfig push failed for runtime {RuntimeId}; daemon will pick up changes on reconnect via the bootstrap delivery.",
                runtimeId);
        }
    }
}

/// <summary>
/// Toggle request for <c>PUT /api/admin/runtimes/{id}/git/auto-commit</c>.
/// </summary>
public record SetAutoCommitRequest(bool Enabled);

/// <summary>
/// Install/replace request for <c>PUT /api/admin/runtimes/{id}/git/deploy-key</c>.
/// <see cref="HostKey"/> is optional — daemon falls back to its bundled
/// <c>known_hosts</c> defaults when null.
/// </summary>
public record SetDeployKeyRequest(string PrivateKey, string? HostKey);

/// <summary>
/// Minimal acknowledgement returned by both endpoints. Echoes the runtime id
/// + version so callers don't need a follow-up read; deliberately does NOT
/// include the deploy key value.
/// </summary>
public record UpdatedGitConfigResponse(Guid RuntimeId, string Version);
