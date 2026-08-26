namespace Source.Features.RuntimeTemplates.Models;

/// <summary>
/// Lifecycle of a registered template box. Persisted as a string so adding
/// states later never breaks existing rows. The provisioner only ever forks
/// the newest <see cref="Active"/> row.
/// </summary>
public enum RuntimeTemplateStatus
{
    /// <summary>Fork source candidate. The newest Active row is the default fork target.</summary>
    Active,

    /// <summary>Still forkable in principle, but no longer the preferred default.</summary>
    Deprecated,

    /// <summary>Must not be used to fork new runtimes (broken build, security issue, ...).</summary>
    Yanked,
}
