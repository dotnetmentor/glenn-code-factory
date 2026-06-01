using Source.Shared;
using Source.Shared.Events;

namespace Source.Features.RuntimeLifecycle.Models;

/// <summary>
/// Append-only audit row capturing a single error the daemon decided was worth
/// reporting back to main API. Mirrors <see cref="RuntimeBootstrap.Models.BootstrapRun"/>:
/// no FK to <see cref="ProjectRuntime"/> (runtimes are soft-deleted; the audit
/// trail must outlive any individual row), no soft-delete (hiding diagnostic
/// rows defeats the point), <see cref="IAuditable"/> for createdAt / updatedAt
/// stamping.
///
/// <list type="bullet">
///   <item><see cref="Category"/> is the daemon-supplied short tag — kept as a
///         string so adding categories doesn't require a coordinated deploy.
///         Capped at 64 chars; the SignalR payload validator caps the same
///         length so the wire and persistence agree.</item>
///   <item><see cref="Message"/> is the human-readable one-liner. Required.
///         4000 chars matches <see cref="RuntimeBootstrap.Models.BootstrapRun.ErrorReason"/>.</item>
///   <item><see cref="StackTrace"/> + <see cref="Context"/> are optional;
///         16000 chars is enough for most language stacks + a redacted
///         diagnostic blob. Truncation belongs to the daemon — anything that
///         fat coming over a hub call already raises eyebrows; the cap is a
///         soft guard, not a contract.</item>
///   <item><see cref="ReportedAt"/> is the server clock at receive — the
///         server's wall-clock is the source of truth for "when did this
///         arrive?", same rationale as
///         <c>ProjectRuntime.LastHeartbeatAt</c>. Daemon clock skew can't
///         shuffle the timeline.</item>
/// </list>
///
/// <para><b>Indexes.</b> The dominant read is "show me the last N errors for
/// runtime X, newest first." Composite (RuntimeId, CreatedAt DESC) is emitted
/// via raw SQL in the migration since EF Core 9 doesn't expose per-column
/// sort direction on relational indexes — same idiom as
/// <c>BootstrapRun.IX_BootstrapRuns_RuntimeId_StartedAt</c> and the
/// RuntimeProposal indexes.</para>
///
/// <para>Inherits <see cref="Entity"/> so future cards can raise events from
/// instance methods without a model change — even though this card has none.</para>
/// </summary>
public class RuntimeErrorReport : Entity, IAuditable
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// The runtime this error came from. Plain Guid (no FK) — see class summary.
    /// </summary>
    public Guid RuntimeId { get; set; }

    /// <summary>
    /// Daemon-supplied short tag: <c>"hook"</c>, <c>"sandbox"</c>,
    /// <c>"bootstrap"</c>, etc. Capped at 64 chars.
    /// </summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable one-line summary. 4000 chars matches BootstrapRun.ErrorReason.
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Optional daemon-side stack trace. 16000 chars is enough for most
    /// language stacks; the daemon truncates beyond that.
    /// </summary>
    public string? StackTrace { get; set; }

    /// <summary>
    /// Optional opaque diagnostic blob (typically JSON). The server doesn't
    /// validate the shape — use cases vary by category.
    /// </summary>
    public string? Context { get; set; }

    /// <summary>
    /// Server clock at receive time — the source of truth for ordering. The
    /// daemon's clock could be skewed; our wall-clock isn't.
    /// </summary>
    public DateTime ReportedAt { get; set; }

    // -------- IAuditable --------
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
