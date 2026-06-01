using Source.Features.RuntimeTokens.Services;

namespace Source.Features.RuntimeTokens.Bootstrap;

/// <summary>
/// One-shot startup hosted service that hydrates the singleton
/// <see cref="IRevocationCache"/> from <c>RuntimeTokenIssue</c> rows where
/// <c>RevokedAt != null AND ExpiresAt &gt; UtcNow</c>.
///
/// <para>Mirrors the lifecycle convention of
/// <see cref="Source.Features.SystemSettings.Bootstrap.SystemSettingsSeeder"/>:
/// <c>StartAsync</c> runs the work, <c>StopAsync</c> is a no-op, and any failure
/// is logged loudly but never blocks API startup. A warm-up that fails just
/// means the cache stays empty; the next revoke command will populate it (and
/// any not-yet-cached jti will pass validation, so this degrades to a missed
/// revoke until the operator rotates keys or restarts — same operational
/// posture as before the cache existed).</para>
/// </summary>
public class RevocationCacheWarmupService : IHostedService
{
    private readonly IRevocationCache _cache;
    private readonly ILogger<RevocationCacheWarmupService> _logger;
    private readonly IHostEnvironment _env;

    public RevocationCacheWarmupService(
        IRevocationCache cache,
        ILogger<RevocationCacheWarmupService> logger,
        IHostEnvironment env)
    {
        _cache = cache;
        _logger = logger;
        _env = env;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Same skip as SystemSettingsSeeder: integration tests boot a host
        // against the EF InMemory provider and don't want the warm-up firing.
        if (_env.IsEnvironment("Testing"))
        {
            return;
        }

        try
        {
            await _cache.WarmFromDatabaseAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RevocationCache warm-up failed; continuing startup with empty cache.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
