namespace Source.Features.Conversations.Models;

/// <summary>
/// Returned by <c>GET /api/runtimes/{runtimeId}/queue</c> — the read-only
/// snapshot the chat panel renders for the queue indicator and reorder UI.
///
/// <para><b>Why two active-session ids, not one.</b> A runtime can host at
/// most one <see cref="AgentSessionStatus.Running"/> session and at most one
/// <see cref="AgentSessionStatus.Canceling"/> session — but both can coexist
/// briefly: a Running session is told to cancel (flips to Canceling, stays
/// occupying the runtime until the daemon's <c>turn_canceled</c> lands), and
/// during that drain window a follow-up session may already be Running on the
/// same runtime if the dispatcher picked the next queued session early. In
/// practice today only one of the two is non-null at a time, but exposing both
/// keeps the contract honest: the UI can decide whether to show "running" vs
/// "canceling…" copy without inferring from a single combined field.</para>
///
/// <para><see cref="Entries"/> is the queued (Pending + non-null QueuePosition)
/// tail, ordered by QueuePosition. Empty when nothing is queued.</para>
/// </summary>
public record QueueResponse(
    IReadOnlyList<QueueEntryDto> Entries,
    Guid? RunningSessionId,
    Guid? CancelingSessionId);

/// <summary>
/// One row in the queue indicator UI. <see cref="PromptPreview"/> is the head
/// of <see cref="AgentSession.Prompt"/> truncated at 120 chars (with an
/// ellipsis when truncated) so the queue list fits on a phone-narrow chat
/// panel without a full-prompt round trip — the user clicks through for the
/// full text.
/// </summary>
public record QueueEntryDto(
    Guid SessionId,
    Guid ConversationId,
    int QueuePosition,
    AgentSessionStatus Status,
    string PromptPreview,
    DateTime CreatedAt);

/// <summary>
/// Returned by <c>GET /api/sessions/{sessionId}/position</c> — a single-session
/// view used by the chat panel to render the "you're #3 in the queue" copy on
/// a Pending session, or "running" / "canceling" / terminal copy otherwise.
///
/// <para><see cref="QueuePosition"/> is null when the session isn't queued —
/// i.e. it's Running, Canceling, or in any terminal state. <see cref="RuntimeQueueLength"/>
/// is the count of Pending+queued sessions on the same runtime regardless of
/// this session's own state, so the UI can show "1 ahead, 3 total queued"
/// without a second round trip.</para>
/// </summary>
public record SessionPositionResponse(
    Guid SessionId,
    AgentSessionStatus Status,
    int? QueuePosition,
    int RuntimeQueueLength);
