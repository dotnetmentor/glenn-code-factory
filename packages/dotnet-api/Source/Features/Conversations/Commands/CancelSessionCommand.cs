using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Source.Features.Conversations.Events;
using Source.Features.Conversations.Models;
using Source.Features.SignalR.Contracts;
using Source.Features.SignalR.Hubs;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.Conversations.Commands;

/// <summary>
/// Cancel an <see cref="AgentSession"/>. Routes by current status:
///
/// <list type="bullet">
///   <item><b>Pending</b>: drop directly to terminal
///         <see cref="AgentSessionStatus.Canceled"/> via
///         <see cref="AgentSession.MarkCanceled"/>. There's no in-flight turn
///         to drain and no daemon to notify; the
///         <see cref="Source.Features.Conversations.EventHandlers.DispatchNextSessionHandler"/>
///         picks up the next queued session on the runtime.</item>
///   <item><b>Running</b>: transition to
///         <see cref="AgentSessionStatus.Canceling"/> via
///         <see cref="AgentSession.MarkCanceling"/>, then push a
///         <see cref="CancelTurnPayload"/> to the daemon group. The session
///         flips to terminal <see cref="AgentSessionStatus.Canceled"/> only
///         once the daemon emits <c>turn_canceled</c> back through
///         <c>RuntimeHub.EmitEvent</c>.</item>
///   <item><b>Canceling</b>, <b>Canceled</b>, <b>Succeeded</b>, <b>Failed</b>:
///         idempotent no-op — return success with the current status. Repeated
///         user cancel clicks must not double-fan-out or surface errors.</item>
/// </list>
///
/// <para>Returns <see cref="Result.Failure"/> only when the session id doesn't
/// match a row. SignalR fan-out failures are logged-and-swallowed: the session
/// has already been flipped to Canceling in the DB, and the orphan-session
/// janitor (Card 8) is the recovery path if the daemon never confirms.</para>
/// </summary>
public record CancelSessionCommand(Guid SessionId, string Reason) : ICommand<Result<CancelSessionResponse>>;

/// <summary>
/// Result shape for <see cref="CancelSessionCommand"/>. <c>FinalStatus</c> is
/// the session's status <i>after</i> the command runs:
/// <see cref="AgentSessionStatus.Canceled"/> for the Pending → Canceled drop,
/// <see cref="AgentSessionStatus.Canceling"/> for the Running → Canceling
/// transition (terminal Canceled lands later when the daemon confirms), or
/// the unchanged status for an idempotent no-op on an already-terminal
/// session.
/// </summary>
public record CancelSessionResponse(Guid SessionId, AgentSessionStatus FinalStatus);

public class CancelSessionCommandHandler : ICommandHandler<CancelSessionCommand, Result<CancelSessionResponse>>
{
    private readonly ApplicationDbContext _db;
    private readonly IHubContext<RuntimeHub, IRuntimeClient> _runtimeHub;
    private readonly ILogger<CancelSessionCommandHandler> _logger;

    public CancelSessionCommandHandler(
        ApplicationDbContext db,
        IHubContext<RuntimeHub, IRuntimeClient> runtimeHub,
        ILogger<CancelSessionCommandHandler> logger)
    {
        _db = db;
        _runtimeHub = runtimeHub;
        _logger = logger;
    }

    public async Task<Result<CancelSessionResponse>> Handle(
        CancelSessionCommand request,
        CancellationToken cancellationToken)
    {
        // IgnoreQueryFilters so an archived parent conversation does not hide
        // a still-in-flight session — the user might cancel from a thread they
        // archived seconds earlier and we still want the cancel to land.
        //
        // Eager-load the parent Conversation: the Pending→Canceled branch
        // synthesizes an AgentEvent + AgentEventEmitted domain event and the
        // event payload carries ProjectId / BranchId off the conversation.
        // Mirrors RuntimeHub.EmitEvent which loads the same shape.
        var session = await _db.AgentSessions
            .IgnoreQueryFilters()
            .Include(s => s.Conversation)
            .FirstOrDefaultAsync(s => s.Id == request.SessionId, cancellationToken);

        if (session is null)
        {
            return Result.Failure<CancelSessionResponse>("Session not found");
        }

        // Idempotent on already-terminal / already-Canceling states. Repeated
        // cancel clicks from the user, retries from the UI on a flaky
        // connection, or the orphan-janitor running after the user already
        // canceled — all collapse to a clean success with the current status.
        if (session.Status is AgentSessionStatus.Canceled
            or AgentSessionStatus.Canceling
            or AgentSessionStatus.Succeeded
            or AgentSessionStatus.Failed)
        {
            return Result.Success(new CancelSessionResponse(session.Id, session.Status));
        }

        // Pending: no in-flight turn to drain, no daemon to notify. Drop
        // straight to terminal Canceled; the AgentSessionTerminated event
        // raised by MarkCanceled feeds the dispatch-next handler so the
        // runtime's queue keeps moving.
        if (session.Status == AgentSessionStatus.Pending)
        {
            session.MarkCanceled(request.Reason);

            // Wire-truth Cancelled status frame. The chat panel's activity
            // pill reads the event stream (not session.Status) to pick its
            // headline; without an explicit Cancelled row a late-landing
            // Error event from the daemon — even one that races our cancel
            // and arrives after the session is already Canceled in the DB —
            // would poison the pill into showing "Error". Anchoring an
            // authoritative Cancelled into the stream gives the UI something
            // to lock onto that survives a full page reload / scrollback
            // re-render, where the live session.Status hint is gone.
            //
            // Only emitted on the Pending fast path: the Running branch
            // transitions to Canceling (not Canceled), and the daemon's own
            // turn_canceled status event flows through RuntimeHub.EmitEvent
            // and creates the real Cancelled row when it lands. Synthesizing
            // here too would duplicate.
            //
            // No "isSynthetic" flag — to the wire and to the frontend this
            // row is indistinguishable from a daemon-emitted Cancelled, which
            // is exactly what we want.
            await EmitSyntheticCancelledStatusEventAsync(session, request.Reason, cancellationToken);

            await _db.SaveChangesAsync(cancellationToken);

            _logger.LogInformation(
                "CancelSession: Pending session {SessionId} on runtime {RuntimeId} canceled directly (reason {Reason}).",
                session.Id, session.RuntimeId, request.Reason);

            return Result.Success(new CancelSessionResponse(session.Id, AgentSessionStatus.Canceled));
        }

        // Running: intermediate transition to Canceling. The daemon will send
        // back a terminal turn_canceled event which flips Canceling → Canceled
        // via MarkCanceled — that's where AgentSessionTerminated fires.
        session.MarkCanceling(request.Reason);
        await _db.SaveChangesAsync(cancellationToken);

        // SignalR fan-out is best-effort, OUTSIDE the SaveChanges scope. If the
        // daemon is offline or the hub throws, the session stays in Canceling
        // and the orphan-session janitor (Card 8) is the safety net — it will
        // mark the session terminal Canceled with reason "runtime_unavailable"
        // once the runtime is observed disconnected. We don't roll back the
        // Canceling transition because the user-facing intent ("I want this
        // canceled") is already recorded; rolling back would lose that signal.
        var cancelPayload = new CancelTurnPayload(
            SessionId: session.Id,
            Reason: request.Reason);

        try
        {
            await _runtimeHub.Clients
                .Group($"runtime-{session.RuntimeId}")
                .CancelTurn(cancelPayload);

            _logger.LogInformation(
                "CancelSession: Running session {SessionId} on runtime {RuntimeId} -> Canceling; CancelTurn pushed (reason {Reason}).",
                session.Id, session.RuntimeId, request.Reason);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "CancelSession: CancelTurn fan-out failed for session {SessionId} on runtime {RuntimeId}; session is Canceling in DB. Janitor will reap if the daemon never confirms.",
                session.Id, session.RuntimeId);
        }

