namespace Source.Infrastructure.ErrorHandling;

/// <summary>
/// Process-wide counters for the error-capture pipeline, kept deliberately simple:
/// plain <see cref="Interlocked"/>-backed <c>long</c> fields with a couple of helper
/// methods. Readable, allocation-free, and good enough to expose via <c>ILogger</c>
/// today and a metrics endpoint tomorrow.
///
/// <para><b>Scope.</b> Five monotonic counters cover the full lifecycle of an entry:</para>
/// <list type="bullet">
///   <item><see cref="Enqueued"/>      — a write landed in the bounded channel.</item>
///   <item><see cref="Dropped"/>       — an entry was evicted because the bounded channel
///                                       was full (<c>BoundedChannelFullMode.DropOldest</c>).</item>
///   <item><see cref="Persisted"/>     — a row was written to the database by the worker.</item>
///   <item><see cref="PersistFailed"/> — a batch was dropped because <c>SaveChanges</c> threw.
///                                       Counted per ENTRY in the failed batch.</item>
///   <item><see cref="Suppressed"/>    — an entry's detail row was skipped because the owning
///                                       signature was already at its rolling-sample cap.</item>
/// </list>
///
/// <para>Queue depth is intentionally NOT a counter here — it's a gauge (current value),
/// not a monotonic total, so <see cref="ErrorPipelineSummaryReporter"/> reads it straight
/// off the <see cref="ErrorQueue"/>.</para>
/// </summary>
public static class ErrorPipelineMetrics
{
    private static long _enqueued;
    private static long _dropped;
    private static long _persisted;
    private static long _persistFailed;
    private static long _suppressed;

    /// <summary>Cumulative count of entries successfully written to the bounded channel.</summary>
    public static long Enqueued => Interlocked.Read(ref _enqueued);

    /// <summary>
    /// Cumulative count of entries evicted by the bounded channel's DropOldest policy.
    /// Best-effort: the channel gives no eviction callback, so this is bumped at the
    /// same call site as <see cref="ErrorQueue.DroppedCount"/> infers it.
    /// </summary>
    public static long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>Cumulative count of error rows successfully written to the database.</summary>
    public static long Persisted => Interlocked.Read(ref _persisted);

    /// <summary>Cumulative count of error rows that were dropped because SaveChanges threw.</summary>
    public static long PersistFailed => Interlocked.Read(ref _persistFailed);

    /// <summary>
    /// Cumulative count of error rows whose detail row was suppressed because the owning
    /// signature had already reached its rolling-sample cap (10 samples per signature).
    /// The signature's <c>Count</c> still increments — only the per-occurrence ErrorLog row
    /// is skipped. This is the "10,000 occurrences of same fingerprint → 1 insert + updates,
    /// not 10,000 inserts" guarantee from the spec in metric form.
    /// </summary>
    public static long Suppressed => Interlocked.Read(ref _suppressed);

    public static void IncrementEnqueued(int count = 1) => Interlocked.Add(ref _enqueued, count);
    public static void IncrementDropped(int count = 1) => Interlocked.Add(ref _dropped, count);
    public static void IncrementPersisted(int count) => Interlocked.Add(ref _persisted, count);
    public static void IncrementPersistFailed(int count) => Interlocked.Add(ref _persistFailed, count);
    public static void IncrementSuppressed(int count) => Interlocked.Add(ref _suppressed, count);

    /// <summary>
    /// Capture all five counters in a single consistent read. Used by the summary reporter
    /// and (if/when present) the stats endpoint so they render a coherent snapshot rather
    /// than a set of values smeared across multiple <c>Interlocked.Read</c> calls.
    /// </summary>
    public static CounterSnapshot Snapshot() => new(
        Enqueued,
        Dropped,
        Persisted,
        PersistFailed,
        Suppressed);

    /// <summary>
    /// Immutable point-in-time view of the five pipeline counters. Records are used for the
    /// obvious value-semantics and for easy JSON serialisation from the stats endpoint.
    /// </summary>
    public record CounterSnapshot(
        long Enqueued,
        long Dropped,
        long Persisted,
        long PersistFailed,
        long Suppressed);
}
