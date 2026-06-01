using Source.Features.SystemSettings.Services;

namespace Source.Features.FlyManagement.Configuration;

/// <summary>
/// Indirect-read façade for <see cref="FlyOptions"/>. Mirrors
/// <see cref="Source.Features.GitHub.Configuration.IGithubOptionsAccessor"/> exactly so
/// every Fly-feature service has the same construction shape.
///
/// <para>Why an adapter instead of <c>ISystemSettingsService.GetSection&lt;FlyOptions&gt;("Fly")</c>
/// inline at each call site:
/// <list type="bullet">
///   <item>Keeps every consumer's constructor signature uniform.</item>
///   <item>Lets tests swap in a hand-built <see cref="FlyOptions"/> with a one-line stub.</item>
///   <item>Single place to change if the prefix or binding strategy ever moves.</item>
/// </list>
/// </para>
/// </summary>
public interface IFlyOptionsAccessor
{
    /// <summary>
    /// Snapshot of the Fly configuration, materialised on each access by binding the
    /// cached <c>Fly:*</c> keys onto a fresh <see cref="FlyOptions"/> instance. Cheap —
    /// hits the in-memory <c>SystemSettingsCache</c>.
    /// </summary>
    FlyOptions Current { get; }
}

/// <summary>Default implementation backed by <see cref="ISystemSettingsService"/>.</summary>
public class FlyOptionsAccessor : IFlyOptionsAccessor
{
    private readonly ISystemSettingsService _settings;

    public FlyOptionsAccessor(ISystemSettingsService settings)
    {
        _settings = settings;
    }

    public FlyOptions Current => _settings.GetSection<FlyOptions>(FlyOptions.SectionName);
}
