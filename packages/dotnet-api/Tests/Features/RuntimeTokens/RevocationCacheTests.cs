using Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Source.Features.RuntimeTokens.Models;
using Source.Features.RuntimeTokens.Services;
using Source.Features.SystemSettings.Services;
using Source.Infrastructure;

namespace Api.Tests.Features.RuntimeTokens;

/// <summary>
/// Unit + integration coverage for <see cref="RevocationCache"/>:
/// the pure cache mechanics (Revoke / IsRevoked / auto-eviction) are exercised
/// against a fresh <see cref="MemoryCache"/>; the warm-up path uses a real
/// <see cref="ApplicationDbContext"/>; the wired-into-validation case uses the
/// real <see cref="RuntimeTokenService"/> with a shared cache instance.
/// </summary>
public class RevocationCacheTests : IDisposable
{
    private readonly string _dbName;
    private readonly ServiceProvider _sp;

    public RevocationCacheTests()
    {
        _dbName = Guid.NewGuid().ToString();

        // Same DI shape as RuntimeTokenServiceTests so we can mint real tokens
        // for the integration cases (tests 6 and 7).
        var cipherKeyB64 = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Options.Create(new SystemSettingsCipherOptions { EncryptionKey = cipherKeyB64 }));
        services.AddSingleton<ISystemSettingsCipher, SystemSettingsCipher>();
        services.AddSingleton<SystemSettingsCache>();
        services.AddScoped(_ => TestDbContextFactory.Create(_dbName));
        services.AddScoped<ISystemSettingsService, SystemSettingsService>();
        _sp = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        _sp.Dispose();
        GC.SuppressFinalize(this);
    }

    // -- helpers -----------------------------------------------------------------

    private static RevocationCache NewCache(IServiceScopeFactory? scopeFactory = null)
    {
        var memory = new MemoryCache(new MemoryCacheOptions());
        return new RevocationCache(
            memory,
            scopeFactory ?? new NullScopeFactory(),
            NullLogger<RevocationCache>.Instance);
    }

    /// <summary>
    /// Returns a scope factory backed by the same in-memory DB name as this test
    /// fixture — used by the warm-from-DB scenario so the cache reads what the
    /// test seeded.
    /// </summary>
    private IServiceScopeFactory FixtureScopeFactory() =>
        _sp.GetRequiredService<IServiceScopeFactory>();

    // -- pure cache mechanics ----------------------------------------------------

    [Fact]
    public void Revoke_then_IsRevoked_returns_true()
    {
        var cache = NewCache();
        var jti = Guid.NewGuid();

        cache.Revoke(jti, DateTime.UtcNow.AddHours(1));

        cache.IsRevoked(jti).Should().BeTrue();
    }

    [Fact]
    public void IsRevoked_returns_false_for_unknown_jti()
    {
        var cache = NewCache();

        cache.IsRevoked(Guid.NewGuid()).Should().BeFalse();
    }

    [Fact]
    public void Revoke_with_already_expired_expiresAt_is_a_noop()
    {
        var cache = NewCache();
        var jti = Guid.NewGuid();

        cache.Revoke(jti, DateTime.UtcNow.AddHours(-1));

        cache.IsRevoked(jti).Should().BeFalse(
            "an already-expired token will be rejected by JWT lifetime checks anyway; nothing to cache");
    }

    [Fact]
    public void Entry_auto_evicts_past_expiresAt()
    {
        var cache = NewCache();
        var jti = Guid.NewGuid();

        cache.Revoke(jti, DateTime.UtcNow.AddMilliseconds(50));
        cache.IsRevoked(jti).Should().BeTrue("entry must be present immediately after Revoke");

        Thread.Sleep(150);

        cache.IsRevoked(jti).Should().BeFalse(
            "IMemoryCache evicts on read once the AbsoluteExpiration moment has passed");
    }

    // -- warm-from-DB ------------------------------------------------------------

    [Fact]
    public async Task WarmFromDatabaseAsync_loads_only_revoked_and_non_expired_rows()
    {
        // Seed: A=Revoked+future, B=Revoked+past, C=NotRevoked+future.
        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();
        var idC = Guid.NewGuid();
        var now = DateTime.UtcNow;

        await using (var seedDb = TestDbContextFactory.Create(_dbName))
        {
            seedDb.RuntimeTokenIssues.AddRange(
                new RuntimeTokenIssue
                {
                    Id = idA,
                    RuntimeId = Guid.NewGuid(),
                    ProjectId = Guid.NewGuid(),
                    Scope = "runtime",
                    TokenHash = new string('a', 64),
                    IssuedAt = now.AddHours(-2),
                    ExpiresAt = now.AddHours(1),
                    RevokedAt = now.AddMinutes(-5),
                },
                new RuntimeTokenIssue
                {
                    Id = idB,
                    RuntimeId = Guid.NewGuid(),
                    ProjectId = Guid.NewGuid(),
                    Scope = "runtime",
                    TokenHash = new string('b', 64),
                    IssuedAt = now.AddHours(-3),
                    ExpiresAt = now.AddHours(-1),
                    RevokedAt = now.AddMinutes(-30),
                },
                new RuntimeTokenIssue
                {
                    Id = idC,
                    RuntimeId = Guid.NewGuid(),
                    ProjectId = Guid.NewGuid(),
                    Scope = "runtime",
                    TokenHash = new string('c', 64),
                    IssuedAt = now.AddHours(-1),
                    ExpiresAt = now.AddHours(1),
                    RevokedAt = null,
                });
            await seedDb.SaveChangesAsync();
        }

        var cache = NewCache(FixtureScopeFactory());

        await cache.WarmFromDatabaseAsync();

        cache.IsRevoked(idA).Should().BeTrue("A is revoked and not yet expired");
        cache.IsRevoked(idB).Should().BeFalse("B is revoked but already expired — pointless to cache");
        cache.IsRevoked(idC).Should().BeFalse("C is not revoked");
    }

    // -- wired into RuntimeTokenService.ValidateAsync ----------------------------

    [Fact]
    public async Task ValidateAsync_returns_token_revoked_when_jti_is_in_cache()
    {
        var (cache, service, _) = BuildService();

        var mintResult = await service.MintAsync(new MintTokenRequest(
            RuntimeId: Guid.NewGuid(),
            ProjectId: Guid.NewGuid(),
            BranchId: null,
            TenantId: Guid.NewGuid(),
            Lifetime: TimeSpan.FromHours(1)));
        mintResult.IsSuccess.Should().BeTrue($"setup mint must succeed (error={mintResult.Error})");
        var minted = mintResult.Value;

        cache.Revoke(minted.Jti, minted.ExpiresAt);

        var result = await service.ValidateAsync(minted.Token);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("token_revoked");
    }

    [Fact]
    public async Task ValidateAsync_succeeds_for_non_revoked_token()
    {
        var (_, service, _) = BuildService();

        var mintResult = await service.MintAsync(new MintTokenRequest(
            RuntimeId: Guid.NewGuid(),
            ProjectId: Guid.NewGuid(),
            BranchId: null,
            TenantId: Guid.NewGuid(),
            Lifetime: TimeSpan.FromHours(1)));
        mintResult.IsSuccess.Should().BeTrue($"setup mint must succeed (error={mintResult.Error})");
        var minted = mintResult.Value;

        var result = await service.ValidateAsync(minted.Token);

        result.IsSuccess.Should().BeTrue($"non-revoked token must validate (error={result.Error})");
        result.Value.Jti.Should().Be(minted.Jti);
    }

    private (RevocationCache cache, RuntimeTokenService service, ApplicationDbContext db) BuildService()
    {
        var signingKeyService = new RuntimeTokenSigningKeyService(
            FixtureScopeFactory(),
            NullLogger<RuntimeTokenSigningKeyService>.Instance);
        var db = TestDbContextFactory.Create(_dbName);
        var cache = NewCache(FixtureScopeFactory());
        var service = new RuntimeTokenService(
            signingKeyService,
            db,
            cache,
            NullLogger<RuntimeTokenService>.Instance);
        return (cache, service, db);
    }

    /// <summary>
    /// Stand-in for IServiceScopeFactory in pure-cache scenarios that never call
    /// <see cref="RevocationCache.WarmFromDatabaseAsync"/>. If any test inadvertently
    /// hits the warm path, we'd rather see a NotImplementedException than a
    /// NullReferenceException — clearer signal that the test was misconfigured.
    /// </summary>
    private sealed class NullScopeFactory : IServiceScopeFactory
    {
        public IServiceScope CreateScope() =>
            throw new NotImplementedException(
                "Test used NullScopeFactory but reached WarmFromDatabaseAsync; pass a real IServiceScopeFactory.");
    }
}
