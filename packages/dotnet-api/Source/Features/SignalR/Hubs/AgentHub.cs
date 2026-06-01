using System.Security.Claims;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Source.Features.Conversations.Commands;
using Source.Features.Conversations.Models;
using Source.Features.Conversations.Services;
using Source.Features.FlyManagement;
using Source.Features.RuntimeBootstrap.Contracts;
using Source.Features.RuntimeLifecycle.Commands;
using Source.Features.RuntimeLifecycle.Models;
using Source.Features.RuntimePresets.Contracts;
using Source.Features.SignalR.Contracts;
using Source.Features.SignalR.Services;
using Source.Infrastructure;
using Source.Infrastructure.AuthorizationModels;
using Source.Infrastructure.Extensions;
using TypedSignalR.Client;

namespace Source.Features.SignalR.Hubs;

/// <summary>
/// React-facing hub. One connection per browser tab. JWT-authenticated via the
/// existing user identity setup; on connect we auto-join the per-user group so
/// the platform can target broadcasts at the user without a method invocation.
///
/// <para>Per the signalr-architecture spec this hub stays thin: lifecycle hooks
/// + the user-driven <c>SubmitPrompt</c> entry point. <c>CancelTurn</c> and
/// the replay request/response method land in subsequent cards.</para>
/// </summary>
[Authorize]
public class AgentHub : Hub<IAgentClient>, IAgentHub
{
    private readonly ApplicationDbContext _db;
    private readonly IHubContext<RuntimeHub, IRuntimeClient> _runtimeHub;
    private readonly ITurnDispatcher _turnDispatcher;
    private readonly IMediator _mediator;
    private readonly IAgentSecretsResolver _secretsResolver;
    private readonly ILogger<AgentHub> _logger;

    public AgentHub(
        ApplicationDbContext db,
        IHubContext<RuntimeHub, IRuntimeClient> runtimeHub,
        ITurnDispatcher turnDispatcher,
        IMediator mediator,
        IAgentSecretsResolver secretsResolver,
        ILogger<AgentHub> logger)
    {
        _db = db;
        _runtimeHub = runtimeHub;
        _turnDispatcher = turnDispatcher;
        _mediator = mediator;
        _secretsResolver = secretsResolver;
        _logger = logger;
    }

