namespace Source.Features.GitHub.Configuration;

/// <summary>
/// Strongly-typed binding for the <c>GitHub</c> configuration section.
/// Sourced from appsettings.json (placeholder values) — real secrets are injected
/// via environment / user-secrets in deployed environments.
/// </summary>
public class GithubOptions
{
    public const string SectionName = "GitHub";

    /// <summary>The numeric GitHub App ID (used as the <c>iss</c> claim of the App JWT).</summary>
    public string AppId { get; set; } = string.Empty;

    /// <summary>OAuth client id of the same App — used for user-identity sign-in flow.</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>OAuth client secret — paired with <see cref="ClientId"/> to exchange OAuth codes.</summary>
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>RSA private key in PEM format. Imported via <c>RSA.ImportFromPem</c>.</summary>
    public string PrivateKeyPem { get; set; } = string.Empty;

    /// <summary>Shared HMAC secret for webhook signature validation.</summary>
    public string WebhookSecret { get; set; } = string.Empty;

    /// <summary>Slug used in the GitHub-hosted install URL: <c>https://github.com/apps/{AppSlug}/installations/new</c>.</summary>
    public string AppSlug { get; set; } = string.Empty;

    /// <summary>Redirect target for the OAuth user-identity callback.</summary>
    public string OAuthRedirectUri { get; set; } = string.Empty;

    /// <summary>Redirect target after a workspace owner installs the GitHub App.</summary>
    public string AppInstallRedirectUri { get; set; } = string.Empty;
}
