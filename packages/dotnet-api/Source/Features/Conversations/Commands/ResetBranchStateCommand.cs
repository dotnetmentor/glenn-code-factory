using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Source.Features.Conversations.Models;
using Source.Features.SignalR.Contracts;
using Source.Features.SignalR.Hubs;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.Conversations.Commands;

/// <summary>
/// "Cancel everything on this branch" — bulk cancel ALL in-flight and queued
/// <see cref="AgentSession"/> rows attached to the
/// <see cref="Source.Features.RuntimeLifecycle.Models.ProjectRuntime"/> that
/// owns <c>(ProjectId, BranchId)</c>. Conceptually a for-each over
/// <see cref="CancelSessionCommand"/>: the per-status routing rules are
/// identical, just applied to every non-terminal session on the runtime in a
/// single transaction.
///
/// <list type="bullet">
///   <item><b>Pending</b>: drop straight to terminal
///         <see cref="AgentSessionStatus.Canceled"/> via
///         <see cref="AgentSession.MarkCanceled"/>. No daemon involved — the
///         queue just empties.</item>
///   <item><b>Running</b>: flip to <see cref="AgentSessionStatus.Canceling"/>
///         via <see cref="AgentSession.MarkCanceling"/> and push a
///         <see cref="CancelTurnPayload"/> to the daemon group; the session
///         lands on terminal Canceled when the daemon emits
///         <c>turn_canceled</c>. In practice there is at most one Running
///         session per runtime (single-turn invariant) but the loop handles
///         any count defensively.</item>
///   <item><b>Canceling</b>: already in the drain window — counted but no-op'd.
///         The user's "reset" intent is already in flight; double-pushing
///         CancelTurn would just give the daemon a duplicate to ignore.</item>
/// </list>
///
/// <para><b>Counts.</b> The response reports <c>CanceledRunning</c> (sessions
/// that were Running OR Canceling at the moment we ran) and
/// <c>CanceledPending</c> (Pending sessions dropped to terminal). Already-
/// terminal sessions are excluded entirely from both the query and the counts
/// — a "reset" on a branch with nothing in flight returns <c>(0, 0)</c>.</para>
///
/// <para><b>One save, many events.</b> All entity transitions run inside a
/// single <c>SaveChangesAsync</c> so domain events
/// (<see cref="Source.Features.Conversations.Events.AgentSessionTerminated"/>,
/// <see cref="Source.Features.Conversations.Events.SessionCancelRequested"/>)
/// fire naturally via the DomainEventInterceptor. The dispatch-next handler
/// will not re-fire on these terminated rows because the queue is empty.</para>
///
/// <para><b>SignalR fan-out semantics.</b> Same best-effort logged-and-
/// swallowed pattern as <see cref="CancelSessionCommandHandler"/>: each
/// CancelTurn push is OUTSIDE the DB transaction. If the daemon is offline,
/// the sessions stay in Canceling and the orphan-session janitor is the
/// recovery path. We don't roll back the DB writes — the user-facing intent
/// ("reset everything on this branch") is recorded the moment SaveChanges
/// commits and must survive a flaky daemon.</para>
/// </summary>
public sealed record ResetBranchStateCommand(Guid ProjectId, Guid BranchId)
    : ICommand<Result<ResetBranchStateResponse>>;

/// <summary>
/// Counts of what the reset actually canceled. <c>CanceledRunning</c> includes
/// rows that were already <see cref="AgentSessionStatus.Canceling"/> at the
/// moment of the call (idempotent no-op, but visible in-flight intent counts
/// for the UI's "we canceled N things" copy). <c>CanceledPending</c> is the
/// number of queued sessions dropped straight to terminal Canceled.
/// </summary>
public sealed record ResetBranchStateResponse(int CanceledRunning, int CanceledPending);

