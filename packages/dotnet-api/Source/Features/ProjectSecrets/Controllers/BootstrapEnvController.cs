using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Source.Features.ProjectSecrets.Models;
using Source.Features.ProjectSecrets.Services;
using Source.Features.RuntimeTokens.Models;
using Source.Infrastructure;
using Source.Infrastructure.Extensions;

namespace Source.Features.ProjectSecrets.Controllers;

/// <summary>
/// Daemon-facing cold-boot delivery for project secrets. A freshly respawned
/// daemon calls <c>GET /api/runtimes/{runtimeId}/bootstrap-env</c> on boot and
/// receives the full set of env vars, decrypted server-side, in one HTTP call.
///
/// <para><b>Why HTTP and not SignalR.</b> Bootstrap may run before the SignalR
/// connection is established — the daemon needs an env file on disk before its
/// hub client even starts negotiating. HTTP keeps the daemon-side bootstrap
/// stage (spec 14 Card 8) trivial: a single fetch with the runtime token in the
/// <c>Authorization: Bearer</c> header. The SignalR
/// <see cref="EventHandlers.PushSecretToRuntimeHandler"/> path takes over for
/// live deltas after that.</para>
///
/// <para><b>Why no MediatR.</b> Same pragmatic reasoning as
/// <see cref="Source.Features.RuntimeLifecycle.Controllers.RuntimeStatusController"/>:
/// this is a thin passthrough — one tracked-runtime read, one secrets-list read,
/// a decrypt loop, one audit insert. Wrapping in a query handler would add
/// files without changing behaviour. The slice stays thin and the controller
/// talks straight to the DbContext + encryption service.</para>
///
/// <para><b>Auth.</b> Gated on <c>[Authorize(AuthenticationSchemes = "RuntimeToken")]</c>
/// — the <see cref="RuntimeTokenAuthenticationDefaults.SchemeName"/> scheme
/// registered in <see cref="AuthenticationExtensions.AddRuntimeTokenAuthScheme"/>.
/// Signature, lifetime, issuer, audience, and revocation are all verified by the
/// JWT bearer middleware before the action runs (missing/invalid/expired/revoked
/// → 401 at the middleware layer); we only need to enforce that the token's
/// runtime id matches the path. Mismatched claim → 403, not 401, since the
/// caller IS authenticated, just unauthorised for this resource. Same pattern
/// as <c>RuntimeStatusController.GetActiveSession</c>.</para>
///
/// <para><b>Plaintext lifetime.</b> The encryption service hands back each
/// secret as a <see cref="string"/>; once materialised on the heap, .NET's
/// immutable string semantics mean we cannot deterministically zero the buffer.
/// The strings live until GC — same pragmatic fallback as
/// <see cref="EventHandlers.PushSecretToRuntimeHandler"/>. We never log keys or
/// values, never log the response body, and the audit row records only
/// (runtimeId, deliveredCount), never the keys.</para>
/// </summary>
[ApiController]
[Tags("ProjectSecrets")]
public class BootstrapEnvController : ControllerBase
{
    private const string SystemBootstrapActor = "system:bootstrap";

    private readonly ApplicationDbContext _db;
    private readonly SecretEncryptionService _encryption;
    private readonly ILogger<BootstrapEnvController> _logger;

    public BootstrapEnvController(
        ApplicationDbContext db,
        SecretEncryptionService encryption,
        ILogger<BootstrapEnvController> logger)
    {
        _db = db;
        _encryption = encryption;
        _logger = logger;
    }

