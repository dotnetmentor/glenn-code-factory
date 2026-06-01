using Hangfire;
using Microsoft.EntityFrameworkCore;
using Source.Features.Conversations.Events;
using Source.Features.Conversations.Models;
using Source.Features.RuntimeLifecycle.Models;
using Source.Infrastructure;
using Source.Shared;

namespace Source.Features.Conversations.Jobs;

/// <summary>
/// Run-liveness reconciler. Reaps <see cref="AgentSession"/> rows left in
/// <see cref="AgentSessionStatus.Running"/> or <see cref="AgentSessionStatus.Canceling"/>
/// on a runtime that is <i>provably alive and idle</i> — i.e. the daemon is
/// heartbeating right now but its authoritative
/// <c>HeartbeatPayload.activeSessionId</c> (persisted to
/// <see cref="ProjectRuntime.ActiveSessionId"/>) is either <c>null</c> or points
/// at a different session.
///
/// <para><b>The bug this closes.</b> The per-conversation dispatch queue
/// (<c>DispatchNextSessionHandler</c>) only advances when a session reaches a
/// terminal state and raises <see cref="AgentSessionTerminated"/>. When the
/// daemon's cursor subprocess dies mid-stream and the terminal
/// <c>turn_completed</c> / <c>turn_failed</c> / <c>turn_canceled</c> event is
/// lost, the session sits Running forever and blocks every later message in that
/// conversation. <see cref="OrphanSessionJanitorJob"/> can't help: the runtime is
/// still <see cref="RuntimeState.Online"/>, not Crashed/Suspended. The control
/// plane already has ground truth on every heartbeat — the daemon tells us which
/// run it is executing — we just consume it here.</para>
///
/// <para><b>Why the two thresholds.</b> We only act on runtimes whose daemon is
/// demonstrably alive (<see cref="RuntimeState.Online"/> AND a heartbeat within
/// <see cref="HeartbeatFreshnessSeconds"/>), so a silent/crashed daemon is left
/// to <see cref="HeartbeatWatcherJob"/> / <see cref="OrphanSessionJanitorJob"/>.
/// And we only reap a session whose <see cref="AgentSession.UpdatedAt"/> is older
/// than <see cref="GraceSeconds"/> — a just-dispatched session that the daemon
/// hasn't yet named in its <i>next</i> heartbeat must not be reaped.</para>
///
/// <para><b>Idempotent / safe.</b> <see cref="AgentSession.Fail(string?)"/> is a
/// no-op on already-terminal rows, so a late daemon reply racing this job is
/// harmless. Sessions are processed in fixed-size batches, each its own
/// <c>SaveChangesAsync</c>, so the <see cref="AgentSessionTerminated"/> domain
/// events fan out incrementally and <c>DispatchNextSessionHandler</c> drains the
/// queue automatically — this job never dispatches directly.</para>
///
/// <para><b>Concurrency.</b> Decorated with
/// <see cref="DisableConcurrentExecutionAttribute"/> with a 60-second timeout
/// (matches the minutely cron) so two Hangfire workers can't overlap on the same
/// minute. <see cref="AutomaticRetry"/> with <c>Attempts = 0</c> stops Hangfire
/// from stacking a partially-cancelled run on top of the next scheduled tick.</para>
/// </summary>
public class ReconcileStaleSessionsJob
{
    /// <summary>
    /// Batch size for the reconcile scan. Same bound + reasoning as
    /// <see cref="OrphanSessionJanitorJob"/>: small enough to keep transactions
    /// and per-batch domain-event fan-out bounded, large enough that a realistic
    /// burst clears in a single Hangfire fire.
    /// </summary>
    private const int BatchSize = 50;

    /// <summary>
    /// How fresh a runtime's <see cref="ProjectRuntime.LastHeartbeatAt"/> must be
    /// for us to trust its <see cref="ProjectRuntime.ActiveSessionId"/> as ground
    /// truth. 90s = generous cover over the daemon's 5-second heartbeat cadence
    /// (and comfortably below <see cref="HeartbeatWatcherJob"/>'s 60s crash
    /// threshold + reconnect slack) so we only act on a daemon that is
    /// demonstrably alive and reporting.
    /// </summary>
    private const int HeartbeatFreshnessSeconds = 90;

    /// <summary>
    /// Grace window before a Running/Canceling session becomes eligible for
    /// reaping, measured against <see cref="AgentSession.UpdatedAt"/>. 60s leaves
    /// ample room for a just-dispatched session to be picked up and named in the
    /// daemon's next heartbeat before we'd ever consider it stale — without this,
    /// a session dispatched between two heartbeats could be reaped a beat early.
    /// </summary>
    private const int GraceSeconds = 60;

    private readonly ApplicationDbContext _db;
    private readonly IClock _clock;
    private readonly ILogger<ReconcileStaleSessionsJob> _logger;

    public ReconcileStaleSessionsJob(
        ApplicationDbContext db,
        IClock clock,
        ILogger<ReconcileStaleSessionsJob> logger)
    {
        _db = db;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// Hangfire entry point. Wraps the inner <see cref="Run(CancellationToken)"/>
    /// in a linked <see cref="CancellationTokenSource"/> with a hard 50-second
    /// budget so the job can never hold the
    /// <see cref="DisableConcurrentExecutionAttribute"/> lock past the 60-second
    /// TTL even if a database call hangs.
    /// </summary>
    [DisableConcurrentExecution(timeoutInSeconds: 60)]
    [AutomaticRetry(Attempts = 0)]
    public async Task Run(IJobCancellationToken hangfireCt)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(hangfireCt.ShutdownToken);
        cts.CancelAfter(TimeSpan.FromSeconds(50));
        await Run(cts.Token);
    }

