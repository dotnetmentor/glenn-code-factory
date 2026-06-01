namespace Source.Features.GitHub.Services;

/// <summary>
/// Produces the two GitHub App credentials we need at runtime:
///   1. App JWT — RS256 token signed with our App's private key, valid 10 minutes.
///   2. Installation token — short-lived (1h) bearer token scoped to a single installation.
///
/// We never persist installation tokens; they are cached in-memory below their TTL
/// and re-fetched on demand from <c>POST /app/installations/{id}/access_tokens</c>.
/// </summary>
public interface IGithubAppTokenService
{
    /// <summary>Creates a fresh App-level JWT (10-min lifetime).</summary>
    string CreateAppJwt();

    /// <summary>Returns a (possibly cached) installation access token for the given installation id.</summary>
    Task<string> GetInstallationTokenAsync(long installationId, CancellationToken ct = default);

    /// <summary>
    /// Returns a (possibly cached) installation access token scoped to a single
    /// repository with <c>contents:write</c> + <c>metadata:read</c> permissions.
    /// Used by the runtime-clone flow: the daemon asks for a short-lived token
    /// just before <c>git clone https://x-access-token:{token}@github.com/owner/repo.git</c>
    /// rather than carrying a long-lived deploy key.
    ///
    /// <para>The token + its <c>expires_at</c> are cached under a per-pair key so
    /// repeated daemon requests within the same minute don't hammer
    /// <c>POST /app/installations/{id}/access_tokens</c>.</para>
    /// </summary>
    Task<ScopedInstallationToken> MintScopedTokenAsync(
        long installationId,
        long repositoryId,
        CancellationToken ct = default);

    /// <summary>
    /// Like <see cref="MintScopedTokenAsync(long,long,CancellationToken)"/> but
    /// scopes by <c>"owner/name"</c> instead of the numeric repo id. Lets the
    /// caller avoid a round-trip through the local <c>GithubRepositories</c>
    /// cache (which is webhook-maintained and can be stale or unseeded for a
    /// project that legitimately exists on GitHub). The Project row already
    /// stores owner + name as the source of truth, so the hub can mint a token
    /// straight from those without any cache lookup.
    ///
    /// <para>Implementation note: GitHub's
    /// <c>POST /app/installations/{id}/access_tokens</c> accepts either
    /// <c>repository_ids</c> (numeric) or <c>repositories</c> (string names);
    /// this method uses the names variant. Equivalent token output, no numeric
    /// id dependency.</para>
    /// </summary>
    Task<ScopedInstallationToken> MintScopedTokenByNameAsync(
        long installationId,
        string repoFullName,
        CancellationToken ct = default);
}

/// <summary>
/// A repository-scoped installation access token paired with its expiry timestamp.
/// The expiry is in UTC and originates from GitHub's <c>expires_at</c> field on
/// the access-tokens response (default 1h lifetime).
/// </summary>
public sealed record ScopedInstallationToken(string Token, DateTime ExpiresAt);
