using Source.Features.Workspaces.Models;
using Source.Shared;
using Source.Shared.Events;

namespace Source.Features.GitHub.Models;

/// <summary>
/// One row per GitHub App installation. An installation is owned by a single workspace.
/// Created when an Admin redirects the user through GitHub's install flow and we receive
/// the <c>installation_id</c> on the callback.
/// </summary>
public class GithubInstallation : Entity, IAuditable
{
    public Guid Id { get; set; }

    /// <summary>FK to the workspace that owns this installation.</summary>
    public Guid WorkspaceId { get; set; }
    public Workspace? Workspace { get; set; }

    /// <summary>The GitHub-side numeric installation id. Globally unique.</summary>
    public long InstallationId { get; set; }

    /// <summary>The login of the account the App was installed on (user or org).</summary>
    public string AccountLogin { get; set; } = string.Empty;

    /// <summary>"User" or "Organization". Stored as string for forward-compat.</summary>
    public string AccountType { get; set; } = string.Empty;

    /// <summary>Optional avatar URL surfaced from the install payload.</summary>
    public string? AccountAvatarUrl { get; set; }

    /// <summary>Set true when GitHub fires an <c>installation.suspend</c> webhook.</summary>
    public bool Suspended { get; set; }

    // -------- User Access Token (UAT) — populated only for User-account installs --------
    //
    // GitHub App installation tokens (`ghs_…`) cannot create repos under a User namespace
    // (only Orgs). To unblock solo devs we capture a User Access Token (`ghu_…`) via
    // GitHub's "Request user authorization (OAuth) during installation" feature at install
    // time, store it here, and use it on the (narrow) endpoints that require it
    // (create-repo, add-repo-to-installation). Everything else stays on the IAT.
    //
    // These fields are null for Org installs — they don't need a UAT.
    //
    // TODO: encrypt the tokens at rest. The existing GithubUserIdentity / GithubOptions
    // (ClientSecret in DB-backed SystemSettings) pattern keeps secrets in plain text today,
    // so for consistency these are plain text too. Encrypt-at-rest is tracked separately.

    /// <summary>The <c>ghu_…</c> user access token. ~8h lifetime.</summary>
    public string? UserAccessToken { get; set; }

    /// <summary>UTC expiry of <see cref="UserAccessToken"/>. Refresh ~2min before this.</summary>
    public DateTime? UserAccessTokenExpiresAt { get; set; }

    /// <summary>The <c>ghr_…</c> refresh token. ~6mo lifetime.</summary>
    public string? UserRefreshToken { get; set; }

    /// <summary>UTC expiry of <see cref="UserRefreshToken"/>. After this, the user must reauthorize.</summary>
    public DateTime? UserRefreshTokenExpiresAt { get; set; }

    /// <summary>GitHub login of the user who authorized. Sanity-check vs <see cref="AccountLogin"/> for User installs.</summary>
    public string? UserLogin { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public ICollection<GithubRepository> Repositories { get; set; } = new List<GithubRepository>();
}
