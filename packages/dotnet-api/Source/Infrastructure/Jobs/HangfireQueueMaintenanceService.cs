using System.Diagnostics;
using Npgsql;
using NpgsqlTypes;
using Source.Infrastructure.Database;

namespace Source.Infrastructure.Jobs;

/// <summary>
/// Drops stale entries from Hangfire's <c>default</c> queue, and keeps dropping
/// them — this is a standing guardrail, not a one-off cleanup.
///
/// <para><b>The incident this exists for.</b> Every job this platform enqueues is
/// either a minutely sweep or an ad-hoc kick whose work is re-derivable from live
/// database state (provision this runtime, respawn that one). None of it is
/// meaningful once it is minutes old: a sweep has a fresh copy along every minute,
/// and an ad-hoc kick re-checks the row's state and no-ops if another path already
/// handled it. So when throughput fell behind — three minutely jobs each holding a
/// worker ~50s against a 2-worker server, see <see cref="ContinuousSweepService{TJob}"/>
/// — the queue did not degrade gracefully, it grew without bound: 231,777 jobs deep
/// with a 20-day-old head, all of it worthless, and all of it in front of every new
/// runtime's provisioning kick. Removing the root cause stops the queue growing;
/// this stops a queue that HAS grown from staying poisoned for weeks.</para>
///
/// <para><b>Why it is safe to drop.</b> Only entries that are still waiting
/// (<c>fetchedat IS NULL</c> — nothing is executing them) and whose job was created
/// longer than <see cref="StaleAfter"/> ago are removed. On a healthy queue that
/// set is empty and this service is a no-op: jobs are picked up in about a second,
/// so anything that has been waiting half an hour is by definition abandoned.
/// Scheduled (delayed) jobs are untouched — they haven't been enqueued yet — and so
/// are Processing and Failed rows, which carry real state and real history.</para>
///
/// <para>Dropped jobs are left in the <c>Enqueued</c> state but stamped with an
/// <c>expireat</c>, so Hangfire's own expiration manager reclaims the job, state and
/// parameter rows through the schema's cascade rather than this code reaching into
/// tables it doesn't own.</para>
/// </summary>
public sealed class HangfireQueueMaintenanceService : BackgroundService
{
    private const string Schema = "hangfire";

    /// <summary>Bound on batches per pass, so a pathological queue can't monopolise the process.</summary>
    private const int MaxBatchesPerPass = 250;

    /// <summary>Bound on rows touched per statement, so no single statement takes a long lock.</summary>
    private readonly int _batchSize;

    private readonly TimeSpan _interval;
    private readonly TimeSpan _staleAfter;
    private readonly bool _enabled;
    private readonly string _connectionString;
    private readonly ILogger<HangfireQueueMaintenanceService> _logger;

    public HangfireQueueMaintenanceService(
        IConfiguration configuration,
        ILogger<HangfireQueueMaintenanceService> logger)
    {
        _logger = logger;
        _enabled = configuration.GetValue("Hangfire:QueueMaintenance:Enabled", true);
        _staleAfter = TimeSpan.FromMinutes(
            configuration.GetValue("Hangfire:QueueMaintenance:StaleAfterMinutes", 30));
        _interval = TimeSpan.FromMinutes(
            configuration.GetValue("Hangfire:QueueMaintenance:IntervalMinutes", 5));
        _batchSize = configuration.GetValue("Hangfire:QueueMaintenance:BatchSize", 5_000);
        _connectionString = PostgresConnectionString.Resolve(configuration);
    }

