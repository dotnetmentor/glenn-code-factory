using Hangfire;
using Microsoft.EntityFrameworkCore;
using Source.Features.Conversations.Events;
using Source.Features.Conversations.Models;
using Source.Infrastructure;

namespace Source.Features.Conversations.Jobs;

/// <summary>
/// Per-turn deadline safety net. When a session is dispatched the
/// <see cref="EventHandlers.ScheduleSessionStartupTimeoutHandler"/> schedules a
/// single delayed run of this job <see cref="Delay"/> minutes out. When the
/// timer fires we inspect the session: if it's still
/// <see cref="AgentSessionStatus.Running"/> and the daemon has not emitted a
/// single audit row beyond the seed <see cref="AgentEventKind.PromptReceived"/>
/// at sequence 0, we conclude the daemon is wedged and tear the session down
/// from the server side.
///
/// <para>The shutdown is deliberately deterministic: append a Status row
/// (<see cref="AgentEventKind.Status"/> + <see cref="AgentEventRunStatus.Expired"/>)
/// at the next sequence (so the chat UI shows a "the agent never responded"
/// entry exactly like any other terminal event), then call
/// <see cref="AgentSession.Fail(string?)"/> with reason <c>sdk_no_response</c>.
/// <see cref="AgentSession.Fail(string?)"/>
/// is already idempotent on terminal sessions, so a late daemon reply
/// (turn_completed / turn_failed) racing this job is harmless — whichever lands
/// first wins, the other no-ops.</para>
///
/// <para><b>Why a single delayed job, not a periodic watchdog.</b> A periodic
/// scan would need a "session timed out / wedged" predicate baked into a global
/// janitor — more code, more state, harder to reason about per-turn deadlines.
/// One Schedule(...) call per dispatch keeps the policy local: dispatch happens
/// in <c>TurnDispatcher</c> / <c>SubmitUrgentPromptCommand</c> / queue-drain
/// handlers, every one of those raises <see cref="SessionDispatched"/>, and the
/// handler hooks the timer in lockstep. If the daemon emits ANY event (even
/// just a Status row) within the deadline the job
/// no-ops; the daemon's own pipeline guarantees a terminal event eventually
/// (TurnCompleted / TurnFailed / TurnCanceled) so a healthy turn is never
/// killed by this safety net.</para>
///
/// <para><b>Both Claude and OpenCode backends are covered.</b> Both route
/// through <see cref="TurnDispatcher"/> / <see cref="SubmitUrgentPromptCommand"/>
/// → <c>AgentSession.Dispatch()</c> → <see cref="SessionDispatched"/>. One
/// handler, one job, both backends — by construction.</para>
///
/// <para><b>Idempotent against re-runs.</b> If the Hangfire worker dies
/// mid-execution and the schedule re-fires the job after the session already
/// reached a terminal state, both guards short-circuit: the predicate check
/// returns false and <see cref="AgentSession.Fail(string?)"/> is a no-op on
/// terminal rows. No double-Fail, no double-event.</para>
/// </summary>
public class SessionStartupTimeoutJob
{
    /// <summary>
    /// Per-turn deadline. A turn that hasn't produced a single audit row in
    /// this window is considered wedged. Two minutes is comfortably longer
    /// than a cold-start <c>query()</c> handshake (a few seconds) and leaves
    /// no human user staring at a "Working…" spinner for half an hour.
    /// </summary>
    public static readonly TimeSpan Delay = TimeSpan.FromMinutes(2);

    private readonly ApplicationDbContext _db;
    private readonly ILogger<SessionStartupTimeoutJob> _logger;

