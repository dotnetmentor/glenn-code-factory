using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Source.Features.ErrorLog.Models;
using Source.Infrastructure;
using Source.Shared;

namespace Source.Infrastructure.ErrorHandling;

/// <summary>
/// Background service that reads <see cref="ErrorEntry"/> items from the
/// <see cref="ErrorQueue"/> and persists them to the database as
/// <see cref="Features.ErrorLog.Models.ErrorLog"/> rows.
///
/// <para><b>Batched writes.</b> The worker reads up to
/// <see cref="ErrorPersistenceWorkerOptions.MaxBatchSize"/> entries or waits up to
/// <see cref="ErrorPersistenceWorkerOptions.BatchTimeout"/>, whichever comes first, then
/// does a single <c>AddRange + SaveChanges</c>. This collapses a worst-case
/// 100-inserts-per-burst into one round trip and is the whole point of this file.</para>
///
/// <para><b>Never-crash contract.</b> Every failure inside a flush is swallowed: the
/// batch is logged, the <see cref="ErrorPipelineMetrics.PersistFailed"/> counter is
/// bumped, and the worker keeps spinning. Dropping errors is bad; crashing the error
/// logger and taking the host down with it is worse. We deliberately do NOT re-enqueue a
/// failed batch — during a storm that would amplify the problem.</para>
///
/// <para><b>Graceful drain on shutdown.</b> When the stopping token fires, we drain any
/// remaining entries out of the channel with a bounded final flush so in-flight errors
/// aren't lost to a clean shutdown.</para>
///
/// <para><b>Time abstraction.</b> The worker takes an <see cref="IClock"/>; it is used
/// by the batch-collection helper to compute the flush deadline. Wall-clock is still
/// what <see cref="Task.Delay"/> / <see cref="CancellationTokenSource.CancelAfter"/> use
/// under the hood, but the public surface reads time through <see cref="IClock"/> so
/// tests can reason deterministically about "have we passed the deadline?".</para>
/// </summary>
public class ErrorPersistenceWorker : BackgroundService
{
    /// <summary>
    /// Maximum number of detail <see cref="Features.ErrorLog.Models.ErrorLog"/> rows kept
    /// per <see cref="ErrorSignature"/>. Once a signature has this many samples, additional
    /// occurrences bump its <c>Count</c> but don't persist a new detail row (they are counted
    /// via <see cref="ErrorPipelineMetrics.Suppressed"/> instead). This is the bound that
    /// turns "10,000 occurrences of same fingerprint" into "1 insert + updates".
    /// </summary>
    internal const int MaxSamplesPerSignature = 10;

    private readonly ErrorQueue _errorQueue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ErrorPersistenceWorker> _logger;
    private readonly IClock _clock;
    private readonly IErrorSignatureHasher _hasher;
    private readonly ErrorPersistenceWorkerOptions _options;

    public ErrorPersistenceWorker(
        ErrorQueue errorQueue,
        IServiceScopeFactory scopeFactory,
        ILogger<ErrorPersistenceWorker> logger,
        IClock clock,
        IErrorSignatureHasher hasher,
        ErrorPersistenceWorkerOptions? options = null)
    {
        _errorQueue = errorQueue;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _clock = clock;
        _hasher = hasher;
        _options = options ?? new ErrorPersistenceWorkerOptions();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ErrorPersistenceWorker started (batchSize={BatchSize}, timeout={TimeoutMs}ms)",
            _options.MaxBatchSize, (int)_options.BatchTimeout.TotalMilliseconds);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var batch = await CollectBatchAsync(
                    _errorQueue.ReaderForWorker,
                    _options.MaxBatchSize,
                    _options.BatchTimeout,
                    _clock,
                    stoppingToken);

                if (batch.Count > 0)
                {
                    await FlushAsync(batch, stoppingToken);
                }
                else if (_errorQueue.ReaderForWorker.Completion.IsCompleted)
                {
                    // Channel is drained and no more writes will come. Time to exit.
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Expected on shutdown — fall through to the drain pass.
        }

        // Drain pass: the stopping token may have cancelled mid-batch. Flush whatever
        // the channel still holds so we don't lose in-flight errors on a graceful stop.
        // We use CancellationToken.None for the drain itself — by the time we get here
        // stoppingToken is cancelled and the inner linked CTS would exit immediately.
        await DrainAsync();

