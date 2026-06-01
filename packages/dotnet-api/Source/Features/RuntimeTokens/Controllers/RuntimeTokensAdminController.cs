using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Source.Features.RuntimeTokens.Commands;
using Source.Features.RuntimeTokens.Models;
using Source.Infrastructure;
using Source.Infrastructure.AuthorizationModels;

namespace Source.Features.RuntimeTokens.Controllers;

/// <summary>
/// Operator-only HTTP surface for revoking RuntimeTokens. Three flavours: per-token
/// (jti), per-runtime (all alive tokens for one runtime), per-tenant (everything
/// signed for that tenant). Each endpoint is a thin pass-through to the matching
/// MediatR command from <see cref="Source.Features.RuntimeTokens.Commands"/> —
/// the commands own the domain rules (idempotency, already-expired no-op, cache prime).
///
/// <para><b>Why MediatR here, when other admin controllers don't use it.</b> The
/// revocation commands already exist for the rotation job to call; reusing them
/// keeps the cache-prime + idempotency policy in exactly one place. The controller
/// stays thin in spirit even with one extra hop.</para>
///
/// <para><b>Auth.</b> User-JWT scheme + <see cref="RoleConstants.SuperAdmin"/> role —
/// matches every other admin surface. Operators are users, not runtimes; do NOT use
/// the RuntimeToken scheme here.</para>
/// </summary>
[ApiController]
[Route("api/admin")]
[Authorize(Roles = RoleConstants.SuperAdmin)]
[Tags("RuntimeTokensAdmin")]
public class RuntimeTokensAdminController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly ApplicationDbContext _db;

    public RuntimeTokensAdminController(IMediator mediator, ApplicationDbContext db)
    {
        _mediator = mediator;
        _db = db;
    }

    /// <summary>
    /// List <see cref="RuntimeTokenIssue"/> rows, optionally filtered by
    /// <paramref name="runtimeId"/>. Returns the full audit columns (issue
    /// time, expiry, revocation) plus the batched usage metrics
    /// (<c>LastUsedAt</c>, <c>RequestCount</c>) so the admin UI can render a
    /// "tokens for this runtime" panel without a follow-up read.
    ///
    /// <para>Ordered by <c>IssuedAt DESC</c> — newest first matches operator
    /// intuition. Hard-capped at 200 rows; bigger windows can paginate later.
    /// Soft-deleted tokens are not a concept here (the table is append-only),
    /// but revoked rows are included — the <c>RevokedAt</c> column is the
    /// signal.</para>
    /// </summary>
    [HttpGet("runtime-tokens")]
    [ProducesResponseType(typeof(List<RuntimeTokenIssueDto>), 200)]
    [ProducesResponseType(401)]
    [ProducesResponseType(403)]
    public async Task<ActionResult<List<RuntimeTokenIssueDto>>> List(
        [FromQuery] Guid? runtimeId,
        CancellationToken ct)
    {
        var query = _db.RuntimeTokenIssues.AsQueryable();
        if (runtimeId.HasValue)
        {
            query = query.Where(t => t.RuntimeId == runtimeId.Value);
        }

        var rows = await query
            .OrderByDescending(t => t.IssuedAt)
            .Take(200)
            .Select(t => new RuntimeTokenIssueDto(
                t.Id,
                t.TenantId,
                t.RuntimeId,
                t.ProjectId,
                t.IssuedAt,
                t.ExpiresAt,
                t.RevokedAt,
                t.LastUsedAt,
                t.RequestCount))
            .ToListAsync(ct);

        return Ok(rows);
    }

    /// <summary>
    /// Revoke a single token by its JWT id (jti). Idempotent — revoking an
    /// already-revoked or already-expired token returns 200 with the same jti.
    /// 404 only when no row matches the given jti at all.
    /// </summary>
    [HttpPost("runtime-tokens/{jti:guid}/revoke")]
    [ProducesResponseType(typeof(RevokeTokenResponse), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(401)]
    [ProducesResponseType(403)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<RevokeTokenResponse>> RevokeToken(
        Guid jti,
        [FromBody] RevokeRequest body,
        CancellationToken ct)
    {
        var result = await _mediator.Send(new RevokeTokenCommand(jti, body.Reason), ct);
        if (result.IsFailure)
        {
            // RevokeTokenCommand surfaces:
            //   "revocation_reason_required" -> 400
            //   "token_not_found"            -> 404
            return result.Error switch
            {
                "token_not_found"            => NotFound(new { error = result.Error }),
                "revocation_reason_required" => BadRequest(new { error = result.Error }),
                _                            => BadRequest(new { error = result.Error }),
            };
        }
        return Ok(new RevokeTokenResponse(jti));
    }

    /// <summary>
    /// Revoke every alive (non-revoked, non-expired) token for one runtime.
    /// Returns the number of rows actually flipped to revoked. 200 even when
    /// the count is zero — that's the "nothing to revoke" idempotent response.
    /// </summary>
    [HttpPost("runtimes/{runtimeId:guid}/revoke-tokens")]
    [ProducesResponseType(typeof(BulkRevokeResponse), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(401)]
    [ProducesResponseType(403)]
    public async Task<ActionResult<BulkRevokeResponse>> RevokeForRuntime(
        Guid runtimeId,
        [FromBody] RevokeRequest body,
        CancellationToken ct)
    {
        var result = await _mediator.Send(new RevokeAllForRuntimeCommand(runtimeId, body.Reason), ct);
        if (result.IsFailure)
        {
            return BadRequest(new { error = result.Error });
        }
        return Ok(new BulkRevokeResponse(result.Value));
    }

    /// <summary>
    /// Revoke every alive token whose <c>TenantId</c> claim matches.
    /// </summary>
    [HttpPost("tenants/{tenantId:guid}/revoke-tokens")]
    [ProducesResponseType(typeof(BulkRevokeResponse), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(401)]
    [ProducesResponseType(403)]
    public async Task<ActionResult<BulkRevokeResponse>> RevokeForTenant(
        Guid tenantId,
        [FromBody] RevokeRequest body,
        CancellationToken ct)
    {
        var result = await _mediator.Send(new RevokeAllForTenantCommand(tenantId, body.Reason), ct);
        if (result.IsFailure)
        {
            return BadRequest(new { error = result.Error });
        }
        return Ok(new BulkRevokeResponse(result.Value));
    }
}

/// <summary>
/// HTTP-only projection of <see cref="RuntimeTokenIssue"/> for the admin
/// list endpoint. Deliberately omits <c>TokenHash</c>, <c>Scope</c>,
/// <c>BranchId</c> and <c>RevocationReason</c> — the admin UI only needs
/// "who, when, expires, revoked, last used, request count". Add fields here
/// (and only here) when the UI grows new columns.
///
/// <para>NOT marked <c>[TranspilationSource]</c> — this rides over plain
/// HTTP / Orval, not SignalR, so the daemon-side TS generator doesn't see it.</para>
/// </summary>
public record RuntimeTokenIssueDto(
    Guid Jti,
    Guid? TenantId,
    Guid? RuntimeId,
    Guid? ProjectId,
    DateTime IssuedAt,
    DateTime? ExpiresAt,
    DateTime? RevokedAt,
    DateTime? LastUsedAt,
    long RequestCount);
