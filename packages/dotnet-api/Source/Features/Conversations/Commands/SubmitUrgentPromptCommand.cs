using MediatR;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Source.Features.Attachments.Services;
using Source.Features.Conversations.Events;
using Source.Features.Conversations.Models;
using Source.Features.SignalR.Contracts;
using Source.Features.SignalR.Hubs;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.Conversations.Commands;

/// <summary>
/// Submit an <em>urgent</em> prompt to a conversation: cancel the runtime's
/// current in-flight session (if any) and queue the urgent prompt at the head
/// of the runtime's queue so it dispatches as soon as the runtime frees up.
///
/// <para>Confirmation flow (the chat UI prompts the user "this will cancel
/// the running turn — continue?") is owned by the frontend; by the time this
/// command runs the user has already accepted. The handler is idempotent in
/// spirit: a runtime that is already idle skips the preempt step, a runtime
/// whose current session is already Canceling skips the second
/// <see cref="AgentSession.MarkCanceling"/> call (no double CancelTurn fan-out).</para>
///
/// <list type="bullet">
///   <item><b>Active runtime</b> (Running OR Canceling current session): the
///         current session transitions to <see cref="AgentSessionStatus.Canceling"/>
///         (no-op if already Canceling). Existing Pending queued sessions get
///         their <see cref="AgentSession.QueuePosition"/> shifted by +1. The
///         new urgent session is inserted at position 1. <c>CancelTurn</c> is
///         pushed to the daemon. The
///         <c>DispatchNextSessionHandler</c> dispatches the urgent session
///         once the canceled session lands in a terminal state.</item>
///   <item><b>Idle runtime</b> (no Running/Canceling session): no preempt
///         happens. The urgent session is inserted directly at status
///         <see cref="AgentSessionStatus.Running"/> via
///         <see cref="AgentSession.Dispatch"/> and <c>StartTurn</c> is pushed
///         immediately — same shape as the regular SubmitPrompt happy path
///         when the runtime is idle.</item>
/// </list>
///
/// <para><b>Why shift +1 instead of using position 0 / negative.</b> The queue
/// is 1-based contiguous by convention (Card 2). Inserting at position 0 or
/// -1 forces every consumer to handle a special-case head; renumbering the
/// existing tail by +1 keeps the queue normalised and matches the renumber
/// logic in <see cref="ReorderQueueCommand"/>. The cost is one extra UPDATE
/// per queued session — a handful of rows in practice.</para>
///
/// <para><b>SignalR fan-out is best-effort.</b> The cancel/start push happens
/// AFTER SaveChanges, with try/catch + warn-log on failure. The session row is
/// already in the right state (Canceling / Running) and the orphan-session
/// janitor (Card 8) handles the "daemon never confirmed" recovery.</para>
///
/// <para><b>Event publishing.</b> <see cref="SessionUrgentPreempted"/> is
/// runtime-scoped (no single owning entity row) so we publish it directly via
/// <see cref="IMediator.Publish"/> after SaveChanges, mirroring the pattern in
/// <see cref="ReorderQueueCommand"/>.</para>
/// </summary>
public record SubmitUrgentPromptCommand(
    Guid ConversationId,
    string Prompt,
    string ActorUserId,
    /// <summary>
    /// Optional per-session model override. <c>null</c> falls back to the
    /// project default (and then the SDK default). Same semantics as the
    /// regular <c>SubmitPrompt</c> path on <c>AgentHub</c> — the resolved
    /// slug rides on <c>StartTurnPayload.Model</c>.
    /// </summary>
    Guid? ModelId = null
) : ICommand<Result<SubmitUrgentPromptResponse>>;

/// <summary>
/// Result shape for <see cref="SubmitUrgentPromptCommand"/>.
///
/// <list type="bullet">
///   <item><see cref="SessionId"/> — the freshly-inserted urgent session.</item>
///   <item><see cref="CanceledSessionId"/> — the previously-active session
///         that was asked to cancel; <c>null</c> when the runtime was idle
///         (no preempt happened).</item>
///   <item><see cref="Queued"/> — <c>true</c> when the urgent session was
///         placed at the head of the queue behind a session being canceled;
///         <c>false</c> when the runtime was idle and the urgent session
///         dispatched immediately.</item>
///   <item><see cref="QueuePosition"/> — mirrors the session's queue position
///         when <see cref="Queued"/> is true (always <c>1</c> by definition);
///         <c>null</c> when <see cref="Queued"/> is false.</item>
/// </list>
/// </summary>
public record SubmitUrgentPromptResponse(
    Guid SessionId,
    Guid? CanceledSessionId,
    bool Queued,
    int? QueuePosition);

