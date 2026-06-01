using Hangfire;
using Microsoft.EntityFrameworkCore;
using Source.Features.Conversations.Models;
using Source.Features.RuntimeLifecycle.Models;
using Source.Infrastructure;

namespace Source.Features.Conversations.Jobs;

/// <summary>
/// Periodic scan that reaps <see cref="AgentSession"/> rows left in
/// <see cref="AgentSessionStatus.Running"/> or <see cref="AgentSessionStatus.Canceling"/>
/// while their underlying <see cref="ProjectRuntime"/> has moved into a state
/// where no daemon will ever drive the session to a terminal outcome —
/// <see cref="RuntimeState.Crashed"/>, <see cref="RuntimeState.Failed"/>,
/// <see cref="RuntimeState.Suspended"/>, <see cref="RuntimeState.Suspending"/>,
/// <see cref="RuntimeState.Deleting"/>, or <see cref="RuntimeState.Deleted"/>.
///
/// <para>Sessions in those buckets are <i>orphans</i>: the <c>turn_completed</c> /
/// <c>turn_failed</c> / <c>turn_canceled</c> event that would normally close them
/// is never coming. The janitor flips each one to
/// <see cref="AgentSessionStatus.Failed"/> with reason <c>"runtime_unavailable"</c>
/// so the user sees a deterministic outcome and the dispatch chain (Card 3,
/// <c>DispatchNextSessionHandler</c>) gets a chance to drain any queued siblings
/// — though if the runtime is genuinely dead the dispatch fan-out will fail
/// too, and that's fine: the orphan janitor will sweep those siblings in a
/// later pass.</para>
///
/// <para><b>Per the agent-execution-control spec:</b> "Default for MVP: mark
/// Failed, prompt user. Resume is a future improvement."</para>
///
/// <para><b>Idempotent.</b> <see cref="AgentSession.Fail(string?)"/> is a no-op
/// on already-terminal rows, so a re-run after a partial sweep / crash never
/// double-raises <c>AgentSessionTerminated</c> on the same session.</para>
///
/// <para><b>Concurrency.</b> Decorated with
/// <see cref="DisableConcurrentExecutionAttribute"/> with a 60-second timeout
/// (matches the minutely cron) so two Hangfire workers can't overlap on the
/// same minute.</para>
///
/// <para><b>Batched.</b> Sessions are processed in fixed-size batches so a
/// large outage (hundreds of orphans) doesn't produce one giant transaction.
/// Each batch is its own <c>SaveChangesAsync</c>, so domain events fan out as
/// they're persisted.</para>
/// </summary>
public class OrphanSessionJanitorJob
{
    /// <summary>
    /// Batch size for the orphan scan. Small enough to keep transactions and
    /// per-batch domain-event fan-out bounded, large enough that a typical
    /// outage clears in a single Hangfire fire.
    /// </summary>
    private const int BatchSize = 50;

    /// <summary>
    /// Runtime states under which a session can no longer reach a terminal
    /// status via the normal daemon round-trip. Matches the spec's
    /// "Crashed/Failed/Suspended" enumeration; we additionally include
    /// <see cref="RuntimeState.Suspending"/> (the daemon is on its way out and
    /// won't pick up cancels), <see cref="RuntimeState.Deleting"/> and
    /// <see cref="RuntimeState.Deleted"/> (operator teardown — there is no
    /// daemon to talk to). <see cref="RuntimeState.Booting"/> /
    /// <see cref="RuntimeState.Bootstrapping"/> / <see cref="RuntimeState.Waking"/>
    /// are deliberately excluded — those are recoverable transitions and any
    /// Running session against them is genuinely waiting on the daemon.
    /// </summary>
    private static readonly RuntimeState[] UnavailableStates =
    {
        RuntimeState.Crashed,
        RuntimeState.Failed,
        RuntimeState.Suspending,
        RuntimeState.Suspended,
        RuntimeState.Deleting,
        RuntimeState.Deleted,
    };

    private readonly ApplicationDbContext _db;
    private readonly ILogger<OrphanSessionJanitorJob> _logger;

    public OrphanSessionJanitorJob(
        ApplicationDbContext db,
        ILogger<OrphanSessionJanitorJob> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Hangfire entry point. Wraps the inner <see cref="Run(CancellationToken)"/>
    /// in a linked <see cref="CancellationTokenSource"/> with a hard 50-second
    /// budget so the job can never hold the
    /// <see cref="DisableConcurrentExecutionAttribute"/> lock past the 60-second
    /// TTL — even if a database call hangs forever. When the CTS trips, control
    /// returns, Hangfire releases the lock, and the next tick acquires on
    /// schedule.
    ///
    /// <para><see cref="AutomaticRetry"/> with <c>Attempts = 0</c> stops Hangfire
    /// from auto-requeuing a partially-cancelled run on top of the next scheduled
    /// tick.</para>
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
    /// Scans for orphaned sessions in batches of <see cref="BatchSize"/> until
    /// the table is clean (or the cancellation token trips). Per-pass
    /// <c>SaveChangesAsync</c> ensures domain events fan out incrementally
    /// rather than at the end of one giant transaction.
    /// </summary>
    public async Task Run(CancellationToken ct = default)
    {
        var totalProcessed = 0;

        while (!ct.IsCancellationRequested)
        {
            // Pull a batch of orphans. Joining through the Runtime nav property
            // (added in Card 1) keeps the filter in the database — no in-memory
            // post-filter, no two-step query.
            var batch = await _db.AgentSessions
                .Include(s => s.Runtime)
                .Where(s => (s.Status == AgentSessionStatus.Running
                          || s.Status == AgentSessionStatus.Canceling)
                         && UnavailableStates.Contains(s.Runtime.State))
                .OrderBy(s => s.Id)
                .Take(BatchSize)
                .ToListAsync(ct);

            if (batch.Count == 0)
            {
                break;
            }

            foreach (var session in batch)
            {
                // Fail() is idempotent on already-terminal rows, so even if a
                // concurrent path raced us to a terminal status the call is
                // safe — see AgentSession.Fail.
                session.Fail("runtime_unavailable");
            }

            await _db.SaveChangesAsync(ct);
            totalProcessed += batch.Count;

            // Smaller-than-batch result means there's nothing left to scan.
            if (batch.Count < BatchSize)
            {
                break;
            }
        }

        if (totalProcessed > 0)
        {
            _logger.LogWarning(
                "OrphanSessionJanitor marked {Count} sessions Failed (reason=runtime_unavailable)",
                totalProcessed);
        }
        else
        {
            _logger.LogDebug("OrphanSessionJanitor: no orphan sessions found");
        }
    }
}
