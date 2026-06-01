using System.Text.Json;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Source.Infrastructure.ErrorHandling;

namespace Api.Tests.Infrastructure.ErrorHandling;

/// <summary>
/// Tests for <see cref="ErrorCaptureJobFilter"/> — the Hangfire filter that enqueues an
/// <see cref="ErrorEntry"/> into the <see cref="ErrorQueue"/> when a background job reaches
/// its <b>final</b> failure state (i.e. after all retries have been exhausted).
///
/// <para>Guiding invariants (per spec):</para>
/// <list type="bullet">
///   <item>Captures exactly once per job, on the terminal FailedState transition.</item>
///   <item>Never double-captures on transient retries.</item>
///   <item>Never re-throws — the Hangfire state machine must be unaffected.</item>
///   <item>Carries <c>Source = "Hangfire"</c> and ContextData containing JobName/Queue/RetryCount/Arguments.</item>
/// </list>
/// </summary>
[Collection(HangfireTestCollection.Name)]
public class ErrorCaptureJobFilterTests : HangfireTestBase
{
    // These statics let the throwing jobs coordinate with the test — without this, xUnit's
    // parallelism would need per-test IoC plumbing for the jobs, which Hangfire.InMemory
    // doesn't offer out of the box. All tests in this class run serially by xUnit default
    // because they share a class fixture.
    private static int _attemptCount;
    private static int _succeedOnAttempt;
    private static readonly object _attemptLock = new();

    private ErrorQueue _queue = null!;

    public override Task InitializeAsync()
    {
        lock (_attemptLock)
        {
            _attemptCount = 0;
            _succeedOnAttempt = int.MaxValue;
        }

        _queue = new ErrorQueue(new PiiRedactor());
        RegisterGlobalFilter(new ErrorCaptureJobFilter(_queue));
        return base.InitializeAsync();
    }

    [Fact]
    public async Task FailingJob_FinalAttempt_EnqueuesErrorEntry()
    {
        // No retries: every failure is final. Attribute on the method wins over global default.
        var finalState = await EnqueueAndWait<ThrowingJobNoRetry>(j => j.Run(), TimeSpan.FromSeconds(5));

        finalState.Should().Be(FailedState.StateName);

        var entries = await DrainQueue();
        entries.Should().HaveCount(1, "the terminal failure should produce exactly one error entry");
        entries[0].Source.Should().Be("Hangfire");
        entries[0].Message.Should().Contain("boom");
    }

    [Fact]
    public async Task SuccessfulJob_DoesNotEnqueue()
    {
        TrivialJob.Reset();

        var finalState = await EnqueueAndWait<TrivialJob>(j => j.Run(), TimeSpan.FromSeconds(5));

        finalState.Should().Be(SucceededState.StateName);

        var entries = await DrainQueue();
        entries.Should().BeEmpty("successful jobs must not touch the error pipeline");
    }

    [Fact]
    public async Task FailingJob_TransientAttempt_DoesNotEnqueueBeforeRetriesExhausted()
    {
        // Job will throw on attempt 1, succeed on attempt 2. The filter should NOT enqueue
        // for the transient failure — we only capture terminal FailedState transitions.
        lock (_attemptLock) { _succeedOnAttempt = 2; }

        var finalState = await EnqueueAndWait<FlakeyJobWithRetries>(j => j.Run(), TimeSpan.FromSeconds(15));

        finalState.Should().Be(SucceededState.StateName,
            "the second attempt succeeds, so the job ends Succeeded");

        var entries = await DrainQueue();
        entries.Should().BeEmpty("transient failures that ultimately succeed must not enqueue");
    }

    [Fact]
    public async Task FailingJob_AllRetriesExhausted_EnqueuesExactlyOnce()
    {
        // Job always throws; the AutomaticRetryAttribute on the method caps retries at 1
        // (so two attempts total). The filter should enqueue exactly once — when the job
        // reaches FailedState after the final attempt.
        lock (_attemptLock) { _succeedOnAttempt = int.MaxValue; } // never succeed

        var finalState = await EnqueueAndWait<FlakeyJobWithRetries>(j => j.Run(), TimeSpan.FromSeconds(30));

        finalState.Should().Be(FailedState.StateName,
            "after retries exhausted the job should land in FailedState");

        var entries = await DrainQueue();
        entries.Should().HaveCount(1, "exactly one capture for the terminal failure — no double-count");
        entries[0].Source.Should().Be("Hangfire");
    }

