using Tapper;

namespace Source.Features.RuntimeBootstrap.Contracts;

/// <summary>
/// Short-lived (≈1 h) GitHub-App installation token scoped to a single repository,
/// minted on demand for a connected daemon. The daemon uses it as the basic-auth
/// password for an HTTPS clone:
/// <c>git clone https://x-access-token:{Token}@github.com/owner/repo.git</c>.
///
/// <para>Returned by the daemon-invoked <c>GetRepoAccessToken</c> hub method
/// (see <see cref="SignalR.Hubs.IRuntimeHub"/>). Replaces the legacy SSH +
/// deploy-key flow — the deploy key on <see cref="RepoConfig"/> is now optional
/// and the daemon prefers fetching a fresh token per clone.</para>
///
/// <para><see cref="ExpiresAt"/> is UTC and sourced from GitHub's
/// <c>expires_at</c> field on <c>POST /app/installations/{id}/access_tokens</c>;
/// the daemon should treat it as the firm upper bound for caching.</para>
/// </summary>
[TranspilationSource]
public record RepoAccessToken(string Token, DateTime ExpiresAt);