    private TimeSpan StaleAfter => _staleAfter;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            _logger.LogInformation("Hangfire queue maintenance is disabled by configuration.");
            return;
        }

        try
        {
            // Let the Hangfire server settle first: on a jammed queue the very first
            // pass is the big one, and we want it after startup, not during it.
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

            using var timer = new PeriodicTimer(_interval);
            do
            {
                try
                {
                    await PurgeAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Hangfire queue maintenance pass failed; will retry next interval.");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    /// <summary>
    /// One maintenance pass. Public so it can be driven directly from a test or an
    /// admin action without waiting on the timer. Returns the number of stale queue
    /// entries dropped.
    /// </summary>
    public async Task<int> PurgeAsync(CancellationToken ct)
    {
        var cutoff = AsHangfireTimestamp(DateTime.UtcNow - StaleAfter);
        var started = Stopwatch.GetTimestamp();

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        var totalDropped = 0;
        for (var batch = 0; batch < MaxBatchesPerPass; batch++)
        {
            var dropped = await DropStaleQueueEntriesAsync(connection, cutoff, ct);
            totalDropped += dropped;
            if (dropped < _batchSize)
            {
                break;
            }
        }

        if (totalDropped == 0)
        {
            _logger.LogDebug("Hangfire queue maintenance: no stale queue entries.");
            return 0;
        }

        var expired = 0;
        for (var batch = 0; batch < MaxBatchesPerPass; batch++)
        {
            var stamped = await ExpireOrphanedJobsAsync(connection, cutoff, ct);
            expired += stamped;
            if (stamped < _batchSize)
            {
                break;
            }
        }

        _logger.LogWarning(
            "Hangfire queue maintenance dropped {Dropped} stale queue entries (waiting longer than {StaleMinutes} min) and marked {Expired} orphaned job rows for expiry in {ElapsedMs}ms. A non-zero count means throughput fell behind — check worker count and long-running jobs.",
            totalDropped,
            StaleAfter.TotalMinutes,
            expired,
            (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds);

        return totalDropped;
    }

    /// <summary>
    /// Remove queue entries that are still waiting and whose job is older than the
    /// cutoff. <c>fetchedat IS NULL</c> is the safety interlock: an entry a worker
    /// has already picked up is never touched.
    /// </summary>
    private async Task<int> DropStaleQueueEntriesAsync(
        NpgsqlConnection connection,
        DateTime cutoff,
        CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            $"""
            DELETE FROM {Schema}.jobqueue q
            USING (
                SELECT candidate.id
                FROM {Schema}.jobqueue candidate
                JOIN {Schema}.job j ON j.id = candidate.jobid
                WHERE candidate.fetchedat IS NULL
                  AND j.createdat < @cutoff
                ORDER BY candidate.id
                LIMIT @batchSize
            ) AS stale
            WHERE q.id = stale.id;
            """,
            connection);
        cmd.Parameters.Add(new NpgsqlParameter("cutoff", NpgsqlDbType.Timestamp) { Value = cutoff });
        cmd.Parameters.AddWithValue("batchSize", _batchSize);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Stamp an expiry on jobs left in <c>Enqueued</c> with no queue entry, so
    /// Hangfire's expiration manager reclaims them (and their state and parameter
    /// rows, via the schema's ON DELETE CASCADE) on its own schedule.
    /// </summary>
    private async Task<int> ExpireOrphanedJobsAsync(
        NpgsqlConnection connection,
        DateTime cutoff,
        CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            $"""
            UPDATE {Schema}.job
            SET expireat = @expireAt
            WHERE id IN (
                SELECT j.id
                FROM {Schema}.job j
                WHERE j.expireat IS NULL
                  AND j.statename = 'Enqueued'
                  AND j.createdat < @cutoff
                  AND NOT EXISTS (SELECT 1 FROM {Schema}.jobqueue q WHERE q.jobid = j.id)
                LIMIT @batchSize
            );
            """,
            connection);
        cmd.Parameters.Add(new NpgsqlParameter("cutoff", NpgsqlDbType.Timestamp) { Value = cutoff });
        cmd.Parameters.Add(new NpgsqlParameter("expireAt", NpgsqlDbType.Timestamp)
        {
            Value = AsHangfireTimestamp(DateTime.UtcNow),
        });
        cmd.Parameters.AddWithValue("batchSize", _batchSize);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Hangfire's <c>createdat</c> and <c>expireat</c> columns are
    /// <c>TIMESTAMP</c> — no time zone — holding UTC by convention. Npgsql maps a
    /// <see cref="DateTime"/> whose <see cref="DateTime.Kind"/> is
    /// <see cref="DateTimeKind.Utc"/> to <c>timestamptz</c> instead, which Postgres
    /// would then coerce using the session time zone: on a non-UTC session that
    /// silently shifts the cutoff by the offset, and the purge quietly compares
    /// against the wrong instant. Stripping the kind (and binding the parameter as
    /// <c>NpgsqlDbType.Timestamp</c>) keeps both sides in the same units as the
    /// values Hangfire itself writes.
    /// </summary>
    private static DateTime AsHangfireTimestamp(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Unspecified);
}