public class SubmitUrgentPromptCommandHandler
    : ICommandHandler<SubmitUrgentPromptCommand, Result<SubmitUrgentPromptResponse>>
{
    private const string PreemptReason = "urgent_preempted";

    private readonly ApplicationDbContext _db;
    private readonly IHubContext<RuntimeHub, IRuntimeClient> _runtimeHub;
    private readonly IMediator _mediator;
    private readonly ILogger<SubmitUrgentPromptCommandHandler> _logger;

    public SubmitUrgentPromptCommandHandler(
        ApplicationDbContext db,
        IHubContext<RuntimeHub, IRuntimeClient> runtimeHub,
        IMediator mediator,
        ILogger<SubmitUrgentPromptCommandHandler> logger)
    {
        _db = db;
        _runtimeHub = runtimeHub;
        _mediator = mediator;
        _logger = logger;
    }

    public async Task<Result<SubmitUrgentPromptResponse>> Handle(
        SubmitUrgentPromptCommand request,
        CancellationToken cancellationToken)
    {
        // Lightweight payload validation. The hub-side SubmitPrompt path caps
        // at 50_000 chars; we mirror that to keep the two surfaces consistent.
        if (string.IsNullOrWhiteSpace(request.Prompt))
        {
            return Result.Failure<SubmitUrgentPromptResponse>("Prompt cannot be empty");
        }
        if (request.Prompt.Length > 50_000)
        {
            return Result.Failure<SubmitUrgentPromptResponse>("Prompt too long (max 50000 chars)");
        }

        // IgnoreQueryFilters so an archived conversation can still receive an
        // urgent prompt — same stance as CancelSessionCommand and the rename /
        // GetConversation flows. Archived is a UX flag, not a hard lock.
        var conversation = await _db.Conversations
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Id == request.ConversationId, cancellationToken);
        if (conversation is null)
        {
            return Result.Failure<SubmitUrgentPromptResponse>("Conversation not found");
        }

        // Resolve the runtime via the conversation's BRANCH — same lookup the
        // ITurnDispatcher does. A project can own multiple ProjectRuntime rows
        // after CopyBranch (one per branch); filtering by project alone would
        // hand the urgent prompt to the wrong Fly machine. The conversation's
        // BranchId is the authoritative scope.
        var runtime = await _db.ProjectRuntimes
            .FirstOrDefaultAsync(r => r.BranchId == conversation.BranchId, cancellationToken);
        if (runtime is null)
        {
            return Result.Failure<SubmitUrgentPromptResponse>("No runtime for this branch");
        }

        // Defense-in-depth: the resolved runtime's project must match the
        // conversation's. If they diverge, something has corrupted the
        // (project, branch) pairing — refuse rather than silently dispatch.
        if (runtime.ProjectId != conversation.ProjectId)
        {
            _logger.LogError(
                "SubmitUrgentPrompt: branch/project mismatch — runtime {RuntimeId} on branch {BranchId} belongs to project {RuntimeProjectId}, but conversation {ConversationId} is on project {ConversationProjectId}. Aborting.",
                runtime.Id, conversation.BranchId, runtime.ProjectId, conversation.Id, conversation.ProjectId);
            return Result.Failure<SubmitUrgentPromptResponse>(
                "Runtime/conversation project mismatch");
        }

        // The "current" session on the runtime — Running OR already Canceling.
        // Both occupy the runtime: a Canceling session is still draining the
        // in-flight turn until the daemon emits turn_canceled. There can only
        // be one such session per runtime by invariant (Card 2's enqueue path
        // queues new sessions whenever any active session exists).
        var current = await _db.AgentSessions
            .FirstOrDefaultAsync(s => s.RuntimeId == runtime.Id
                && (s.Status == AgentSessionStatus.Running
                    || s.Status == AgentSessionStatus.Canceling), cancellationToken);

        // Branch on idle vs busy. Idle path mirrors ITurnDispatcher's idle
        // branch — Dispatch() the new session immediately and StartTurn over
        // SignalR. Busy path is the actual urgent-preempt flow.
        var nowUtc = DateTime.UtcNow;

        var projectDefaults = await _db.Projects
            .AsNoTracking()
            .Where(p => p.Id == conversation.ProjectId)
            .Select(p => new
            {
                p.ModelId,
                ModelSlug = p.Model != null && p.Model.IsActive ? p.Model.Slug : null,
            })
            .FirstOrDefaultAsync(cancellationToken);
        if (projectDefaults is null)
        {
            return Result.Failure<SubmitUrgentPromptResponse>("Project not found");
        }

        string? resolvedModelSlug = null;
        Guid? resolvedModelId = null;
        if (request.ModelId is { } sessionModelId)
        {
            resolvedModelSlug = await _db.CursorModels
                .AsNoTracking()
                .Where(m => m.Id == sessionModelId && m.IsActive)
                .Select(m => m.Slug)
                .FirstOrDefaultAsync(cancellationToken);
            if (resolvedModelSlug is not null)
            {
                resolvedModelId = sessionModelId;
            }
        }
        if (resolvedModelSlug is null)
        {
            resolvedModelSlug = projectDefaults.ModelSlug;
            resolvedModelId = projectDefaults.ModelId;
        }

        // daemon-git-sync-redesign: compute pullBeforeStart BEFORE adding the
        // new session row, so the new session itself doesn't count as "prior".
        // True iff no AgentSession yet exists on this runtime — daemon will
        // run `git pull --ff-only` once before the first SDK invocation, then
        // never again on this runtime (volume becomes the source of truth).
        var pullBeforeStart = !await _db.AgentSessions
            .AnyAsync(s => s.RuntimeId == runtime.Id, cancellationToken);

        // Scene 5 diagnostic — same as ITurnDispatcher.
        var sessionCountForRuntime = await _db.AgentSessions
            .CountAsync(s => s.RuntimeId == runtime.Id, cancellationToken);
        _logger.LogInformation(
            "SubmitUrgentPrompt: runtime {RuntimeId} pullBeforeStart={PullBeforeStart} (existing AgentSessions count={SessionCount}).",
            runtime.Id, pullBeforeStart, sessionCountForRuntime);

        var session = new AgentSession
        {
            ConversationId = conversation.Id,
            RuntimeId = runtime.Id,
            Prompt = request.Prompt,
            Status = AgentSessionStatus.Pending,
            ModelId = resolvedModelId,
        };
        _db.AgentSessions.Add(session);

        // Seed the PromptReceived audit row at sequence 0 — same shape as the
        // shared dispatcher writes, so frontend / replay paths see identical
        // history regardless of which entry point the prompt came in through.
        // Cursor-native schema: Kind = PromptReceived, prompt body lives in
        // the first-class Text column.
        var promptEvent = new AgentEvent
        {
            SessionId = session.Id,
            Sequence = 0,
            Kind = AgentEventKind.PromptReceived,
            Text = request.Prompt,
            CreatedAt = nowUtc,
        };
        _db.AgentEvents.Add(promptEvent);

        // Denormalized counters on the parent conversation. Same maintenance
        // as TurnDispatcher — keeps the conversation list query cheap.
        conversation.LastActivityAt = nowUtc;
        conversation.EventCount += 1;

        // chat-file-attachments — stamp this conversation's draft attachments
        // (uploaded, not yet associated with a turn) onto the new urgent
        // session, mirroring TurnDispatcher. Persisted by the SaveChanges below.
        var attachments = await _db.Attachments
            .Where(a => a.ConversationId == conversation.Id
                && a.UploadedAt != null
                && a.SessionId == null)
            .OrderBy(a => a.CreatedAt)
            .ToListAsync(cancellationToken);

        foreach (var att in attachments)
        {
            att.SessionId = session.Id;
        }

        // Build the typed DTO snapshot for the broadcast — identical shape to
        // what TurnDispatcher emits, so replay / live views are uniform.
        var promptDto = new PromptReceivedEventDto(
            SessionId: session.Id,
            Sequence: 0,
            CreatedAt: nowUtc,
            Text: request.Prompt);

        session.RecordEventEmitted(new AgentEventEmitted(
            ConversationId: conversation.Id,
            ProjectId: conversation.ProjectId,
            BranchId: conversation.BranchId,
            Kind: AgentEventKind.PromptReceived,
            Event: promptDto,
            OccurredAt: nowUtc));

        bool queued;
        int? queuePosition;
        Guid? canceledSessionId;
        bool fanOutCancel;

        if (current is null)
        {
            // Idle runtime → dispatch immediately. No preempt, no queue shift.
            session.Dispatch();
            queued = false;
            queuePosition = null;
            canceledSessionId = null;
            fanOutCancel = false;
        }
        else
        {
            // Busy runtime → preempt the current session and head-queue the urgent.
            // MarkCanceling is idempotent on already-Canceling, so a runtime
            // whose current session is already draining doesn't double-fan-out
            // the CancelTurn or double-raise SessionCancelRequested.
            var wasAlreadyCanceling = current.Status == AgentSessionStatus.Canceling;
            current.MarkCanceling(PreemptReason);

            // Shift every existing Pending+queued session by +1 so the urgent
            // session can claim position 1. EF tracks each mutation; one
            // SaveChanges below flushes them as a single Postgres batch.
            var existingQueued = await _db.AgentSessions
                .Where(s => s.RuntimeId == runtime.Id
                    && s.Status == AgentSessionStatus.Pending
                    && s.QueuePosition != null
                    && s.Id != session.Id)
                .ToListAsync(cancellationToken);
            foreach (var s in existingQueued)
            {
                s.QueuePosition = (s.QueuePosition ?? 0) + 1;
            }

            // Head of the queue — the dispatch-next handler will pick this up
            // when the canceled session terminates.
            session.Enqueue(1);

            queued = true;
            queuePosition = 1;
            canceledSessionId = current.Id;
            // Skip the CancelTurn fan-out if the session was already Canceling
            // — the original cancel command already sent it; another would be
            // a duplicate and could confuse the daemon.
            fanOutCancel = !wasAlreadyCanceling;
        }

        await _db.SaveChangesAsync(cancellationToken);

        // SignalR fan-out — best-effort, OUTSIDE the SaveChanges scope. Both
        // paths log warn on failure: the DB is in the right state regardless,
        // and the orphan janitor (Card 8) reaps stuck sessions.
        if (current is null)
        {
            // Idle path: push StartTurn directly — same as ITurnDispatcher's
            // idle branch.
            try
            {
                // chat-file-attachments — augment the daemon-bound prompt with
                // the per-file path block; the stored Prompt stays the raw
                // user text. Empty list → byte-for-byte passthrough.
                var promptForAgent = PromptPrefixBuilder.BuildPromptWithAttachments(
                    request.Prompt, attachments);

                var startPayload = new StartTurnPayload(
                    SessionId: session.Id,
                    ConversationId: conversation.Id,
                    Prompt: promptForAgent,
                    Model: resolvedModelSlug,
                    PullBeforeStart: pullBeforeStart);

                await _runtimeHub.Clients
                    .Group($"runtime-{runtime.Id}")
                    .StartTurn(startPayload);

                _logger.LogInformation(
                    "SubmitUrgentPrompt: idle runtime {RuntimeId}; dispatched StartTurn for urgent session {SessionId} (conversation {ConversationId}, user {ActorUserId}).",
                    runtime.Id, session.Id, conversation.Id, request.ActorUserId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "SubmitUrgentPrompt: StartTurn fan-out failed for urgent session {SessionId} on runtime {RuntimeId}; session is Running in DB. Janitor will reap if the daemon never picks it up.",
                    session.Id, runtime.Id);
            }
        }
        else if (fanOutCancel)
        {
            // Busy path: push CancelTurn so the daemon drains the in-flight
            // turn. The dispatch-next handler will fire StartTurn for the
            // urgent session once the canceled session reaches a terminal
            // state.
            try
            {
                var cancelPayload = new CancelTurnPayload(
                    SessionId: current.Id,
                    Reason: PreemptReason);

                await _runtimeHub.Clients
                    .Group($"runtime-{runtime.Id}")
                    .CancelTurn(cancelPayload);

                _logger.LogInformation(
                    "SubmitUrgentPrompt: preempted session {CanceledSessionId} on runtime {RuntimeId}; queued urgent session {SessionId} at position 1 (conversation {ConversationId}, user {ActorUserId}).",
                    current.Id, runtime.Id, session.Id, conversation.Id, request.ActorUserId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "SubmitUrgentPrompt: CancelTurn fan-out failed for session {CanceledSessionId} on runtime {RuntimeId}; session is Canceling in DB. Janitor will reap if the daemon never confirms.",
                    current.Id, runtime.Id);
            }
        }
        else
        {
            // Busy path, current already Canceling — no extra fan-out needed.
            _logger.LogInformation(
                "SubmitUrgentPrompt: current session {CanceledSessionId} on runtime {RuntimeId} was already Canceling; queued urgent session {SessionId} at position 1 without re-dispatching CancelTurn (conversation {ConversationId}, user {ActorUserId}).",
                current.Id, runtime.Id, session.Id, conversation.Id, request.ActorUserId);
        }

        // Manual publish AFTER SaveChanges — the audit row should never claim
        // a preempt that didn't commit. Runtime-scoped event, no owning
        // entity row, mirrors the ReorderQueueCommand pattern.
        await _mediator.Publish(
            new SessionUrgentPreempted(
                NewSessionId: session.Id,
                CanceledSessionId: canceledSessionId,
                RuntimeId: runtime.Id,
                ActorUserId: request.ActorUserId),
            cancellationToken);

        return Result.Success(new SubmitUrgentPromptResponse(
            SessionId: session.Id,
            CanceledSessionId: canceledSessionId,
            Queued: queued,
            QueuePosition: queuePosition));
    }
}
