using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Source.Features.Cost.Models;
using Source.Features.Cost.Queries;
using Source.Infrastructure;
using Source.Infrastructure.Extensions;

namespace Source.Features.Cost.Controllers;

/// <summary>
/// Read-only HTTP surface for LLM cost rollups at every level of the parent
/// chain: conversation, branch, project, workspace. Each endpoint returns the
/// same <see cref="CostSummaryResponse"/> shape — total cost in USD plus the
/// six token counters and a session count — so the frontend can render the
/// same panel anywhere the chain is mounted.
///
/// <para><b>Authorization.</b> Conversation / project / branch endpoints gate
/// via <see cref="OwnershipExtensions.CallerOwnsProjectAsync"/> or
/// <see cref="OwnershipExtensions.ResolveOwnedConversationAsync"/> — same
/// owner-only stance as the rest of the conversations surface. The workspace
/// endpoint gates on workspace membership (any role) so non-owner teammates
/// see workspace-wide spend in the in-workspace debug panel; same convention
/// as the workspace-specs catalog.</para>
///
/// <para>Both "no such resource" and "exists but not yours" surface as
/// <c>404</c> — never <c>403</c> — so cross-tenant existence isn't leaked.</para>
/// </summary>
[ApiController]
[Authorize]
[Tags("Cost")]
public class CostController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly ApplicationDbContext _db;

    public CostController(IMediator mediator, ApplicationDbContext db)
    {
        _mediator = mediator;
        _db = db;
    }

    /// <summary>
    /// Sum cost + token usage across every <c>AgentSession</c> in this
    /// conversation. Returns a zero-shaped response when the conversation has
    /// no sessions yet.
    /// </summary>
    [HttpGet("api/conversations/{id:guid}/cost")]
    [ProducesResponseType(typeof(CostSummaryResponse), 200)]
    [ProducesResponseType(401)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<CostSummaryResponse>> GetConversation(
        Guid id,
        CancellationToken ct)
    {
        // Same gate as the conversations controller — surfaces uniform 404 on
        // either "no such conversation" or "not yours".
        if (await _db.ResolveOwnedConversationAsync(User, id, ct) is null)
        {
            return NotFound();
        }

        var result = await _mediator.Send(new GetConversationCostQuery(id), ct);
        if (!result.IsSuccess)
        {
            return BadRequest(new { error = result.Error });
        }
        return Ok(result.Value);
    }

    /// <summary>
    /// Sum cost + token usage across every session under any conversation in
    /// this branch. Route includes <c>projectId</c> as the ownership anchor
    /// (the project is the owned resource) and validates the branch belongs to
    /// that project so cross-project branch-id probes don't bypass the gate.
    /// </summary>
    [HttpGet("api/projects/{projectId:guid}/branches/{branchId:guid}/cost")]
    [ProducesResponseType(typeof(CostSummaryResponse), 200)]
    [ProducesResponseType(401)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<CostSummaryResponse>> GetBranch(
        Guid projectId,
        Guid branchId,
        CancellationToken ct)
    {
        if (!await _db.CallerOwnsProjectAsync(User, projectId, ct))
        {
            return NotFound();
        }

        // Validate the branch is actually inside the project we just gated on.
        // Otherwise a caller could query any branchId once they own ANY project.
        var branchInProject = await _db.ProjectBranches
            .AsNoTracking()
            .AnyAsync(b => b.Id == branchId && b.ProjectId == projectId, ct);
        if (!branchInProject)
        {
            return NotFound();
        }

        var result = await _mediator.Send(new GetBranchCostQuery(branchId), ct);
        if (!result.IsSuccess)
        {
            return BadRequest(new { error = result.Error });
        }
        return Ok(result.Value);
    }

    /// <summary>
    /// Sum cost + token usage across every session under any branch of this
    /// project.
    /// </summary>
    [HttpGet("api/projects/{projectId:guid}/cost")]
    [ProducesResponseType(typeof(CostSummaryResponse), 200)]
    [ProducesResponseType(401)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<CostSummaryResponse>> GetProject(
        Guid projectId,
        CancellationToken ct)
    {
        if (!await _db.CallerOwnsProjectAsync(User, projectId, ct))
        {
            return NotFound();
        }

        var result = await _mediator.Send(new GetProjectCostQuery(projectId), ct);
        if (!result.IsSuccess)
        {
            return BadRequest(new { error = result.Error });
        }
        return Ok(result.Value);
    }

    /// <summary>
    /// Sum cost + token usage across every session under any project in this
    /// workspace. Workspace membership (any role) is the gate — matches the
    /// observability stance of the workspace specs catalog so non-owner
    /// teammates see total spend.
    /// </summary>
    [HttpGet("api/workspaces/{workspaceId:guid}/cost")]
    [ProducesResponseType(typeof(CostSummaryResponse), 200)]
    [ProducesResponseType(401)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<CostSummaryResponse>> GetWorkspace(
        Guid workspaceId,
        CancellationToken ct)
    {
        var callerUserId = User.GetUserId();
        if (string.IsNullOrEmpty(callerUserId))
        {
            return NotFound();
        }

        // Any-role membership is enough for read-only spend rollups.
        var isMember = await _db.WorkspaceMemberships
            .AsNoTracking()
            .AnyAsync(m => m.WorkspaceId == workspaceId && m.UserId == callerUserId, ct);
        if (!isMember)
        {
            return NotFound();
        }

        var result = await _mediator.Send(new GetWorkspaceCostQuery(workspaceId), ct);
        if (!result.IsSuccess)
        {
            return BadRequest(new { error = result.Error });
        }
        return Ok(result.Value);
    }
}
