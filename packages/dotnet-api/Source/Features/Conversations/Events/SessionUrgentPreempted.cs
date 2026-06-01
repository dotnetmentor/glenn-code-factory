using Source.Features.Conversations.Models;
using Source.Shared.Events;

namespace Source.Features.Conversations.Events;

/// <summary>
/// Raised when a user submits an <em>urgent</em> prompt via
/// <c>POST /api/conversations/{conversationId}/urgent-prompt</c>. The new
/// session is enqueued at the head of the runtime's queue (position 1) and —
/// if the runtime was busy — the currently-running (or already-canceling)
/// session is asked to cancel via <see cref="AgentSession.MarkCanceling"/>.
/// The new urgent session then dispatches automatically when the canceled
/// session terminates and the
/// <c>DispatchNextSessionHandler</c> picks the head of the queue.
///
/// <para><b>Why runtime-scoped, not session-scoped.</b> An urgent submission
/// touches up to N+1 sessions in one go: the canceled current session, the
/// newly-inserted urgent session, plus the +1 shift on every other queued
/// session. A single audit row capturing the whole reshuffle is more useful
/// than N near-identical rows — same reasoning as
/// <see cref="QueueReordered"/>.</para>
///
/// <para><b>Fields.</b> <see cref="NewSessionId"/> is the urgent session that
/// was just inserted. <see cref="CanceledSessionId"/> is the previously-active
/// (Running or already-Canceling) session that was asked to cancel —
/// <c>null</c> when the runtime was idle and no preempt was needed.
/// <see cref="RuntimeId"/> identifies the runtime whose queue was reshuffled.
/// <see cref="ActorUserId"/> is the user who initiated the urgent submission.</para>
///
/// <para>Only <see cref="IDomainEvent"/> (not <see cref="IEntityDomainEvent"/>)
/// because there's no single owning entity row — the event is raised against
/// the runtime's queue, not any one session. Mirrors
/// <see cref="QueueReordered"/>.</para>
/// </summary>
public record SessionUrgentPreempted(
    Guid NewSessionId,
    Guid? CanceledSessionId,
    Guid RuntimeId,
    string ActorUserId
) : IDomainEvent
{
    public DateTime OccurredAt { get; init; } = DateTime.UtcNow;
}
