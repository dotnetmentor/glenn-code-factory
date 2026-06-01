using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Source.Features.RuntimeCuration.Models;
using Source.Features.RuntimeCuration.Queries;
using Source.Infrastructure;
using Source.Infrastructure.Extensions;

namespace Source.Features.RuntimeCuration.Controllers;

/// <summary>
/// User-facing read surface for the runtime curation slice — the data the
/// project page renders for the proposal timeline + the live runtime spec
/// card. Three endpoints, all project-scoped, all behind plain
/// <see cref="AuthorizeAttribute"/>.
///
/// <para><b>Why one controller.</b> The two surfaces (proposal history and
/// live spec) are read together by the runtime card and benefit from a single
/// owner; mirrors the pattern used by other read-only project-scoped
/// controllers in the codebase.</para>
///
/// <para><b>Auth + access.</b> Default JWT bearer plus per-project access
/// gating via <see cref="OwnershipExtensions.CallerCanAccessProjectAsync"/> —
/// SuperAdmin OR project owner OR a member of the project's workspace. Same
/// read-side widening as <see cref="Source.Features.RuntimeLifecycle.Controllers.RuntimeStatusController"/>
/// and <see cref="BranchRuntimeProposalsReadController"/>: the workspace
/// debug panel consumes <c>runtime/spec</c> on every mount (to resolve the
/// runtime id) and the SpecTab consumes <c>proposals</c> for the pending-diff
/// preview, so workspace members must get through the gate too. Spec view
/// itself is still super-admin only in the UI initially; the endpoint
/// widening just matches the rest of the read-side observability surfaces
/// (spec: workspace-runtime-observability, Section E). Both "no such project"
/// and "exists but no access" surface as <c>404</c> so cross-tenant
/// proposal/runtime existence isn't leaked — same anti-leak convention as
/// <see cref="Source.Features.Diffs.Controllers.DiffsController"/>.</para>
/// </summary>
[ApiController]
[Route("api/projects/{projectId:guid}")]
[Authorize]
[Tags("RuntimeProposalsRead")]
public class RuntimeProposalsReadController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly ApplicationDbContext _db;

    public RuntimeProposalsReadController(IMediator mediator, ApplicationDbContext db)
    {
        _mediator = mediator;
        _db = db;
    }

    /// <summary>
    /// Proposal history for the project, newest first. Optional <c>status</c>
    /// query filter; <c>limit</c> defaults to 50 and is capped at 200 by the
    /// handler. 200 always — an empty list is the correct shape for "I have
    /// no proposals here".
    ///
    /// <para><b>Access gate.</b> Widened from strict-owner-only to the
    /// read-side gate (SuperAdmin OR project owner OR workspace member). The
    /// SpecTab in the workspace debug panel calls this endpoint to fetch the
    /// most-recent pending proposal (rendered as a diff preview); even though
    /// the Spec view itself is super-admin-gated in the UI today, the
    /// endpoint should match the rest of the read-side observability surfaces
    /// so super-admins who don't own the project still see the pending diff,
    /// and a future relaxation to all workspace members needs no backend
    /// change (spec: workspace-runtime-observability, Section E). Non-members
    /// (and probes for non-existent projects) get <c>404</c>, never <c>403</c>.</para>
    /// </summary>
    [HttpGet("proposals")]
    [ProducesResponseType(typeof(List<RuntimeProposalDto>), 200)]
    [ProducesResponseType(401)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<List<RuntimeProposalDto>>> List(
        Guid projectId,
        [FromQuery] RuntimeProposalStatus? status,
        [FromQuery] int limit = 50,
        CancellationToken ct = default)
    {
        if (!await _db.CallerCanAccessProjectAsync(User, projectId, ct))
        {
            return NotFound();
        }

        var result = await _mediator.Send(
            new ListRuntimeProposalsQuery(projectId, status, limit), ct);
        return result.IsSuccess
            ? Ok(result.Value)
            : BadRequest(new { error = result.Error });
    }

    /// <summary>
    /// Single proposal lookup. 404 (not 403) on cross-project to avoid
    /// leaking existence — same convention as the decision endpoints + the
    /// Spec 15 Kanban MCP.
    ///
    /// <para><b>Access gate.</b> Read-side widening — SuperAdmin OR project
    /// owner OR workspace member. The Apply History section expands each row
    /// to a side-by-side diff by fetching this endpoint twice (current +
    /// prior applied), so any caller that can see the history list must also
    /// be able to see the proposal it references (spec: workspace-runtime-
    /// observability, Section E). Non-members (and probes for non-existent
    /// projects/proposals) get <c>404</c>, never <c>403</c>.</para>
    /// </summary>
    [HttpGet("proposals/{proposalId:guid}")]
    [ProducesResponseType(typeof(RuntimeProposalDto), 200)]
    [ProducesResponseType(401)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<RuntimeProposalDto>> Get(
        Guid projectId,
        Guid proposalId,
        CancellationToken ct)
    {
        if (!await _db.CallerCanAccessProjectAsync(User, projectId, ct))
        {
            return NotFound();
        }

        var result = await _mediator.Send(
            new GetRuntimeProposalQuery(projectId, proposalId), ct);

        if (!result.IsSuccess)
        {
            return result.Error == "not_found"
                ? NotFound()
                : BadRequest(new { error = result.Error });
        }
        return Ok(result.Value);
    }

    /// <summary>
    /// Current runtime spec for the project — drives the runtime card's
    /// services / languages / extras list. 404 when the project has no
    /// runtime row at all; 200 with empty arrays when the runtime exists but
    /// the spec is null/legacy/malformed (the card renders "no spec yet").
    ///
    /// <para><b>Access gate.</b> Widened from strict-owner-only to the same
    /// read-side gate as <see cref="Source.Features.RuntimeLifecycle.Controllers.RuntimeStatusController"/>
    /// and <see cref="BranchRuntimeProposalsReadController"/>: SuperAdmin OR
    /// project owner OR a member of the project's workspace. The in-workspace
    /// debug panel uses this endpoint to resolve the runtime id (which then
    /// powers every other surface — service logs, sysstats, daemon logs), so a
    /// non-owner workspace teammate hitting the panel needs to get past this
    /// gate too. Spec view itself is still super-admin only in the UI; the
    /// endpoint widening is purely so the rest of the panel works for everyone
    /// (spec: workspace-runtime-observability, Section E). Non-members (and
    /// probes for non-existent projects) get <c>404</c>, never <c>403</c>.</para>
    /// </summary>
    [HttpGet("runtime/spec")]
    [ProducesResponseType(typeof(ProjectRuntimeSpecDto), 200)]
    [ProducesResponseType(401)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<ProjectRuntimeSpecDto>> GetSpec(
        Guid projectId,
        CancellationToken ct)
    {
        if (!await _db.CallerCanAccessProjectAsync(User, projectId, ct))
        {
            return NotFound();
        }

        var result = await _mediator.Send(
            new GetProjectRuntimeSpecQuery(projectId), ct);

        if (!result.IsSuccess)
        {
            return result.Error == "not_found"
                ? NotFound()
                : BadRequest(new { error = result.Error });
        }
        return Ok(result.Value);
    }
}
