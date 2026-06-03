using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Source.Features.ProjectSecrets.Commands;
using Source.Features.ProjectSecrets.Models;
using Source.Features.ProjectSecrets.Queries;
using Source.Infrastructure;

namespace Source.Features.ProjectSecrets.Controllers;

/// <summary>
/// User-facing HTTP surface for the project-secrets CRUD slice. Mediates over
/// the <see cref="Commands.AddSecretCommand"/> / <see cref="Commands.UpdateSecretCommand"/> /
/// <see cref="Commands.DeleteSecretCommand"/> commands and the
/// <see cref="Queries.ListSecretsQuery"/> / <see cref="Queries.RevealSecretQuery"/>
/// queries.
///
/// <para><b>Authorisation.</b> Plain <see cref="AuthorizeAttribute"/> plus a
/// per-project ownership gate in <see cref="EnforceProjectOwnershipAsync"/>:
/// every action that takes a <c>projectId</c> calls the helper first and bails
/// out on its return value. The helper queries
/// <c>_db.Projects.AnyAsync(p =&gt; p.Id == projectId &amp;&amp; p.OwnerUserId == userId)</c>
/// and returns 404 (not 403) on miss so we don't leak project existence
/// cross-tenant. Soft-deleted projects are filtered out by the
/// <c>!IsDeleted</c> query filter on
/// <see cref="Source.Features.Projects.Models.Project"/>.</para>
///
/// <para><b>Cross-tenant denial audit.</b> When ownership ever fails, we write
/// a <see cref="SecretAuditAction.CrossTenantDenied"/> audit row inline with
/// a SHA-256 IP fingerprint in <see cref="SecretAuditEvent.Metadata"/>. The
/// raw IP is never logged — the fingerprint is enough to correlate repeated
/// probes from the same source without persisting PII. The audit write goes
/// through this controller's own DbContext (not the command pipeline) because
/// the action is a denial, not a successful CQRS operation.</para>
///
/// <para><b>Plaintext lifetime.</b> Plaintext lives only on the request /
/// response wire (POST / PUT body in, reveal response out) and on the stack
/// of the handler. We never log it, never retain it, never echo it back on
/// the create/update path. The reveal endpoint returns it exactly once — the
/// caller is responsible for clearing the response body from any cache.</para>
/// </summary>
[ApiController]
[Route("api/projects/{projectId:guid}/secrets")]
[Authorize]
[Tags("ProjectSecrets")]
public class ProjectSecretsController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly ApplicationDbContext _db;
    private readonly ILogger<ProjectSecretsController> _logger;

    public ProjectSecretsController(
        IMediator mediator,
        ApplicationDbContext db,
        ILogger<ProjectSecretsController> logger)
    {
        _mediator = mediator;
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// List metadata for every (non-soft-deleted) secret in the project.
    /// Plaintext / ciphertext / nonce are deliberately excluded — see
    /// <see cref="SecretMetadataDto"/>. Listing does not write an audit row;
    /// the volume would drown out security-relevant signals (Reveal,
    /// CrossTenantDenied).
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(List<SecretMetadataDto>), 200)]
    [ProducesResponseType(401)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<List<SecretMetadataDto>>> List(
        Guid projectId,
        CancellationToken ct)
    {
        var deny = await EnforceProjectOwnershipAsync(projectId, secretKey: null, ct);
        if (deny is not null) return deny;

        var result = await _mediator.Send(new ListSecretsQuery(projectId), ct);
        if (!result.IsSuccess)
        {
            return NotFound(new { error = result.Error });
        }
        return Ok(result.Value);
    }

    /// <summary>
    /// Project-wide rollup of which (non-archived) branches have missing required
    /// env vars. Drives the cross-branch env indicators — sidebar branch dots and
    /// the settings-trigger badge — from a single request rather than one
    /// per-branch status call.
    /// </summary>
    [HttpGet("status-summary")]
    [ProducesResponseType(typeof(ProjectEnvStatusSummaryResponse), 200)]
    [ProducesResponseType(401)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<ProjectEnvStatusSummaryResponse>> StatusSummary(
        Guid projectId,
        CancellationToken ct)
    {
        var deny = await EnforceProjectOwnershipAsync(projectId, secretKey: null, ct);
        if (deny is not null) return deny;

        var result = await _mediator.Send(new GetProjectEnvStatusSummaryQuery(projectId), ct);
        if (!result.IsSuccess)
        {
            return NotFound(new { error = result.Error });
        }
        return Ok(result.Value);
    }

    /// <summary>
    /// Add a new secret. 400 covers both validation failures
    /// (<c>invalid_key_format</c> / <c>invalid_plaintext</c>) and the
    /// unique-key conflict (<c>key_already_exists</c>); the error code in the
    /// body lets the frontend render a specific message.
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(AddSecretResponse), 201)]
    [ProducesResponseType(400)]
    [ProducesResponseType(401)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<AddSecretResponse>> Add(
        Guid projectId,
        [FromBody] AddSecretRequest request,
        CancellationToken ct)
    {
        var deny = await EnforceProjectOwnershipAsync(projectId, request?.Key, ct);
        if (deny is not null) return deny;

        if (request is null)
        {
            return BadRequest(new { error = "invalid_body" });
        }

        var actor = GetActorUserId();
        var result = await _mediator.Send(
            new AddSecretCommand(projectId, request.Key ?? string.Empty, request.Value ?? string.Empty, actor),
            ct);

        if (!result.IsSuccess)
        {
            return BadRequest(new { error = result.Error });
        }

        // 201 + body. We deliberately don't generate a Location header — the
        // reveal endpoint is the only "GET single" surface and it's a deliberate
        // privileged action we don't want clients to follow on creation.
        return StatusCode(StatusCodes.Status201Created, result.Value);
    }

    /// <summary>
    /// Rotate the value of an existing secret.
    /// </summary>
    [HttpPut("{key}")]
    [ProducesResponseType(typeof(UpdateSecretResponse), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(401)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<UpdateSecretResponse>> Update(
        Guid projectId,
        string key,
        [FromBody] UpdateSecretRequest request,
        CancellationToken ct)
    {
        var deny = await EnforceProjectOwnershipAsync(projectId, key, ct);
        if (deny is not null) return deny;

        if (request is null)
        {
            return BadRequest(new { error = "invalid_body" });
        }

        var actor = GetActorUserId();
        var result = await _mediator.Send(
            new UpdateSecretCommand(projectId, key, request.Value ?? string.Empty, actor),
            ct);

        if (!result.IsSuccess)
        {
            return result.Error == "not_found"
                ? NotFound(new { error = result.Error })
                : BadRequest(new { error = result.Error });
        }

        return Ok(result.Value);
    }

    /// <summary>
    /// Soft-delete a secret. 204 on success; 404 when the key doesn't exist.
    /// </summary>
    [HttpDelete("{key}")]
    [ProducesResponseType(204)]
    [ProducesResponseType(400)]
    [ProducesResponseType(401)]
    [ProducesResponseType(404)]
    public async Task<ActionResult> Delete(
        Guid projectId,
        string key,
        CancellationToken ct)
    {
        var deny = await EnforceProjectOwnershipAsync(projectId, key, ct);
        if (deny is not null) return deny;

        var actor = GetActorUserId();
        var result = await _mediator.Send(new DeleteSecretCommand(projectId, key, actor), ct);

        if (!result.IsSuccess)
        {
            return result.Error == "not_found"
                ? NotFound(new { error = result.Error })
                : BadRequest(new { error = result.Error });
        }

        return NoContent();
    }

    /// <summary>
    /// Decrypt and return the plaintext for a single secret. Privileged action
    /// — every successful call writes a <see cref="SecretAuditAction.Revealed"/>
    /// audit row before the plaintext leaves the handler. The frontend renders
    /// this once on demand and the user is expected to copy / use immediately.
    /// </summary>
    [HttpGet("{key}/reveal")]
    [ProducesResponseType(typeof(RevealSecretResponse), 200)]
    [ProducesResponseType(401)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<RevealSecretResponse>> Reveal(
        Guid projectId,
        string key,
        CancellationToken ct)
    {
        var deny = await EnforceProjectOwnershipAsync(projectId, key, ct);
        if (deny is not null) return deny;

        var actor = GetActorUserId();
        var result = await _mediator.Send(new RevealSecretQuery(projectId, key, actor), ct);

        if (!result.IsSuccess)
        {
            return NotFound(new { error = result.Error });
        }

        return Ok(result.Value);
    }

    /// <summary>
    /// Project-ownership gate. Returns null when the current caller owns the
    /// project; otherwise an <see cref="UnauthorizedResult"/> (no Identity
    /// claim) or a <see cref="NotFoundResult"/> (project missing OR owned by
    /// someone else — we deliberately collapse the two so non-owners cannot
    /// distinguish "exists but forbidden" from "doesn't exist", per the
    /// e2e-smoketest spec on tenancy isolation). On the deny branch we also
    /// persist an inline <see cref="SecretAuditAction.CrossTenantDenied"/>
    /// audit row via <see cref="WriteCrossTenantDeniedAuditAsync"/>.
    ///
    /// <para>The query relies on the global <c>!IsDeleted</c> query filter on
    /// <see cref="Source.Features.Projects.Models.Project"/> so soft-deleted
    /// projects are treated as not-found from the caller's perspective.</para>
    /// </summary>
    private async Task<ActionResult?> EnforceProjectOwnershipAsync(
        Guid projectId,
        string? secretKey,
        CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized();
        }

        var owns = await _db.Projects
            .AsNoTracking()
            .AnyAsync(p => p.Id == projectId && p.OwnerUserId == userId, ct);

        if (owns)
        {
            return null;
        }

        // Per spec: 404, never 403. Don't leak project existence to non-owners.
        // Audit the denial inline so security tooling can correlate probes.
        await WriteCrossTenantDeniedAuditAsync(projectId, secretKey, userId, ct);
        return NotFound();
    }

    /// <summary>
    /// Persist an inline <see cref="SecretAuditAction.CrossTenantDenied"/>
    /// audit row. Called from <see cref="EnforceProjectOwnershipAsync"/> on
    /// the failure branch. We hash the caller IP rather than persist it —
    /// tenant-correlation is the only thing we need from the field, raw
    /// addresses are PII we don't want lying around.
    /// </summary>
    private async Task WriteCrossTenantDeniedAuditAsync(
        Guid projectId,
        string? secretKey,
        string actor,
        CancellationToken ct)
    {
        var rawIp = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(rawIp));
        var fingerprint = Convert.ToHexString(hash, 0, 8).ToLowerInvariant();

        var metadata = JsonSerializer.Serialize(new { ipFingerprint = fingerprint });

        _db.SecretAuditEvents.Add(new SecretAuditEvent
        {
            Id = Guid.NewGuid(),
            Action = SecretAuditAction.CrossTenantDenied,
            ProjectId = projectId,
            SecretId = null,
            SecretKey = secretKey,
            Actor = actor,
            Metadata = metadata,
            CreatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync(ct);

        _logger.LogWarning(
            "ProjectSecrets: CrossTenantDenied logged for project {ProjectId} actor {Actor} ip {Fingerprint}",
            projectId, actor, fingerprint);
    }

    private string GetActorUserId()
        => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "unknown";
}

/// <summary>
/// Body shape for <c>POST /api/projects/{projectId}/secrets</c>. Both fields
/// are required at the validation layer in <see cref="AddSecretCommand"/>;
/// declared nullable here so a missing body surfaces as an explicit
/// <c>invalid_key_format</c> / <c>invalid_plaintext</c> rather than a 400 from
/// the binding layer.
/// </summary>
public record AddSecretRequest(string? Key, string? Value);

/// <summary>
/// Body shape for <c>PUT /api/projects/{projectId}/secrets/{key}</c>. The key
/// is in the route — only the new plaintext value goes in the body.
/// </summary>
public record UpdateSecretRequest(string? Value);
