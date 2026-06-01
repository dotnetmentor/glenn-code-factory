using System.ComponentModel.DataAnnotations;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Source.Features.Workspaces.Commands;
using Source.Shared.Controllers;

namespace Source.Features.Workspaces.Controllers;

/// <summary>
/// Top-level invite-acceptance endpoint. Lives outside <c>/api/workspaces/{slug}</c> because
/// the caller is not yet a member of the target workspace, so workspace-scoped auth would 403 them.
/// We rely on plain authentication + the invite's email-match check inside the handler.
/// </summary>
[ApiController]
[Route("api/invites")]
[Authorize]
[Tags("Invites")]
public class InvitesController : BaseApiController
{
    public InvitesController(IMediator mediator, ILogger<InvitesController> logger)
        : base(mediator, logger) { }

    /// <summary>
    /// Accept a workspace invite. The caller's email must match the invite's email.
    /// On success: a Membership row is created and the invite is marked accepted.
    /// </summary>
    [HttpPost("accept")]
    [ProducesResponseType<AcceptInviteResponse>(200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(401)]
    public async Task<ActionResult<AcceptInviteResponse>> Accept([FromBody] AcceptInviteRequest request)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        var result = await Mediator.Send(new AcceptInviteCommand(userId, request.Token));
        return HandleResult(result);
    }
}

public class AcceptInviteRequest
{
    [Required, StringLength(256, MinimumLength = 16)]
    public required string Token { get; init; }
}
