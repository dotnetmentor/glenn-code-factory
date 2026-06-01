namespace Source.Features.GitHub.Models;

/// <summary>
/// Diagnostic payload returned by <c>POST /api/admin/github/test-connection</c>. Reports
/// (a) which <c>GitHub:*</c> SystemSettings keys are populated, (b) whether the App JWT
/// can be minted from the current key material, and (c) whether GitHub itself accepts
/// the credentials by way of <c>GET /app</c>.
///
/// <para>The endpoint always returns 200 with this body even on auth failure — the UI
/// reads the structured fields to render a checklist. <see cref="IsValid"/> is the
/// single boolean the UI uses for the "good / bad" overall verdict; <see cref="Message"/>
/// is the one-liner human-readable summary shown next to the button.</para>
/// </summary>
public sealed record GithubTestConnectionResponse(
    bool AppIdSet,
    bool PrivateKeyPemSet,
    bool ClientIdSet,
    bool ClientSecretSet,
    bool OAuthRedirectUriSet,
    bool WebhookSecretSet,
    bool AppSlugSet,
    bool AppInstallRedirectUriSet,
    bool JwtMintable,
    string? JwtMintError,
    bool AppCallSucceeded,
    int? AppCallStatusCode,
    string? AppCallError,
    string? AppName,
    string? AppOwner,
    string? AppId,
    string? AppSlug,
    bool IsValid,
    string Message);