public class ResetBranchStateCommandHandler
    : ICommandHandler<ResetBranchStateCommand, Result<ResetBranchStateResponse>>
{
    /// <summary>
    /// Reason stamped on every session's <c>CancelReason</c> when the bulk
    /// reset runs. Distinct from the per-session cancel default
    /// (<c>"user"</c>) so the audit trail makes it obvious these rows were
    /// terminated by the branch-reset endpoint, not individual cancel clicks.
    /// </summary>
    public const string ResetReason = "branch_reset";

    private readonly ApplicationDbContext _db;
    private readonly IHubContext<RuntimeHub, IRuntimeClient> _runtimeHub;
    private readonly ILogger<ResetBranchStateCommandHandler> _logger;

    public ResetBranchStateCommandHandler(
        ApplicationDbContext db,
        IHubContext<RuntimeHub, IRuntimeClient> runtimeHub,
        ILogger<ResetBranchStateCommandHandler> logger)
    {
        _db = db;
        _runtimeHub = runtimeHub;
        _logger = logger;
    }

    public async Task<Result<ResetBranchStateResponse>> Handle(
        ResetBranchStateCommand request,
        CancellationToken cancellationToken)
    {
        // Resolve the (project, branch) → runtime first. Same shape as
        // RuntimeStatusController.GetStatus: most-recent non-deleted row wins,
        // and a project owns one runtime per branch post-CopyBranch. Soft-
        // deleted runtimes are filtered out by the global query filter — a
        // "reset" on a torn-down branch has nothing to do and falls through
        // to the not-found path.
        var runtime = await _db.ProjectRuntimes
            .Where(r => r.ProjectId == request.ProjectId && r.BranchId == request.BranchId)
            .OrderByDescending(r => r.CreatedAt)
            .Select(r => new { r.Id })
            .FirstOrDefaultAsync(cancellationToken);

        if (runtime is null)
        {
            return Result.Failure<ResetBranchStateResponse>("Runtime not found");
        }

        // IgnoreQueryFilters on AgentSessions so we still catch sessions whose
        // parent conversation was archived by the user — same defensive
        // pattern as CancelSessionCommandHandler. The user might reset a
        // branch after archiving the conversation that holds an in-flight
        // session, and we still want the cancel to land.
        var sessions = await _db.AgentSessions
            .IgnoreQueryFilters()
            .Where(s => s.RuntimeId == runtime.Id
                && (s.Status == AgentSessionStatus.Pending
                    || s.Status == AgentSessionStatus.Running
                    || s.Status == AgentSessionStatus.Canceling))
            .ToListAsync(cancellationToken);

        if (sessions.Count == 0)
        {
            _logger.LogInformation(
                "ResetBranchState: project {ProjectId}, branch {BranchId}, runtime {RuntimeId} — nothing to cancel.",
                request.ProjectId, request.BranchId, runtime.Id);
            return Result.Success(new ResetBranchStateResponse(0, 0));
        }

        var canceledPending = 0;
        var canceledRunning = 0;
        var cancelTurnTargets = new List<Guid>();

        foreach (var session in sessions)
        {
            switch (session.Status)
            {
                case AgentSessionStatus.Pending:
                    // No daemon to notify, no in-flight turn to drain. Drop
                    // straight to terminal Canceled — the entity method handles
                    // QueuePosition clearing and raises AgentSessionTerminated.
                    session.MarkCanceled(ResetReason);
                    canceledPending++;
                    break;

                case AgentSessionStatus.Running:
                    // Intermediate Canceling transition + queue a daemon push.
                    // We collect the SessionId here and fan out CancelTurn
                    // AFTER SaveChanges, mirroring CancelSessionCommandHandler:
                    // the DB write is the durable signal of intent; the SignalR
                    // push is best-effort.
                    session.MarkCanceling(ResetReason);
                    cancelTurnTargets.Add(session.Id);
                    canceledRunning++;
                    break;

                case AgentSessionStatus.Canceling:
                    // Already in the drain window. Don't re-call MarkCanceling
                    // (it's idempotent but would still no-op) and don't re-push
                    // CancelTurn — the daemon already has the push from the
                    // earlier cancel. Still counted as part of CanceledRunning
                    // so the UI's "we canceled N things" copy lines up with
                    // what the user actually sees in flight.
                    canceledRunning++;
                    break;
            }
        }

        await _db.SaveChangesAsync(cancellationToken);

        // Best-effort SignalR fan-out — see class XML doc. Each push is wrapped
        // individually so one daemon-side hub failure doesn't strand the rest
        // of the running sessions.
        foreach (var sessionId in cancelTurnTargets)
        {
            var payload = new CancelTurnPayload(sessionId, ResetReason);

            try
            {
                await _runtimeHub.Clients
                    .Group($"runtime-{runtime.Id}")
                    .CancelTurn(payload);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "ResetBranchState: CancelTurn fan-out failed for session {SessionId} on runtime {RuntimeId}; session is Canceling in DB. Janitor will reap if the daemon never confirms.",
                    sessionId, runtime.Id);
            }
        }

        _logger.LogInformation(
            "ResetBranchState: project {ProjectId}, branch {BranchId}, runtime {RuntimeId} — canceled {CanceledPending} Pending and {CanceledRunning} Running/Canceling sessions.",
            request.ProjectId, request.BranchId, runtime.Id, canceledPending, canceledRunning);

        return Result.Success(new ResetBranchStateResponse(canceledRunning, canceledPending));
    }
}
