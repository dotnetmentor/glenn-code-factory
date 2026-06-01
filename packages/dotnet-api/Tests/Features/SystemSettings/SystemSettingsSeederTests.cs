using Api.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Source.Features.SystemSettings;
using Source.Features.SystemSettings.Bootstrap;
using Source.Features.SystemSettings.Services;

namespace Api.Tests.Features.SystemSettings;

/// <summary>
/// Tests for <see cref="SystemSettingsSeeder"/>:
/// first run inserts catalog rows, second run is a no-op (idempotency).
/// </summary>
public class SystemSettingsSeederTests
{
    /// <summary>
    /// Build a <see cref="SystemSettingsSeeder"/> wired to a single shared in-memory DB
    /// and a fixed IConfiguration. We use a tiny inline service-provider so the seeder
    /// can resolve a fresh scope per call (matching prod behavior).
    /// </summary>
    private static (SystemSettingsSeeder Seeder, Source.Infrastructure.ApplicationDbContext Db) Build(
        Dictionary<string, string?>? configValues = null)
    {
        var dbName = Guid.NewGuid().ToString();
        var db = TestDbContextFactory.Create(dbName);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(configValues ?? new Dictionary<string, string?>())
            .Build();

        var keyB64 = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());
        var cipher = new SystemSettingsCipher(
            Options.Create(new SystemSettingsCipherOptions { EncryptionKey = keyB64 }));

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddSingleton<ISystemSettingsCipher>(cipher);
        // Each scope gets a NEW context bound to the same in-memory DB (by name),
        // matching what the production scope-per-resolution behavior would do.
        services.AddScoped(_ => TestDbContextFactory.Create(dbName));

        var sp = services.BuildServiceProvider();

        var seeder = new SystemSettingsSeeder(
            sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<SystemSettingsSeeder>.Instance,
            new TestEnvironment());

        return (seeder, db);
    }

    [Fact]
    public async Task First_run_inserts_one_row_per_catalog_setting()
    {
        var (seeder, db) = Build();

        await seeder.SeedAsync(CancellationToken.None);

        // The in-memory provider shares its store by database name, so the original db
        // reference sees writes made through scope-resolved contexts.
        db.SystemSettings.ToList().Should().HaveCount(SystemSettingsCatalog.AllSettings.Count());
        db.SystemSettings.Select(s => s.Key).Should().Contain("GitHub:AppId");
        db.SystemSettings.Select(s => s.Key).Should().Contain("GitHub:ClientSecret");

        // Catalog secret rows must be flagged as such.
        var clientSecret = db.SystemSettings.Single(s => s.Key == "GitHub:ClientSecret");
        clientSecret.IsSecret.Should().BeTrue();
        clientSecret.Value.Should().BeNull("no value provided in IConfiguration");
    }

    [Fact]
    public async Task Second_run_is_a_no_op()
    {
        var (seeder, db) = Build();

        await seeder.SeedAsync(CancellationToken.None);
        var firstCount = db.SystemSettings.Count();

        await seeder.SeedAsync(CancellationToken.None);
        var secondCount = db.SystemSettings.Count();

        secondCount.Should().Be(firstCount, "seeder must be idempotent");
    }

    [Fact]
    public async Task Initial_values_are_pulled_from_IConfiguration_and_secrets_are_encrypted_at_rest()
    {
        var (seeder, db) = Build(new Dictionary<string, string?>
        {
            ["GitHub:AppId"] = "config-app-id",
            ["GitHub:ClientSecret"] = "config-secret",
        });

        await seeder.SeedAsync(CancellationToken.None);

        var appId = db.SystemSettings.Single(s => s.Key == "GitHub:AppId");
        appId.Value.Should().Be("config-app-id"); // not secret — stored cleartext

        var clientSecret = db.SystemSettings.Single(s => s.Key == "GitHub:ClientSecret");
        clientSecret.IsSecret.Should().BeTrue();
        clientSecret.Value.Should().NotBeNullOrEmpty();
        clientSecret.Value.Should().NotBe("config-secret", "secret values must be encrypted before persisting");
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
