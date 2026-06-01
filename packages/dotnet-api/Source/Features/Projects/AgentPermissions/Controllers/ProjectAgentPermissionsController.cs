using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Source.Features.Projects.AgentPermissions.Commands.RemoveProjectAgentPermissions;
using Source.Features.Projects.AgentPermissions.Commands.UpsertProjectAgentPermissions;
using Source.Features.Projects.AgentPermissions.Models;
using Source.Features.Projects.AgentPermissions.Queries.GetProjectAgentPermissions;
using Source.Infrastructure.AuthorizationExtensions;
using Source.Infrastructure.AuthorizationModels;
using Source.Shared.Controllers;

namespace Source.Features.Projects.AgentPermissions.Controllers;

/// <summary>
/// CRUD endpoints for a project's Agent SDK permission override row. Powers
/// the project settings page at
/// <c>/w/{slug}/projects/{id}/settings/agent-permissions</c>. The shape is
/// 1-row-or-zero — presence of the row IS the override; absence means the
/// project falls through to the system defaults.
///
/// <para><b>Endpoints</b> (all under <c>/api/projects/{projectId}/agent-permissions</c>):</para>
/// <list type="bullet">
///   <item><c>GET</c> — returns the override row as
///         <see cref="ProjectAgentPermissionsDto"/>, or <c>null</c> if no
///         override is set. The settings UI uses the null to render its
///         toggle in the "off" position.</item>
///   <item><c>PUT</c> — upserts the override row from
///         <see cref="UpsertProjectAgentPermissionsRequest"/>. Validates the
///         permission mode and the bypass-needs-skip pairing.</item>
///   <item><c>DELETE</c> — hard-deletes the override row. Idempotent: removing
///         a non-existent override is a 204, not a 404.</item>
/// </list>
///
/// <para><b>Authorisation.</b> Caller must be a member of the project's
/// workspace — same gate as <c>GET /api/projects/{id}</c>. Non-members and
/// missing projects both collapse to a 404 so existence cannot be probed.</para>
/// </summary>
[ApiController]
[Route("api/projects/{projectId:guid}/agent-permissions")]
[Authorize]
[Tags("ProjectAgentPermissions")]
public class ProjectAgentPermissionsController : BaseApiController
{
    public ProjectAgentPermissionsController(
        IMediator mediator,
        ILogger<ProjectAgentPermissionsController> logger)
        : base(mediator, logger) { }

    /// <summary>
    /// Load the project's Agent SDK permission override row, or <c>null</c> if
    /// no override is set. A null body is a meaningful "no override" signal,
    /// distinct from a 404 (which means "no project / no access"). The
    /// settings UI uses the null to render its toggle in the "off" position.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(ProjectAgentPermissionsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ProjectAgentPermissionsDto?>> Get(Guid projectId)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        var result = await Mediator.Send(new GetProjectAgentPermissionsQuery(
            ProjectId: projectId,
            CallerUserId: userId));

        if (!result.IsSuccess)
        {
            if (result.Error?.StartsWith(GetProjectAgentPermissionsHandler.NotFoundPrefix, StringComparison.Ordinal) == true)
            {
                Logger.LogInformation(
                    "GetProjectAgentPermissions: 404 for project {ProjectId} caller {UserId}: {Error}",
                    projectId, userId, result.Error);
                return NotFound();
            }

            Logger.LogWarning("GetProjectAgentPermissions failed: {Error}", result.Error);
            return BadRequest(new { error = result.Error });
        }

        // result.Value is intentionally nullable — null is the "no override
        // row" signal, returned as a 200 with a null body so the frontend
        // can distinguish it from a 404 ("no project / no access").
        return Ok(result.Value);
    }

    /// <summary>
    /// Upsert the project's Agent SDK permission override row. Idempotent —
    /// the handler inserts a new row if none exists, or updates the existing
    /// one in place. Validates the mode and the bypass-needs-skip pairing
    /// before touching the row, so a bad request never leaves the database
    /// in a half-written state.
    /// </summary>
    [HttpPut]
    [ProducesResponseType(typeof(ProjectAgentPermissionsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ProjectAgentPermissionsDto>> Upsert(
        Guid projectId,
        [FromBody] UpsertProjectAgentPermissionsRequest? request)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        if (request is null)
        {
            return BadRequest(new { error = "invalid_body" });
        }

        var command = new UpsertProjectAgentPermissionsCommand(
            ProjectId: projectId,
            CallerUserId: userId,
            CallerIsSuperAdmin: User.IsInRole(RoleConstants.SuperAdmin),
            PermissionMode: request.PermissionMode ?? string.Empty,
            AllowDangerouslySkipPermissions: request.AllowDangerouslySkipPermissions,
            AllowedTools: request.AllowedTools ?? Array.Empty<string>(),
            DisallowedTools: request.DisallowedTools ?? Array.Empty<string>(),
            AdditionalDirectories: request.AdditionalDirectories ?? Array.Empty<string>());

        var result = await Mediator.Send(command);

        if (!result.IsSuccess)
        {
            if (result.Error?.StartsWith(UpsertProjectAgentPermissionsHandler.NotFoundPrefix, StringComparison.Ordinal) == true)
            {
                Logger.LogInformation(
                    "UpsertProjectAgentPermissions: 404 for project {ProjectId} caller {UserId}: {Error}",
                    projectId, userId, result.Error);
                return NotFound();
            }

            Logger.LogWarning(
                "UpsertProjectAgentPermissions validation failed for project {ProjectId}: {Error}",
                projectId, result.Error);
            return BadRequest(new { error = result.Error });
        }

        return Ok(result.Value);
    }

    /// <summary>
    /// Hard-delete the project's Agent SDK permission override row. After this
    /// returns 204, the resolver falls through to system defaults at the
    /// next turn. Idempotent: removing a non-existent override is also a
    /// 204, so two settings-page tabs racing each other don't trigger a 404
    /// on the second click.
    /// </summary>
    [HttpDelete]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> Delete(Guid projectId)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        var result = await Mediator.Send(new RemoveProjectAgentPermissionsCommand(
            ProjectId: projectId,
            CallerUserId: userId));

        if (!result.IsSuccess)
        {
            if (result.Error?.StartsWith(RemoveProjectAgentPermissionsHandler.NotFoundPrefix, StringComparison.Ordinal) == true)
            {
                Logger.LogInformation(
                    "RemoveProjectAgentPermissions: 404 for project {ProjectId} caller {UserId}: {Error}",
                    projectId, userId, result.Error);
                return NotFound();
            }

            Logger.LogWarning(
                "RemoveProjectAgentPermissions failed for project {ProjectId}: {Error}",
                projectId, result.Error);
            return BadRequest(new { error = result.Error });
        }

        return NoContent();
    }
}