    /// <summary>
    /// Return every (non-soft-deleted) project secret for the runtime's project,
    /// decrypted. Empty list is still 200 (not 204) — clearer for the daemon,
    /// which treats "200 with []" as "wipe the env file". 401 is handled by the
    /// JWT bearer middleware (token missing/invalid/expired/revoked); 403 is
    /// returned when the principal IS authenticated but the <c>rt_runtime</c>
    /// claim doesn't match the path. 404 is returned when the runtime row is
    /// gone (including soft-deleted, via the global query filter).
    ///
    /// <para>Writes one <see cref="SecretAuditAction.BootstrapDelivered"/> row
    /// per call (not per-key). Actor = <c>"system:bootstrap"</c>; metadata
    /// carries <c>{ runtimeId, deliveredCount }</c> as JSON so the audit trail
    /// answers "how many vars did the daemon receive on this boot?" without
    /// recording the keys themselves.</para>
    /// </summary>
    [HttpGet("/api/runtimes/{runtimeId:guid}/bootstrap-env")]
    [Authorize(AuthenticationSchemes = RuntimeTokenAuthenticationDefaults.SchemeName)]
    [ProducesResponseType(typeof(BootstrapEnvResponse), 200)]
    [ProducesResponseType(401)]
    [ProducesResponseType(403)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<BootstrapEnvResponse>> GetBootstrapEnv(
        Guid runtimeId,
        CancellationToken ct)
    {
        // The RuntimeToken JWT scheme has already validated signature + lifetime
        // + issuer/audience and consulted the revocation cache (a missing/invalid
        // token is rejected at the middleware layer with 401). We still enforce
        // that the token's runtimeId claim matches the path — a daemon may only
        // bootstrap itself. Mismatched claim → 403, not 401, since the caller
        // *is* authenticated, just unauthorised for this resource.
        var claimRuntimeIdRaw = User.FindFirstValue(RuntimeTokenClaimNames.RuntimeId);
        if (!Guid.TryParse(claimRuntimeIdRaw, out var claimRuntimeId) || claimRuntimeId != runtimeId)
        {
            return Forbid();
        }

        // Default query — soft-deleted runtimes are filtered out by the global
        // filter, which is exactly what we want here: a torn-down runtime has
        // no bootstrap bundle. Mirrors RuntimeStatusController.GetActiveSession.
        var runtime = await _db.ProjectRuntimes
            .FirstOrDefaultAsync(r => r.Id == runtimeId, ct);
        if (runtime is null)
        {
            return NotFound();
        }

        // Branch-effective resolution: pull project-wide rows (BranchId == null)
        // AND the rows scoped to THIS runtime's branch, then overlay so the
        // branch-specific value wins on a key collision. The global query filter
        // on ProjectSecret already excludes IsDeleted=true rows; we don't need
        // IgnoreQueryFilters here.
        var candidates = await _db.ProjectSecrets
            .Where(s => s.ProjectId == runtime.ProjectId
                && (s.BranchId == null || s.BranchId == runtime.BranchId))
            .ToListAsync(ct);

        // Effective set keyed by env-var name: start with project-wide defaults,
        // then let branch-specific rows override. A row with BranchId ==
        // runtime.BranchId always wins over the project-wide (null) row.
        var effective = new Dictionary<string, ProjectSecret>(StringComparer.Ordinal);
        foreach (var secret in candidates)
        {
            // Project-wide row: only take it if no branch row has claimed the key.
            if (secret.BranchId is null)
            {
                if (!effective.ContainsKey(secret.Key))
                {
                    effective[secret.Key] = secret;
                }
            }
            else
            {
                // Branch-specific row always wins.
                effective[secret.Key] = secret;
            }
        }

        // Sort by Key for deterministic response bodies (diff-friendly daemon logs).
        var entries = new List<EnvVarEntry>(effective.Count);
        foreach (var secret in effective.Values.OrderBy(s => s.Key, StringComparer.Ordinal))
        {
            // Each plaintext is a string from the encryption service; see the
            // class doc for the lifetime-on-heap caveat.
            var plaintext = await _encryption.DecryptAsync(
                runtime.ProjectId,
                secret.Ciphertext,
                secret.Nonce,
                secret.DekVersion,
                ct);
            entries.Add(new EnvVarEntry(secret.Key, plaintext));
        }

        // One audit row per call, NOT per-key. The metadata JSON shape is
        // { "runtimeId": "...", "deliveredCount": N }. We never record the keys
        // themselves — the (project, key) audit trail is owned by Created /
        // Updated / Deleted / Revealed rows.
        var metadata = JsonSerializer.Serialize(new
        {
            runtimeId = runtimeId.ToString(),
            deliveredCount = entries.Count,
        });

        _db.SecretAuditEvents.Add(new SecretAuditEvent
        {
            Id = Guid.NewGuid(),
            Action = SecretAuditAction.BootstrapDelivered,
            ProjectId = runtime.ProjectId,
            SecretId = null,
            SecretKey = null,
            Actor = SystemBootstrapActor,
            Metadata = metadata,
            CreatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync(ct);

        // Info-level: count only, no keys, no values, no body. Keys are
        // potentially sensitive (they reveal the integration surface of the
        // project: STRIPE_API_KEY, OPENAI_API_KEY, etc); the count is enough
        // for boot diagnostics.
        _logger.LogInformation(
            "BootstrapEnv: delivered {Count} env-vars to runtime {RuntimeId} for project {ProjectId}.",
            entries.Count,
            runtimeId,
            runtime.ProjectId);

        return Ok(new BootstrapEnvResponse(entries));
    }
}