    public SessionStartupTimeoutJob(
        ApplicationDbContext db,
        ILogger<SessionStartupTimeoutJob> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Hangfire entry point. Loads the session, inspects whether it's still
    /// wedged at sequence 0, and if so emits a Status row
    /// (<see cref="AgentEventKind.Status"/> + <see cref="AgentEventRunStatus.Expired"/>)
    /// + flips the session to <see cref="AgentSessionStatus.Failed"/>.
    ///
    /// <para><see cref="AutomaticRetry"/> with <c>Attempts = 0</c> stops
    /// Hangfire from auto-requeuing on a transient failure — the worst that
    /// happens if this job throws is the session stays wedged until the
    /// runtime suspends and the <c>OrphanSessionJanitorJob</c> sweeps it.
    /// Auto-retry would just stack more attempts at no benefit.</para>
    /// </summary>
    [AutomaticRetry(Attempts = 0)]
    public async Task Run(Guid sessionId, CancellationToken ct = default)
    {
        // We need the conversation's ProjectId/BranchId to construct the
        // AgentEventEmitted payload that the broadcast handler fans out to
        // SignalR groups. Single round trip via projection — cheap, and
        // avoids loading two large entities.
        var row = await _db.AgentSessions
            .AsNoTracking()
            .Where(s => s.Id == sessionId)
            .Select(s => new
            {
                s.Id,
                s.Status,
                s.ConversationId,
                s.RuntimeId,
                ProjectId = s.Conversation.ProjectId,
                BranchId = s.Conversation.BranchId,
            })
            .FirstOrDefaultAsync(ct);

        if (row is null)
        {
            // Session was deleted between scheduling and firing — nothing to do.
            _logger.LogDebug(
                "SessionStartupTimeout: session {SessionId} no longer exists, skipping",
                sessionId);
            return;
        }

        // Pending counts as wedged too: the daemon should have flipped it
        // Running via TurnStarted by now. Anything Canceling / terminal is
        // already on its way out — leave it alone.
        if (row.Status is not (AgentSessionStatus.Pending or AgentSessionStatus.Running))
        {
            _logger.LogDebug(
                "SessionStartupTimeout: session {SessionId} is in state {Status}, skipping",
                sessionId, row.Status);
            return;
        }

        // The wedge predicate: the daemon has emitted zero events beyond the
        // seed PromptReceived row at sequence 0. Any seq >= 1 means the daemon
        // is alive and the turn is making progress — let it run.
        var hasDaemonActivity = await _db.AgentEvents
            .AnyAsync(e => e.SessionId == sessionId && e.Sequence > 0, ct);

        if (hasDaemonActivity)
        {
            _logger.LogDebug(
                "SessionStartupTimeout: session {SessionId} has daemon activity (seq > 0), skipping",
                sessionId);
            return;
        }

        // Load the tracked entity for the mutating step. We re-check Status on
        // the live row to close the small race between the AsNoTracking probe
        // and now — a turn_completed / turn_failed event could have landed
        // mid-function and flipped the session to terminal.
        var session = await _db.AgentSessions
            .FirstOrDefaultAsync(s => s.Id == sessionId, ct);

        if (session is null)
        {
            return;
        }

        if (session.Status is AgentSessionStatus.Succeeded
            or AgentSessionStatus.Failed
            or AgentSessionStatus.Canceled)
        {
            // Raced — daemon delivered a terminal event between our probe and
            // the load. Fail() would no-op anyway, but skipping here avoids
            // the audit row insert.
            return;
        }

        var nowUtc = DateTime.UtcNow;

        // Compute the next sequence. PromptReceived is always at seq 0; the
        // very next row from the daemon would have been seq 1. We append at
        // max+1 to stay gap-free in line with the normal RuntimeHub.EmitEvent
        // contract.
        var maxSeq = await _db.AgentEvents
            .Where(e => e.SessionId == sessionId)
            .MaxAsync(e => (long?)e.Sequence, ct);
        var nextSeq = (maxSeq ?? -1L) + 1L;

        // Cursor-native schema: the old SessionTimedOut event type is gone —
        // server-side timeouts are now expressed as a Status row with
        // RunStatus = Expired. Free-form message lives on the first-class
        // StatusMessage column.
        var timeoutMessage = $"sdk_no_response (deadline {(int)Delay.TotalSeconds}s)";

        var timeoutEvent = new AgentEvent
        {
            SessionId = session.Id,
            Sequence = nextSeq,
            Kind = AgentEventKind.Status,
            RunStatus = AgentEventRunStatus.Expired,
            StatusMessage = timeoutMessage,
            CreatedAt = nowUtc,
        };
        _db.AgentEvents.Add(timeoutEvent);

        // Raise the broadcast event BEFORE SaveChanges so the
        // DomainEventInterceptor sees it as part of the same transaction. The
        // BroadcastAgentEventHandler fans it out to branch + workspace groups
        // and the chat UI shows the terminal row alongside other events.
        // Build the typed DTO snapshot for the broadcast — Status with
        // RunStatus = Expired, free-form message in the StatusMessage column.
        var timeoutDto = new StatusEventDto(
            SessionId: session.Id,
            Sequence: nextSeq,
            CreatedAt: nowUtc,
            Status: AgentEventRunStatus.Expired,
            Message: timeoutMessage);

        session.RecordEventEmitted(new AgentEventEmitted(
            ConversationId: row.ConversationId,
            ProjectId: row.ProjectId,
            BranchId: row.BranchId,
            Kind: AgentEventKind.Status,
            Event: timeoutDto,
            OccurredAt: nowUtc));

        // Flip to terminal Failed. AgentSession.Fail is idempotent — safe even
        // if a concurrent path beat us to terminal.
        session.Fail("sdk_no_response");

        await _db.SaveChangesAsync(ct);

        _logger.LogWarning(
            "SessionStartupTimeout: session {SessionId} (runtime {RuntimeId}) marked Failed (reason=sdk_no_response) after {DeadlineSeconds}s with no daemon activity",
            session.Id, session.RuntimeId, (int)Delay.TotalSeconds);
    }
}
