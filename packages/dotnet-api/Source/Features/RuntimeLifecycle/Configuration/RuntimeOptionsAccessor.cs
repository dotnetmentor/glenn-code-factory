using Source.Features.SystemSettings.Services;

namespace Source.Features.RuntimeLifecycle.Configuration;

/// <summary>
/// Indirect-read façade for <see cref="RuntimeOptions"/>. Mirrors
/// <see cref="Source.Features.FlyManagement.Configuration.IFlyOptionsAccessor"/> so every
/// runtime-feature service has the same construction shape, and so the <c>PublicApiUrl</c>
/// can be live-edited from Super Admin → System Settings without a process restart.
///
/// <para>Reads pass through the in-memory <see cref="SystemSettingsCache"/>; the very
/// first read of the <c>Runtime</c> category triggers a single DB roundtrip that
/// populates the cache for the whole category at once. After that, reads are O(1).</para>
///
/// <para><c>Runtime:PublicApiUrl</c> from configuration (e.g. <c>Runtime__PublicApiUrl</c>
/// in <c>.env</c>) overrides the SystemSettings value when non-empty. Local dev uses this
/// with an ephemeral Cloudflare quick tunnel so Fly runtimes can dial back without a
/// named tunnel hostname.</para>
/// </summary>
public interface IRuntimeOptionsAccessor
{
    /// <summary>
    /// Snapshot of the runtime configuration, materialised on each access by binding
    /// the cached <c>Runtime:*</c> keys onto a fresh <see cref="RuntimeOptions"/>
    /// instance.
    /// </summary>
    RuntimeOptions Current { get; }
}

/// <summary>Default implementation backed by <see cref="ISystemSettingsService"/>.</summary>
public class RuntimeOptionsAccessor : IRuntimeOptionsAccessor
{
    private readonly ISystemSettingsService _settings;
    private readonly IConfiguration _configuration;

    public RuntimeOptionsAccessor(
        ISystemSettingsService settings,
        IConfiguration configuration)
    {
        _settings = settings;
        _configuration = configuration;
    }

    public RuntimeOptions Current
    {
        get
        {
            var options = _settings.GetSection<RuntimeOptions>(RuntimeOptions.SectionName);
            ApplyPublicApiUrlOverride(options);
            return options;
        }
    }

    private void ApplyPublicApiUrlOverride(RuntimeOptions options)
    {
        var envOverride = _configuration[$"{RuntimeOptions.SectionName}:PublicApiUrl"];
        if (string.IsNullOrWhiteSpace(envOverride))
        {
            return;
        }

        options.PublicApiUrl = envOverride.Trim().TrimEnd('/');
    }
}
