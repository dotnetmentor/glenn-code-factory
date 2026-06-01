using Source.Shared;
using Source.Shared.Events;

namespace Source.Features.Health.Models;

/// <summary>
/// Append-only audit row capturing one disk-pressure transition the daemon
/// reported through <c>RuntimeHub.ReportDiskPressure</c>. One row per
/// emitted transition (ok→warn, warn→critical, critical→warn, …) — the daemon
/// only emits on level change so the row count stays bounded even when a
/// runtime sits at <c>warn</c> for hours.
///
/// <para>Same shape conventions as <c>RuntimeErrorReport</c> /
/// <c>BootstrapRun</c>: no FK to <see cref="RuntimeLifecycle.Models.ProjectRuntime"/>
/// (runtimes can be hard-deleted; the audit trail must outlive any individual
/// row), no soft-delete (hiding diagnostic rows defeats the point of an audit
/// feed), <see cref="IAuditable"/> for createdAt / updatedAt stamping.</para>
///
/// <list type="bullet">
///   <item><see cref="Level"/> is the daemon's pressure level — <c>"ok"</c>,
///         <c>"warn"</c>, or <c>"critical"</c>. Stored as a string so the
///         vocabulary can grow without a coordinated deploy. Capped at 16 chars
///         (matches the SignalR payload's wire validator).</item>
///   <item><see cref="UsedBytes"/> + <see cref="TotalBytes"/> mirror the
///         daemon's <c>statfs</c> sample at the moment of transition. Stored as
///         <see cref="long"/> — disk capacity in bytes is comfortably under
///         <c>2^53</c> for any plausible runtime.</item>
///   <item><see cref="UsedPct"/> is a denormalised <see cref="double"/>
///         (<c>UsedBytes / TotalBytes * 100</c>) the daemon computes once and
///         we persist verbatim. Convenient for dashboards that don't want to
///         re-divide every row.</item>
///   <item><see cref="ReportedAt"/> is the server clock at receive time — the
///         source of truth for ordering. Daemon clock skew can't shuffle the
///         timeline (same rationale as <c>ProjectRuntime.LastHeartbeatAt</c>
///         and <c>RuntimeErrorReport.ReportedAt</c>).</item>
///   <item><see cref="SampledAt"/> preserves the daemon's own clock at sample
///         time for clock-skew telemetry. Not used for ordering on the server
///         side.</item>
/// </list>
///
/// <para><b>Indexes.</b> Dominant read is "show me the disk-pressure timeline
/// for runtime X, newest first." Composite (RuntimeId, CreatedAt DESC) — emitted
/// via raw SQL in the migration since EF Core 9 doesn't expose per-column sort
/// direction on relational indexes. Same idiom as
/// <c>RuntimeErrorReport.IX_RuntimeErrorReports_RuntimeId_CreatedAt</c>.</para>
///
/// <para>Inherits <see cref="Entity"/> so future cards can raise events from
/// instance methods without a model change — even though this card has none.</para>
/// </summary>
public class RuntimeDiskPressureEvent : Entity, IAuditable
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The runtime that reported the transition. Plain Guid (no FK) — see class summary.</summary>
    public Guid RuntimeId { get; set; }

    /// <summary>
    /// Daemon-reported pressure level: <c>"ok"</c>, <c>"warn"</c>, or
    /// <c>"critical"</c>. Capped at 16 chars; stored as string for forward-
    /// compat with future levels.
    /// </summary>
    public string Level { get; set; } = string.Empty;

    /// <summary>Used bytes on the monitored filesystem at sample time.</summary>
    public long UsedBytes { get; set; }

    /// <summary>Total bytes on the monitored filesystem at sample time.</summary>
    public long TotalBytes { get; set; }

    /// <summary>
    /// Denormalised used fraction expressed as 0..100 — daemon computes
    /// <c>usedBytes / totalBytes * 100</c> once, we persist verbatim for
    /// dashboard convenience.
    /// </summary>
    public double UsedPct { get; set; }

    /// <summary>Daemon-side wall clock at the moment of sampling.</summary>
    public DateTime SampledAt { get; set; }

    /// <summary>
    /// Server clock at receive time — the source of truth for ordering. Daemon
    /// clock skew can't shuffle the timeline.
    /// </summary>
    public DateTime ReportedAt { get; set; }

    // -------- IAuditable --------
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
