using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Source.Features.GitHub.Commands;
using Source.Features.Workspaces.Models;
using Source.Infrastructure.Workspaces;
using Source.Shared.Controllers;
// ReSharper disable UnusedParameter.Global -- {slug} read by RequireWorkspaceRoleAttribute

namespace Source.Features.GitHub.Controllers;

/// <summary>
/// Re-authorize endpoints for the slim OAuth-only flow — used when an existing GitHub
/// installation has no usable User Access Token (legacy install captured before the UAT
/// feature, or refresh-expired after 6+ months idle). Distinct from the install
/// controller because we don't want to re-install the App on github.com — only the
/// user-OAuth side.
///
/// The <c>start</c> action is workspace-scoped + authenticated. The companion <c>callback</c>
/// is anonymous because GitHub configures a single Setup/Callback URL per App.
/// </summary>
[ApiController]
[Route("api/workspaces/{slug}/github/user-auth")]
[Authorize]
[Tags("GitHub User Auth")]
public class GithubUserAuthController : BaseApiController
{
    public GithubUserAuthController(IMediator mediator, ILogger<GithubUserAuthController> logger)
        : base(mediator, logger)
    {
    }

    /// <summary>
    /// Begin the OAuth-only re-authorize flow for the given installation. Returns the
    /// GitHub <c>https://github.com/login/oauth/authorize?…</c> URL — the frontend
    /// performs the redirect (top-level navigation) so the user lands back on the
    /// configured callback URL with <c>?code</c> and <c>?state</c>.
    /// </summary>
    [HttpPost("start")]
    [RequireWorkspaceRole(WorkspaceRole.Admin)]
    [ProducesResponseType<StartGithubUserAuthResponse>(200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(401)]
    [ProducesResponseType(403)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<StartGithubUserAuthResponse>> Start(
        string slug,
        [FromQuery] Guid installationId)
    {
        if (installationId == Guid.Empty)
        {
            return BadRequest(new { error = "installationId query parameter is required" });
        }

        var result = await Mediator.Send(new StartGithubUserAuthCommand(installationId));
        return HandleResultWithNotFound(result);
    }
}

/// <summary>
/// Public callback for the OAuth-only re-authorize flow. Anonymous — the workspace +
/// installation are recovered from the signed state token.
///
/// Sits at <c>/api/github</c> alongside <see cref="GithubInstallCallbackController"/>
/// because GitHub configures a single Callback URL per App; we can't include a
/// per-workspace path segment.
/// </summary>
[ApiController]
[Route("api/github/user-auth")]
[AllowAnonymous]
[Tags("GitHub User Auth")]
public class GithubUserAuthCallbackController : BaseApiController
{
    public GithubUserAuthCallbackController(
        IMediator mediator,
        ILogger<GithubUserAuthCallbackController> logger)
        : base(mediator, logger)
    {
    }

    /// <summary>
    /// OAuth re-authorize callback. Validates the signed state, exchanges the code for a
    /// fresh UAT/refresh pair, persists them on the installation, and bounces the user back
    /// to the workspace projects page with <c>?reauth=success|error</c>.
    /// </summary>
    [HttpGet("callback")]
    [ProducesResponseType(302)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> Callback(
        [FromQuery] string? code = null,
        [FromQuery] string? state = null)
    {
        var result = await Mediator.Send(new HandleGithubUserAuthCallbackCommand(
            Code: code,
            StateToken: state));

        if (!result.IsSuccess)
        {
            // State validation / workspace lookup failures — surface as 400. There's no
            // workspace slug to redirect to when the state token itself is bad, so we
            // can't bounce the user back to the projects page.
            if (string.Equals(result.Error, "Workspace not found", StringComparison.Ordinal))
            {
                return NotFound(new { error = result.Error });
            }
            Logger.LogWarning("GitHub user-auth callback failed: {Error}", result.Error);
            return BadRequest(new { error = result.Error });
        }

        var slug = result.Value.WorkspaceSlug;
        var flag = result.Value.Success ? "success" : "error";
        return Redirect($"/w/{slug}/projects?reauth={flag}");
    }
}
