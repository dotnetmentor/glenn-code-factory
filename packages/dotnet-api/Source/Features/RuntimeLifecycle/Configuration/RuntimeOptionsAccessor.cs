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

    public RuntimeOptionsAccessor(ISystemSettingsService settings)
    {
        _settings = settings;
    }

    public RuntimeOptions Current => _settings.GetSection<RuntimeOptions>(RuntimeOptions.SectionName);
}
