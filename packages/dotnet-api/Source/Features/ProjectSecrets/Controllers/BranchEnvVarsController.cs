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
/// Branch-scoped env-var CRUD + status surface. Sits at
/// <c>api/projects/{projectId}/branches/{branchId}/env</c> and operates on the
/// branch-effective view: project-wide rows (<c>BranchId == null</c>) overlaid by
/// this branch's own rows, branch winning per key. Writes always target the
/// branch (<c>BranchId == branchId</c>) — they create / rotate / delete a
/// branch-specific override, never the project-wide row (that lives on
/// <see cref="ProjectSecretsController"/>).
///
/// <para><b>Authorisation.</b> Two gates, both collapsing to 404 so cross-tenant
/// callers can't distinguish missing from forbidden:</para>
/// <list type="number">
///   <item>Project ownership — same query / audit as
///         <see cref="ProjectSecretsController.EnforceProjectOwnershipAsync"/>.</item>
///   <item>Branch existence + membership — the branch row must exist AND have
///         <c>ProjectId == projectId</c>. A branch from another project, or a
///         non-existent branch id, is 404.</item>
/// </list>
///
/// <para>Both gates run on every action via
/// <see cref="EnforceProjectAndBranchAsync"/> before any command / query is
/// dispatched. The commands themselves are the branch-aware
/// <see cref="AddSecretCommand"/> / <see cref="UpdateSecretCommand"/> /
/// <see cref="DeleteSecretCommand"/> reused from the project-level slice.</para>
/// </summary>
[ApiController]
[Route("api/projects/{projectId:guid}/branches/{branchId:guid}/env")]
[Authorize]
[Tags("BranchEnvVars")]
public class BranchEnvVarsController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly ApplicationDbContext _db;
    private readonly ILogger<BranchEnvVarsController> _logger;

    public BranchEnvVarsController(
        IMediator mediator,
        ApplicationDbContext db,
        ILogger<BranchEnvVarsController> logger)
    {
        _mediator = mediator;
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// List the branch-effective env vars for this branch. Plaintext is included
    /// only for non-secret rows; secret rows return <c>value: null</c> and must
    /// be revealed via the dedicated endpoint.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(List<BranchEnvVarItem>), 200)]
    [ProducesResponseType(401)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<List<BranchEnvVarItem>>> List(
        Guid projectId,
        Guid branchId,
        CancellationToken ct)
    {
        var deny = await EnforceProjectAndBranchAsync(projectId, branchId, secretKey: null, ct);
        if (deny is not null) return deny;

        var result = await _mediator.Send(new ListBranchEnvVarsQuery(projectId, branchId), ct);
        if (!result.IsSuccess)
        {
            return NotFound(new { error = result.Error });
        }
        return Ok(result.Value);
    }

    /// <summary>
    /// Add a branch-specific env var. <c>isSecret</c> defaults to true. 400 covers
    /// validation failures (<c>invalid_key_format</c> / <c>invalid_plaintext</c>)
    /// and the unique-key conflict (<c>key_already_exists</c>) for this branch.
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(AddSecretResponse), 201)]
    [ProducesResponseType(400)]
    [ProducesResponseType(401)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<AddSecretResponse>> Add(
        Guid projectId,
        Guid branchId,
        [FromBody] AddBranchEnvVarRequest request,
        CancellationToken ct)
    {
        var deny = await EnforceProjectAndBranchAsync(projectId, branchId, request?.Key, ct);
        if (deny is not null) return deny;

        if (request is null)
        {
            return BadRequest(new { error = "invalid_body" });
        }

        var actor = GetActorUserId();
        var result = await _mediator.Send(
            new AddSecretCommand(
                projectId,
                request.Key ?? string.Empty,
                request.Value ?? string.Empty,
                actor,
                BranchId: branchId,
                IsSecret: request.IsSecret ?? true),
            ct);

        if (!result.IsSuccess)
        {
            return BadRequest(new { error = result.Error });
        }

        return StatusCode(StatusCodes.Status201Created, result.Value);
    }

    /// <summary>
    /// Update / rotate a branch-specific env var's value (and its secret toggle).
    /// 404 when this branch has no row for the key.
    /// </summary>
    [HttpPut("{key}")]
    [ProducesResponseType(typeof(UpdateSecretResponse), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(401)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<UpdateSecretResponse>> Update(
        Guid projectId,
        Guid branchId,
        string key,
        [FromBody] UpdateBranchEnvVarRequest request,
        CancellationToken ct)
    {
        var deny = await EnforceProjectAndBranchAsync(projectId, branchId, key, ct);
        if (deny is not null) return deny;

        if (request is null)
        {
            return BadRequest(new { error = "invalid_body" });
        }

        var actor = GetActorUserId();
        var result = await _mediator.Send(
            new UpdateSecretCommand(
                projectId,
                key,
                request.Value ?? string.Empty,
                actor,
                BranchId: branchId,
                IsSecret: request.IsSecret ?? true),
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
    /// Soft-delete a branch-specific env var. 204 on success; 404 when this branch
    /// has no row for the key. Deleting a branch override does NOT touch the
    /// project-wide row — the key simply falls back to the project-wide value.
    /// </summary>
    [HttpDelete("{key}")]
    [ProducesResponseType(204)]
    [ProducesResponseType(400)]
    [ProducesResponseType(401)]
    [ProducesResponseType(404)]
    public async Task<ActionResult> Delete(
        Guid projectId,
        Guid branchId,
        string key,
        CancellationToken ct)
    {
        var deny = await EnforceProjectAndBranchAsync(projectId, branchId, key, ct);
        if (deny is not null) return deny;

        var actor = GetActorUserId();
        var result = await _mediator.Send(
            new DeleteSecretCommand(projectId, key, actor, BranchId: branchId), ct);

        if (!result.IsSuccess)
        {
            return result.Error == "not_found"
                ? NotFound(new { error = result.Error })
                : BadRequest(new { error = result.Error });
        }

        return NoContent();
    }

    /// <summary>
    /// Decrypt + return the plaintext for an env var as this branch sees it
    /// (branch-specific row if present, else the project-wide fallback).
    /// Privileged + audited — every success writes a
    /// <see cref="SecretAuditAction.Revealed"/> row.
    /// </summary>
    [HttpGet("{key}/reveal")]
    [ProducesResponseType(typeof(RevealSecretResponse), 200)]
    [ProducesResponseType(401)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<RevealSecretResponse>> Reveal(
        Guid projectId,
        Guid branchId,
        string key,
        CancellationToken ct)
    {
        var deny = await EnforceProjectAndBranchAsync(projectId, branchId, key, ct);
        if (deny is not null) return deny;

        var actor = GetActorUserId();
        var result = await _mediator.Send(
            new RevealBranchEnvVarQuery(projectId, branchId, key, actor), ct);

        if (!result.IsSuccess)
        {
            return NotFound(new { error = result.Error });
        }

        return Ok(result.Value);
    }

    /// <summary>
    /// Env readiness for this branch: which required vars the deployed spec
    /// declares, which the branch has (branch-effective), and which are missing.
    /// </summary>
    [HttpGet("status")]
    [ProducesResponseType(typeof(EnvStatusResponse), 200)]
    [ProducesResponseType(401)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<EnvStatusResponse>> Status(
        Guid projectId,
        Guid branchId,
        CancellationToken ct)
    {
        var deny = await EnforceProjectAndBranchAsync(projectId, branchId, secretKey: null, ct);
        if (deny is not null) return deny;

        var result = await _mediator.Send(new GetBranchEnvStatusQuery(projectId, branchId), ct);
        if (!result.IsSuccess)
        {
            return NotFound(new { error = result.Error });
        }
        return Ok(result.Value);
    }

    /// <summary>
    /// Combined ownership + branch-membership gate. Returns null when the caller
    /// owns the project AND the branch exists under that project; otherwise
    /// <see cref="UnauthorizedResult"/> (no identity) or <see cref="NotFoundResult"/>
    /// (project missing / not owned, OR branch missing / belongs to another
    /// project). Collapses missing vs forbidden to 404 so non-owners and
    /// cross-tenant probes cannot distinguish the two — same contract as the
    /// project-level controller. A failed ownership check also writes an inline
    /// <see cref="SecretAuditAction.CrossTenantDenied"/> row.
    /// </summary>
    private async Task<ActionResult?> EnforceProjectAndBranchAsync(
        Guid projectId,
        Guid branchId,
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

        if (!owns)
        {
            // 404, never 403 — don't leak project existence cross-tenant. Audit
            // the denial inline so security tooling can correlate probes.
            await WriteCrossTenantDeniedAuditAsync(projectId, secretKey, userId, ct);
            return NotFound();
        }

        // Branch must exist AND belong to this project. A cross-project branch id
        // (or a non-existent one) collapses to 404 — same don't-distinguish
        // contract. We already know the caller owns the project at this point, so
        // no extra audit row is needed for the branch miss.
        var branchOk = await _db.ProjectBranches
            .AsNoTracking()
            .AnyAsync(b => b.Id == branchId && b.ProjectId == projectId, ct);

        if (!branchOk)
        {
            return NotFound();
        }

        return null;
    }

    /// <summary>
    /// Persist an inline <see cref="SecretAuditAction.CrossTenantDenied"/> row on
    /// the ownership-failure branch. Mirrors
    /// <see cref="ProjectSecretsController"/>: we hash the caller IP (SHA-256,
    /// truncated) rather than persist raw PII.
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
            "BranchEnvVars: CrossTenantDenied logged for project {ProjectId} actor {Actor} ip {Fingerprint}",
            projectId, actor, fingerprint);
    }

    private string GetActorUserId()
        => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "unknown";
}

/// <summary>
/// Body for <c>POST .../env</c>. <see cref="Key"/> / <see cref="Value"/> are
/// validated in <see cref="AddSecretCommand"/>; nullable here so a missing body
/// surfaces as an explicit validation failure rather than a binding 400.
/// <see cref="IsSecret"/> defaults to true at the controller when omitted.
/// </summary>
public record AddBranchEnvVarRequest(string? Key, string? Value, bool? IsSecret);

/// <summary>
/// Body for <c>PUT .../env/{key}</c>. The key is in the route; the body carries
/// the new value and (optionally) the secret toggle. <see cref="IsSecret"/>
/// defaults to true when omitted.
/// </summary>
public record UpdateBranchEnvVarRequest(string? Value, bool? IsSecret);
