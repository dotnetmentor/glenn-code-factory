using System.Threading.Channels;

namespace Source.Infrastructure.ErrorHandling;

/// <summary>
/// Singleton bounded channel for queueing errors to be persisted by the background worker.
///
/// <para>Capacity is fixed at 10_000; once full, the channel drops the OLDEST queued entry
/// to make room — we'd rather lose historical context than lose a fresh burst that likely
/// indicates the current incident.</para>
///
/// <para><b>Never-throw contract:</b> <see cref="EnqueueAsync"/> is explicitly wrapped so
/// that no failure of the pipeline can propagate into user code. An error logger that can
/// crash its host is worse than no error logger at all.</para>
///
/// <para>PII/secrets are redacted via <see cref="IPiiRedactor"/> on the single hot path
/// (inside <see cref="EnqueueAsync"/>) before anything is written to the channel, so
/// downstream consumers and fingerprinting always see sanitized text.</para>
/// </summary>
public class ErrorQueue
{
    private const int Capacity = 10_000;

    private readonly Channel<ErrorEntry> _channel = Channel.CreateBounded<ErrorEntry>(
        new BoundedChannelOptions(Capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

    private readonly IPiiRedactor _redactor;

    // Best-effort drop accounting. Channel<T> with DropOldest silently evicts entries and
    // provides no callback; we infer dropped = enqueued - read - currentDepth. At steady
    // drain (depth == 0), dropped == enqueued - read exactly. During live operation the
    // value may briefly lag by items currently between TryWrite and the reader loop.
    private long _enqueued;
    private long _read;
    private long _failureCount;

    // Running total of dropped entries already reported to ErrorPipelineMetrics.Dropped.
    // On every successful enqueue we recompute (enqueued - read - depth); if that number
    // has grown since the last report, we bump the static metric by the delta. This turns
    // the inferred gauge into a monotonic counter that matches DroppedCount in aggregate.
    private long _reportedDropped;

    public ErrorQueue(IPiiRedactor redactor)
    {
        _redactor = redactor;
    }

    /// <summary>
    /// Approximate count of entries evicted due to <see cref="BoundedChannelFullMode.DropOldest"/>.
    /// Exact once the reader has fully drained the channel; best-effort otherwise.
    /// </summary>
    public long DroppedCount
    {
        get
        {
            var enq = Interlocked.Read(ref _enqueued);
            var rd = Interlocked.Read(ref _read);
            var depth = _channel.Reader.Count;
            var dropped = enq - rd - depth;
            return dropped < 0 ? 0 : dropped;
        }
    }

    /// <summary>
    /// Count of times <see cref="EnqueueAsync"/> swallowed an exception from the pipeline
    /// itself (e.g. the channel was already completed). Exposed for observability.
    /// </summary>
    public long FailureCount => Interlocked.Read(ref _failureCount);

    /// <summary>
    /// Current (approximate) number of items sitting in the channel, from
    /// <see cref="System.Threading.Channels.ChannelReader{T}.Count"/>. This is a gauge,
    /// not a monotonic counter — used by <see cref="ErrorPipelineSummaryReporter"/> to
    /// render the QueueDepth field in its summary line.
    /// </summary>
    public int ApproximateDepth => _channel.Reader.Count;

    /// <summary>
    /// Enqueue an error entry. Never throws — all failures are counted and logged to stderr.
    /// PII is redacted before the entry is written to the channel.
    ///
    /// <para>Virtual only so that <see cref="Source.Shared.Behaviors.ErrorCaptureBehavior{TRequest, TResponse}"/>'s
    /// defense-in-depth test can inject a broken-queue subclass that throws. Production code
    /// MUST rely on the contract that this method does not throw.</para>
    /// </summary>
    public virtual ValueTask EnqueueAsync(ErrorEntry error)
    {
        try
        {
            var redacted = error with
            {
                Message = _redactor.Redact(error.Message) ?? error.Message,
                StackTrace = _redactor.Redact(error.StackTrace),
                ContextData = _redactor.Redact(error.ContextData)
            };

            // TryWrite on a bounded DropOldest channel returns true unless the writer is
            // completed — in which case we silently drop, as we would prefer over throwing.
            if (_channel.Writer.TryWrite(redacted))
            {
                Interlocked.Increment(ref _enqueued);
                ErrorPipelineMetrics.IncrementEnqueued();

                // Reconcile dropped-count into the static metric. The channel silently evicts
                // the oldest entry when at capacity; we see that only via the inferred formula
                // below. We bump ErrorPipelineMetrics.Dropped by the delta since the last write,
                // so the monotonic static counter stays in sync with the inferred gauge.
                ReportDroppedDeltaIfAny();
            }
            else
            {
                Interlocked.Increment(ref _failureCount);
            }
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _failureCount);
            try
            {
                Console.Error.WriteLine($"[ErrorQueue] enqueue failure: {ex.Message}");
            }
            catch
            {
                // stderr is the fallback of last resort; if even that throws, we're done.
            }
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Drains the channel. Wraps the raw reader so we can track read-count for drop
    /// accounting without requiring the caller to do anything special.
    /// </summary>
    public async IAsyncEnumerable<ErrorEntry> ReadAllAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var entry in _channel.Reader.ReadAllAsync(ct))
        {
            Interlocked.Increment(ref _read);
            yield return entry;
        }
    }

    /// <summary>
    /// Direct access to the underlying reader for consumers that need the low-level
    /// <see cref="ChannelReader{T}.WaitToReadAsync"/> / <see cref="ChannelReader{T}.TryRead"/>
    /// primitives — specifically <see cref="ErrorPersistenceWorker"/>, whose batched
    /// read-up-to-N-or-wait-M loop cannot be expressed through the IAsyncEnumerable API.
    ///
    /// Reads taken via this reader bypass the <c>_read</c> counter so <see cref="DroppedCount"/>'s
    /// best-effort math only holds exactly for consumers that use <see cref="ReadAllAsync"/>.
    /// The worker doesn't participate in drop accounting (it's the single trusted consumer)
    /// so this trade-off is acceptable.
    /// </summary>
    internal ChannelReader<ErrorEntry> ReaderForWorker => _channel.Reader;

    /// <summary>
    /// Signals no more writes will be made. Used by tests to terminate <see cref="ReadAllAsync"/>.
    /// </summary>
    public void CompleteWriting() => _channel.Writer.TryComplete();

    /// <summary>
    /// Best-effort propagation of the inferred drop-count into the static
    /// <see cref="ErrorPipelineMetrics.Dropped"/> counter.
    ///
    /// <para>The bounded <see cref="System.Threading.Channels.BoundedChannelFullMode.DropOldest"/>
    /// channel gives no eviction callback, so we can only infer drops after the fact:
    /// <c>dropped = enqueued - read - currentDepth</c>. On every successful enqueue we
    /// recompute that number and, if it has grown beyond what we've previously reported,
    /// bump the static counter by the delta. That turns a gauge-shaped value into a
    /// monotonic counter that aggregate-matches the inferred <see cref="DroppedCount"/>.</para>
    ///
    /// <para><b>Concurrency.</b> We use a short <c>Interlocked.CompareExchange</c> loop on
    /// <c>_reportedDropped</c> so two concurrent writers who both observe an increased
    /// inferred-drop value do not double-count: whichever one wins the CAS owns the delta,
    /// the loser just observes the already-increased reported value and walks away with
    /// delta == 0.</para>
    /// </summary>
    private void ReportDroppedDeltaIfAny()
    {
        var enq = Interlocked.Read(ref _enqueued);
        var rd = Interlocked.Read(ref _read);
        var depth = _channel.Reader.Count;
        var inferred = enq - rd - depth;
        if (inferred <= 0)
        {
            return;
        }

        while (true)
        {
            var reported = Interlocked.Read(ref _reportedDropped);
            if (inferred <= reported)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _reportedDropped, inferred, reported) == reported)
            {
                ErrorPipelineMetrics.IncrementDropped((int)(inferred - reported));
                return;
            }
        }
    }
}
