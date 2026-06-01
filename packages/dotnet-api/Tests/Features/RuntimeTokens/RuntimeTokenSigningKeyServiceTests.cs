using System.Security.Cryptography;
using Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Source.Features.RuntimeTokens.EventHandlers;
using Source.Features.RuntimeTokens.Services;
using Source.Features.SystemSettings.Events;
using Source.Features.SystemSettings.Services;
using Source.Infrastructure;

namespace Api.Tests.Features.RuntimeTokens;

/// <summary>
/// Unit tests for <see cref="RuntimeTokenSigningKeyService"/>: first-boot auto-seed,
/// caching, two-key validation set during rotation, concurrent first-read safety,
/// and event-driven cache invalidation when an operator updates the keys via the
/// SystemSettings admin UI.
/// </summary>
public class RuntimeTokenSigningKeyServiceTests
{
    /// <summary>
    /// Build a fully-wired environment with a single shared in-memory DB. The service
    /// receives an <see cref="IServiceScopeFactory"/> that hands out scopes containing
    /// a fresh <see cref="ISystemSettingsService"/> bound to that DB — same shape the
    /// production singleton sees.
    /// </summary>
    private static (RuntimeTokenSigningKeyService Service, IServiceProvider Sp, string DbName) Build()
    {
        var dbName = Guid.NewGuid().ToString();
        var keyB64 = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Options.Create(new SystemSettingsCipherOptions { EncryptionKey = keyB64 }));
        services.AddSingleton<ISystemSettingsCipher, SystemSettingsCipher>();
        services.AddSingleton<SystemSettingsCache>();
        // Each scope gets a fresh DbContext bound to the same in-memory DB by name —
        // matches how Program.cs registers ApplicationDbContext (scoped) + how
        // SystemSettingsService consumes it.
        services.AddScoped(_ => TestDbContextFactory.Create(dbName));
        services.AddScoped<ISystemSettingsService, SystemSettingsService>();

        var sp = services.BuildServiceProvider();
        var service = new RuntimeTokenSigningKeyService(
            sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<RuntimeTokenSigningKeyService>.Instance);

