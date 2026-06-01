namespace Source.Features.Conversations.Models;

/// <summary>
/// Append-only audit row capturing one Cursor SDK message the daemon emitted
/// while running an <see cref="AgentSession"/>. Every prompt receipt, every
/// chunk of assistant text, every thinking frame, every tool call (and its
/// terminal result), every run-status transition, every task milestone — one
/// row each.
///
/// <para>This is event-sourced ground truth: the chat panel does not have a
/// separate "messages" table, it reads these rows and renders them. The
/// audit log <i>is</i> the chat.</para>
///
/// <para><b>Shape (Cursor-native, post cursor-native-chat-ux spec).</b> A
/// single table with a <see cref="Kind"/> discriminator + per-kind nullable
/// columns + <see cref="Args"/> / <see cref="Result"/> as <c>jsonb</c> for
/// tool payloads (whose shape varies per tool). First-class promoted columns
/// for everything else mean the chat panel + analytics can read state without
/// parsing JSON in the query plan.</para>
///
/// <list type="bullet">
///   <item>Composite primary key <c>(SessionId, Sequence)</c> — the natural
///         clustering for "give me events 100..200 of session X" range scans
///         and the safety net for monotonic ordering. <see cref="Sequence"/>
///         is assigned server-side, gap-free per session, shared across all
///         kinds (so chronological reads stay one-shot).</item>
///   <item>Deliberately NOT an <see cref="Source.Shared.Events.Entity"/> — no
///         <c>Guid Id</c>, no domain-event raising. Rows are immutable once
///         written. Domain events about "an event was emitted" are raised on
///         the parent <see cref="AgentSession"/> instead.</item>
///   <item>Only <see cref="CreatedAt"/> is tracked. The <c>IAuditable</c>
///         interface implies <c>UpdatedAt</c> too, so we deliberately don't
///         implement it; the auto-stamp interceptor would never have anything
///         to update. The event-emit handler sets <c>CreatedAt</c> explicitly
///         to the server's UTC clock at receive time.</item>
/// </list>
/// </summary>
public class AgentEvent
{
    // ----------------------------------------------------------------------
    // Shared columns — every row, every kind
    // ----------------------------------------------------------------------

    /// <summary>The session this event belongs to. PK part 1, FK + cascade.</summary>
    public Guid SessionId { get; set; }

    /// <summary>
    /// Per-session monotonic, gap-free counter starting at 0. PK part 2.
    /// Assigned server-side atomically with the insert. <c>long</c> (Postgres
    /// <c>bigint</c>) — a long-running session can easily emit tens of
    /// thousands of events; <c>long</c> is free and keeps the column future-proof.
    /// Shared across all <see cref="Kind"/>s so chronological reads stay
    /// one-shot — the chat panel reads in <c>Sequence</c> ascending order.
    /// </summary>
    public long Sequence { get; set; }

    /// <summary>
    /// Discriminator — which Cursor SDK message shape this row represents.
    /// Drives which subset of nullable columns is populated. Persisted as a
    /// string so adding a kind doesn't reshuffle existing rows.
    /// </summary>
    public AgentEventKind Kind { get; set; }

    /// <summary>UTC timestamp the event was recorded server-side.</summary>
    public DateTime CreatedAt { get; set; }

    // ----------------------------------------------------------------------
    // ToolUse columns — populated when Kind = ToolUse
    // ----------------------------------------------------------------------

    /// <summary>
    /// Cursor's <c>SDKToolUseMessage.call_id</c> — pairs the running row with
    /// its terminal (completed / error) row. Nullable; only populated for
    /// <see cref="AgentEventKind.ToolUse"/> rows. The chat panel uses this to
    /// fix the orphan bug — every result is paired with its call.
    /// </summary>
    public string? CallId { get; set; }

    /// <summary>
    /// Tool name (e.g. <c>"shell"</c>, <c>"read"</c>, <c>"edit"</c>).
    /// Drives the formatter registry lookup on the frontend (see
    /// cursor-native-chat-ux §4). Nullable; only populated for
    /// <see cref="AgentEventKind.ToolUse"/> rows.
    /// </summary>
    public string? ToolName { get; set; }

    /// <summary>
    /// Tool call lifecycle status — running / completed / error. Nullable;
    /// only populated for <see cref="AgentEventKind.ToolUse"/> rows.
    /// Persisted as a string for wire stability.
    /// </summary>
    public AgentEventToolStatus? ToolStatus { get; set; }

