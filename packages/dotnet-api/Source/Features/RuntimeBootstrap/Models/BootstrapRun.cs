using Source.Shared;
using Source.Shared.Events;

namespace Source.Features.RuntimeBootstrap.Models;

/// <summary>
/// Append-only audit row capturing a single bootstrap attempt against a runtime
/// machine. The bootstrap daemon (deferred — not yet implemented) writes one row
/// per attempt so we can:
///
/// <list type="bullet">
///   <item>see when a runtime last booted successfully;</item>
///   <item>diagnose stuck/failed boots by looking at <see cref="FinalStage"/>
///         and <see cref="ErrorReason"/>;</item>
///   <item>correlate failures with a specific daemon build
///         (<see cref="DaemonVersion"/>) or base image
///         (<see cref="ImageDigest"/>) for blast-radius analysis.</item>
/// </list>
///
/// <para>Deliberately NOT soft-deletable — these rows are the audit trail and
/// must never disappear. Mirrors the <c>FlyOperation</c> pattern: thin POCO,
/// no FK to runtime (runtimes can be torn down while history must outlive them).</para>
/// </summary>
public class BootstrapRun : Entity, IAuditable
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// The runtime this attempt targets. Plain Guid (no FK) — see class summary.
    /// </summary>
    public Guid RuntimeId { get; set; }

    /// <summary>UTC timestamp when the daemon started this attempt.</summary>
    public DateTime StartedAt { get; set; }

    /// <summary>UTC timestamp when the attempt finished. <c>null</c> while still in flight.</summary>
    public DateTime? EndedAt { get; set; }

    /// <summary>
    /// Last stage the daemon reported reaching. On success this is
    /// <see cref="BootstrapStage.Ready"/>; on failure it pinpoints where we got stuck.
    /// Persisted as a string.
    /// </summary>
    public BootstrapStage FinalStage { get; set; }

    /// <summary>True iff the attempt reached <see cref="BootstrapStage.Ready"/>.</summary>
    public bool Success { get; set; }

    /// <summary>
    /// Human-readable failure detail — typically a short message plus a stack
    /// trace or git/transport error. Sized for full traces (4000 chars).
    /// <c>null</c> on success.
    /// </summary>
    public string? ErrorReason { get; set; }

    /// <summary>Build identifier of the daemon that produced this row, e.g. a git sha.</summary>
    public string? DaemonVersion { get; set; }

    /// <summary>OCI digest of the base image the runtime booted from, e.g. <c>sha256:...</c>.</summary>
    public string? ImageDigest { get; set; }

    /// <summary>
    /// Schema version of the bootstrap payload contract. Bumped when the
    /// daemon &lt;-&gt; API protocol changes incompatibly. e.g. <c>"v1"</c>.
    /// </summary>
    public string BootstrapVersion { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
