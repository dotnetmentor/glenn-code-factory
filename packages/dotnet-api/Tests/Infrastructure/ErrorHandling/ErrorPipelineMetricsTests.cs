using Source.Infrastructure.ErrorHandling;

namespace Api.Tests.Infrastructure.ErrorHandling;

/// <summary>
/// Tests for <see cref="ErrorPipelineMetrics"/> — the process-wide counter surface for
/// the error-capture pipeline.
///
/// <para><b>Important caveat.</b> These counters are <c>static</c> state that persists for
/// the lifetime of the test process, so every assertion here is RELATIVE: capture the
/// starting value, do some work, assert the delta. Never assert an absolute value — other
/// tests in the suite bump the same counters and the order of execution is not guaranteed.</para>
/// </summary>
public class ErrorPipelineMetricsTests
{
    private static ErrorEntry NewEntry(string message) => new(
        Message: message,
        StackTrace: null,
        Source: "Test",
        Severity: "Error",
        CorrelationId: null,
        RequestPath: null,
        RequestMethod: null,
        ContextData: null,
        OccurredAt: DateTime.UtcNow);

    // -----------------------------------------------------------------------------
    // Enqueued counter — bumps on every successful channel write from EnqueueAsync.
    // -----------------------------------------------------------------------------

    [Fact]
    public async Task Enqueued_IncrementsOnEnqueue()
    {
        var queue = new ErrorQueue(new PiiRedactor());
        var before = ErrorPipelineMetrics.Enqueued;

        await queue.EnqueueAsync(NewEntry("one"));
        await queue.EnqueueAsync(NewEntry("two"));
        await queue.EnqueueAsync(NewEntry("three"));

        (ErrorPipelineMetrics.Enqueued - before).Should().Be(3);
    }

    [Fact]
    public async Task Enqueued_DoesNotIncrement_WhenWriterCompleted()
    {
        // If the channel writer is already completed, TryWrite returns false and nothing
        // should land in Enqueued — the failure counter on ErrorQueue picks it up instead.
        var queue = new ErrorQueue(new PiiRedactor());
        queue.CompleteWriting();
        var before = ErrorPipelineMetrics.Enqueued;

        await queue.EnqueueAsync(NewEntry("post-complete"));

        (ErrorPipelineMetrics.Enqueued - before).Should().Be(0);
    }

    // -----------------------------------------------------------------------------
    // Dropped counter — bumps when DropOldest evicts entries because the bounded
    // channel overflowed. Because Channel<T> with DropOldest is silent, the metric
    // is inferred (enqueued - read - depth) exactly as DroppedCount does it.
    // -----------------------------------------------------------------------------

    [Fact]
    public async Task Dropped_IncrementsOnOverflow()
    {
        // Capacity is 10_000; pushing 10_500 without draining should record at least some
        // drops. We assert a lower-bound delta rather than an exact number because the
        // Dropped counter is "best effort" (same math as ErrorQueue.DroppedCount).
        var queue = new ErrorQueue(new PiiRedactor());
        var before = ErrorPipelineMetrics.Dropped;

        for (var i = 0; i < 10_500; i++)
        {
            await queue.EnqueueAsync(NewEntry($"msg-{i}"));
        }

        // We slightly pad the lower bound to allow for in-flight writes that may not yet
        // have been accounted for, but we still expect a large positive delta.
        (ErrorPipelineMetrics.Dropped - before).Should().BeGreaterThanOrEqualTo(400);
    }

    [Fact]
    public async Task Dropped_Zero_WhenNoOverflow()
    {
        // 100 entries into a 10_000-capacity queue — zero drops. The delta must be 0.
        var queue = new ErrorQueue(new PiiRedactor());
        var before = ErrorPipelineMetrics.Dropped;

        for (var i = 0; i < 100; i++)
        {
            await queue.EnqueueAsync(NewEntry($"msg-{i}"));
        }

        (ErrorPipelineMetrics.Dropped - before).Should().Be(0);
    }

    // -----------------------------------------------------------------------------
    // Persisted / PersistFailed / Suppressed — already covered by the worker tests,
    // but we re-assert the pure counter API here so a future refactor that changes
    // the increment semantics trips this file first.
    // -----------------------------------------------------------------------------

    [Fact]
    public void Persisted_IncrementPersisted_BumpsByCount()
    {
        var before = ErrorPipelineMetrics.Persisted;

        ErrorPipelineMetrics.IncrementPersisted(3);

        (ErrorPipelineMetrics.Persisted - before).Should().Be(3);
    }

    [Fact]
    public void PersistFailed_IncrementPersistFailed_BumpsByCount()
    {
        var before = ErrorPipelineMetrics.PersistFailed;

        ErrorPipelineMetrics.IncrementPersistFailed(5);

        (ErrorPipelineMetrics.PersistFailed - before).Should().Be(5);
    }

    [Fact]
    public void Suppressed_IncrementSuppressed_BumpsByCount()
    {
        var before = ErrorPipelineMetrics.Suppressed;

        ErrorPipelineMetrics.IncrementSuppressed(7);

        (ErrorPipelineMetrics.Suppressed - before).Should().Be(7);
    }

    // -----------------------------------------------------------------------------
    // Snapshot — the one-shot struct that Summary reporting / metrics endpoints use
    // so they read a consistent set of values from the static class.
    // -----------------------------------------------------------------------------

    [Fact]
    public void Snapshot_ReturnsCurrentValues()
    {
        // Bump each counter by a known-unique delta, then snapshot and assert the snapshot
        // reflects exactly those deltas relative to the starting baseline.
        var baseline = ErrorPipelineMetrics.Snapshot();

        ErrorPipelineMetrics.IncrementEnqueued(2);
        ErrorPipelineMetrics.IncrementDropped(3);
        ErrorPipelineMetrics.IncrementPersisted(5);
        ErrorPipelineMetrics.IncrementPersistFailed(7);
        ErrorPipelineMetrics.IncrementSuppressed(11);

        var snap = ErrorPipelineMetrics.Snapshot();

        (snap.Enqueued - baseline.Enqueued).Should().Be(2);
        (snap.Dropped - baseline.Dropped).Should().Be(3);
        (snap.Persisted - baseline.Persisted).Should().Be(5);
        (snap.PersistFailed - baseline.PersistFailed).Should().Be(7);
        (snap.Suppressed - baseline.Suppressed).Should().Be(11);
    }

    [Fact]
    public void Snapshot_IsAValueRecord_SoCopiesAreImmutable()
    {
        // The snapshot is captured at call time; subsequent increments must not mutate a
        // previously-captured snapshot (records are value types for our purposes here).
        var before = ErrorPipelineMetrics.Snapshot();
        ErrorPipelineMetrics.IncrementEnqueued(1);

        // The "before" snapshot still holds its old Enqueued value.
        before.Enqueued.Should().Be(ErrorPipelineMetrics.Enqueued - 1);
    }
}
