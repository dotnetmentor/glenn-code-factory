using Source.Features.GitHub.Models;

namespace Source.Features.GitHub.Services;

/// <summary>
/// Returns a valid User Access Token (UAT) for a GitHub installation, transparently
/// refreshing it when near expiry. Used only by the create-repo path (and the
/// add-repo-to-installation follow-up) — every other GitHub call keeps using the
/// installation token (IAT) via <see cref="IGithubAppTokenService"/>.
/// </summary>
public interface IGithubUserTokenService
{
    /// <summary>
    /// Returns a valid <c>ghu_…</c> token for this installation. Refreshes via the
    /// stored refresh-token if the current UAT is within 2 minutes of expiry.
    /// </summary>
    /// <param name="installation">The installation row whose UAT we need. The row is
    /// updated in-place (and saved) when a refresh occurs.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="GithubUserAuthRequiredException">No UAT stored, or refresh
    /// token expired, or refresh API call failed.</exception>
    Task<string> GetValidUserAccessTokenAsync(GithubInstallation installation, CancellationToken ct);
}
