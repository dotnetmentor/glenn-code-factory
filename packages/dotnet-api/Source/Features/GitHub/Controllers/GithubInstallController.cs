using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Source.Features.GitHub.Commands;
using Source.Features.GitHub.Queries;
using Source.Features.Workspaces.Models;
using Source.Infrastructure.Workspaces;
using Source.Shared.Controllers;
// ReSharper disable UnusedParameter.Global -- {slug} read by RequireWorkspaceRoleAttribute

namespace Source.Features.GitHub.Controllers;

/// <summary>
/// Workspace-scoped GitHub App management endpoints. Every action is gated by
/// <see cref="RequireWorkspaceRoleAttribute"/> at the appropriate role level.
/// The companion <see cref="GithubInstallCallbackController"/> hosts the public callback that
/// GitHub redirects to after the user completes the install flow.
/// </summary>
[ApiController]
[Route("api/workspaces/{slug}/github")]
[Authorize]
[Tags("GitHub Install")]
public class GithubInstallController : BaseApiController
{
    /// <summary>Cookie that carries the signed install state across the github.com round-trip.</summary>
    public const string StateCookieName = "gh_install_state";
    /// <summary>Cookie path — scoped to <c>/api/github</c> so it's sent on the public callback only.</summary>
    public const string StateCookiePath = "/api/github";

    private readonly IWebHostEnvironment _environment;

    public GithubInstallController(
        IMediator mediator,
        ILogger<GithubInstallController> logger,
        IWebHostEnvironment environment)
        : base(mediator, logger)
    {
        _environment = environment;
    }

    /// <summary>
    /// Begin a GitHub App install. Sets a short-lived signed state cookie and 302s to GitHub.
    /// </summary>
    [HttpGet("install/start")]
    [RequireWorkspaceRole(WorkspaceRole.Admin)]
    [ProducesResponseType(302)]
    [ProducesResponseType(400)]
    [ProducesResponseType(401)]
    [ProducesResponseType(403)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> StartInstall(string slug)
    {
        var result = await Mediator.Send(new StartGithubInstallCommand());
        if (!result.IsSuccess)
        {
            return BadRequest(new { error = result.Error });
        }

        Response.Cookies.Append(
            StateCookieName,
            result.Value.StateToken,
            BuildStateCookieOptions(result.Value.StateTtl));

        return Redirect(result.Value.RedirectUrl);
    }

    /// <summary>List GitHub App installations attached to this workspace.</summary>
    [HttpGet("installations")]
    [RequireWorkspaceRole(WorkspaceRole.Member)]
    [ProducesResponseType<IReadOnlyList<GithubInstallationListItem>>(200)]
    [ProducesResponseType(401)]
    [ProducesResponseType(403)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<IReadOnlyList<GithubInstallationListItem>>> GetInstallations(string slug)
    {
        var result = await Mediator.Send(new GetGithubInstallationsQuery());
        return HandleResult(result);
    }

    /// <summary>
    /// List repositories connected through this workspace's installations.
    /// Pass <paramref name="installationId"/> to filter to a single installation.
    /// </summary>
    [HttpGet("repositories")]
    [RequireWorkspaceRole(WorkspaceRole.Member)]
    [ProducesResponseType<IReadOnlyList<GithubRepositoryListItem>>(200)]
    [ProducesResponseType(401)]
    [ProducesResponseType(403)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<IReadOnlyList<GithubRepositoryListItem>>> GetRepositories(
        string slug,
        [FromQuery] Guid? installationId = null)
    {
        var result = await Mediator.Send(new GetGithubRepositoriesQuery(installationId));
        return HandleResult(result);
    }

    /// <summary>
    /// Re-pull the repository list for an installation from GitHub. Returns the diff counts.
    /// </summary>
    [HttpPost("repositories/sync")]
    [RequireWorkspaceRole(WorkspaceRole.Admin)]
    [ProducesResponseType<SyncGithubRepositoriesResponse>(200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(401)]
    [ProducesResponseType(403)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<SyncGithubRepositoriesResponse>> SyncRepositories(
        string slug,
        [FromQuery] Guid? installationId = null)
    {
        if (installationId is null || installationId == Guid.Empty)
        {
            return BadRequest(new { error = "installationId query parameter is required" });
        }

        var result = await Mediator.Send(new SyncGithubRepositoriesCommand(installationId.Value));
        return HandleResult(result);
    }

    /// <summary>
    /// Disconnect a GitHub installation from this workspace. Hard-removes our local rows
    /// and gracefully detaches any projects that were authenticated through it — the FK
    /// becomes NULL and the projects survive in a "detached" state, ready to be
    /// reconnected via <c>POST .../installations/{id}/reconnect-projects</c> after the
    /// user reinstalls the GitHub App. Does NOT revoke the installation on github.com —
    /// the caller must do that themselves.
    /// </summary>
    [HttpDelete("installations/{id}")]
    [RequireWorkspaceRole(WorkspaceRole.Admin)]
    [ProducesResponseType(204)]
    [ProducesResponseType(401)]
    [ProducesResponseType(403)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> RemoveInstallation(string slug, Guid id)
    {
        var result = await Mediator.Send(new RemoveGithubInstallationCommand(id));
        if (!result.IsSuccess)
        {
            // "not found" → 404; everything else → 400.
            if (result.Error?.Contains("not found", StringComparison.OrdinalIgnoreCase) == true)
            {
                return NotFound(new { error = result.Error });
            }
            return BadRequest(new { error = result.Error });
        }
        return NoContent();
    }

    /// <summary>
    /// Re-link every detached project in this workspace (FK NULL) whose
    /// <c>GithubRepoOwner</c> matches this installation's <c>AccountLogin</c>
    /// (case-insensitive). Idempotent — running it with no detached matches
    /// returns <c>ReconnectedCount = 0</c> instead of an error. The response
    /// carries the list of project ids that were rejoined so the UI can
    /// targeted-refresh only the rows that changed.
    /// </summary>
    [HttpPost("installations/{id}/reconnect-projects")]
    [RequireWorkspaceRole(WorkspaceRole.Admin)]
    [ProducesResponseType<ReconnectProjectsResponse>(200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(401)]
    [ProducesResponseType(403)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<ReconnectProjectsResponse>> ReconnectProjects(string slug, Guid id)
    {
        var result = await Mediator.Send(new ReconnectProjectsCommand(id));
        if (!result.IsSuccess)
        {
            if (result.Error?.Contains("not found", StringComparison.OrdinalIgnoreCase) == true)
            {
                return NotFound(new { error = result.Error });
            }
            return BadRequest(new { error = result.Error });
        }
        return Ok(result.Value);
    }

    // -----------------------------------------------------------------------

    private CookieOptions BuildStateCookieOptions(TimeSpan ttl) => new()
    {
        HttpOnly = true,
        Secure = _environment.IsDevelopment() || Request.IsHttps,
        // Lax (not Strict) — survives the top-level redirect from github.com.
        SameSite = _environment.IsDevelopment() ? SameSiteMode.None : SameSiteMode.Lax,
        Expires = DateTimeOffset.UtcNow.Add(ttl),
        MaxAge = ttl,
        Path = StateCookiePath,
    };
}
