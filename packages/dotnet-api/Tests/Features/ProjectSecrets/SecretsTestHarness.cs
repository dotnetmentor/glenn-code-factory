using Api.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Source.Features.ProjectSecrets.Services;
using Source.Features.SystemSettings.Services;
using Source.Infrastructure;

namespace Api.Tests.Features.ProjectSecrets;

/// <summary>
/// Shared test rig for the project-secrets command and query handler tests.
/// Mirrors the harness used by <c>SecretEncryptionServiceTests</c>: a tiny DI
/// graph wiring the in-memory <see cref="ApplicationDbContext"/>, the real
/// <see cref="SystemSettingsService"/> + cipher, and the singleton-style
/// <see cref="SecretEncryptionService"/>.
/// </summary>
internal static class SecretsTestHarness
{
    /// <summary>
    /// Build a fresh harness pinned to the supplied database name so multiple
    /// DbContext instances within one test see the same in-memory store.
    /// </summary>
    public static (SecretEncryptionService Encryption, IServiceProvider Sp) Build(string dbName)
    {
        var keyB64 = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Options.Create(new SystemSettingsCipherOptions { EncryptionKey = keyB64 }));
        services.AddSingleton<ISystemSettingsCipher, SystemSettingsCipher>();
        services.AddSingleton<SystemSettingsCache>();
        services.AddScoped(_ => TestDbContextFactory.Create(dbName));
        services.AddScoped<ISystemSettingsService, SystemSettingsService>();

        var sp = services.BuildServiceProvider();
        var encryption = new SecretEncryptionService(
            sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<SecretEncryptionService>.Instance);

        return (encryption, sp);
    }

    /// <summary>Convenience factory: open a fresh DbContext on the shared db.</summary>
    public static ApplicationDbContext OpenDb(string dbName) => TestDbContextFactory.Create(dbName);
}
