using System.ComponentModel.DataAnnotations;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Source.Features.Projects.Queries;
using Source.Features.Projects.Queries.ListWorkspaceProjects;
using Source.Features.Projects.Queries.ListWorkspaceRecentBranches;
using Source.Features.Projects.Commands.UpdateProjectByok;
using Source.Features.Workspaces.Commands;
using Source.Features.Workspaces.Commands.UpdateWorkspaceByok;
using Source.Features.Workspaces.Models;
using Source.Features.Workspaces.Queries;
using Source.Infrastructure.Workspaces;
using Source.Shared.Controllers;
// ReSharper disable UnusedParameter.Global  -- {slug} route values are read by RequireWorkspaceRoleAttribute

namespace Source.Features.Workspaces.Controllers;

[ApiController]
[Route("api/workspaces")]
[Authorize]
[Tags("Workspaces")]
public class WorkspacesController : BaseApiController
{
    public WorkspacesController(IMediator mediator, ILogger<WorkspacesController> logger)
        : base(mediator, logger) { }

    /// <summary>
    /// Create a new workspace owned by the current user. They become the Owner.
    /// </summary>
    [HttpPost]
    [ProducesResponseType<CreateWorkspaceResponse>(201)]
    [ProducesResponseType(400)]
    [ProducesResponseType(401)]
    public async Task<ActionResult<CreateWorkspaceResponse>> Create([FromBody] CreateWorkspaceRequest request)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        var result = await Mediator.Send(new CreateWorkspaceCommand(
            OwnerUserId: userId,
            Name: request.Name,
            Slug: request.Slug,
            SlugSeed: request.Name));

