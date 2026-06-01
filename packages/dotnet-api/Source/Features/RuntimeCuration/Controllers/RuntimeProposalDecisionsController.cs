using System.Security.Claims;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Source.Features.RuntimeCuration.Commands;
using Source.Features.RuntimeCuration.Models;
using Source.Features.RuntimePresets.Contracts;
using Source.Infrastructure;
using Source.Infrastructure.Extensions;
using Source.Shared.Results;

namespace Source.Features.RuntimeCuration.Controllers;

/// <summary>
/// User-facing HTTP surface for resolving a pending <see cref="RuntimeProposal"/>
/// — Approve, Edit, or Reject. The daemon-facing
/// <see cref="RuntimeProposalsController"/> creates the proposal; this
/// controller's actions drive the user-decision flow that pushes a delta back
/// to the daemon (Approve / Edit) or dismisses the proposal (Reject).
///
/// <para><b>Auth.</b> Plain <see cref="AuthorizeAttribute"/> — i.e. user JWT,
/// NOT the RuntimeToken scheme. Per-project ownership is verified via
/// <see cref="OwnershipExtensions.CallerOwnsProjectAsync"/> at the top of
/// every action (Approve / Edit / Reject) — the same pattern as
/// <see cref="Source.Features.ProjectSecrets.Controllers.ProjectSecretsController"/>
/// and <see cref="Source.Features.GitOps.Controllers.GitBranchesController"/>.
/// These are write/decision actions so we use the strict owner-only gate
/// (NOT <see cref="OwnershipExtensions.CallerCanAccessProjectAsync"/>, which
/// widens to workspace members — read-only surfaces only). Non-owners and
/// non-existent project ids both collapse to <c>404</c> so cross-tenant
/// project existence isn't leaked.</para>
///
/// <para><b>Error mapping.</b> Single private <see cref="MapResult"/> helper:
/// <c>not_found</c> → 404, <c>already_decided</c> → 400, anything else → 400.
/// Mirrors the idiom used by <see cref="ProjectSecretsController.Update"/>.</para>
/// </summary>
[ApiController]
[Route("api/projects/{projectId:guid}/proposals/{proposalId:guid}")]
[Authorize]
[Tags("RuntimeProposalDecisions")]
public class RuntimeProposalDecisionsController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly ApplicationDbContext _db;

    public RuntimeProposalDecisionsController(IMediator mediator, ApplicationDbContext db)
    {
        _mediator = mediator;
        _db = db;
    }

    /// <summary>
    /// Approve a pending proposal verbatim. The daemon's proposed spec becomes
    /// <c>AppliedSpec</c> and is pushed back to the daemon as an additive
    /// delta. The daemon's eventual ack moves the row to
    /// <see cref="RuntimeProposalStatus.Applied"/> /
    /// <see cref="RuntimeProposalStatus.Failed"/>.
    /// </summary>
    [HttpPost("approve")]
    [ProducesResponseType(typeof(RuntimeProposalDto), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(401)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<RuntimeProposalDto>> Approve(
        Guid projectId,
        Guid proposalId,
        CancellationToken ct)
    {
        // Project-ownership gate — 404 (not 403) on mismatch so a probing
        // client can't distinguish "no such project" from "exists but not
        // yours". Write/decision action so we use CallerOwnsProjectAsync,
        // NOT the read-widening CallerCanAccessProjectAsync.
        if (!await _db.CallerOwnsProjectAsync(User, projectId, ct))
        {
            return NotFound();
        }

        var actor = GetActorUserId();
        var result = await _mediator.Send(
            new ApproveProposalCommand(projectId, proposalId, actor), ct);
        return MapResult(result);
    }

    /// <summary>
    /// Edit a pending proposal: ship a user-edited <see cref="RuntimeSpecV3"/>
    /// body in place of the daemon's. Structural validation mirrors the create
    /// path (version stamp, unique kind/name pairs) and runs through the same
    /// expander for preset existence + parameter typing. Same delta-push +
    /// broadcast as <see cref="Approve"/>.
    /// </summary>
    [HttpPost("edit")]
    [ProducesResponseType(typeof(RuntimeProposalDto), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(401)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<RuntimeProposalDto>> Edit(
        Guid projectId,
        Guid proposalId,
        [FromBody] EditProposalRequest request,
        CancellationToken ct)
    {
        // Project-ownership gate — see Approve for rationale.
        if (!await _db.CallerOwnsProjectAsync(User, projectId, ct))
        {
            return NotFound();
        }

        if (request?.EditedSpec is null)
        {
            return BadRequest(new { error = "spec_required" });
        }

        var actor = GetActorUserId();
        var result = await _mediator.Send(
            new EditProposalCommand(
                ProjectId: projectId,
                ProposalId: proposalId,
                EditedSpec: request.EditedSpec,
                ActorUserId: actor),
            ct);
        return MapResult(result);
    }

    /// <summary>
    /// Reject a pending proposal. Runtime is untouched, daemon receives no
    /// push, only the project group hears about it.
    /// </summary>
    [HttpPost("reject")]
    [ProducesResponseType(typeof(RuntimeProposalDto), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(401)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<RuntimeProposalDto>> Reject(
        Guid projectId,
        Guid proposalId,
        CancellationToken ct)
    {
        // Project-ownership gate — see Approve for rationale.
        if (!await _db.CallerOwnsProjectAsync(User, projectId, ct))
        {
            return NotFound();
        }

        var actor = GetActorUserId();
        var result = await _mediator.Send(
            new RejectProposalCommand(projectId, proposalId, actor), ct);
        return MapResult(result);
    }

    /// <summary>
    /// Translate a <see cref="Result{T}"/> into the appropriate
    /// <see cref="ActionResult{T}"/>. <c>not_found</c> → 404 (also covers the
    /// cross-tenant proposal lookup miss), everything else → 400 with the raw
    /// error code in the body for the frontend to switch on
    /// (<c>already_decided</c>, <c>unsupported_language: ...</c>, etc.).
    /// </summary>
    private ActionResult<RuntimeProposalDto> MapResult(Result<RuntimeProposalDto> result)
    {
        if (result.IsSuccess)
        {
            return Ok(result.Value);
        }

        var err = result.Error ?? "unknown_error";
        return err == "not_found"
            ? NotFound(new { error = err })
            : BadRequest(new { error = err });
    }

    private string GetActorUserId()
        => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "unknown";
}

/// <summary>
/// Body shape for <c>POST /api/projects/{projectId}/proposals/{proposalId}/edit</c>.
/// Carries the user-edited <see cref="RuntimeSpecV3"/> verbatim — the user is
/// overriding the daemon's proposed spec, not re-explaining it (so no
/// <c>Reason</c> field).
/// </summary>
public record EditProposalRequest(
    RuntimeSpecV3 EditedSpec);
