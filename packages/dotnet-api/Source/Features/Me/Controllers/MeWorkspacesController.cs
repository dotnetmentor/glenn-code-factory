using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Source.Features.Workspaces.Queries;
using Source.Shared.Controllers;

namespace Source.Features.Me.Controllers;

/// <summary>
/// "Me"-scoped endpoints for workspaces — what the *currently authenticated user* can see,
/// not the per-workspace endpoints (which are at /api/workspaces/{slug}/...).
/// </summary>
[ApiController]
[Route("api/me/workspaces")]
[Authorize]
[Tags("Me")]
public class MeWorkspacesController : BaseApiController
{
    public MeWorkspacesController(IMediator mediator, ILogger<MeWorkspacesController> logger)
        : base(mediator, logger) { }

    /// <summary>
    /// All workspaces the caller belongs to. Used by the workspace picker on the frontend.
    /// </summary>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<MyWorkspaceItem>>(200)]
    [ProducesResponseType(401)]
    public async Task<ActionResult<IReadOnlyList<MyWorkspaceItem>>> GetMyWorkspaces()
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        var result = await Mediator.Send(new GetMyWorkspacesQuery(userId));
        return HandleResult(result);
    }
}
