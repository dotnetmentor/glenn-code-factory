using Api.Tests.Infrastructure;
using Microsoft.Extensions.Options;
using Source.Features.SystemSettings.Services;

namespace Api.Tests.Features.SystemSettings;

/// <summary>
/// Tests for <see cref="SystemSettingsService"/>: lazy load, cache hit on second read,
/// SetAsync round-trip, GetSection POCO binding, transparent secret decryption.
/// </summary>
public class SystemSettingsServiceTests
{
    private static (SystemSettingsService Service, SystemSettingsCache Cache, Source.Infrastructure.ApplicationDbContext Db, ISystemSettingsCipher Cipher) Build()
    {
        var db = TestDbContextFactory.Create();
        var cache = new SystemSettingsCache();
        var keyB64 = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());
        var cipher = new SystemSettingsCipher(
            Options.Create(new SystemSettingsCipherOptions { EncryptionKey = keyB64 }));
        var service = new SystemSettingsService(db, cache, cipher);
        return (service, cache, db, cipher);
    }

    [Fact]
    public async Task SetAsync_then_Get_returns_the_new_value()
    {
        var (service, _, _, _) = Build();

        await service.SetAsync("GitHub:AppId", "12345", isSecret: false);
        var got = service.Get("GitHub:AppId");

        got.Should().Be("12345");
    }

    [Fact]
    public async Task Get_lazy_loads_on_first_read_then_serves_from_cache()
    {
        var (service, cache, _, _) = Build();

        // Persist directly via the service so the row exists.
        await service.SetAsync("GitHub:ClientId", "client-abc", isSecret: false);

        // SetAsync invalidates — confirm next read repopulates the cache.
        cache.TryGetCategory("GitHub", out _).Should().BeFalse("just invalidated");

        var first = service.Get("GitHub:ClientId");
        cache.TryGetCategory("GitHub", out var cached).Should().BeTrue("first read should populate cache");
        cached!["GitHub:ClientId"].Should().Be("client-abc");

        // Second read: same value, still served from same cache entry.
        var second = service.Get("GitHub:ClientId");
        second.Should().Be(first);
    }

    [Fact]
    public async Task SetAsync_invalidates_the_cached_category_so_subsequent_reads_see_the_update()
    {
        var (service, _, _, _) = Build();

        await service.SetAsync("GitHub:AppId", "first", isSecret: false);
        service.Get("GitHub:AppId").Should().Be("first");

        await service.SetAsync("GitHub:AppId", "second", isSecret: false);
        service.Get("GitHub:AppId").Should().Be("second");
    }

    [Fact]
    public async Task Secret_values_are_decrypted_transparently_on_read()
    {
        var (service, _, db, _) = Build();

        await service.SetAsync("GitHub:WebhookSecret", "shh-this-is-secret", isSecret: true);

        // Direct DB inspection — value at rest must NOT be plaintext.
        var stored = db.SystemSettings.Single(s => s.Key == "GitHub:WebhookSecret");
        stored.Value.Should().NotBe("shh-this-is-secret");
        stored.IsSecret.Should().BeTrue();

        // Service read — comes back as plaintext.
        service.Get("GitHub:WebhookSecret").Should().Be("shh-this-is-secret");
    }

    [Fact]
    public async Task GetSection_populates_a_POCO_from_cached_keys()
    {
        var (service, _, _, _) = Build();

        await service.SetAsync("GitHub:AppId", "999", isSecret: false);
        await service.SetAsync("GitHub:ClientId", "abc", isSecret: false);
        await service.SetAsync("GitHub:ClientSecret", "shh", isSecret: true);

        var bound = service.GetSection<Source.Features.GitHub.Configuration.GithubOptions>("GitHub");

        bound.AppId.Should().Be("999");
        bound.ClientId.Should().Be("abc");
        bound.ClientSecret.Should().Be("shh");
        // Properties without a stored value retain their default (empty string).
        bound.AppSlug.Should().Be(string.Empty);
    }

    [Fact]
    public void Get_returns_null_when_key_not_present()
    {
        var (service, _, _, _) = Build();
        service.Get("GitHub:Nonexistent").Should().BeNull();
    }

    [Fact]
    public async Task InvalidateCategory_drops_cached_entries()
    {
        var (service, cache, _, _) = Build();

        await service.SetAsync("GitHub:AppId", "1", isSecret: false);
        service.Get("GitHub:AppId"); // populate
        cache.TryGetCategory("GitHub", out _).Should().BeTrue();

        service.InvalidateCategory("GitHub");
        cache.TryGetCategory("GitHub", out _).Should().BeFalse();
    }
}
