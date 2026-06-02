using Microsoft.Extensions.Configuration;
using Source.Features.RuntimeLifecycle.Configuration;
using Source.Features.SystemSettings.Services;

namespace Tests.Features.RuntimeLifecycle;

public class RuntimeOptionsAccessorTests
{
    [Fact]
    public void Current_uses_system_settings_when_env_override_is_missing()
    {
        var settings = new FakeSystemSettingsService(new Dictionary<string, string?>
        {
            ["Runtime:PublicApiUrl"] = "https://from-db.example.com",
        });
        var config = new ConfigurationBuilder().Build();
        var accessor = new RuntimeOptionsAccessor(settings, config);

        Assert.Equal("https://from-db.example.com", accessor.Current.PublicApiUrl);
    }

    [Fact]
    public void Current_prefers_env_override_over_system_settings()
    {
        var settings = new FakeSystemSettingsService(new Dictionary<string, string?>
        {
            ["Runtime:PublicApiUrl"] = "https://from-db.example.com",
        });
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Runtime:PublicApiUrl"] = "https://tunnel.trycloudflare.com",
            })
            .Build();
        var accessor = new RuntimeOptionsAccessor(settings, config);

        Assert.Equal("https://tunnel.trycloudflare.com", accessor.Current.PublicApiUrl);
    }

    [Fact]
    public void Current_trims_trailing_slash_from_env_override()
    {
        var settings = new FakeSystemSettingsService(new Dictionary<string, string?>());
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Runtime:PublicApiUrl"] = "https://tunnel.trycloudflare.com/",
            })
            .Build();
        var accessor = new RuntimeOptionsAccessor(settings, config);

        Assert.Equal("https://tunnel.trycloudflare.com", accessor.Current.PublicApiUrl);
    }

    private sealed class FakeSystemSettingsService : ISystemSettingsService
    {
        private readonly IReadOnlyDictionary<string, string?> _values;

        public FakeSystemSettingsService(IReadOnlyDictionary<string, string?> values)
        {
            _values = values;
        }

        public string? Get(string key) =>
            _values.TryGetValue(key, out var value) ? value : null;

        public T GetSection<T>(string prefix) where T : new()
        {
            var instance = new T();
            foreach (var prop in typeof(T).GetProperties())
            {
                if (!prop.CanWrite || prop.PropertyType != typeof(string))
                {
                    continue;
                }

                var key = $"{prefix}:{prop.Name}";
                if (_values.TryGetValue(key, out var raw) && raw is not null)
                {
                    prop.SetValue(instance, raw);
                }
            }

            return instance;
        }

        public Task SetAsync(
            string key,
            string? value,
            bool isSecret,
            string? updatedBy = null,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public void InvalidateCategory(string category) { }

        public Task PreloadAsync(string category, CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}
