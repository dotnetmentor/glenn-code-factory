using Microsoft.Extensions.Options;
using Source.Features.SystemSettings.Bootstrap;
using Source.Features.SystemSettings.Services;

namespace Source.Features.SystemSettings.Extensions;

/// <summary>
/// Wires up the SystemSettings feature: cipher options, the singleton cache + cipher,
/// the scoped service, and the one-shot seeder.
/// </summary>
public static class SystemSettingsExtensions
{
    private const string ConfigSection = "SystemSettings";
    private const string KeyName = "EncryptionKey";

    public static IServiceCollection AddSystemSettingsFeature(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        // Resolve the encryption key BEFORE we hand the value off to the options
        // binder. If the key is missing we throw here so the host fails fast at
        // startup — a misconfigured cipher key silently destroys every existing
        // encrypted row (master DEK, project DEKs, tunnel tokens, OAuth secrets,
        // GitHub App PEM, etc.) the moment a downstream "auto-seed if missing"
        // path fires. Refusing to boot is by design — see RequireEncryptionKey.
        var resolvedKey = RequireEncryptionKey(configuration, environment);

        services.Configure<SystemSettingsCipherOptions>(opts =>
        {
            opts.EncryptionKey = resolvedKey;
        });

        services.AddSingleton<ISystemSettingsCipher, SystemSettingsCipher>();
        services.AddSingleton<SystemSettingsCache>();
        services.AddScoped<ISystemSettingsService, SystemSettingsService>();
        services.AddHostedService<SystemSettingsSeeder>();

        return services;
    }

    /// <summary>
    /// Read <c>SystemSettings:EncryptionKey</c> from configuration. If absent (or
    /// blank) we throw, in every environment. There is intentionally no dev-time
    /// auto-generation: silently minting a fresh key on first boot orphans every
    /// previously-encrypted row, which has happened in practice and lost data.
    /// The operator is responsible for generating the key once and persisting it
    /// to a durable secret store (1Password / Fly secret / cloud KMS / etc.).
    /// </summary>
    private static string RequireEncryptionKey(IConfiguration configuration, IHostEnvironment environment)
    {
        var existing = configuration[$"{ConfigSection}:{KeyName}"];
        if (!string.IsNullOrWhiteSpace(existing))
        {
            return existing;
        }

        if (environment.IsEnvironment("Testing"))
        {
            return "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";
        }

        // Single, actionable error. The cipher constructor also validates length
        // and base64 shape — we just need to refuse to boot when the key is unset
        // so no auto-seed path downstream interprets the absence as "fresh DB".
        var hint = environment.IsDevelopment()
            ? $"appsettings.Development.json (under \"{ConfigSection}\": {{ \"{KeyName}\": \"...\" }})"
            : $"the {ConfigSection}__{KeyName} environment variable / secret store";

        throw new InvalidOperationException(
            $"""
            {ConfigSection}:{KeyName} is not configured.

            This key encrypts every secret row in the database (master DEK, project
            DEKs, tunnel tokens, OAuth secrets, GitHub App PEM, etc.). Booting with
            a missing or rotated key silently orphans existing data — we refuse to
            start so the operator can fix the config before any auto-seed runs.

            To populate the key for the first time:
              1. Generate a 32-byte base64 value:
                   openssl rand -base64 32
              2. Set it in {hint}.
              3. Back the value up in a durable secret store. Losing it means
                 re-entering every encrypted secret manually.

            If you are re-deploying and previously had a key, restore the OLD value
            from your secret store rather than generating a new one.
            """);
    }
}