        _logger.LogInformation("ErrorPersistenceWorker stopped");
    }

    /// <summary>
    /// Reads from <paramref name="reader"/> until either <paramref name="maxBatchSize"/>
    /// entries have been collected, the batch <paramref name="timeout"/> has elapsed
    /// (measured via <paramref name="clock"/>), the channel is marked complete, or
    /// <paramref name="stoppingToken"/> fires — whichever happens first.
    ///
    /// Returns whatever was collected by the exit condition, possibly empty.
    ///
    /// This is the read-up-to-N-or-wait-M core of the batching strategy; it is
    /// <c>internal static</c> rather than a private instance method so it can be unit-
    /// tested without spinning up a full worker + DbContext.
    /// </summary>
    internal static async Task<List<ErrorEntry>> CollectBatchAsync(
        ChannelReader<ErrorEntry> reader,
        int maxBatchSize,
        TimeSpan timeout,
        IClock clock,
        CancellationToken stoppingToken)
    {
        var batch = new List<ErrorEntry>(capacity: maxBatchSize);
        var deadline = clock.UtcNow + timeout;

        while (batch.Count < maxBatchSize)
        {
            if (stoppingToken.IsCancellationRequested)
            {
                return batch;
            }

            // Synchronous drain first — if items are already sitting in the channel we
            // take them without setting up a cancellation timer.
            while (batch.Count < maxBatchSize && reader.TryRead(out var ready))
            {
                batch.Add(ready);
            }
            if (batch.Count >= maxBatchSize)
            {
                return batch;
            }

            var remaining = deadline - clock.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                return batch;
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            timeoutCts.CancelAfter(remaining);

            try
            {
                var hasMore = await reader.WaitToReadAsync(timeoutCts.Token);
                if (!hasMore)
                {
                    // Channel writer has completed; nothing more will arrive.
                    return batch;
                }
            }
            catch (OperationCanceledException)
            {
                // Either the batch timer fired (timeout branch → flush what we have)
                // or the caller's stoppingToken fired (shutdown branch → same thing).
                return batch;
            }
        }

        return batch;
    }

    /// <summary>
    /// Persist one batch of <see cref="ErrorEntry"/> items. This is the hot path and also
    /// where signature aggregation happens.
    ///
    /// <para><b>Flow.</b> For each entry in the batch, compute its signature hash and group
    /// entries by hash in-memory. For each group: upsert the <see cref="ErrorSignature"/>
    /// row (INSERT on first occurrence, UPDATE count/last-seen otherwise), then add up to
    /// <see cref="MaxSamplesPerSignature"/> detail <see cref="Features.ErrorLog.Models.ErrorLog"/>
    /// rows; any entries beyond that cap bump the signature count but are counted as suppressed
    /// rather than persisted.</para>
    ///
    /// <para><b>Why group first.</b> A storm of 15 identical errors arriving in one batch
    /// should produce ONE UPDATE on the signature row, not 15. The in-memory group keeps the
    /// DB work bounded by "unique hashes per batch" rather than "entries per batch".</para>
    ///
    /// <para><b>Cancellation.</b> We deliberately do NOT pass the caller's cancellation token
    /// into <c>SaveChangesAsync</c>. If the host is shutting down, we want an in-progress
    /// flush to complete and land its rows — aborting mid-save would double-cost us: once to
    /// drop the in-flight batch, and again because the <see cref="DrainAsync"/> that follows
    /// would also see a cancelled token and not get any further.</para>
    ///
    /// <para><b>Known concurrency limitation.</b> The upsert pattern (fetch-then-add) is not
    /// strictly safe if two workers run in parallel on the same DB; the unique index on
    /// <c>ErrorSignature.Hash</c> would reject the second insert. This app runs a single
    /// worker instance so the race cannot occur; hardening (catch <c>DbUpdateException</c>
    /// and retry once) is left for a future phase if multi-worker operation is ever added.</para>
    ///
    /// <para>Internal for unit testing — <c>InternalsVisibleTo Api.Tests</c> is set on the
    /// production assembly. Tests drive this method directly with a deterministic batch to
    /// assert the aggregation contract without going through the queue + timer loop.</para>
    /// </summary>
    internal async Task FlushAsync(List<ErrorEntry> batch, CancellationToken _)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        try
        {
            await FlushWithAggregationAsync(dbContext, batch);
            ErrorPipelineMetrics.IncrementPersisted(batch.Count);
            _logger.LogDebug("Persisted {Count} error log entries", batch.Count);
        }
        catch (Exception ex)
        {
            // Hard contract: no re-throw, no re-enqueue. A failed batch is dropped and
            // counted so operators can see the pipeline is under duress. Re-enqueueing
            // during a storm would amplify the problem (same DB failure on retry).
            _logger.LogError(ex,
                "ErrorPersistenceWorker flush failed; dropping batch of {Count}",
                batch.Count);
            ErrorPipelineMetrics.IncrementPersistFailed(batch.Count);
        }
    }

    /// <summary>
    /// Core aggregation logic — one read query ("existing signatures for these hashes"),
    /// one per-group sample-count probe, and one final <c>SaveChangesAsync</c> covering all
    /// inserts and updates for the batch. The goal is: a storm of 100 same-hash entries
    /// costs one row update and at most 10 row inserts, not 100 inserts.
    /// </summary>
    private async Task FlushWithAggregationAsync(ApplicationDbContext dbContext, List<ErrorEntry> batch)
    {
        var now = _clock.UtcNow;

        // 1. Group in-memory by hash so we do one upsert per unique signature, not per entry.
        var groups = batch
            .Select(entry => new { Entry = entry, Hash = _hasher.Hash(entry) })
            .GroupBy(x => x.Hash)
            .Select(g => new { Hash = g.Key, Entries = g.Select(x => x.Entry).ToList() })
            .ToList();

        // 2. Single round-trip to fetch any signatures that already exist for these hashes.
        //    Using a dictionary keyed by hash makes the per-group lookup O(1).
        var hashes = groups.Select(g => g.Hash).ToList();
        var existingSignatures = await dbContext.ErrorSignatures
            .Where(s => hashes.Contains(s.Hash))
            .ToDictionaryAsync(s => s.Hash, CancellationToken.None);

        foreach (var group in groups)
        {
            var entries = group.Entries;
            var firstEntry = entries[0];

            ErrorSignature signature;
            int existingSampleCount;

            if (existingSignatures.TryGetValue(group.Hash, out var found))
            {
                // UPDATE path — bump count, advance last-seen/updated-at.
                signature = found;
                signature.Count += entries.Count;
                signature.LastSeenAt = now;
                signature.UpdatedAt = now;

                // Probe current sample count so the rolling cap can see pre-existing rows.
                // One count query per unique hash in the batch — bounded and cheap.
                existingSampleCount = await dbContext.ErrorLogs
                    .CountAsync(e => e.SignatureId == signature.Id, CancellationToken.None);
            }
            else
            {
                // INSERT path — brand-new signature.
                signature = new ErrorSignature
                {
                    Id = Guid.NewGuid(),
                    Hash = group.Hash,
                    Source = firstEntry.Source,
                    Severity = firstEntry.Severity,
                    FirstSeenAt = now,
                    LastSeenAt = now,
                    Count = entries.Count,
                    IsResolved = false,
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                dbContext.ErrorSignatures.Add(signature);
                existingSampleCount = 0;
            }

            // 3. Rolling-sample cap: persist up to (MaxSamplesPerSignature - existing) detail
            //    rows from this group. The remainder is suppressed (count still climbs).
            var capacityForSamples = Math.Max(0, MaxSamplesPerSignature - existingSampleCount);
            var toPersist = capacityForSamples == 0
                ? new List<ErrorEntry>()
                : entries.Take(capacityForSamples).ToList();
            var suppressedCount = entries.Count - toPersist.Count;

            foreach (var entry in toPersist)
            {
                dbContext.ErrorLogs.Add(new Features.ErrorLog.Models.ErrorLog
                {
                    Id = Guid.NewGuid(),
                    Message = entry.Message,
                    StackTrace = entry.StackTrace,
                    Source = entry.Source,
                    Severity = entry.Severity,
                    CorrelationId = entry.CorrelationId,
                    RequestPath = entry.RequestPath,
                    RequestMethod = entry.RequestMethod,
                    ContextData = entry.ContextData,
                    IsResolved = false,
                    ResolvedAt = null,
                    CreatedAt = entry.OccurredAt,
                    UpdatedAt = entry.OccurredAt,
                    SignatureId = signature.Id,
                });
            }

            if (suppressedCount > 0)
            {
                ErrorPipelineMetrics.IncrementSuppressed(suppressedCount);
            }
        }

        // 4. One SaveChanges per batch — keeps the "single round trip per flush" property
        //    of the pre-aggregation implementation.
        await dbContext.SaveChangesAsync(CancellationToken.None);
    }

    /// <summary>
    /// Drain any remaining entries synchronously on the way out. Best-effort: if the
    /// drain flush itself fails it is logged and swallowed — the process is already
    /// shutting down, there's nowhere to escalate.
    /// </summary>
    private async Task DrainAsync()
    {
        var drained = new List<ErrorEntry>();
        while (_errorQueue.ReaderForWorker.TryRead(out var entry))
        {
            drained.Add(entry);
            // Flush in chunks of MaxBatchSize so the final write isn't pathologically big.
            if (drained.Count >= _options.MaxBatchSize)
            {
                await FlushAsync(drained, CancellationToken.None);
                drained = new List<ErrorEntry>();
            }
        }

        if (drained.Count > 0)
        {
            await FlushAsync(drained, CancellationToken.None);
        }
    }
}