    /// <summary>
    /// Scans for stale sessions in batches of <see cref="BatchSize"/> until the
    /// table is clean (or the cancellation token trips). Per-pass
    /// <c>SaveChangesAsync</c> ensures domain events fan out incrementally.
    /// </summary>
    public async Task Run(CancellationToken ct = default)
    {
        var nowUtc = _clock.UtcNow;
        var heartbeatCutoff = nowUtc.AddSeconds(-HeartbeatFreshnessSeconds);
        var graceCutoff = nowUtc.AddSeconds(-GraceSeconds);

        var totalProcessed = 0;

        while (!ct.IsCancellationRequested)
        {
            // Pull a batch of stale sessions. The filter lives entirely in the
            // database via the Runtime nav property: runtime provably alive
            // (Online + fresh heartbeat), the daemon is NOT executing this
            // session (ActiveSessionId null or different), and the grace window
            // has elapsed since the session was last touched.
            var batch = await _db.AgentSessions
                .Include(s => s.Runtime)
                .Where(s => (s.Status == AgentSessionStatus.Running
                          || s.Status == AgentSessionStatus.Canceling)
                         && s.Runtime.State == RuntimeState.Online
                         && s.Runtime.LastHeartbeatAt != null
                         && s.Runtime.LastHeartbeatAt >= heartbeatCutoff
                         && s.Runtime.ActiveSessionId != s.Id
                         && s.UpdatedAt < graceCutoff)
                .OrderBy(s => s.Id)
                .Take(BatchSize)
                .ToListAsync(ct);

            if (batch.Count == 0)
            {
                break;
            }

            foreach (var session in batch)
            {
                await ReapAsync(session, nowUtc, ct);
            }

            await _db.SaveChangesAsync(ct);
            totalProcessed += batch.Count;

            if (batch.Count < BatchSize)
            {
                break;
            }
        }

        if (totalProcessed > 0)
        {
            _logger.LogWarning(
                "ReconcileStaleSessions marked {Count} sessions Failed (reason=daemon_run_not_active)",
                totalProcessed);
        }
        else
        {
            _logger.LogDebug("ReconcileStaleSessions: no stale sessions found");
        }
    }

    /// <summary>
    /// Appends a terminal Status row (<see cref="AgentEventKind.Status"/> +
    /// <see cref="AgentEventRunStatus.Expired"/>) for UI consistency — exactly
    /// like <see cref="SessionStartupTimeoutJob"/> / <see cref="OrphanSessionJanitorJob"/>
    /// — then flips the session to terminal Failed. <see cref="AgentSession.Fail(string?)"/>
    /// is idempotent, so a concurrent terminal event landing mid-function is safe.
    /// </summary>
    private async Task ReapAsync(AgentSession session, DateTime nowUtc, CancellationToken ct)
    {
        // Need the conversation's ProjectId/BranchId to construct the broadcast
        // DTO the BroadcastAgentEventHandler fans out to SignalR groups.
        var route = await _db.AgentSessions
            .AsNoTracking()
            .Where(s => s.Id == session.Id)
            .Select(s => new
            {
                s.ConversationId,
                ProjectId = s.Conversation.ProjectId,
                BranchId = s.Conversation.BranchId,
            })
            .FirstOrDefaultAsync(ct);

        if (route is null)
        {
            // Conversation row vanished mid-scan — just fail the session without
            // the broadcast row.
            session.Fail("daemon_run_not_active");
            return;
        }

        // Append at max+1 to stay gap-free in line with the RuntimeHub.EmitEvent
        // contract.
        var maxSeq = await _db.AgentEvents
            .Where(e => e.SessionId == session.Id)
            .MaxAsync(e => (long?)e.Sequence, ct);
        var nextSeq = (maxSeq ?? -1L) + 1L;

        const string statusMessage = "daemon_run_not_active";

        var terminalEvent = new AgentEvent
        {
            SessionId = session.Id,
            Sequence = nextSeq,
            Kind = AgentEventKind.Status,
            RunStatus = AgentEventRunStatus.Expired,
            StatusMessage = statusMessage,
            CreatedAt = nowUtc,
        };
        _db.AgentEvents.Add(terminalEvent);

        var terminalDto = new StatusEventDto(
            SessionId: session.Id,
            Sequence: nextSeq,
            CreatedAt: nowUtc,
            Status: AgentEventRunStatus.Expired,
            Message: statusMessage);

        // Raise the broadcast event BEFORE SaveChanges so the
        // DomainEventInterceptor sees it as part of the same transaction.
        session.RecordEventEmitted(new AgentEventEmitted(
            ConversationId: route.ConversationId,
            ProjectId: route.ProjectId,
            BranchId: route.BranchId,
            Kind: AgentEventKind.Status,
            Event: terminalDto,
            OccurredAt: nowUtc));

        // Terminal flip — raises AgentSessionTerminated so DispatchNextSessionHandler
        // advances the conversation queue. Idempotent on already-terminal rows.
        session.Fail("daemon_run_not_active");
    }
}
