using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Source.Features.GitHub.Configuration;
using Source.Features.GitHub.Models;
using Source.Features.GitHub.Services;
using Source.Infrastructure.AuthorizationModels;

namespace Source.Features.GitHub.Controllers;

/// <summary>
/// Operator-only diagnostics for the GitHub integration. Distinct from
/// <see cref="GithubAuthController"/> (user-facing OAuth) and
/// <see cref="GithubController"/> (workspace-scoped repo / branch reads): this surface
/// is for the SystemSettings UI to verify that the configured <c>GitHub:*</c> values
/// actually authenticate against GitHub.
///
/// <para>SuperAdmin only — these endpoints surface configuration material in their
/// response shape (presence flags, App identity) that lower roles shouldn't see.</para>
/// </summary>
[ApiController]
[Route("api/admin/github")]
[Authorize(Roles = RoleConstants.SuperAdmin)]
[Tags("GitHubAdmin")]
public class GithubAdminController : ControllerBase
{
    private readonly IGithubOptionsAccessor _options;
    private readonly IGithubAppTokenService _tokenService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<GithubAdminController> _logger;

    public GithubAdminController(
        IGithubOptionsAccessor options,
        IGithubAppTokenService tokenService,
        IHttpClientFactory httpClientFactory,
        ILogger<GithubAdminController> logger)
    {
        _options = options;
        _tokenService = tokenService;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Probe the configured GitHub App credentials. Reads <c>GitHub:*</c> SystemSettings,
    /// reports presence of every key, mints an App JWT in-process, then calls
    /// <c>GET https://api.github.com/app</c> to confirm GitHub accepts the App identity.
    ///
    /// <para>Always returns 200 — auth failures are reported in the body so the UI can
    /// render a structured checklist. The only non-200 outcomes are the auth gate
    /// (401/403) before the action even runs.</para>
    /// </summary>
    [HttpPost("test-connection")]
    [ProducesResponseType(typeof(GithubTestConnectionResponse), 200)]
    [ProducesResponseType(401)]
    [ProducesResponseType(403)]
    public async Task<ActionResult<GithubTestConnectionResponse>> TestConnection(CancellationToken ct)
    {
        var options = _options.Current;

        var appIdSet = !string.IsNullOrWhiteSpace(options.AppId);
        var privateKeyPemSet = !string.IsNullOrWhiteSpace(options.PrivateKeyPem);
        var clientIdSet = !string.IsNullOrWhiteSpace(options.ClientId);
        var clientSecretSet = !string.IsNullOrWhiteSpace(options.ClientSecret);
        var oauthRedirectUriSet = !string.IsNullOrWhiteSpace(options.OAuthRedirectUri);
        var webhookSecretSet = !string.IsNullOrWhiteSpace(options.WebhookSecret);
        var appSlugSet = !string.IsNullOrWhiteSpace(options.AppSlug);
        var appInstallRedirectUriSet = !string.IsNullOrWhiteSpace(options.AppInstallRedirectUri);

        // 1) Try to mint a JWT — only meaningful when both AppId and PrivateKeyPem are present.
        var jwtMintable = false;
        string? jwtMintError = null;
        string? jwt = null;
        if (appIdSet && privateKeyPemSet)
        {
            try
            {
                jwt = _tokenService.CreateAppJwt();
                jwtMintable = true;
            }
            catch (Exception ex)
            {
                jwtMintError = ex.Message;
                _logger.LogWarning(ex, "GitHub test-connection: JWT mint failed");
            }
        }

        // 2) Call GET /app with the freshly minted JWT.
        var appCallSucceeded = false;
        int? appCallStatusCode = null;
        string? appCallError = null;
        string? appName = null;
        string? appOwner = null;
        string? appIdValue = null;
        string? appSlugValue = null;

        if (jwtMintable && jwt is not null)
        {
            try
            {
                var client = _httpClientFactory.CreateClient(GithubApiClient.HttpClientName);
                using var req = new HttpRequestMessage(HttpMethod.Get, "app");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
                req.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");

                using var resp = await client.SendAsync(req, ct);
                appCallStatusCode = (int)resp.StatusCode;

                if (resp.IsSuccessStatusCode)
                {
                    var body = await resp.Content.ReadAsStringAsync(ct);
                    using var doc = JsonDocument.Parse(body);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("id", out var idEl))
                    {
                        appIdValue = idEl.ValueKind == JsonValueKind.Number
                            ? idEl.GetRawText()
                            : idEl.GetString();
                    }
                    if (root.TryGetProperty("slug", out var slugEl) && slugEl.ValueKind == JsonValueKind.String)
                    {
                        appSlugValue = slugEl.GetString();
                    }
                    if (root.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
                    {
                        appName = nameEl.GetString();
                    }
                    if (root.TryGetProperty("owner", out var ownerEl) && ownerEl.ValueKind == JsonValueKind.Object
                        && ownerEl.TryGetProperty("login", out var loginEl) && loginEl.ValueKind == JsonValueKind.String)
                    {
                        appOwner = loginEl.GetString();
                    }
                    appCallSucceeded = true;
                }
                else
                {
                    var body = await resp.Content.ReadAsStringAsync(ct);
                    // Cap to avoid putting an entire HTML error page in the diagnostic body.
                    appCallError = body.Length > 500 ? body[..500] : body;
                    if (string.IsNullOrWhiteSpace(appCallError))
                    {
                        appCallError = resp.ReasonPhrase ?? "GitHub returned a non-success status";
                    }
                }
            }
            catch (Exception ex)
            {
                appCallError = ex.Message;
                _logger.LogWarning(ex, "GitHub test-connection: GET /app failed");
            }
        }

        var isValid = jwtMintable && appCallSucceeded && clientIdSet && clientSecretSet;

        var message = ComposeMessage(
            isValid,
            appIdSet, privateKeyPemSet, clientIdSet, clientSecretSet,
            oauthRedirectUriSet, webhookSecretSet, appSlugSet, appInstallRedirectUriSet,
            jwtMintable, jwtMintError,
            appCallSucceeded, appCallStatusCode, appCallError,
            appName, appOwner);

        return Ok(new GithubTestConnectionResponse(
            AppIdSet: appIdSet,
            PrivateKeyPemSet: privateKeyPemSet,
            ClientIdSet: clientIdSet,
            ClientSecretSet: clientSecretSet,
            OAuthRedirectUriSet: oauthRedirectUriSet,
            WebhookSecretSet: webhookSecretSet,
            AppSlugSet: appSlugSet,
            AppInstallRedirectUriSet: appInstallRedirectUriSet,
            JwtMintable: jwtMintable,
            JwtMintError: jwtMintError,
            AppCallSucceeded: appCallSucceeded,
            AppCallStatusCode: appCallStatusCode,
            AppCallError: appCallError,
            AppName: appName,
            AppOwner: appOwner,
            AppId: appIdValue,
            AppSlug: appSlugValue,
            IsValid: isValid,
            Message: message));
    }

    private static string ComposeMessage(
        bool isValid,
        bool appIdSet, bool privateKeyPemSet, bool clientIdSet, bool clientSecretSet,
        bool oauthRedirectUriSet, bool webhookSecretSet, bool appSlugSet, bool appInstallRedirectUriSet,
        bool jwtMintable, string? jwtMintError,
        bool appCallSucceeded, int? appCallStatusCode, string? appCallError,
        string? appName, string? appOwner)
    {
        if (isValid)
        {
            // All-green path. Owner login is the most useful identifier — use the App
            // name as a secondary label so the operator sees what they think they see.
            var ownerStr = string.IsNullOrEmpty(appOwner) ? "GitHub" : $"@{appOwner}";
            var nameStr = string.IsNullOrEmpty(appName) ? "(unknown)" : appName;
            return $"Connected as {ownerStr}'s App: {nameStr}";
        }

        // Missing-keys: report all missing rather than just the first — the UI can show a list.
        var missing = new List<string>();
        if (!appIdSet) missing.Add("AppId");
        if (!privateKeyPemSet) missing.Add("PrivateKeyPem");
        if (!clientIdSet) missing.Add("ClientId");
        if (!clientSecretSet) missing.Add("ClientSecret");
        if (!oauthRedirectUriSet) missing.Add("OAuthRedirectUri");
        if (!webhookSecretSet) missing.Add("WebhookSecret");
        if (!appSlugSet) missing.Add("AppSlug");
        if (!appInstallRedirectUriSet) missing.Add("AppInstallRedirectUri");
        if (missing.Count > 0)
        {
            return $"Configuration incomplete: missing {string.Join(", ", missing)}";
        }

        // Keys were all present but JWT mint blew up — usually a malformed PEM.
        if (!jwtMintable)
        {
            return $"Cannot mint JWT: {jwtMintError ?? "unknown error"}";
        }

        // JWT minted but GitHub rejected it.
        if (!appCallSucceeded)
        {
            var code = appCallStatusCode?.ToString() ?? "no-response";
            var err = string.IsNullOrEmpty(appCallError) ? "(no body)" : appCallError;
            return $"GitHub rejected the App credentials: HTTP {code} {err}";
        }

        // Catch-all; shouldn't be reachable given the branches above but keeps the message contract.
        return "Configuration incomplete";
    }
}