    [Fact]
    public async Task FailingJob_ContextData_ContainsJobNameQueueRetryCount()
    {
        var finalState = await EnqueueAndWait<ThrowingJobNoRetry>(j => j.Run(), TimeSpan.FromSeconds(5));
        finalState.Should().Be(FailedState.StateName);

        var entries = await DrainQueue();
        entries.Should().HaveCount(1);

        entries[0].ContextData.Should().NotBeNullOrWhiteSpace();
        using var doc = JsonDocument.Parse(entries[0].ContextData!);
        var root = doc.RootElement;

        root.TryGetProperty("JobName", out var jobNameEl).Should().BeTrue("ContextData must carry JobName");
        jobNameEl.GetString().Should().Contain("ThrowingJobNoRetry");
        jobNameEl.GetString().Should().Contain("Run");

        root.TryGetProperty("Queue", out var queueEl).Should().BeTrue("ContextData must carry Queue");
        queueEl.GetString().Should().NotBeNullOrWhiteSpace();

        root.TryGetProperty("RetryCount", out var retryEl).Should().BeTrue("ContextData must carry RetryCount");
        retryEl.ValueKind.Should().Be(JsonValueKind.Number);
    }

    [Fact]
    public async Task FailingJob_ContextData_ContainsSerializedArguments()
    {
        var finalState = await EnqueueAndWait<ThrowingJobWithArgs>(j => j.Run(42, "hello"), TimeSpan.FromSeconds(5));
        finalState.Should().Be(FailedState.StateName);

        var entries = await DrainQueue();
        entries.Should().HaveCount(1);

        entries[0].ContextData.Should().NotBeNullOrWhiteSpace();
        using var doc = JsonDocument.Parse(entries[0].ContextData!);
        doc.RootElement.TryGetProperty("Arguments", out var argsEl).Should().BeTrue();
        argsEl.ValueKind.Should().Be(JsonValueKind.Array);

        var args = argsEl.EnumerateArray().Select(e => e.GetString()).ToArray();
        args.Should().Contain("42");
        args.Should().Contain("hello");
    }

    [Fact]
    public async Task Filter_DoesNotReThrow_WhenQueueThrows()
    {
        // A pathological ErrorQueue whose EnqueueAsync always throws. The filter must
        // swallow it — Hangfire's state machine should still move the job to FailedState.
        // We can't easily re-register a different filter mid-fixture, so spin up a
        // second filter manually and pass it directly into the jobs path by substituting
        // the global filter list.
        GlobalJobFilters.Filters.Clear();
        GlobalJobFilters.Filters.Add(new ErrorCaptureJobFilter(new ThrowingErrorQueue()));

        var finalState = await EnqueueAndWait<ThrowingJobNoRetry>(j => j.Run(), TimeSpan.FromSeconds(5));

        finalState.Should().Be(FailedState.StateName,
            "the filter must never let its own failure escape into Hangfire's pipeline");
    }

    // ---- Helpers ----

    private async Task<List<ErrorEntry>> DrainQueue()
    {
        // Give the filter pipeline a moment to finish writing before we drain.
        await Task.Delay(100);

        _queue.CompleteWriting();

        var result = new List<ErrorEntry>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var e in _queue.ReadAllAsync(cts.Token))
        {
            result.Add(e);
        }
        return result;
    }

    // ---- Job classes ----

    /// <summary>Throws on every attempt, with retries disabled. Lands in FailedState immediately.</summary>
    public class ThrowingJobNoRetry
    {
        [AutomaticRetry(Attempts = 0)]
        public void Run() => throw new InvalidOperationException("boom");
    }

    /// <summary>Throws on every attempt by default; succeeds once <see cref="_succeedOnAttempt"/> is reached.</summary>
    public class FlakeyJobWithRetries
    {
        [AutomaticRetry(Attempts = 1, DelaysInSeconds = new[] { 1 })]
        public void Run()
        {
            int attempt;
            int successOn;
            lock (_attemptLock)
            {
                _attemptCount++;
                attempt = _attemptCount;
                successOn = _succeedOnAttempt;
            }

            if (attempt < successOn)
            {
                throw new InvalidOperationException($"transient failure on attempt {attempt}");
            }
            // else: succeed silently
        }
    }

    /// <summary>Throws with primitive args so we can verify argument serialization.</summary>
    public class ThrowingJobWithArgs
    {
        [AutomaticRetry(Attempts = 0)]
        public void Run(int x, string y) => throw new InvalidOperationException($"boom {x} {y}");
    }

    public class TrivialJob
    {
        public static int ExecutionCount;
        public static void Reset() => Interlocked.Exchange(ref ExecutionCount, 0);
        public void Run() => Interlocked.Increment(ref ExecutionCount);
    }

    /// <summary>An ErrorQueue stand-in that always blows up — used to exercise the filter's defensive try/catch.</summary>
    private sealed class ThrowingErrorQueue : ErrorQueue
    {
        public ThrowingErrorQueue() : base(new ThrowingRedactor()) { }

        private sealed class ThrowingRedactor : IPiiRedactor
        {
            public string? Redact(string? input) => throw new InvalidOperationException("redactor exploded");
        }
    }
}
