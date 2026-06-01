namespace Source.Features.RuntimeTokens.Services;

/// <summary>
/// Hot-path lookup for the JWT validation pipeline: "is this jti revoked?".
/// In-memory only — process-local. The startup warm-up
/// (<see cref="WarmFromDatabaseAsync"/>) reloads the persistent state from
/// <c>RuntimeTokenIssue</c> so a restart never drops a revocation.
///
/// <para>Backed by <see cref="Microsoft.Extensions.Caching.Memory.IMemoryCache"/>.
/// Each entry is set with <c>AbsoluteExpiration = token.ExpiresAt</c> so it
/// auto-evicts at the moment the token would have expired anyway — no point
/// holding revoked-jti rows past their expiry; the JWT lifetime check would
/// reject them regardless.</para>
/// </summary>
public interface IRevocationCache
{
    bool IsRevoked(Guid jti);
    void Revoke(Guid jti, DateTime expiresAt);
    Task WarmFromDatabaseAsync(CancellationToken ct = default);
}