        if (!result.IsSuccess) return BadRequest(new { error = result.Error });
        return CreatedAtAction(nameof(GetBySlug), new { slug = result.Value.Slug }, result.Value);
    }

    [HttpGet("{slug}")]
    [RequireWorkspaceRole(WorkspaceRole.Member)]
    [ProducesResponseType<WorkspaceDetailsResponse>(200)]
    [ProducesResponseType(401)]
    [ProducesResponseType(403)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<WorkspaceDetailsResponse>> GetBySlug(string slug)
    {
        var result = await Mediator.Send(new GetCurrentWorkspaceQuery());
        return HandleResult(result);
    }

    [HttpPut("{slug}")]
    [RequireWorkspaceRole(WorkspaceRole.Owner)]
    [ProducesResponseType<RenameWorkspaceResponse>(200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(401)]
    [ProducesResponseType(403)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<RenameWorkspaceResponse>> Rename(string slug, [FromBody] RenameWorkspaceRequest request)
    {
        var result = await Mediator.Send(new RenameWorkspaceCommand(request.Name, request.Slug));
        return HandleResult(result);
    }

    [HttpDelete("{slug}")]
    [RequireWorkspaceRole(WorkspaceRole.Owner)]
    [ProducesResponseType(204)]
    [ProducesResponseType(401)]
    [ProducesResponseType(403)]
    [ProducesResponseType(404)]
    public async Task<ActionResult> Delete(string slug)
    {
        var result = await Mediator.Send(new DeleteWorkspaceCommand());
        if (!result.IsSuccess) return BadRequest(new { error = result.Error });
        return NoContent();
    }

    // ----- Members -------------------------------------------------------

    /// <summary>List all members of the workspace (any member can read).</summary>
    [HttpGet("{slug}/members")]
    [RequireWorkspaceRole(WorkspaceRole.Member)]
    [ProducesResponseType<IReadOnlyList<WorkspaceMemberItem>>(200)]
    [ProducesResponseType(401)]
    [ProducesResponseType(403)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<IReadOnlyList<WorkspaceMemberItem>>> GetMembers(string slug)
    {
        var result = await Mediator.Send(new GetWorkspaceMembersQuery());
        return HandleResult(result);
    }

    /// <summary>Change a member's role. Admin+ required. Cannot demote the last Owner.</summary>
    [HttpPut("{slug}/members/{userId}")]
    [RequireWorkspaceRole(WorkspaceRole.Admin)]
    [ProducesResponseType<ChangeMemberRoleResponse>(200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(401)]
    [ProducesResponseType(403)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<ChangeMemberRoleResponse>> ChangeMemberRole(string slug, string userId, [FromBody] ChangeMemberRoleRequest request)
    {
        var result = await Mediator.Send(new ChangeMemberRoleCommand(userId, request.Role));
        return HandleResult(result);
    }

    /// <summary>Remove a member. Admin+ required. Cannot remove the last Owner.</summary>
    [HttpDelete("{slug}/members/{userId}")]
    [RequireWorkspaceRole(WorkspaceRole.Admin)]
    [ProducesResponseType(204)]
    [ProducesResponseType(400)]
    [ProducesResponseType(401)]
    [ProducesResponseType(403)]
    [ProducesResponseType(404)]
    public async Task<ActionResult> RemoveMember(string slug, string userId)
    {
        var result = await Mediator.Send(new RemoveMemberCommand(userId));
        if (!result.IsSuccess) return BadRequest(new { error = result.Error });
        return NoContent();
    }

    // ----- Invites -------------------------------------------------------

    /// <summary>Create an invite. Admin+ required. Returns the token (only time it's exposed).</summary>
    [HttpPost("{slug}/invites")]
    [RequireWorkspaceRole(WorkspaceRole.Admin)]
    [ProducesResponseType<CreateInviteResponse>(201)]
    [ProducesResponseType(400)]
    [ProducesResponseType(401)]
    [ProducesResponseType(403)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<CreateInviteResponse>> CreateInvite(string slug, [FromBody] CreateInviteRequest request)
    {
        var result = await Mediator.Send(new CreateInviteCommand(request.Email, request.Role));
        if (!result.IsSuccess) return BadRequest(new { error = result.Error });
        return CreatedAtAction(nameof(GetInvites), new { slug }, result.Value);
    }

    /// <summary>List pending invites. Admin+ required.</summary>
    [HttpGet("{slug}/invites")]
    [RequireWorkspaceRole(WorkspaceRole.Admin)]
    [ProducesResponseType<IReadOnlyList<WorkspaceInviteItem>>(200)]
    [ProducesResponseType(401)]
    [ProducesResponseType(403)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<IReadOnlyList<WorkspaceInviteItem>>> GetInvites(string slug)
    {
        var result = await Mediator.Send(new GetWorkspaceInvitesQuery());
        return HandleResult(result);
    }

    /// <summary>Revoke a pending invite. Admin+ required.</summary>
    [HttpDelete("{slug}/invites/{id}")]
    [RequireWorkspaceRole(WorkspaceRole.Admin)]
    [ProducesResponseType(204)]
    [ProducesResponseType(400)]
    [ProducesResponseType(401)]
    [ProducesResponseType(403)]
    [ProducesResponseType(404)]
    public async Task<ActionResult> RevokeInvite(string slug, Guid id)
    {
        var result = await Mediator.Send(new RevokeInviteCommand(id));
        if (!result.IsSuccess) return BadRequest(new { error = result.Error });
        return NoContent();
    }

    /// <summary>
    /// Workspace-level Cursor BYOK credentials. Admin+ required. Plaintext never echoes back.
    /// </summary>
    [HttpPost("{slug}/byok")]
    [RequireWorkspaceRole(WorkspaceRole.Admin)]
    [ProducesResponseType(typeof(UpdateWorkspaceByokResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<UpdateWorkspaceByokResponse>> UpdateByok(
        string slug,
        [FromBody] UpdateWorkspaceByokRequest? request)
    {
        if (request is null)
        {
            return BadRequest(new { error = "invalid_body" });
        }

        var cursor = request.SetCursorApiKey
            ? new OptionalSecret(IsSet: true, Value: request.CursorApiKey)
            : OptionalSecret.Unchanged();

        var result = await Mediator.Send(new UpdateWorkspaceByokCommand(
            CursorApiKey: cursor,
            SetAllowProjectCursorApiKeyOverride: request.SetAllowProjectCursorApiKeyOverride,
            AllowProjectCursorApiKeyOverride: request.AllowProjectCursorApiKeyOverride));

        if (!result.IsSuccess)
        {
            if (result.Error == "forbidden")
            {
                return Forbid();
            }

            if (result.Error == "not_found")
            {
                return NotFound();
            }

            return BadRequest(new { error = result.Error });
        }

        return Ok(result.Value);
    }

    // ----- Projects ------------------------------------------------------

    /// <summary>
    /// List all (non-soft-deleted) projects inside this workspace. Any member
    /// can read. Used by the workspace shell sidebar and landing page; the
    /// response is sorted "most recently updated first" so active projects
    /// float to the top.
    /// </summary>
    [HttpGet("{slug}/projects")]
    [RequireWorkspaceRole(WorkspaceRole.Member)]
    [ProducesResponseType(typeof(List<ProjectSummaryDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<List<ProjectSummaryDto>>> GetProjects(string slug)
    {
        var result = await Mediator.Send(new ListWorkspaceProjectsQuery());
        return HandleResult(result);
    }

    /// <summary>
    /// List the most recently active branches across all projects in this
    /// workspace. Used by the workspace landing page (<c>/w/:slug</c>) to
    /// render its "Recent work" list — clicks land on the exact branch the
    /// user last touched rather than the project root (which always redirects
    /// to the default branch). Sorted by activity recency, defaults to 10 rows,
    /// caps at 50 via <c>?limit=</c>.
    /// </summary>
    [HttpGet("{slug}/branches/recent")]
    [RequireWorkspaceRole(WorkspaceRole.Member)]
    [ProducesResponseType(typeof(List<RecentBranchDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<List<RecentBranchDto>>> GetRecentBranches(
        string slug,
        [FromQuery] int limit = 10)
    {
        var result = await Mediator.Send(new ListWorkspaceRecentBranchesQuery(limit));
        return HandleResult(result);
    }

    /// <summary>
    /// List projects in this workspace whose GitHub installation has been
    /// disconnected (FK NULL — the "detached" state). The frontend renders
    /// these in a "needs reconnection" panel and offers a one-click bulk
    /// reconnect via <c>POST /api/workspaces/{slug}/github/installations/{id}/reconnect-projects</c>
    /// once the user reinstalls the GitHub App for the same owner / org.
    /// </summary>
    [HttpGet("{slug}/projects/detached")]
    [RequireWorkspaceRole(WorkspaceRole.Member)]
    [ProducesResponseType(typeof(IReadOnlyList<DetachedProjectDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<DetachedProjectDto>>> GetDetachedProjects(string slug)
    {
        var result = await Mediator.Send(new GetDetachedProjectsQuery());
        return HandleResult(result);
    }
}

public class CreateWorkspaceRequest
{
    [Required, StringLength(120, MinimumLength = 1)]
    public required string Name { get; init; }

    [StringLength(60)]
    public string? Slug { get; init; }
}

public class RenameWorkspaceRequest
{
    [StringLength(120, MinimumLength = 1)]
    public string? Name { get; init; }

    [StringLength(60)]
    public string? Slug { get; init; }
}

public class ChangeMemberRoleRequest
{
    [Required]
    public required WorkspaceRole Role { get; init; }
}

public class CreateInviteRequest
{
    [Required, EmailAddress, StringLength(254)]
    public required string Email { get; init; }

    [Required]
    public required WorkspaceRole Role { get; init; }
}

public record UpdateWorkspaceByokRequest(
    bool SetCursorApiKey = false,
    string? CursorApiKey = null,
    bool SetAllowProjectCursorApiKeyOverride = false,
    bool? AllowProjectCursorApiKeyOverride = null);
