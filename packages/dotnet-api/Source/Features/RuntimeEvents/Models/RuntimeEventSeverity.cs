namespace Source.Features.RuntimeEvents.Models;

/// <summary>
/// Severity classification for a <see cref="RuntimeEvent"/>. Persisted as a
/// string (see <c>ApplicationDbContext.OnModelCreating</c>) so the column is
/// readable in <c>psql</c> and new severities don't shuffle integer values
/// underneath existing rows.
///
/// <list type="bullet">
///   <item><see cref="Info"/> — routine lifecycle (<c>InstallStarted</c>,
///         <c>ServiceRunning</c>, <c>SpecDeltaApplied</c>). Shown in the
///         drawer's Timeline as the default tone.</item>
///   <item><see cref="Warn"/> — non-fatal anomalies (e.g. install skipped
///         because a hash matched, but the user might still want to know).
///         Drawer renders these in amber.</item>
///   <item><see cref="Error"/> — anything ending in <c>Failed</c>
///         (<c>InstallFailed</c>, <c>SpecValidationFailed</c>,
///         <c>ServiceCrashed</c>). Drawer renders these in red and they
///         drive the bell-icon unread badge.</item>
/// </list>
/// </summary>
public enum RuntimeEventSeverity
{
    Info,
    Warn,
    Error,
}
