using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Source.Infrastructure;

namespace Source.Features.RuntimeTokens.Services;

/// <summary>
/// Singleton in-memory implementation of <see cref="IRevocationCache"/>.
///
/// <para><b>Key shape:</b> <c>"runtime-token-revoked:{jti}"</c> — namespaced
/// to avoid collision with any other cache use of <see cref="IMemoryCache"/>.
/// Value is the literal sentinel <c>true</c>; only <i>presence</i> matters.</para>
///
/// <para><b>Eviction:</b> entries are inserted with
/// <see cref="MemoryCacheEntryOptions.AbsoluteExpiration"/> = the token's
/// <c>ExpiresAt</c>. Once that moment passes, the JWT lifetime validator would
/// reject the token anyway; holding the revocation row longer is wasted memory.
/// No background sweeper needed — <see cref="IMemoryCache"/> evicts lazily on
/// read.</para>
/// </summary>
public class RevocationCache : IRevocationCache
{
    private const string KeyPrefix = "runtime-token-revoked:";

    private readonly IMemoryCache _cache;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RevocationCache> _logger;

    public RevocationCache(
        IMemoryCache cache,
        IServiceScopeFactory scopeFactory,
        ILogger<RevocationCache> logger)
    {
        _cache = cache;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public bool IsRevoked(Guid jti)
    {
        // TryGetValue triggers IMemoryCache's lazy eviction check — entries past
        // their AbsoluteExpiration are evicted in-line and report "not found",
        // which is exactly what we want.
        return _cache.TryGetValue(BuildKey(jti), out _);
    }

    public void Revoke(Guid jti, DateTime expiresAt)
    {
        // No point caching a revocation that's already past its expiry — the JWT
        // lifetime validator will reject any such token before it ever reaches
        // the cache. Skip the insert entirely.
        if (expiresAt <= DateTime.UtcNow)
        {
            return;
        }

        var options = new MemoryCacheEntryOptions
        {
            // DateTimeOffset with explicit UTC offset — DateTime alone can be
            // interpreted as Local on some platforms; we always store/compare UTC.
            AbsoluteExpiration = new DateTimeOffset(
                DateTime.SpecifyKind(expiresAt, DateTimeKind.Utc),
                TimeSpan.Zero),
        };
        _cache.Set(BuildKey(jti), true, options);
    }

    public async Task WarmFromDatabaseAsync(CancellationToken ct = default)
    {
        // Singleton can't hold a scoped DbContext directly; create a scope per warm.
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var now = DateTime.UtcNow;
        var rows = await db.RuntimeTokenIssues
            .Where(r => r.RevokedAt != null && r.ExpiresAt > now)
            .Select(r => new { r.Id, r.ExpiresAt })
            .ToListAsync(ct);

        foreach (var row in rows)
        {
            Revoke(row.Id, row.ExpiresAt);
        }

        _logger.LogInformation("RevocationCache warmed: {Count} entries", rows.Count);
    }

    private static string BuildKey(Guid jti) => $"{KeyPrefix}{jti}";
}
