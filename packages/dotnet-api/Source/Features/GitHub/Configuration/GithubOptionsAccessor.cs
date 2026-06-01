using Source.Features.SystemSettings.Services;

namespace Source.Features.GitHub.Configuration;

/// <summary>
/// Indirect-read façade for <see cref="GithubOptions"/>. Replaces the
/// <see cref="Microsoft.Extensions.Options.IOptions{TOptions}"/> consumption
/// pattern at every call site so values come from <see cref="ISystemSettingsService"/>
/// (DB-backed, cached) instead of <c>appsettings.json</c>.
///
/// <para>Why an adapter instead of <c>ISystemSettingsService.GetSection&lt;GithubOptions&gt;("GitHub")</c>
/// inline at each call site:
/// <list type="bullet">
///   <item>Keeps every consumer's constructor signature uniform.</item>
///   <item>Lets tests swap in a hand-built <see cref="GithubOptions"/> with a one-line stub.</item>
///   <item>Single place to change if the prefix or binding strategy ever moves.</item>
/// </list>
/// </para>
/// </summary>
public interface IGithubOptionsAccessor
{
    /// <summary>
    /// Snapshot of the GitHub configuration, materialised on each access by binding the
    /// cached <c>GitHub:*</c> keys onto a fresh <see cref="GithubOptions"/> instance.
    /// Cheap — the underlying read hits the in-memory <see cref="SystemSettingsCache"/>.
    /// </summary>
    GithubOptions Current { get; }
}

/// <summary>Default implementation backed by <see cref="ISystemSettingsService"/>.</summary>
public class GithubOptionsAccessor : IGithubOptionsAccessor
{
    private readonly ISystemSettingsService _settings;

    public GithubOptionsAccessor(ISystemSettingsService settings)
    {
        _settings = settings;
    }

    public GithubOptions Current => _settings.GetSection<GithubOptions>(GithubOptions.SectionName);
}
