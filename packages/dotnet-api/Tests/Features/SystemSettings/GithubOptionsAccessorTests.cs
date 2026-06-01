using Api.Tests.Infrastructure;
using Microsoft.Extensions.Options;
using Source.Features.GitHub.Configuration;
using Source.Features.SystemSettings.Services;

namespace Api.Tests.Features.SystemSettings;

/// <summary>
/// End-to-end test for the SS.2 swap: <see cref="GithubOptionsAccessor"/> must surface whatever
/// <see cref="ISystemSettingsService"/> currently has for the <c>GitHub:*</c> keys, including
/// after a <see cref="ISystemSettingsService.SetAsync"/> mutation (the cache invalidation
/// pathway should propagate so the next <see cref="IGithubOptionsAccessor.Current"/> read sees
/// the new value).
/// </summary>
public class GithubOptionsAccessorTests
{
    private static (GithubOptionsAccessor Accessor, SystemSettingsService Settings) Build()
    {
        var db = TestDbContextFactory.Create();
        var cache = new SystemSettingsCache();
        var keyB64 = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());
        var cipher = new SystemSettingsCipher(
            Options.Create(new SystemSettingsCipherOptions { EncryptionKey = keyB64 }));
        var service = new SystemSettingsService(db, cache, cipher);
        var accessor = new GithubOptionsAccessor(service);
        return (accessor, service);
    }

    [Fact]
    public void Current_returns_default_GithubOptions_when_no_settings_are_persisted()
    {
        var (accessor, _) = Build();

        var opts = accessor.Current;

        opts.Should().NotBeNull();
        opts.AppId.Should().BeEmpty();
        opts.ClientId.Should().BeEmpty();
        opts.ClientSecret.Should().BeEmpty();
        opts.WebhookSecret.Should().BeEmpty();
    }

    [Fact]
    public async Task Current_reflects_settings_set_via_the_service_including_secrets()
    {
        var (accessor, settings) = Build();

        await settings.SetAsync("GitHub:AppId", "12345", isSecret: false);
        await settings.SetAsync("GitHub:ClientId", "iv1.abc", isSecret: false);
        await settings.SetAsync("GitHub:ClientSecret", "shh-secret", isSecret: true);
        await settings.SetAsync("GitHub:OAuthRedirectUri", "https://localhost/api/github/login/callback", isSecret: false);

        var opts = accessor.Current;

        opts.AppId.Should().Be("12345");
        opts.ClientId.Should().Be("iv1.abc");
        // Secret values are decrypted transparently when read via the service — the accessor
        // doesn't know or care about the secret flag, it just binds the section.
        opts.ClientSecret.Should().Be("shh-secret");
        opts.OAuthRedirectUri.Should().Be("https://localhost/api/github/login/callback");
    }

    [Fact]
    public async Task Current_picks_up_subsequent_updates_via_cache_invalidation()
    {
        var (accessor, settings) = Build();

        await settings.SetAsync("GitHub:AppId", "first", isSecret: false);
        accessor.Current.AppId.Should().Be("first");

        await settings.SetAsync("GitHub:AppId", "second", isSecret: false);
        accessor.Current.AppId.Should().Be("second",
            "SetAsync invalidates the cached category, so the accessor re-reads from the DB next time");
    }

    [Fact]
    public async Task Current_returns_a_fresh_instance_each_call()
    {
        var (accessor, settings) = Build();
        await settings.SetAsync("GitHub:AppId", "x", isSecret: false);

        var first = accessor.Current;
        var second = accessor.Current;

        first.Should().NotBeSameAs(second,
            "GetSection materialises a new POCO on each call — protects against accidental mutation across consumers");
        first.AppId.Should().Be(second.AppId);
    }
}
