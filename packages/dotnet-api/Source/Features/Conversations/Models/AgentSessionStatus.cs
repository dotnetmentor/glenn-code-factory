using Tapper;

namespace Source.Features.Conversations.Models;

/// <summary>
/// Lifecycle of a single <see cref="AgentSession"/> — one prompt → one outcome.
///
/// <para>The state graph is:</para>
/// <list type="bullet">
///   <item><see cref="Pending"/> → <see cref="Running"/> when the daemon picks
///         up the dispatch and emits <c>turn_started</c>.</item>
///   <item><see cref="Running"/> → <see cref="Succeeded"/> on
///         <c>turn_completed</c>.</item>
///   <item><see cref="Running"/> → <see cref="Failed"/> on
///         <c>turn_failed</c> (rate limit, tool error, internal error).</item>
///   <item><see cref="Running"/> → <see cref="Canceling"/> → <see cref="Canceled"/>
///         when the user cancels mid-flight (the <c>Canceling</c> step covers
///         the in-flight CancelTurn round-trip; once the daemon emits
///         <c>turn_canceled</c> the session flips to terminal <c>Canceled</c>).</item>
/// </list>
///
/// <para>Persisted as a string (see <c>HasConversion&lt;string&gt;()</c> in
/// <c>ApplicationDbContext</c>) so adding new states later doesn't break
/// existing rows. The numeric values below are source-code stable but never
/// touch the DB; they're irrelevant for migrations.</para>
///
/// <para><b>Note on enum order vs declaration order.</b> Logically the
/// lifecycle is Pending → Running → <i>Canceling</i> → Canceled / Succeeded /
/// Failed. <see cref="Canceling"/> is declared <i>last</i> (with the highest
/// numeric value) on purpose: persistence is by name, but appending keeps the
/// existing numeric values stable for any in-source consumer (debugger views,
/// log dumps) that may still reference them. Read the lifecycle in the
/// XML-doc list above, not from the declaration order here.</para>
/// </summary>
[TranspilationSource]
public enum AgentSessionStatus
{
    /// <summary>Created and waiting in the soft-queue for dispatch. Default starting state.</summary>
    Pending = 0,

    /// <summary>Daemon picked up the dispatch and the turn is in flight.</summary>
    Running = 1,

    /// <summary>Turn completed successfully — terminal.</summary>
    Succeeded = 2,

    /// <summary>Turn failed (rate limit, tool error, internal error) — terminal.</summary>
    Failed = 3,

    /// <summary>User canceled the turn mid-flight — terminal.</summary>
    Canceled = 4,

    /// <summary>
    /// User requested cancellation; the <c>CancelTurn</c> push has been sent
    /// to the daemon but the daemon has not yet emitted <c>turn_canceled</c>.
    /// Transient state — flips to <see cref="Canceled"/> once the daemon
    /// confirms (or to <see cref="Failed"/> with reason <c>"runtime_unavailable"</c>
    /// if the orphan-session janitor reaps it). Logically this lives between
    /// <see cref="Running"/> and <see cref="Canceled"/>; declared last for
    /// numeric-value stability — see the type-level XML doc.
    /// </summary>
    Canceling = 5,
}