    public override async Task OnConnectedAsync()
    {
        var userId = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
        {
            // [Authorize] should have already rejected this, but belt-and-braces:
            // a connection with no NameIdentifier claim cannot be safely placed
            // into a per-user group, so abort rather than fan out anything.
            _logger.LogWarning("AgentHub connection {ConnectionId} authenticated but missing NameIdentifier claim; aborting.", Context.ConnectionId);
            Context.Abort();
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, $"user-{userId}");

        // Branch group membership for live broadcasts. The frontend ships
        // projectId AND branchId on the negotiate query string for a branch-
        // scoped tab. Branch determines group membership (so live AgentEvent
        // broadcasts only reach tabs on the same branch — sibling-branch tabs
        // don't see each other's chat).
        //
        // GetHttpContext() is an extension method that dereferences Features
        // and can NRE on a context whose Features bag isn't wired (notably in
        // unit tests with a hand-rolled mock). Wrap defensively so a stripped
        // test context can't break the connect path — production transports
        // always have a populated HttpContext on the negotiate handshake.
        //
        // NOTE: this hub used to also auto-wake the runtime on connect, but
        // that caused the bad UX of "opening a stale conversation spins up a
        // VM the user didn't ask for". Wake is now only triggered by
        //   (a) SubmitPrompt — see the wake-on-Suspended block below, or
        //   (b) the user explicitly clicking Restart — see RestartRuntimeHandler,
        //       which routes Suspended through WakeRuntimeOnConnectCommand.
        string? branchIdRaw = null;
        string? projectIdRaw = null;
        try
        {
            var query = Context.GetHttpContext()?.Request.Query;
            branchIdRaw = query?["branchId"].ToString();
            projectIdRaw = query?["projectId"].ToString();
        }
        catch (NullReferenceException)
        {
            // No HTTP context (unusual transport / test context).
        }

        // Join the branch group if we got one. Branch is the unit of live
        // broadcast routing for AgentEvent / RuntimeStateChanged after
        // CopyBranch — a project may own N branches, each with its own
        // runtime + conversations, and the React tab is scoped to exactly
        // one of them. Missing branchId is logged as a warning (the tab won't
        // receive live ticks but REST history still works).
        if (Guid.TryParse(branchIdRaw, out var branchId))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, $"branch-{branchId}");
        }
        else
        {
            _logger.LogWarning(
                "AgentHub connection {ConnectionId} (user {UserId}) negotiated without a valid branchId query parameter; live AgentEvent broadcasts will not be delivered to this tab.",
                Context.ConnectionId, userId);
        }

        // Join the project group when projectId is on the negotiate query so
        // project-scoped broadcasts (RuntimeProposalCreated / Updated,
        // ConversationRenamed, RuntimeStateChanged fan-outs, etc.) reach every
        // open tab on this project — not just the super-admin runtime page
        // which previously was the only surface that received them by virtue of
        // its own out-of-band hub instance.
        //
        // Without this, the workspace chat canvas would never see the
        // RuntimeProposalCreated event when the agent submits a proposal via
        // propose_runtime_spec — users would only see the agent's chat text
        // asking for approval, with no UI affordance to actually approve.
        //
        // Missing projectId is harmless: the connection still gets the user
        // and (if present) branch groups, so non-project-scoped broadcasts
        // still flow.
        if (Guid.TryParse(projectIdRaw, out var projectId))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, $"project-{projectId}");
        }

        _logger.LogInformation("AgentHub connected. User {UserId}, Connection {ConnectionId}", userId, Context.ConnectionId);
        await base.OnConnectedAsync();
    }

    /// <summary>
    /// User-facing entry point for "I typed a prompt; run a turn." The hub:
    ///
    /// <list type="number">
    ///   <item>Authenticates via the connection's user claim (<c>[Authorize]</c>
    ///         on the class enforces presence; we re-check NameIdentifier for
    ///         belt-and-braces).</item>
    ///   <item>Validates the prompt: non-empty, ≤50_000 chars. Throws
    ///         <see cref="HubException"/> on failure — the message lands on
    ///         the JS client and surfaces as a toast / inline error.</item>
    ///   <item>Resolves the parent <see cref="Conversation"/> — either loads
    ///         the one the client referenced (and verifies it belongs to the
    ///         same project) or creates a new one with a title derived from
    ///         the first 80 chars of the prompt.</item>
    ///   <item>Looks up the project's <see cref="ProjectRuntime"/>. The
    ///         runtime must be <see cref="RuntimeState.Online"/> to dispatch.
    ///         TODO(soft-queue): enqueue when not Online instead of failing.</item>
    ///   <item>For continuations, captures the resume hint by reading the
    ///         most recent <see cref="AgentSessionStatus.Succeeded"/> session's
    ///         <see cref="AgentSession.ClaudeSessionId"/> on the same conversation.</item>
    ///   <item>Creates a fresh <see cref="AgentSession"/> in
    ///         <see cref="AgentSessionStatus.Pending"/> — the daemon walks it
    ///         to <see cref="AgentSessionStatus.Running"/> on TurnStarted.</item>
    ///   <item>Inserts the <c>PromptReceived</c> <see cref="AgentEvent"/> at
    ///         <c>Sequence = 0</c> — this is the first event of the session.</item>
    ///   <item>Bumps the conversation's <see cref="Conversation.LastActivityAt"/>
    ///         and <see cref="Conversation.EventCount"/> denormalized counters
    ///         so the conversation list view doesn't need a join.</item>
    ///   <item>Raises <see cref="AgentEventEmitted"/> on the session entity so
    ///         the <c>DomainEventInterceptor</c> publishes after commit and
    ///         <c>BroadcastAgentEventHandler</c> pushes to other connected
    ///         tabs in the <c>project-{projectId}</c> group.</item>
    ///   <item>Single <c>SaveChangesAsync</c>, then dispatches
    ///         <see cref="StartTurnPayload"/> to the runtime group via
    ///         cross-hub <see cref="IHubContext{THub, T}"/>. Save first so
    ///         the session row exists before the daemon's first
    ///         <c>EmitEvent</c> can land.</item>
    /// </list>
    ///
    /// <para><b>Error model.</b> All client-visible failures throw
    /// <see cref="HubException"/> — the SignalR convention to surface the
    /// message back to the JS invocation. Internal logic errors go through
    /// the logger and would surface as a generic "invocation failed" client-side.</para>
    ///
    /// <para><b>Auth scope.</b> Today we only verify the caller is authenticated.
    /// Per-project ownership belongs to the future Project entity / membership
    /// model — TODO(project-ownership) below.</para>
    /// </summary>
    public async Task<SubmitPromptResponse> SubmitPrompt(SubmitPromptPayload payload)
    {
        var userId = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
        {
            // [Authorize] should have rejected this — defense in depth.
            throw new HubException("Unauthenticated");
        }

        if (string.IsNullOrWhiteSpace(payload.Text))
        {
            throw new HubException("Prompt cannot be empty");
        }
        if (payload.Text.Length > 50_000)
        {
            throw new HubException("Prompt too long (max 50000 chars)");
        }

        // BranchId is now a real FK to ProjectBranch (promoted in the
        // e2e-smoketest spec) — the client must pass the id of an existing
        // branch row, typically the project's default. Resolution / validation
        // of "does this branch belong to the project?" lands in card 3+ when
        // the conversation creation moves to a command handler with the
        // branch lookup baked in.
        var branchId = payload.BranchId;

        // TODO(project-ownership): when the Project entity lands, verify
        // userId owns / has access to payload.ProjectId. For now we accept
        // any authenticated user — admin / dev mode.

        // Resolve or create the parent conversation.
        Conversation conversation;
        if (payload.ConversationId.HasValue)
        {
            var found = await _db.Conversations
                .FirstOrDefaultAsync(c => c.Id == payload.ConversationId.Value);
            if (found is null)
            {
                throw new HubException("Conversation not found");
            }
            if (found.ProjectId != payload.ProjectId)
            {
                throw new HubException("Conversation does not belong to this project");
            }
            conversation = found;
        }
        else
        {
            // New conversation — title from first 80 chars of prompt.
            var title = payload.Text.Length > 80
                ? payload.Text.Substring(0, 77) + "..."
                : payload.Text;
            conversation = new Conversation
            {
                ProjectId = payload.ProjectId,
                BranchId = branchId,
                Title = title,
                Status = ConversationStatus.Active,
                LastActivityAt = DateTime.UtcNow,
                EventCount = 0,
            };
            _db.Conversations.Add(conversation);
        }

        // Runtime must exist and be in a state that can either accept a turn
        // now (Online) or eventually reach Online so a queued prompt can be
        // drained (Pending / Booting / Bootstrapping / Suspending / Suspended /
        // Waking). Terminal-ish states (Failed / Crashed / Deleting / Deleted)
        // reject the prompt — there's no future Online transition coming.
        //
        // Filter by BranchId (not ProjectId) because a project owns one
        // ProjectRuntime per branch after CopyBranch. Filtering by project
        // alone returns an arbitrary sibling runtime → the dispatcher stamps
        // session.RuntimeId with the wrong machine → cross-branch corruption.
        var runtime = await _db.ProjectRuntimes
            .FirstOrDefaultAsync(r => r.BranchId == branchId);
        if (runtime is null)
        {
            throw new HubException("No runtime for this branch");
        }

        // Distinguish queueable not-Online states from rejection states. The
        // DispatchQueuedSessionsOnRuntimeOnlineHandler will drain the soft-
        // queue once the runtime hits Online, so for the queueable set we
        // calmly enqueue instead of throwing.
        var forceQueueForNotOnline = runtime.State is
            RuntimeState.Pending
            or RuntimeState.Booting
            or RuntimeState.Bootstrapping
            or RuntimeState.Suspending
            or RuntimeState.Suspended
            or RuntimeState.Waking;

        if (runtime.State != RuntimeState.Online && !forceQueueForNotOnline)
        {
            // Failed / Crashed / Deleting / Deleted — no future Online edge to
            // hang the queued prompt off, so fail fast with the same message
            // shape the chat UI already knows.
            throw new HubException(
                $"Runtime is in state {runtime.State}, must be Online to start a turn.");
        }

        var cursorApiKey = await _secretsResolver.ResolveCursorApiKeyAsync(payload.ProjectId, Context.ConnectionAborted);
        if (string.IsNullOrWhiteSpace(cursorApiKey))
        {
            _logger.LogWarning(
                "AgentHub.SubmitPrompt: rejecting prompt for project {ProjectId} — no Cursor API key configured.",
                payload.ProjectId);
            throw new HubException(
                "No Cursor API key configured for this project. Add CURSOR_API_KEY in project settings or backend environment.");
        }

        string? agentId = null;
        if (payload.ConversationId.HasValue)
        {
            agentId = await _db.AgentSessions
                .Where(s => s.ConversationId == conversation.Id
                    && s.Status == AgentSessionStatus.Succeeded
                    && s.AgentId != null)
                .OrderByDescending(s => s.CreatedAt)
                .Select(s => s.AgentId)
                .FirstOrDefaultAsync();
        }

        // If the conversation row was newly added above, EF needs to flush it
        // before the dispatcher's own change tracker sees it. The dispatcher
        // does its own SaveChangesAsync, so we just persist the conversation
        // creation here and let the dispatcher's transaction add the session
        // + event in a second commit. The order is still safe — runtime
        // dispatch happens AFTER the dispatcher's save.
        if (!payload.ConversationId.HasValue)
        {
            await _db.SaveChangesAsync();
        }

        // Hand off to the shared dispatcher. Same path the self-heal flow on
        // RuntimeHub uses, so the audit-event shape, counter bumps, domain
        // event raise, and StartTurn dispatch ordering are identical for both
        // user-typed and daemon-driven turn starts. ForceQueue is set when the
        // runtime isn't Online yet so the dispatcher persists the session as
        // Pending+queued without firing StartTurn — the queue-drain handler
        // takes over once the runtime reaches Online.
        var dispatch = await _turnDispatcher.DispatchTurnAsync(new DispatchTurnArgs(
            ConversationId: conversation.Id,
            ProjectId: conversation.ProjectId,
            BranchId: conversation.BranchId,
            Prompt: payload.Text,
            AgentId: agentId,
            EventOriginUserId: userId,
            ForceQueue: forceQueueForNotOnline,
            ModelId: payload.ModelId,
            Yolo: payload.Yolo));

        // Suspended runtime → nudge the wake so the queued prompt gets
        // executed rather than stuck behind a sleeping machine. The wake
        // command is idempotent on non-Suspended states (it returns
        // NotApplicable). This is one of only two paths that wake a runtime
        // (the other being the user explicitly clicking Restart, which routes
        // through RestartRuntimeHandler) — OnConnectedAsync used to also auto-
        // wake on tab connect but that produced the bad UX of "opening an old
        // conversation spins up a VM the user didn't ask for", so it was
        // removed. We call wake AFTER the session has been queued so the
        // queue-drain handler has a row to pick up the moment Online lands.
        if (runtime.State == RuntimeState.Suspended)
        {
            try
            {
                var wake = await _mediator.Send(new WakeRuntimeOnConnectCommand(payload.ProjectId, payload.BranchId));
                if (wake.IsFailure)
                {
                    // Don't fail the prompt — it's persisted as Pending and
                    // a subsequent transition into Online (operator reset,
                    // reconciler nudge) will still drain it. Operator sees the
                    // warning.
                    _logger.LogWarning(
                        "AgentHub.SubmitPrompt: WakeRuntimeOnConnect failed while queuing prompt for project {ProjectId}: {Error}",
                        payload.ProjectId, wake.Error);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "AgentHub.SubmitPrompt: WakeRuntimeOnConnect threw while queuing prompt for project {ProjectId}; session is queued and will dispatch when runtime reaches Online.",
                    payload.ProjectId);
            }
        }

        _logger.LogInformation(
            "AgentHub.SubmitPrompt: user {UserId} dispatched StartTurn for session {SessionId} (conversation {ConversationId}, runtime {RuntimeId}, runtimeState={RuntimeState}, queued={Queued}, queuePosition={QueuePosition}).",
            userId, dispatch.SessionId, conversation.Id, runtime.Id, runtime.State, dispatch.Queued, dispatch.QueuePosition);

        return new SubmitPromptResponse(
            ConversationId: conversation.Id,
            SessionId: dispatch.SessionId,
            Queued: dispatch.Queued,
            QueuePosition: dispatch.QueuePosition);
    }

    /// <summary>
    /// User-facing entry point for "I clicked stop; abort the in-flight turn."
    /// The hub:
    ///
    /// <list type="number">
    ///   <item>Authenticates via the connection's user claim.</item>
    ///   <item>Looks up the <see cref="AgentSession"/> together with its parent
    ///         <see cref="Conversation"/> (so we can reach <see cref="Conversation.ProjectId"/>).</item>
    ///   <item>If the session is unknown, silently no-ops — the client may have
    ///         a stale id and there's no useful work to do.</item>
    ///   <item>Only sessions in <see cref="AgentSessionStatus.Running"/> are
    ///         cancelable. <see cref="AgentSessionStatus.Pending"/> is queued
    ///         (soft-queue lands later); <see cref="AgentSessionStatus.Succeeded"/>,
    ///         <see cref="AgentSessionStatus.Failed"/>, <see cref="AgentSessionStatus.Canceled"/>
    ///         are terminal — no-op for all of them.</item>
    ///   <item>Resolves the project's <see cref="ProjectRuntime"/>. If gone,
    ///         logs a warning and returns — there is no daemon to cancel.</item>
    ///   <item>Dispatches a <see cref="CancelTurnPayload"/> to the
    ///         <c>runtime-{runtimeId}</c> group. The daemon does the actual abort
    ///         and emits a <c>TurnCanceled</c> event via <c>RuntimeHub.EmitEvent</c>;
    ///         that path transitions the session to <see cref="AgentSessionStatus.Canceled"/>.</item>
    /// </list>
    ///
    /// <para><b>Why no status mutation here.</b> The session's terminal state
    /// is owned by the daemon's event stream. If we transitioned to Canceled
    /// optimistically, a race with a slow daemon could let a TurnCompleted
    /// event land after the user clicked stop, and we'd have a half-truth in
    /// the audit log. Letting the daemon drive the transition keeps the event
    /// log linear and the status accurate.</para>
    /// </summary>
    public async Task CancelTurn(CancelTurnRequest payload)
    {
        var userId = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
        {
            // [Authorize] should have rejected this — defense in depth.
            throw new HubException("Unauthenticated");
        }

        // Delegate to CancelSessionCommand — the single source of truth for
        // session cancellation. It handles all three cases correctly:
        //   • Pending  → MarkCanceled (drops from queue, raises
        //                AgentSessionTerminated so DispatchNextSessionHandler
        //                advances the runtime's queue). This is what makes
        //                "revoke a queued message" work end-to-end.
        //   • Running  → MarkCanceling + push CancelTurn to the daemon group.
        //                Daemon's terminal turn_canceled event flips Canceling →
        //                Canceled (kept off this code path so the audit log
        //                stays linear — see CancelSessionCommand's comments).
        //   • Canceling / Canceled / Succeeded / Failed → idempotent no-op.
        //
        // The "session not found" Failure is logged as Debug below (matches
        // the previous silent no-op behavior — clients with stale ids).
        //
        // TODO(project-ownership): when the Project entity lands, verify
        // userId owns / has access to session.Conversation.ProjectId BEFORE
        // dispatching the command, so unauthorized users can't cancel
        // arbitrary sessions.
        var result = await _mediator.Send(new CancelSessionCommand(payload.SessionId, "user_canceled"));

        if (!result.IsSuccess)
        {
            // Only failure path today is "Session not found" — stale client id.
            _logger.LogDebug(
                "CancelTurn: command failed for session {SessionId}: {Error}",
                payload.SessionId, result.Error);
            return;
        }

        _logger.LogInformation(
            "AgentHub.CancelTurn: user {UserId} canceled session {SessionId} → {FinalStatus}.",
            userId, payload.SessionId, result.Value!.FinalStatus);
    }

    /// <summary>
    /// React-facing entry point for "I (re)connected; give me events I missed."
    /// Used after a network blip or a fresh tab load so the chat panel can
    /// stitch its in-memory log back together without dropping rows.
    ///
    /// <list type="number">
    ///   <item>Authenticates via the connection's user claim.</item>
    ///   <item>If the session does not exist, returns an empty list — replay
    ///         is best-effort. The client uses other state to figure out
    ///         whether the session is gone.</item>
    ///   <item>Returns events with <c>Sequence &gt; SinceSequence</c>, ordered
    ///         ASC, capped at <c>MaxLimit</c>. Joins <see cref="AgentSession"/>
    ///         once to project the parent <see cref="Conversation.Id"/> onto
    ///         each <see cref="AgentEventNotification"/> — same record type
    ///         the live broadcast handler emits, so the JS client uses one
    ///         code path for live + replayed events.</item>
    /// </list>
    ///
    /// <para><b>Hard cap.</b> 1000 rows per call. If a client genuinely missed
    /// more than that it should reload the conversation via REST instead of
    /// streaming the entire history through this hub. Cheap safety net against
    /// runaway replays.</para>
    /// </summary>
    public async Task<List<AgentEventNotification>> RequestEventReplay(EventReplayRequest payload)
    {
        var userId = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
        {
            // [Authorize] should have rejected this — defense in depth.
            throw new HubException("Unauthenticated");
        }

        // TODO(project-ownership): when the Project entity lands, verify
        // userId owns / has access to the conversation behind this session.
        // For now: existence check only. Unknown id → empty list (NOT throw)
        // because replay is best-effort; the client figures gone-sessions out
        // from other state.
        var sessionExists = await _db.AgentSessions.AnyAsync(s => s.Id == payload.SessionId);
        if (!sessionExists)
        {
            return new List<AgentEventNotification>();
        }

        const int MaxLimit = 1000;

        // Single-shot join: pull events + parent conversation id together so
        // the broadcast-shaped record is fully populated server-side.
        // Cursor-native schema (card 3): we ship the fully-projected
        // discriminated AgentEventDto inline so the client doesn't need a
        // REST refetch per row.
        var rows = await (
            from e in _db.AgentEvents
            join s in _db.AgentSessions on e.SessionId equals s.Id
            join c in _db.Conversations on s.ConversationId equals c.Id
            where e.SessionId == payload.SessionId
                && e.Sequence > payload.SinceSequence
            orderby e.Sequence
            select new
            {
                Event = e,
                ConversationId = s.ConversationId,
                ProjectId = c.ProjectId,
                BranchId = c.BranchId,
            })
            .Take(MaxLimit)
            .ToListAsync();

        return rows
            .Select(r => new AgentEventNotification(
                ConversationId: r.ConversationId,
                ProjectId: r.ProjectId,
                BranchId: r.BranchId,
                Event: ProjectAgentEvent(r.Event)))
            .ToList();
    }

    private static AgentEventDto ProjectAgentEvent(AgentEvent e)
    {
        return e.Kind switch
        {
            AgentEventKind.PromptReceived => new PromptReceivedEventDto(
                e.SessionId, e.Sequence, e.CreatedAt, e.Text ?? string.Empty),
            AgentEventKind.AssistantText => new AssistantTextEventDto(
                e.SessionId, e.Sequence, e.CreatedAt, e.Text ?? string.Empty),
            AgentEventKind.Thinking => new ThinkingEventDto(
                e.SessionId, e.Sequence, e.CreatedAt, e.Text ?? string.Empty, e.ThinkingDurationMs),
            AgentEventKind.ToolUse => new ToolUseEventDto(
                e.SessionId,
                e.Sequence,
                e.CreatedAt,
                CallId: e.CallId ?? string.Empty,
                Name: e.ToolName ?? string.Empty,
                Status: e.ToolStatus ?? AgentEventToolStatus.Running,
                Args: e.Args,
                Result: e.Result,
                ArgsTruncated: e.ArgsTruncated ?? false,
                ResultTruncated: e.ResultTruncated ?? false),
            AgentEventKind.Status => new StatusEventDto(
                e.SessionId,
                e.Sequence,
                e.CreatedAt,
                Status: e.RunStatus ?? AgentEventRunStatus.Creating,
                Message: e.StatusMessage),
            AgentEventKind.Task => new TaskEventDto(
                e.SessionId,
                e.Sequence,
                e.CreatedAt,
                TaskId: e.TaskId,
                Title: e.TaskTitle),
            _ => throw new InvalidOperationException(
                $"Unknown AgentEventKind {e.Kind} on session {e.SessionId} seq {e.Sequence}"),
        };
    }

    /// <summary>
    /// Subscribe this connection to the <c>workspace-{workspaceId}</c> group so
    /// the agent-native sidebar receives cross-project broadcasts (runtime
    /// state, agent turn lifecycle, conversation rename) for every project in
    /// the workspace — not just the one in the active tab.
    ///
    /// <para>Parallel to the project auto-join in <see cref="OnConnectedAsync"/>,
    /// but exposed as an explicit hub method because the workspace identity is
    /// known on the React side at sidebar mount, not necessarily on the negotiate
    /// query string.</para>
    ///
    /// <para><b>Auth.</b> Unlike the project auto-join (which still carries a
    /// TODO(project-ownership) gate), workspaces already have a real membership
    /// model — <see cref="Source.Features.Workspaces.Models.WorkspaceMembership"/>
    /// — so we verify membership here before adding the connection to the
    /// group. SuperAdmins bypass the check, matching the same convention used by
    /// <see cref="Source.Infrastructure.Workspaces.RequireWorkspaceRoleAttribute"/>.</para>
    /// </summary>
    public async Task JoinWorkspace(string workspaceId)
    {
        var userId = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
        {
            // [Authorize] should have rejected this — defense in depth.
            throw new HubException("Unauthenticated");
        }

        if (!Guid.TryParse(workspaceId, out var workspaceGuid))
        {
            throw new HubException("Invalid workspaceId");
        }

        // SuperAdmin bypasses membership — matches the RequireWorkspaceRole
        // attribute convention so the two auth paths stay aligned.
        var isSuperAdmin = Context.User?.IsInRole(RoleConstants.SuperAdmin) ?? false;
        if (!isSuperAdmin)
        {
            var isMember = await _db.WorkspaceMemberships
                .AsNoTracking()
                .AnyAsync(
                    m => m.WorkspaceId == workspaceGuid && m.UserId == userId,
                    Context.ConnectionAborted);
            if (!isMember)
            {
                throw new HubException("Not a member of this workspace");
            }
        }

        await Groups.AddToGroupAsync(
            Context.ConnectionId,
            $"workspace-{workspaceGuid}",
            Context.ConnectionAborted);

        _logger.LogInformation(
            "AgentHub.JoinWorkspace: connection {ConnectionId} (user {UserId}) joined workspace-{WorkspaceId}.",
            Context.ConnectionId, userId, workspaceGuid);
    }

    /// <summary>
    /// Symmetric counterpart to <see cref="JoinWorkspace"/>. Idempotent — SignalR's
    /// <c>RemoveFromGroupAsync</c> is a no-op if the connection wasn't a member.
    /// No auth check: removing yourself from a group you may already have left
    /// is harmless, and forcing a DB round-trip on disconnect would be wasteful.
    /// </summary>
    public async Task LeaveWorkspace(string workspaceId)
    {
        if (!Guid.TryParse(workspaceId, out var workspaceGuid))
        {
            throw new HubException("Invalid workspaceId");
        }

        await Groups.RemoveFromGroupAsync(
            Context.ConnectionId,
            $"workspace-{workspaceGuid}",
            Context.ConnectionAborted);

        _logger.LogDebug(
            "AgentHub.LeaveWorkspace: connection {ConnectionId} left workspace-{WorkspaceId}.",
            Context.ConnectionId, workspaceGuid);
    }

    /// <summary>
    /// User-facing relay for "I clicked one of the four approval-card actions"
    /// on an in-flight <c>canUseTool</c> request from the daemon. The hub:
    ///
    /// <list type="number">
    ///   <item>Authenticates via the connection's user claim.</item>
    ///   <item>Pulls the project id off the connection — the React tab joined
    ///         <c>project-{projectId}</c> on connect when it negotiated with a
    ///         <c>projectId</c> query string, so the hub can read it back from
    ///         the same source without trusting client-supplied routing.</item>
    ///   <item>Resolves the project's active <see cref="ProjectRuntime"/>. If
    ///         the runtime is gone (deleted, terminal), logs and no-ops — the
    ///         daemon will eventually time out its canUseTool wait.</item>
    ///   <item>Forwards the resolution onto the <c>runtime-{runtimeId}</c>
    ///         group via the typed <see cref="IRuntimeClient.PermissionResolved"/>
    ///         channel. Same dispatch pattern as <see cref="CancelTurn"/>.</item>
    /// </list>
    ///
    /// <para><b>No persistence here.</b> Per the spec we deliberately do NOT
    /// write "always allow" to project config — it lives only for the SignalR
    /// session, owned by the daemon's in-memory <c>PermissionGateway</c>. This
    /// method is pure wire.</para>
    ///
    /// <para><b>Correlation.</b> The daemon looks up its in-memory pending
    /// waiter by <see cref="ResolvePermissionPayload.ToolUseId"/>; the hub
    /// itself does not validate the id (the daemon already enforces uniqueness
    /// per SDK call), so a stale id from the client surfaces as a silent
    /// no-op daemon-side, not a hub-level error.</para>
    /// </summary>
    public async Task ResolvePermission(ResolvePermissionPayload payload)
    {
        var userId = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
        {
            // [Authorize] should have rejected this — defense in depth.
            throw new HubException("Unauthenticated");
        }

        // Project routing comes from the connection state populated in
        // OnConnectedAsync (the negotiate query string), not from the payload —
        // the client can't trick the server into addressing another project's
        // runtime by lying on the wire.
        string? projectIdRaw = null;
        try
        {
            projectIdRaw = Context.GetHttpContext()?.Request.Query["projectId"].ToString();
        }
        catch (NullReferenceException)
        {
            // No HTTP context (unusual transport / test). Fall through to the
            // empty-id check below — the resolve simply can't be routed.
        }
        if (!Guid.TryParse(projectIdRaw, out var projectId))
        {
            _logger.LogWarning(
                "AgentHub.ResolvePermission: connection {ConnectionId} (user {UserId}) has no projectId on its negotiate query; dropping decision {Decision} for toolUseId {ToolUseId}.",
                Context.ConnectionId, userId, payload.Decision, payload.ToolUseId);
            throw new HubException("Connection is not bound to a project");
        }

        // A project can own multiple ProjectRuntime rows after CopyBranch (one
        // per branch). Filtering by ProjectId alone would route the resolution
        // to an arbitrary sibling runtime — the WRONG Fly machine — and the
        // daemon waiter on the right branch would time out while the wrong
        // daemon receives an unmatched ToolUseId.
        //
        // The payload echoes the originating PermissionRequested's
        // ConversationId, so we use the conversation row to resolve the branch
        // and from there the correct runtime. If ConversationId is missing
        // (defense in depth — should never happen on the happy path) we cannot
        // safely route and drop the decision.
        if (payload.ConversationId is null)
        {
            _logger.LogWarning(
                "AgentHub.ResolvePermission: connection {ConnectionId} (user {UserId}) sent decision {Decision} for toolUseId {ToolUseId} without a ConversationId; cannot resolve target branch / runtime.",
                Context.ConnectionId, userId, payload.Decision, payload.ToolUseId);
            return;
        }

        var conversation = await _db.Conversations
            .FirstOrDefaultAsync(c => c.Id == payload.ConversationId.Value);
        if (conversation is null)
        {
            _logger.LogWarning(
                "AgentHub.ResolvePermission: conversation {ConversationId} not found (user {UserId}); decision {Decision} for toolUseId {ToolUseId} cannot be delivered.",
                payload.ConversationId.Value, userId, payload.Decision, payload.ToolUseId);
            return;
        }

        // Sanity check: the conversation must belong to the project the
        // connection is bound to. A mismatch would mean a malicious or buggy
        // client invoked ResolvePermission with a ConversationId from a
        // different project than its negotiate query string — refuse to route.
        if (conversation.ProjectId != projectId)
        {
            _logger.LogWarning(
                "AgentHub.ResolvePermission: conversation {ConversationId} belongs to project {ConversationProjectId}, but connection (user {UserId}) is bound to project {ProjectId}; refusing to route decision {Decision} for toolUseId {ToolUseId}.",
                payload.ConversationId.Value, conversation.ProjectId, userId, projectId, payload.Decision, payload.ToolUseId);
            return;
        }

        var runtime = await _db.ProjectRuntimes
            .FirstOrDefaultAsync(r => r.BranchId == conversation.BranchId);
        if (runtime is null)
        {
            // No live runtime — daemon will time out its canUseTool waiter
            // on its own. Log and return; nothing actionable.
            _logger.LogWarning(
                "AgentHub.ResolvePermission: no runtime for branch {BranchId} (project {ProjectId}, user {UserId}); decision {Decision} for toolUseId {ToolUseId} cannot be delivered.",
                conversation.BranchId, projectId, userId, payload.Decision, payload.ToolUseId);
            return;
        }

        // Targeted at the runtime group — single daemon connection lives in
        // there (per RuntimeHub.OnConnectedAsync) so this is effectively a
        // unicast even though SignalR's hub-context API is group-shaped.
        await _runtimeHub.Clients
            .Group($"runtime-{runtime.Id}")
            .PermissionResolved(payload);

        _logger.LogInformation(
            "AgentHub.ResolvePermission: user {UserId} resolved toolUseId {ToolUseId} with {Decision} (runtime {RuntimeId}, branch {BranchId}, project {ProjectId}).",
            userId, payload.ToolUseId, payload.Decision, runtime.Id, conversation.BranchId, projectId);
    }

    /// <summary>
    /// Subscribe this connection to the <c>runtime-events:{runtimeId}</c>
    /// SignalR group so the runtime drawer's Timeline tab AND the workspace
    /// debug panel's live supervisord snapshot receive pushes from
    /// <see cref="IAgentClient.RuntimeEventReceived"/> /
    /// <see cref="IAgentClient.LiveSupervisordSnapshotReceived"/>. Called by
    /// the frontend on drawer / panel mount.
    ///
    /// <para><b>Auth model.</b> Mirrors
    /// <see cref="Source.Features.RuntimeLifecycle.Controllers.RuntimeStatusController.GetStatus"/>
    /// and the apply-history endpoint:
    /// <list type="bullet">
    ///   <item>SuperAdmin (platform-wide bypass), OR</item>
    ///   <item>the project's <c>OwnerUserId</c>, OR</item>
    ///   <item>a <c>WorkspaceMembership</c> for the workspace the runtime's
    ///         project belongs to (any role).</item>
    /// </list>
    /// "Runtime not found" and "exists but caller has no access" both surface
    /// the same <c>Runtime not found</c> HubException so cross-tenant runtime
    /// existence isn't leaked. Driven by the
    /// <c>workspace-runtime-observability</c> spec, Section E — every workspace
    /// teammate gets the live state header that the project owner does.</para>
    ///
    /// <para>Idempotent — re-subscribing is a SignalR no-op. The runtime-
    /// scoped group is independent of the <c>project-{projectId}</c> group
    /// the connection may already be in; runtime-event broadcasts use the
    /// runtime-events group exclusively (see
    /// <see cref="RuntimeHub.RecordRuntimeEvent"/>).</para>
    /// </summary>
    public async Task SubscribeToRuntimeEvents(Guid runtimeId)
    {
        var userId = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
        {
            // [Authorize] should have rejected — defense in depth.
            throw new HubException("Unauthenticated");
        }

        if (runtimeId == Guid.Empty)
        {
            throw new HubException("Invalid runtimeId");
        }

        // Access gate — SuperAdmin OR project owner OR workspace member of the
        // runtime's owning workspace. Returns null on either "no such runtime"
        // or "exists but caller has no access"; both collapse to the same
        // "Runtime not found" error so cross-tenant runtime existence isn't
        // leaked. Same convention used by the HTTP read endpoints (see
        // OwnershipExtensions.ResolveAccessibleRuntimeAsync).
        var runtime = await _db.ResolveAccessibleRuntimeAsync(
            Context.User!, runtimeId, Context.ConnectionAborted);
        if (runtime is null)
        {
            throw new HubException("Runtime not found");
        }

        await Groups.AddToGroupAsync(
            Context.ConnectionId,
            $"runtime-events:{runtimeId}",
            Context.ConnectionAborted);

        _logger.LogInformation(
            "AgentHub.SubscribeToRuntimeEvents: connection {ConnectionId} (user {UserId}) joined runtime-events:{RuntimeId}.",
            Context.ConnectionId, userId, runtimeId);
    }

    /// <summary>
    /// Symmetric counterpart to <see cref="SubscribeToRuntimeEvents"/>.
    /// Idempotent — SignalR's <c>RemoveFromGroupAsync</c> is a no-op for
    /// non-members. No auth check (matching <see cref="LeaveWorkspace"/>):
    /// leaving a group you may already have left is harmless, and forcing
    /// a DB round-trip on every drawer close would be wasteful.
    /// </summary>
    public async Task UnsubscribeFromRuntimeEvents(Guid runtimeId)
    {
        if (runtimeId == Guid.Empty)
        {
            throw new HubException("Invalid runtimeId");
        }

        await Groups.RemoveFromGroupAsync(
            Context.ConnectionId,
            $"runtime-events:{runtimeId}",
            Context.ConnectionAborted);

        _logger.LogDebug(
            "AgentHub.UnsubscribeFromRuntimeEvents: connection {ConnectionId} left runtime-events:{RuntimeId}.",
            Context.ConnectionId, runtimeId);
    }

    /// <summary>
    /// Subscribe this connection to the <c>service-logs:{runtimeId}:{serviceName}</c>
    /// group AND ask the daemon for that runtime to start tailing the named
    /// service's log file. Called by the frontend when the user opens the
    /// runtime drawer's "Logs" tab and picks a service.
    ///
    /// <list type="number">
    ///   <item>Authenticate via the connection's user claim.</item>
    ///   <item>Resolve the runtime row; reject if it does not exist (a Logs
    ///         tab open on a dead runtime is a client-side state bug worth
    ///         surfacing, not a silent no-op).</item>
    ///   <item><b>Safety gate.</b> Parse the runtime's <see cref="ProjectRuntime.Spec"/>
    ///         as V2 and confirm <paramref name="serviceName"/> appears in
    ///         the services list. The daemon's <c>LogTailer</c> spawns
    ///         <c>tail -F /var/log/supervisor/{name}.log</c>, so an arbitrary
    ///         <paramref name="serviceName"/> string would let a client tail
    ///         any file matching that pattern. Validating against the spec
    ///         narrows the surface to "files supervisord might actually have
    ///         written for this runtime."</item>
    ///   <item>Add the caller to the SignalR group and relay
    ///         <see cref="IRuntimeClient.StartLogTail"/> to the daemon.</item>
    /// </list>
    ///
    /// <para><b>Idempotent.</b> Re-subscribing is a SignalR no-op for the
    /// group; the daemon's <c>LogTailer</c> ref-counts, so duplicate
    /// <c>StartLogTail</c> pushes are safe — they bump the count without
    /// spawning a second tail process.</para>
    ///
    /// <para><b>Auth model.</b> Mirrors <see cref="SubscribeToRuntimeEvents"/>:
    /// SuperAdmin OR project owner OR workspace member of the runtime's owning
    /// workspace, resolved via
    /// <see cref="OwnershipExtensions.ResolveAccessibleRuntimeAsync"/>. "Runtime
    /// not found" and "exists but caller has no access" both surface the same
    /// <c>Runtime not found</c> HubException so cross-tenant runtime existence
    /// isn't leaked. The narrower <see cref="SubscribeToDaemonLogs"/> sibling
    /// stays SuperAdmin-only — daemon logs can carry operator-sensitive
    /// bootstrap detail and need the tighter bar.</para>
    /// </summary>
    public async Task SubscribeToServiceLogs(Guid runtimeId, string serviceName)
    {
        var userId = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
        {
            throw new HubException("Unauthenticated");
        }

        if (runtimeId == Guid.Empty)
        {
            throw new HubException("Invalid runtimeId");
        }
        if (string.IsNullOrWhiteSpace(serviceName))
        {
            throw new HubException("Invalid serviceName");
        }

        // Access gate — SuperAdmin OR project owner OR workspace member of the
        // runtime's owning workspace. Returns null on either "no such runtime"
        // or "exists but caller has no access"; both collapse to the same
        // "Runtime not found" so cross-tenant runtime existence isn't leaked.
        // Same convention used by SubscribeToRuntimeEvents.
        var runtime = await _db.ResolveAccessibleRuntimeAsync(
            Context.User!, runtimeId, Context.ConnectionAborted);
        if (runtime is null)
        {
            throw new HubException("Runtime not found");
        }

        // Safety gate: only allow subscribing to services declared in the
        // current spec. Without this, a client could ask the daemon to tail
        // arbitrary <serviceName>.log files. We deliberately fail soft on
        // unparseable specs (return without subscribing) — a malformed spec is
        // a separate problem the drawer's Spec tab surfaces; the Logs tab just
        // shouldn't activate.
        //
        // Spec lives on Project, not ProjectRuntime — see
        // `project-level-runtime-spec`. Cheap projection.
        var projectSpec = await _db.Projects
            .AsNoTracking()
            .Where(p => p.Id == runtime.ProjectId)
            .Select(p => p.Spec)
            .FirstOrDefaultAsync(Context.ConnectionAborted);

        // Spec on Project is V3 (preset-based, user/agent authoring shape).
        // We only need to confirm the requested service name was declared, so
        // we don't expand to V2 here — V3 ServiceInstance carries the same
        // <c>Name</c> field the daemon's supervisord program uses.
        var parsed = string.IsNullOrWhiteSpace(projectSpec)
            ? null
            : RuntimeSpecV3.TryParse(projectSpec);
        if (parsed is null)
        {
            _logger.LogWarning(
                "AgentHub.SubscribeToServiceLogs: runtime {RuntimeId} has missing or unparseable V3 spec; refusing subscribe for service {ServiceName}.",
                runtimeId, serviceName);
            return;
        }

        var services = parsed.Services;
        var serviceDeclared = services is not null && services.Any(s =>
            string.Equals(s.Name, serviceName, StringComparison.Ordinal));
        if (!serviceDeclared)
        {
            _logger.LogWarning(
                "AgentHub.SubscribeToServiceLogs: service {ServiceName} not declared in spec for runtime {RuntimeId}; refusing subscribe.",
                serviceName, runtimeId);
            return;
        }

        await Groups.AddToGroupAsync(
            Context.ConnectionId,
            $"service-logs:{runtimeId}:{serviceName}",
            Context.ConnectionAborted);

        // Tell the daemon to start tailing. Targeted at the runtime group —
        // a single daemon connection lives in that group (per
        // RuntimeHub.OnConnectedAsync) so this is effectively a unicast.
        try
        {
            await _runtimeHub.Clients
                .Group($"runtime-{runtimeId}")
                .StartLogTail(serviceName);
        }
        catch (Exception ex)
        {
            // Daemon-side relay failure: the React client is now in the group
            // but no tail is running. The drawer will show no lines, which is
            // the right UX (empty state, not a stale group). Log + continue.
            _logger.LogWarning(ex,
                "AgentHub.SubscribeToServiceLogs: failed to push StartLogTail to runtime {RuntimeId} for service {ServiceName}.",
                runtimeId, serviceName);
        }

        _logger.LogInformation(
            "AgentHub.SubscribeToServiceLogs: connection {ConnectionId} (user {UserId}) joined service-logs:{RuntimeId}:{ServiceName}.",
            Context.ConnectionId, userId, runtimeId, serviceName);
    }

    /// <summary>
    /// Symmetric counterpart to <see cref="SubscribeToServiceLogs"/>. Removes
    /// the caller from the SignalR group and pushes
    /// <see cref="IRuntimeClient.StopLogTail"/> to the daemon. The daemon's
    /// <c>LogTailer</c> ref-counts subscribers; when the last one leaves it
    /// SIGTERM's the underlying <c>tail -F</c> process.
    ///
    /// <para>No auth check (matching <see cref="UnsubscribeFromRuntimeEvents"/>):
    /// leaving a group you may already have left is harmless. Idempotent —
    /// SignalR's <c>RemoveFromGroupAsync</c> no-ops for non-members, and the
    /// daemon's ref-count floors at zero.</para>
    /// </summary>
    public async Task UnsubscribeFromServiceLogs(Guid runtimeId, string serviceName)
    {
        if (runtimeId == Guid.Empty)
        {
            throw new HubException("Invalid runtimeId");
        }
        if (string.IsNullOrWhiteSpace(serviceName))
        {
            throw new HubException("Invalid serviceName");
        }

        await Groups.RemoveFromGroupAsync(
            Context.ConnectionId,
            $"service-logs:{runtimeId}:{serviceName}",
            Context.ConnectionAborted);

        try
        {
            await _runtimeHub.Clients
                .Group($"runtime-{runtimeId}")
                .StopLogTail(serviceName);
        }
        catch (Exception ex)
        {
            // Daemon-side relay failure: the tail keeps running on the daemon
            // until its ref-count hits zero by some other path (or disconnect).
            // Worst case a slightly leaked tail process, recovered on the next
            // daemon restart. Log + continue.
            _logger.LogWarning(ex,
                "AgentHub.UnsubscribeFromServiceLogs: failed to push StopLogTail to runtime {RuntimeId} for service {ServiceName}.",
                runtimeId, serviceName);
        }

        _logger.LogDebug(
            "AgentHub.UnsubscribeFromServiceLogs: connection {ConnectionId} left service-logs:{RuntimeId}:{ServiceName}.",
            Context.ConnectionId, runtimeId, serviceName);
    }

    /// <summary>
    /// runtime-observability-super-admin — super-admin only. Subscribe this
    /// connection to the <c>daemon-logs:{runtimeId}</c> group and tell the
    /// daemon to start tailing its own stdout/stderr files. Mirrors
    /// <see cref="SubscribeToServiceLogs"/> but tighter on auth (the daemon's
    /// own logs can carry operator-sensitive bootstrap detail) and without the
    /// per-service safety gate — the daemon tails fixed paths, not arbitrary
    /// names.
    /// </summary>
    public async Task SubscribeToDaemonLogs(Guid runtimeId)
    {
        var userId = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
        {
            throw new HubException("Unauthenticated");
        }

        var isSuperAdmin = Context.User?.IsInRole(RoleConstants.SuperAdmin) ?? false;
        if (!isSuperAdmin)
        {
            throw new HubException("SuperAdmin role required");
        }

        if (runtimeId == Guid.Empty)
        {
            throw new HubException("Invalid runtimeId");
        }

        var runtime = await _db.ProjectRuntimes
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == runtimeId, Context.ConnectionAborted);
        if (runtime is null)
        {
            throw new HubException("Runtime not found");
        }

        await Groups.AddToGroupAsync(
            Context.ConnectionId,
            $"daemon-logs:{runtimeId}",
            Context.ConnectionAborted);

        try
        {
            await _runtimeHub.Clients
                .Group($"runtime-{runtimeId}")
                .StartDaemonLogTail();
        }
        catch (Exception ex)
        {
            // Daemon-side relay failure: the React client is now in the group
            // but no tail is running on the daemon. The drawer renders the
            // empty state; the next subscribe attempt heals it. Log + continue.
            _logger.LogWarning(ex,
                "AgentHub.SubscribeToDaemonLogs: failed to push StartDaemonLogTail to runtime {RuntimeId}.",
                runtimeId);
        }

        _logger.LogInformation(
            "AgentHub.SubscribeToDaemonLogs: connection {ConnectionId} (user {UserId}) joined daemon-logs:{RuntimeId}.",
            Context.ConnectionId, userId, runtimeId);
    }

    /// <summary>
    /// Symmetric counterpart to <see cref="SubscribeToDaemonLogs"/>. Removes
    /// the caller from the group and pushes
    /// <see cref="IRuntimeClient.StopDaemonLogTail"/> to the daemon. Same
    /// idempotence guarantees as <see cref="UnsubscribeFromServiceLogs"/>.
    /// </summary>
    public async Task UnsubscribeFromDaemonLogs(Guid runtimeId)
    {
        if (runtimeId == Guid.Empty)
        {
            throw new HubException("Invalid runtimeId");
        }

        await Groups.RemoveFromGroupAsync(
            Context.ConnectionId,
            $"daemon-logs:{runtimeId}",
            Context.ConnectionAborted);

        try
        {
            await _runtimeHub.Clients
                .Group($"runtime-{runtimeId}")
                .StopDaemonLogTail();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "AgentHub.UnsubscribeFromDaemonLogs: failed to push StopDaemonLogTail to runtime {RuntimeId}.",
                runtimeId);
        }

        _logger.LogDebug(
            "AgentHub.UnsubscribeFromDaemonLogs: connection {ConnectionId} left daemon-logs:{RuntimeId}.",
            Context.ConnectionId, runtimeId);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogInformation(exception,
            "AgentHub disconnected. Connection {ConnectionId}", Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }
}
