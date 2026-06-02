using System.Net;
using System.Security.Cryptography;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Source.Features.GitHub.Commands;
using Source.Features.GitHub.Configuration;
using Source.Shared.Controllers;

namespace Source.Features.GitHub.Controllers;

/// <summary>
/// GitHub OAuth user-identity flow (separate from the App-installation flow which lives elsewhere).
/// Both endpoints are anonymous — the user is, by definition, not yet logged in when they hit them.
/// On success the callback issues the same <c>auth-token</c> cookie that the password login emits.
/// </summary>
[ApiController]
[Route("api/github")]
[AllowAnonymous]
[Tags("GitHub Auth")]
public class GithubAuthController : BaseApiController
{
    private const string StateCookieName = "gh_oauth_state";
    private const string StateCookiePath = "/api/github";
    private const string AuthCookieName = "auth-token";
    private const int StateCookieTtlMinutes = 10;

    private readonly IWebHostEnvironment _environment;
    private readonly IGithubOptionsAccessor _options;

    public GithubAuthController(
        IMediator mediator,
        ILogger<GithubAuthController> logger,
        IWebHostEnvironment environment,
        IGithubOptionsAccessor options)
        : base(mediator, logger)
    {
        _environment = environment;
        _options = options;
    }

    /// <summary>
    /// Kick off the GitHub OAuth user-identity flow. Generates a CSRF state token, stashes it
    /// (along with an optional caller-provided post-login redirect path) in a short-lived cookie,
    /// and bounces the browser to GitHub's authorize endpoint.
    /// </summary>
    [HttpGet("login")]
    [ProducesResponseType(302)]
    [ProducesResponseType(400)]
    public IActionResult Login([FromQuery] string? redirectTo = null)
    {
        var options = _options.Current;
        if (string.IsNullOrWhiteSpace(options.ClientId))
        {
            return BadRequest(new { error = "GitHub OAuth is not configured" });
        }

        var state = GenerateUrlSafeToken();
        var safeRedirect = NormaliseRedirectForCookie(redirectTo);

        // Cookie payload is "{state}|{redirectTo}" (URL-encoded). Read back at callback time.
        var cookieValue = $"{state}|{WebUtility.UrlEncode(safeRedirect)}";
        Response.Cookies.Append(StateCookieName, cookieValue, BuildStateCookieOptions(expires: DateTime.UtcNow.AddMinutes(StateCookieTtlMinutes)));

        var authorizeUrl =
            "https://github.com/login/oauth/authorize" +
            $"?client_id={Uri.EscapeDataString(options.ClientId)}" +
            $"&redirect_uri={Uri.EscapeDataString(options.OAuthRedirectUri)}" +
            "&scope=read%3Auser%20user%3Aemail" +
            $"&state={Uri.EscapeDataString(state)}";

        return Redirect(authorizeUrl);
    }

