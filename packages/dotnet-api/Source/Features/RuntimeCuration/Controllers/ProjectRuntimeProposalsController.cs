using System.Security.Claims;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Source.Features.RuntimeCuration.Commands;
using Source.Infrastructure;

namespace Source.Features.RuntimeCuration.Controllers;

/// <summary>
/// User-facing companion to <see cref="RuntimeProposalsController"/>. The
/// daemon-facing endpoint at <c>POST /api/runtimes/{runtimeId}/proposals</c>
/// requires a <c>RuntimeToken</c> JWT (only the in-runtime daemon can call it).
/// When a real user opens the spec editor in the UI and clicks "Propose
/// changes", we need a SEPARATE path that uses standard user authentication.
///
/// <para><b>Same handler, different door.</b> This controller resolves the
/// project's current <c>ProjectRuntime</c> and delegates to the SAME
/// <see cref="CreateRuntimeProposalCommand"/> as the daemon endpoint — the
/// business logic (validate spec, persist row, fan out SignalR) is identical.
/// Only the auth scheme and the way we obtain the <c>RuntimeId</c> differ.</para>
///
/// <para><b>Auth.</b> Plain <see cref="AuthorizeAttribute"/> — user JWT, same
/// pattern as <see cref="RuntimeProposalDecisionsController"/>. The
/// per-project ownership check mirrors <see cref="Source.Features.ProjectSecrets.Controllers.ProjectSecretsController"/>:
/// look up the project by id and owning user id; collapse "doesn't exist" and
/// "exists but not yours" to a single 404 so non-owners cannot probe.</para>
/// </summary>
[ApiController]
[Route("api/projects/{projectId:guid}/proposals")]
[Authorize]
[Tags("ProjectRuntimeProposals")]
public class ProjectRuntimeProposalsController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly ApplicationDbContext _db;

    public ProjectRuntimeProposalsController(IMediator mediator, ApplicationDbContext db)
    {
        _mediator = mediator;
        _db = db;
    }

    /// <summary>
    /// Create a new <c>RuntimeProposal</c> for this project's current runtime
    /// from the user-side spec editor. Resolves the project's most-recent
    /// (non-deleted) <c>ProjectRuntime</c> via the same lookup
    /// <see cref="Source.Features.RuntimeLifecycle.Controllers.RuntimeStatusController.GetStatus"/>
    /// uses, then delegates to the shared
    /// <see cref="CreateRuntimeProposalCommand"/>. Returns the same
    /// <see cref="CreateRuntimeProposalResponse"/> shape as the daemon
    /// endpoint so the frontend can use a single response type.
    ///
    /// <para>Error mapping mirrors the daemon endpoint: <c>not_found</c> →
    /// 404 (also covers the cross-tenant project lookup miss), structural
    /// validation failures → 400 with the raw error code in the body.</para>
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(CreateRuntimeProposalResponse), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(401)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<CreateRuntimeProposalResponse>> Create(
        Guid projectId,
        [FromBody] CreateProjectRuntimeProposalRequest request,
        CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized();
        }

        if (request?.ProposedSpec is null)
        {
            return BadRequest(new { error = "spec_required" });
        }

        // Ownership check — collapse "missing" and "not yours" into 404 so
        // non-owners can't distinguish existence from forbidden. Same pattern
        // as ProjectSecretsController.EnforceProjectOwnershipAsync.
        var owns = await _db.Projects
            .AsNoTracking()
            .AnyAsync(p => p.Id == projectId && p.OwnerUserId == userId, ct);
        if (!owns)
        {
            return NotFound(new { error = "not_found" });
        }

        // Project's current (most-recent, non-deleted) runtime — same lookup as
        // RuntimeStatusController.GetStatus. Soft-deleted rows are filtered by
        // the global query filter on ProjectRuntime.
        var runtime = await _db.ProjectRuntimes
            .AsNoTracking()
            .Where(r => r.ProjectId == projectId)
            .OrderByDescending(r => r.CreatedAt)
            .Select(r => new { r.Id })
            .FirstOrDefaultAsync(ct);
        if (runtime is null)
        {
            return NotFound(new { error = "not_found" });
        }

        var result = await _mediator.Send(new CreateRuntimeProposalCommand(
            runtime.Id,
            request.ProposedSpec,
            request.Reason ?? string.Empty), ct);

        if (!result.IsSuccess)
        {
            var err = result.Error ?? "unknown_error";
            return err == "not_found"
                ? NotFound(new { error = err })
                : BadRequest(new { error = err });
        }

        return Ok(result.Value);
    }
}

/// <summary>
/// Body shape for <c>POST /api/projects/{projectId}/proposals</c>. Mirrors
/// <see cref="CreateRuntimeProposalRequest"/> exactly — same wire shape, just
/// renamed so Swagger generates a distinct DTO for the user-auth path. The
/// frontend's spec editor posts this when there is no pending proposal to
/// edit.
/// </summary>
public record CreateProjectRuntimeProposalRequest(
    Source.Features.RuntimePresets.Contracts.RuntimeSpecV3 ProposedSpec,
    string? Reason);
