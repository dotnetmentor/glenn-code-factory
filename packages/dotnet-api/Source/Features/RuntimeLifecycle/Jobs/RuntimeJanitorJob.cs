using Hangfire;
using Microsoft.EntityFrameworkCore;
using Source.Features.RuntimeLifecycle.Models;
using Source.Infrastructure;

namespace Source.Features.RuntimeLifecycle.Jobs;

/// <summary>
/// Recurring Hangfire job that hard-deletes <see cref="ProjectRuntime"/> rows
/// once they have been in <see cref="RuntimeState.Deleted"/> for at least
/// <see cref="RetentionDays"/> days. Runs once a day via
/// <see cref="RuntimeJanitorJobRegistration"/>.
///
/// <para><b>What stays.</b> The audit tables — <c>RuntimeStateEvents</c>,
/// <c>BootstrapRuns</c>, <c>FlyOperations</c> — are append-only and are
/// deliberately <i>not</i> FK-cascaded to <see cref="ProjectRuntime"/>. They
/// must outlive the runtime row so we can still answer "what happened to
/// runtime X?" weeks after the row itself is gone. This job touches only
/// <c>ProjectRuntimes</c>; everything else stays put.</para>
///
/// <para><b>Eligibility.</b> A row qualifies for hard-delete only when its
/// <see cref="ProjectRuntime.State"/> is <see cref="RuntimeState.Deleted"/>
/// AND <see cref="ProjectRuntime.DeletedAt"/> is older than the cutoff. The
/// state check is what matters: rows that reached the proper Deleting →
/// Deleted transition are the only ones eligible. We don't key off
/// <see cref="ProjectRuntime.IsDeleted"/> alone — that flag could be set in
/// other ways and would let us hard-delete runtimes that never went through
/// the lifecycle terminal state.</para>
///
/// <para><b>Query filter.</b> <c>ApplicationDbContext.ProjectRuntimes</c> has a
/// global query filter that hides soft-deleted rows. The janitor must call
/// <see cref="EntityFrameworkQueryableExtensions.IgnoreQueryFilters{T}"/> to
/// see them at all.</para>
///
/// <para><b>Concurrency.</b> Decorated with
/// <see cref="DisableConcurrentExecutionAttribute"/> so two Hangfire workers
/// can't run the janitor at the same time across the cluster. The 300-second
/// timeout gives the bulk delete comfortable headroom even on a backlog.</para>
///
/// <para><b>Failure handling.</b> No try/catch around the SaveChanges. The
/// janitor only talks to Postgres, so transient DB errors are Hangfire's
/// retry territory; let them propagate. (Contrast with the reconciler, which
/// swallows <c>FlyApiException</c> because Fly is an external dependency.)</para>
/// </summary>
public class RuntimeJanitorJob
{
    /// <summary>
    /// How long a Deleted runtime row sticks around before it's hard-deleted.
    /// Hardcoded for now — if we ever need to tune this, SystemSettings is
    /// the next iteration. Mirroring the spec note.
    /// </summary>
    private const int RetentionDays = 30;

    private readonly ApplicationDbContext _db;
    private readonly ILogger<RuntimeJanitorJob> _logger;

    public RuntimeJanitorJob(
        ApplicationDbContext db,
        ILogger<RuntimeJanitorJob> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Hangfire entry point. Wraps the inner <see cref="Run(CancellationToken)"/>
    /// in a linked <see cref="CancellationTokenSource"/> with a hard 280-second
    /// budget so the job can never hold the
    /// <see cref="DisableConcurrentExecutionAttribute"/> lock past the 300-second
    /// TTL — even if a database operation hangs forever. When the CTS trips,
    /// control returns, Hangfire releases the lock, and the next tick acquires
    /// on schedule.
    ///
    /// <para><see cref="AutomaticRetry"/> with <c>Attempts = 0</c> stops Hangfire
    /// from auto-requeuing a partially-cancelled run on top of the next scheduled
    /// tick.</para>
    /// </summary>
    [DisableConcurrentExecution(timeoutInSeconds: 300)]
    [AutomaticRetry(Attempts = 0)]
    public async Task Run(IJobCancellationToken hangfireCt)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(hangfireCt.ShutdownToken);
        cts.CancelAfter(TimeSpan.FromSeconds(280));
        await Run(cts.Token);
    }

    /// <summary>
    /// Process one janitor pass. The
    /// <see cref="DisableConcurrentExecutionAttribute"/> on the entry point
    /// guards against two workers running this method at the same time across
    /// the cluster.
    /// </summary>
    public async Task Run(CancellationToken ct = default)
    {
        var cutoff = DateTime.UtcNow.AddDays(-RetentionDays);

        // IgnoreQueryFilters: the global filter on ProjectRuntimes hides anything
        // with IsDeleted=true, but those are exactly the rows we need to find.
        var eligible = await _db.ProjectRuntimes
            .IgnoreQueryFilters()
            .Where(r => r.State == RuntimeState.Deleted
                     && r.DeletedAt != null
                     && r.DeletedAt < cutoff)
            .ToListAsync(ct);

        if (eligible.Count == 0)
        {
            _logger.LogInformation(
                "RuntimeJanitorJob: nothing to delete (cutoff={Cutoff:O}, retention_days={RetentionDays})",
                cutoff, RetentionDays);
            return;
        }

        var oldest = eligible.Min(r => r.DeletedAt!.Value);
        var newest = eligible.Max(r => r.DeletedAt!.Value);

        // RemoveRange triggers a hard delete — the global soft-delete filter
        // doesn't apply to Remove, so this is a real DELETE on the table.
        _db.ProjectRuntimes.RemoveRange(eligible);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "RuntimeJanitorJob hard-deleted {Count} ProjectRuntime rows (oldest_deleted_at={Oldest:O}, newest_deleted_at={Newest:O}, cutoff={Cutoff:O})",
            eligible.Count, oldest, newest, cutoff);
    }
}
