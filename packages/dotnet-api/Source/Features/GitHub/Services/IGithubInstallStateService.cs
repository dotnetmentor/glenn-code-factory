namespace Source.Features.GitHub.Services;

/// <summary>
/// Mints + verifies the short-lived signed state token used during the GitHub App
/// install redirect dance. Payload carries the workspace id (so the callback knows
/// where to attach the installation) plus a random nonce + expiry.
///
/// Token format: <c>{base64url(json-payload)}.{base64url(hmac-sha256)}</c>.
/// HMAC key reuses <c>GitHub:WebhookSecret</c> — keeps config slim and there's no
/// cross-protocol confusion risk because the message format is unique to this flow.
/// </summary>
public interface IGithubInstallStateService
{
    /// <summary>Generate a signed state token bound to <paramref name="workspaceId"/>.</summary>
    string Issue(Guid workspaceId, TimeSpan ttl);

    /// <summary>
    /// Verify <paramref name="token"/> structurally + cryptographically. Returns the
    /// workspace id when valid; <c>null</c> when the token is malformed, tampered with,
    /// or expired.
    /// </summary>
    Guid? Verify(string? token);

    /// <summary>
    /// Mint a re-authorize state token binding the workspace + the specific
    /// <see cref="Source.Features.GitHub.Models.GithubInstallation"/> id that needs a UAT.
    /// Used by the slim OAuth-only re-authorize flow (no fresh App install) — distinct from
    /// the install-time state to keep the two flows from being interchangeable.
    /// </summary>
    string IssueReauth(Guid workspaceId, Guid githubInstallationId, TimeSpan ttl);

    /// <summary>Verify a re-authorize state token, returning the bound IDs when valid.</summary>
    ReauthStatePayload? VerifyReauth(string? token);
}

/// <summary>The verified payload of a re-authorize state token.</summary>
public sealed record ReauthStatePayload(Guid WorkspaceId, Guid GithubInstallationId);