        return Result.Success(new CancelSessionResponse(session.Id, AgentSessionStatus.Canceling));
    }

    /// <summary>
    /// Append a Cancelled <see cref="AgentEventKind.Status"/> row to the
    /// session's event stream and raise <see cref="AgentEventEmitted"/> so the
    /// existing broadcast pipeline fans it out to connected web clients on
    /// the <c>branch-{id}</c> + <c>workspace-{id}</c> SignalR groups.
    ///
    /// <para>Deliberately mirrors <see cref="RuntimeHub.EmitEvent"/>'s path
    /// end-to-end — same sequence calculation, same DTO shape, same domain
    /// event — so the wire payload is byte-equivalent to a daemon-emitted
    /// Cancelled frame. No <c>SaveChangesAsync</c> here; the caller flushes
    /// once so the entity mutation, the new event row, and the conversation
    /// counter bumps all commit atomically.</para>
    /// </summary>
    private async Task EmitSyntheticCancelledStatusEventAsync(
        AgentSession session,
        string reason,
        CancellationToken cancellationToken)
    {
        // Per-session monotonic sequence — same read-then-write the hub uses.
        // The composite PK on (SessionId, Sequence) is the safety net; under
        // the spec the daemon owns one session at a time so a collision with
        // an in-flight EmitEvent is not expected (Pending sessions have not
        // dispatched yet — there's no daemon emitting events for them).
        var maxSeq = await _db.AgentEvents
            .Where(e => e.SessionId == session.Id)
            .MaxAsync(e => (long?)e.Sequence, cancellationToken);
        var nextSeq = (maxSeq ?? -1L) + 1L;

        var nowUtc = DateTime.UtcNow;
        var newEvent = new AgentEvent
        {
            SessionId = session.Id,
            Sequence = nextSeq,
            Kind = AgentEventKind.Status,
            CreatedAt = nowUtc,
            RunStatus = AgentEventRunStatus.Cancelled,
            StatusMessage = reason,
        };
        _db.AgentEvents.Add(newEvent);

        // Bump denormalized counters on the parent conversation — same as
        // EmitEvent. Keeps the conversation-list query consistent (would
        // otherwise show stale LastActivityAt for a session that just
        // produced a terminal event).
        session.Conversation.LastActivityAt = nowUtc;
        session.Conversation.EventCount += 1;

        // Typed projection of the row we just queued — matches the
        // BuildAgentEventDto Status case in RuntimeHub so the wire shape
        // is byte-identical to a real daemon-emitted Cancelled.
        var dto = new StatusEventDto(
            SessionId: session.Id,
            Sequence: nextSeq,
            CreatedAt: nowUtc,
            Status: AgentEventRunStatus.Cancelled,
            Message: reason);

        // Raise on the session so the DomainEventInterceptor collects +
        // persists + publishes after SaveChanges. BroadcastAgentEventHandler
        // listens for AgentEventEmitted and fans out to the branch +
        // workspace SignalR groups — same pipeline as EmitEvent.
        session.RecordEventEmitted(new AgentEventEmitted(
            ConversationId: session.ConversationId,
            ProjectId: session.Conversation.ProjectId,
            BranchId: session.Conversation.BranchId,
            Kind: AgentEventKind.Status,
            Event: dto,
            OccurredAt: nowUtc));
    }
}