        return (service, sp, dbName);
    }

    private static ApplicationDbContext OpenDb(string dbName) => TestDbContextFactory.Create(dbName);

    [Fact]
    public async Task First_read_auto_seeds_a_random_current_key_and_persists_it()
    {
        var (service, _, dbName) = Build();

        // Pre-condition: no rows yet.
        await using (var pre = OpenDb(dbName))
        {
            (await pre.SystemSettings.AnyAsync()).Should().BeFalse();
        }

        var creds = service.GetCurrentSigning();

        creds.Algorithm.Should().Be(SecurityAlgorithms.HmacSha256);
        creds.Key.Should().BeOfType<SymmetricSecurityKey>();

        await using var post = OpenDb(dbName);
        var row = await post.SystemSettings
            .SingleAsync(s => s.Key == RuntimeTokenSigningKeyService.CurrentKeyName);
        row.IsSecret.Should().BeTrue();
        row.Category.Should().Be(RuntimeTokenSigningKeyService.Category);
        row.UpdatedBy.Should().Be("system:auto-seed");
        row.Value.Should().NotBeNullOrEmpty("the encrypted blob is persisted");
    }

    [Fact]
    public async Task Second_read_does_not_regenerate_or_re_persist_the_key()
    {
        var (service, _, dbName) = Build();

        // First read seeds + persists.
        var first = service.GetCurrentSigning();

        DateTime updatedAtAfterFirst;
        await using (var db1 = OpenDb(dbName))
        {
            var row1 = await db1.SystemSettings
                .SingleAsync(s => s.Key == RuntimeTokenSigningKeyService.CurrentKeyName);
            updatedAtAfterFirst = row1.UpdatedAt;
        }

        // Pause so any erroneous re-write would have a different UpdatedAt.
        await Task.Delay(10);

        // Second read: must reuse the cached key, never touch the DB.
        var second = service.GetCurrentSigning();

        await using var db2 = OpenDb(dbName);
        var row2 = await db2.SystemSettings
            .SingleAsync(s => s.Key == RuntimeTokenSigningKeyService.CurrentKeyName);

        row2.UpdatedAt.Should().Be(updatedAtAfterFirst, "second read must not re-persist");

        // Both signing instances should wrap the same underlying key bytes.
        var firstBytes = ((SymmetricSecurityKey)first.Key).Key;
        var secondBytes = ((SymmetricSecurityKey)second.Key).Key;
        secondBytes.Should().Equal(firstBytes);
    }

    [Fact]
    public async Task GetValidationKeys_returns_one_key_when_previous_is_unset()
    {
        var (service, _, _) = Build();

        // Trigger seed, then read validation set.
        _ = service.GetCurrentSigning();
        var keys = service.GetValidationKeys();

        keys.Should().HaveCount(1);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task GetValidationKeys_returns_two_keys_when_previous_is_set()
    {
        var (service, sp, _) = Build();

        // Pre-seed BOTH keys before the service ever reads.
        var currentB64 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var previousB64 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        using (var scope = sp.CreateScope())
        {
            var settings = scope.ServiceProvider.GetRequiredService<ISystemSettingsService>();
            await settings.SetAsync(RuntimeTokenSigningKeyService.CurrentKeyName, currentB64, isSecret: true);
            await settings.SetAsync(RuntimeTokenSigningKeyService.PreviousKeyName, previousB64, isSecret: true);
        }

        var keys = service.GetValidationKeys();
        keys.Should().HaveCount(2, "current + previous during rotation window");

        var current = (SymmetricSecurityKey)keys[0];
        var previous = (SymmetricSecurityKey)keys[1];
        current.Key.Should().Equal(Convert.FromBase64String(currentB64));
        previous.Key.Should().Equal(Convert.FromBase64String(previousB64));
    }

    [Fact]
    public async Task Concurrent_first_reads_do_not_double_seed_the_key()
    {
        var (service, _, dbName) = Build();

        const int N = 16;
        var tasks = Enumerable.Range(0, N)
            .Select(_ => Task.Run(() => service.GetCurrentSigning()))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        // All callers see the same key bytes…
        var firstBytes = ((SymmetricSecurityKey)results[0].Key).Key;
        foreach (var creds in results)
        {
            ((SymmetricSecurityKey)creds.Key).Key.Should().Equal(firstBytes);
        }

        // …and exactly one DB row exists.
        await using var db = OpenDb(dbName);
        var rows = await db.SystemSettings
            .Where(s => s.Key == RuntimeTokenSigningKeyService.CurrentKeyName)
            .ToListAsync();
        rows.Should().HaveCount(1, "only one auto-seed should ever occur");
    }

    [Fact]
    public async Task SystemSettingChanged_for_RuntimeTokens_invalidates_the_cache()
    {
        var (service, sp, _) = Build();

        // Cache the initial (auto-seeded) key.
        var firstKey = ((SymmetricSecurityKey)service.GetCurrentSigning().Key).Key;

        // Operator rotates the Current key via the admin UI = SetAsync on the service.
        var rotatedB64 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        using (var scope = sp.CreateScope())
        {
            var settings = scope.ServiceProvider.GetRequiredService<ISystemSettingsService>();
            await settings.SetAsync(
                RuntimeTokenSigningKeyService.CurrentKeyName,
                rotatedB64,
                isSecret: true,
                updatedBy: "admin:rotate");
        }

        // The handler is what the DomainEventInterceptor would invoke in production.
        // We dispatch it directly so this test stays focused on the cache contract.
        var handler = new RuntimeTokenSigningKeyCacheInvalidator(
            service,
            NullLogger<RuntimeTokenSigningKeyCacheInvalidator>.Instance);
        await handler.Handle(
            new SystemSettingChanged(RuntimeTokenSigningKeyService.CurrentKeyName, RuntimeTokenSigningKeyService.Category),
            CancellationToken.None);

        // Next read must surface the new key, not the old cached bytes.
        var secondKey = ((SymmetricSecurityKey)service.GetCurrentSigning().Key).Key;

        secondKey.Should().NotEqual(firstKey, "rotation must replace the cached key");
        secondKey.Should().Equal(Convert.FromBase64String(rotatedB64));
    }
}
