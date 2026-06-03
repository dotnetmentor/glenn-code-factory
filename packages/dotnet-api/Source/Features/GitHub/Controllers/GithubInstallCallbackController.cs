using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Source.Features.GitHub.Commands;
using Source.Features.RuntimeLifecycle.Configuration;
using Source.Shared.Controllers;

namespace Source.Features.GitHub.Controllers;

/// <summary>
/// Public callback that GitHub redirects to after the user finishes the install flow.
/// Anonymous — the workspace is recovered from the signed state token, not the auth principal.
///
/// Sits at <c>/api/github</c> (not <c>/api/workspaces/{slug}/github</c>) because GitHub
/// configures a single Setup URL per App; we can't include a per-workspace path segment.
/// </summary>
[ApiController]
[Route("api/github")]
[AllowAnonymous]
[Tags("GitHub Install")]
public class GithubInstallCallbackController : BaseApiController
{
    private readonly IRuntimeOptionsAccessor _runtimeOptions;

    public GithubInstallCallbackController(
        IMediator mediator,
        ILogger<GithubInstallCallbackController> logger,
        IRuntimeOptionsAccessor runtimeOptions)
        : base(mediator, logger)
    {
        _runtimeOptions = runtimeOptions;
    }

    /// <summary>
    /// GitHub install callback. Validates the signed state, persists the installation row,
    /// kicks off the initial repo sync, and bounces the user back to the workspace
    /// home at <c>/w/{slug}</c>. The {@code ?install=success|pending|cancelled}
    /// query param is read by WorkspaceLandingView to surface a one-time snackbar.
    /// </summary>
    [HttpGet("install/callback")]
    [ProducesResponseType(302)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> Callback(
        [FromQuery(Name = "installation_id")] long? installationId = null,
        [FromQuery(Name = "setup_action")] string? setupAction = null,
        [FromQuery] string? state = null,
        [FromQuery] string? code = null)
    {
        // Always read+delete the cookie so it's single-use, regardless of outcome.
        var cookieValue = Request.Cookies[GithubInstallController.StateCookieName];
        Response.Cookies.Delete(
            GithubInstallController.StateCookieName,
            new CookieOptions { Path = GithubInstallController.StateCookiePath });

        var result = await Mediator.Send(new HandleGithubInstallCallbackCommand(
            StateToken: state,
            StateCookieValue: cookieValue,
            InstallationId: installationId,
            SetupAction: setupAction,
            Code: code));

        if (!result.IsSuccess)
        {
            // 400 covers state mismatch, expired state, missing installation_id, cross-workspace install.
            // 404 only if the workspace is gone — surface it explicitly.
            if (string.Equals(result.Error, "Workspace not found", StringComparison.Ordinal))
            {
                return NotFound(new { error = result.Error });
            }
            Logger.LogWarning("GitHub install callback failed: {Error}", result.Error);
            return BadRequest(new { error = result.Error });
        }

        var slug = result.Value.WorkspaceSlug;
        // Land on the workspace home. The `?install=` flag drives a one-time
        // snackbar in WorkspaceLandingView, then it strips itself from the URL.
        string path;
        if (result.Value.Conflict)
        {
            // Same GitHub account already connected to another workspace. We
            // still bounce the user home (never a raw JSON page) and carry the
            // account + other-workspace name so the snackbar can spell out the
            // fix instead of a generic "installation failed".
            path = WorkspaceFrontendRoutes.HomeWithQuery(slug, new[]
            {
                new KeyValuePair<string, string?>("install", "conflict"),
                new KeyValuePair<string, string?>("conflictAccount", result.Value.ConflictAccountLogin),
                new KeyValuePair<string, string?>("conflictWorkspace", result.Value.ConflictWorkspaceName),
            });
        }
        else
        {
            var status = result.Value.Pending ? "pending" : "success";
            path = WorkspaceFrontendRoutes.HomeWithQuery(slug, "install", status);
        }

        // Build an ABSOLUTE redirect against the deployment's canonical public host
        // (Runtime:PublicApiUrl SystemSetting — live-editable from Super Admin). The
        // incoming Host header is the GitHub App's globally-configured Setup URL host,
        // which may be a stale Cloudflare tunnel hostname; if we returned a relative
        // redirect the browser would resolve it against that stale host and the user
        // would land on a tunnel that no longer routes anywhere. Building the absolute
        // URL from the SystemSetting forwards them onto the current workspace host.
        var publicApiUrl = _runtimeOptions.Current.PublicApiUrl;
        if (!string.IsNullOrWhiteSpace(publicApiUrl)
            && Uri.TryCreate(publicApiUrl, UriKind.Absolute, out var baseUri))
        {
            var absolute = $"{baseUri.GetLeftPart(UriPartial.Authority)}{path}";
            return Redirect(absolute);
        }

        // Fallback: no PublicApiUrl configured — emit a relative redirect (legacy
        // behaviour). The user will land on whichever host they came from, which is
        // fine in single-host setups and acceptable in tests where the setting is unset.
        Logger.LogWarning(
            "GitHub install callback: Runtime:PublicApiUrl is not configured; falling back to relative redirect. " +
            "If the GitHub App's Setup URL points at a stale host, the user will land there. " +
            "Set Runtime:PublicApiUrl in Super Admin → System Settings to fix.");
        return Redirect(path);
    }
}
