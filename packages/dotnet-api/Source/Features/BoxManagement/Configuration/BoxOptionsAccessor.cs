using Source.Features.SystemSettings.Services;

namespace Source.Features.BoxManagement.Configuration;

/// <summary>
/// Indirect-read façade for <see cref="BoxOptions"/>. Mirrors
/// <see cref="Source.Features.GitHub.Configuration.IGithubOptionsAccessor"/> so every
/// Box-feature service has the same construction shape.
///
/// <para>Why an adapter instead of <c>ISystemSettingsService.GetSection&lt;BoxOptions&gt;("Box")</c>
/// inline at each call site:
/// <list type="bullet">
///   <item>Keeps every consumer's constructor signature uniform.</item>
///   <item>Lets tests swap in a hand-built <see cref="BoxOptions"/> with a one-line stub.</item>
///   <item>Single place to change if the prefix or binding strategy ever moves.</item>
/// </list>
/// </para>
/// </summary>
public interface IBoxOptionsAccessor
{
    /// <summary>
    /// Snapshot of the Box configuration, materialised on each access by binding the
    /// cached <c>Box:*</c> keys onto a fresh <see cref="BoxOptions"/> instance. Cheap —
    /// hits the in-memory <c>SystemSettingsCache</c>.
    /// </summary>
    BoxOptions Current { get; }
}

/// <summary>Default implementation backed by <see cref="ISystemSettingsService"/>.</summary>
public class BoxOptionsAccessor : IBoxOptionsAccessor
{
    private readonly ISystemSettingsService _settings;

    public BoxOptionsAccessor(ISystemSettingsService settings)
    {
        _settings = settings;
    }

    public BoxOptions Current => _settings.GetSection<BoxOptions>(BoxOptions.SectionName);
}
