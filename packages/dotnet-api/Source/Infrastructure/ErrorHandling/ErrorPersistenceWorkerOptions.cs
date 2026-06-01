namespace Source.Infrastructure.ErrorHandling;

/// <summary>
/// Tunable knobs for <see cref="ErrorPersistenceWorker"/>. Defaults are calibrated for
/// production ("flush at 100 rows or every 500ms, whichever first"); tests override to
/// much shorter windows so they don't have to wait half a second per scenario.
/// </summary>
public sealed class ErrorPersistenceWorkerOptions
{
    /// <summary>
    /// Maximum number of <see cref="ErrorEntry"/> rows to include in a single
    /// <c>AddRange + SaveChanges</c>. Larger values amortise DB roundtrip cost at the
    /// expense of per-row latency during bursts.
    /// </summary>
    public int MaxBatchSize { get; init; } = 100;

    /// <summary>
    /// Maximum amount of time a partial batch may sit in memory before being flushed.
    /// Trades off freshness of the <c>ErrorLogs</c> table (small value) versus DB write
    /// amplification at steady-state low volume (large value).
    /// </summary>
    public TimeSpan BatchTimeout { get; init; } = TimeSpan.FromMilliseconds(500);
}