    /// <summary>
    /// GitHub redirects the user back here with <c>code</c> and <c>state</c>. We validate state,
    /// resolve the GitHub identity to an app user (creating one + a workspace if new), set the
    /// auth cookie and redirect into the app.
    /// </summary>
    [HttpGet("login/callback")]
    [ProducesResponseType(302)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> Callback(
        [FromQuery] string? code = null,
        [FromQuery] string? state = null,
        [FromQuery(Name = "installation_id")] long? installationId = null,
        [FromQuery(Name = "setup_action")] string? setupAction = null)
    {
        // When the GitHub App has "Request user authorization (OAuth) during installation"
        // enabled, GitHub sends users to THIS URL (the OAuth Callback URL) instead of the
        // Setup URL after install — with both OAuth params (code, state) AND install params
        // (installation_id, setup_action) on the same redirect. Detect that and delegate to
        // the install handler, which owns workspace+install bookkeeping and also captures the
        // User Access Token from `code`. The install cookie path is "/api/github" so the
        // gh_install_state cookie is available on this route too.
        if (installationId.HasValue || !string.IsNullOrEmpty(setupAction))
        {
            var installCookieValue = Request.Cookies[GithubInstallController.StateCookieName];
            Response.Cookies.Delete(
                GithubInstallController.StateCookieName,
                new CookieOptions { Path = GithubInstallController.StateCookiePath });

            var installResult = await Mediator.Send(new HandleGithubInstallCallbackCommand(
                StateToken: state,
                StateCookieValue: installCookieValue,
                InstallationId: installationId,
                SetupAction: setupAction,
                Code: code));

            if (!installResult.IsSuccess)
            {
                if (string.Equals(installResult.Error, "Workspace not found", StringComparison.Ordinal))
                {
                    return NotFound(new { error = installResult.Error });
                }
                Logger.LogWarning("GitHub install (via OAuth callback) failed: {Error}", installResult.Error);
                return BadRequest(new { error = installResult.Error });
            }

            var installSlug = installResult.Value.WorkspaceSlug;
            var installRedirect = installResult.Value.Pending
                ? WorkspaceFrontendRoutes.HomeWithQuery(installSlug, "install", "pending")
                : WorkspaceFrontendRoutes.HomeWithQuery(installSlug, "install", "success");
            return Redirect(installRedirect);
        }

        // Always clear the state cookie — single use, regardless of outcome.
        var rawCookie = Request.Cookies[StateCookieName];
        Response.Cookies.Delete(StateCookieName, new CookieOptions { Path = StateCookiePath });

        if (string.IsNullOrEmpty(rawCookie))
        {
            return BadRequest(new { error = "Invalid state" });
        }

        var (cookieState, cookieRedirect) = ParseStateCookie(rawCookie);
        if (string.IsNullOrEmpty(cookieState) || string.IsNullOrEmpty(state) || !FixedTimeEquals(cookieState, state))
        {
            return BadRequest(new { error = "Invalid state" });
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            return BadRequest(new { error = "Missing code" });
        }

        var result = await Mediator.Send(new LoginWithGithubCommand(code, cookieRedirect));
        if (!result.IsSuccess)
        {
            Logger.LogWarning("GitHub login failed: {Error}", result.Error);
            return BadRequest(new { error = result.Error });
        }

        var cookieOptions = new CookieOptions
        {
            HttpOnly = true,
            Secure = _environment.IsDevelopment() || Request.IsHttps,
            SameSite = _environment.IsDevelopment() ? SameSiteMode.None : SameSiteMode.Lax,
            Expires = result.Value.AuthTokenExpiresAt,
            Path = "/",
        };
        Response.Cookies.Append(AuthCookieName, result.Value.AuthToken, cookieOptions);

        Logger.LogInformation("GitHub OAuth login completed for {Email} (user {UserId})", result.Value.Email, result.Value.UserId);
        return Redirect(result.Value.RedirectTo);
    }

    // -----------------------------------------------------------------------
    // helpers
    // -----------------------------------------------------------------------

    private CookieOptions BuildStateCookieOptions(DateTimeOffset expires) => new()
    {
        HttpOnly = true,
        Secure = _environment.IsDevelopment() || Request.IsHttps,
        // Lax (not Strict) — required so the cookie is sent on the top-level redirect from github.com.
        SameSite = _environment.IsDevelopment() ? SameSiteMode.None : SameSiteMode.Lax,
        Expires = expires,
        MaxAge = TimeSpan.FromMinutes(StateCookieTtlMinutes),
        Path = StateCookiePath,
    };

    private static string GenerateUrlSafeToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    private static (string? State, string? Redirect) ParseStateCookie(string raw)
    {
        var pipeIdx = raw.IndexOf('|');
        if (pipeIdx < 0) return (raw, null);
        var state = raw[..pipeIdx];
        var redirectEncoded = raw[(pipeIdx + 1)..];
        var redirect = string.IsNullOrEmpty(redirectEncoded) ? null : WebUtility.UrlDecode(redirectEncoded);
        return (state, redirect);
    }

    private static string? NormaliseRedirectForCookie(string? redirectTo)
    {
        if (string.IsNullOrWhiteSpace(redirectTo)) return null;
        // We persist exactly what the caller provided — open-redirect guard runs at callback time.
        return redirectTo.Trim();
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        var aBytes = System.Text.Encoding.UTF8.GetBytes(a);
        var bBytes = System.Text.Encoding.UTF8.GetBytes(b);
        if (aBytes.Length != bBytes.Length) return false;
        return CryptographicOperations.FixedTimeEquals(aBytes, bBytes);
    }
}
