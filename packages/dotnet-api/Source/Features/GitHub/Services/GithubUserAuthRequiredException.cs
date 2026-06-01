namespace Source.Features.GitHub.Services;

/// <summary>
/// Thrown by <see cref="IGithubUserTokenService"/> when a UAT is needed but cannot be obtained
/// (never captured, refresh expired, or refresh API call failed). The create-repo path catches
/// this and surfaces it as a <c>github_user_auth_required</c> error code that the frontend
/// uses to render the "Re-authorize GitHub" banner.
/// </summary>
public sealed class GithubUserAuthRequiredException : Exception
{
    /// <summary>
    /// Sub-cause discriminator. Surfaced into the error message so the frontend can
    /// distinguish "you never authorized" from "your authorization expired".
    /// </summary>
    public enum Reason
    {
        /// <summary>No UAT was ever captured on this installation row.</summary>
        NoUat,
        /// <summary>UAT is expired AND the refresh token is past its expiry — full re-auth needed.</summary>
        RefreshExpired,
        /// <summary>Refresh-token API call failed for a non-expiry reason (network, revoked, etc.).</summary>
        RefreshFailed,
    }

    public Reason ReasonCode { get; }

    public GithubUserAuthRequiredException(Reason reason)
        : base($"GitHub user authorization required (reason: {reason})")
    {
        ReasonCode = reason;
    }

    public GithubUserAuthRequiredException(Reason reason, Exception inner)
        : base($"GitHub user authorization required (reason: {reason})", inner)
    {
        ReasonCode = reason;
    }
}