    /// <summary>
    /// Tool arguments as <c>jsonb</c>. Shape varies per tool — kept as opaque
    /// JSON because each formatter on the frontend knows the per-tool schema.
    /// Nullable; only populated for <see cref="AgentEventKind.ToolUse"/> rows.
    /// May be <c>null</c> for tools that take no arguments.
    /// </summary>
    public string? Args { get; set; }

    /// <summary>
    /// Tool result as <c>jsonb</c>. Shape varies per tool (success and error
    /// variants). Nullable; only populated on the terminal
    /// <see cref="AgentEventToolStatus.Completed"/> /
    /// <see cref="AgentEventToolStatus.Error"/> row, never on the
    /// <see cref="AgentEventToolStatus.Running"/> row.
    /// </summary>
    public string? Result { get; set; }

    /// <summary>
    /// Cursor's <c>truncated.args</c> flag — the SDK trimmed the args payload
    /// because it exceeded the wire limit. Frontend renders a "Truncated"
    /// badge when set. Nullable; only populated for
    /// <see cref="AgentEventKind.ToolUse"/> rows.
    /// </summary>
    public bool? ArgsTruncated { get; set; }

    /// <summary>
    /// Cursor's <c>truncated.result</c> flag — sibling of
    /// <see cref="ArgsTruncated"/> for the result payload. Nullable; only
    /// populated for <see cref="AgentEventKind.ToolUse"/> rows.
    /// </summary>
    public bool? ResultTruncated { get; set; }

    // ----------------------------------------------------------------------
    // Thinking + AssistantText + PromptReceived shared text column
    // ----------------------------------------------------------------------

    /// <summary>
    /// The text body for <see cref="AgentEventKind.AssistantText"/>,
    /// <see cref="AgentEventKind.Thinking"/>, and
    /// <see cref="AgentEventKind.PromptReceived"/> rows. Nullable; only
    /// populated for those kinds. Mapped as <c>text</c> (unbounded) because
    /// thinking / assistant output can be arbitrarily long.
    /// </summary>
    public string? Text { get; set; }

    /// <summary>
    /// Cursor's <c>SDKThinkingMessage.thinking_duration_ms</c> — set on the
    /// terminal thinking frame so the chat panel can render
    /// "Thought for 3.4s". Nullable; only populated for
    /// <see cref="AgentEventKind.Thinking"/> rows, and only on the terminal
    /// frame of a thinking burst.
    /// </summary>
    public long? ThinkingDurationMs { get; set; }

    // ----------------------------------------------------------------------
    // Status columns — populated when Kind = Status
    // ----------------------------------------------------------------------

    /// <summary>
    /// Cursor's <c>SDKStatusMessage.status</c> — the run-level lifecycle
    /// state that drives the activity pill's headline. Nullable; only
    /// populated for <see cref="AgentEventKind.Status"/> rows.
    /// </summary>
    public AgentEventRunStatus? RunStatus { get; set; }

    /// <summary>
    /// Optional human-readable message accompanying a status transition (e.g.
    /// the error string on a <see cref="AgentEventRunStatus.Error"/>
    /// transition). Nullable; only populated for
    /// <see cref="AgentEventKind.Status"/> rows.
    /// </summary>
    public string? StatusMessage { get; set; }

    // ----------------------------------------------------------------------
    // Task columns — populated when Kind = Task
    // ----------------------------------------------------------------------

    /// <summary>
    /// Cursor's <c>SDKTaskMessage.task_id</c> — opaque id for the milestone.
    /// Nullable; only populated for <see cref="AgentEventKind.Task"/> rows.
    /// </summary>
    public string? TaskId { get; set; }

    /// <summary>
    /// Cursor's <c>SDKTaskMessage.title</c> — human-readable label for the
    /// milestone (e.g. "Subtask: refactor auth"). Nullable; only populated
    /// for <see cref="AgentEventKind.Task"/> rows.
    /// </summary>
    public string? TaskTitle { get; set; }

    // ----------------------------------------------------------------------
    // Navigation
    // ----------------------------------------------------------------------

    /// <summary>Parent session. Required navigation.</summary>
    public AgentSession Session { get; set; } = null!;
}
