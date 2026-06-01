using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Source.Infrastructure.ErrorHandling;
using Source.Shared;

namespace Api.Tests.Infrastructure.ErrorHandling;

/// <summary>
/// Tests for <see cref="ErrorPipelineSummaryReporter"/> — the hosted service that emits a
/// single <c>LogInformation</c> line with the five counters + queue depth on a regular
/// interval, so operators can eyeball the pipeline's health without scraping a metrics
/// endpoint.
///
/// <para>Deterministic testing strategy: the real periodic loop in <c>ExecuteAsync</c> uses
/// <see cref="PeriodicTimer"/> which is a wall-clock thing and painful to drive from a unit
/// test. Instead, the reporter exposes its core work as an internal
/// <c>TickAsync()</c> method; tests call it directly and inspect the log output captured by
/// a fake <see cref="ILogger"/>. One optional real-timer smoke test at the bottom exercises
/// the actual loop with a 50ms interval — just to prove the plumbing is wired.</para>
/// </summary>
public class ErrorPipelineSummaryReporterTests
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

    private static ErrorPipelineSummaryReporter NewReporter(
        ErrorQueue queue,
        TestLogger<ErrorPipelineSummaryReporter> logger,
        IClock? clock = null,
        ErrorCaptureOptions? options = null)
    {
        var opts = Options.Create(options ?? new ErrorCaptureOptions());
        return new ErrorPipelineSummaryReporter(
            queue,
            clock ?? new FakeClock(),
            logger,
            opts);
    }

    [Fact]
    public async Task TickAsync_LogsSummaryLine_WithAllFiveCountersAndQueueDepth()
    {
        var queue = new ErrorQueue(new PiiRedactor());
        var logger = new TestLogger<ErrorPipelineSummaryReporter>();
        var reporter = NewReporter(queue, logger);

        await reporter.TickAsync(CancellationToken.None);

        var line = logger.Entries.Should().ContainSingle().Subject;
        line.Message.Should().Contain("Enqueued");
        line.Message.Should().Contain("Dropped");
        line.Message.Should().Contain("Persisted");
        line.Message.Should().Contain("Failed");
        line.Message.Should().Contain("Suppressed");
        line.Message.Should().Contain("QueueDepth");
    }

    [Fact]
    public async Task TickAsync_LogsAtInformationLevel()
    {
        var queue = new ErrorQueue(new PiiRedactor());
        var logger = new TestLogger<ErrorPipelineSummaryReporter>();
        var reporter = NewReporter(queue, logger);

        await reporter.TickAsync(CancellationToken.None);

        logger.Entries.Single().Level.Should().Be(LogLevel.Information);
    }

    [Fact]
    public async Task TickAsync_IncludesQueueDepth_ReflectingCurrentReaderCount()
    {
        // Fill the queue with 5 items and don't drain — QueueDepth should report 5.
        var queue = new ErrorQueue(new PiiRedactor());
        for (var i = 0; i < 5; i++)
        {
            await queue.EnqueueAsync(NewEntry($"msg-{i}"));
        }

        var logger = new TestLogger<ErrorPipelineSummaryReporter>();
        var reporter = NewReporter(queue, logger);

        await reporter.TickAsync(CancellationToken.None);

        var line = logger.Entries.Single();
        line.Message.Should().Contain("QueueDepth: 5");
    }

    [Fact]
    public async Task TickAsync_ReflectsCounterDeltas()
    {
        // Capture the baseline counters, bump each by a known amount, tick, assert the
        // rendered numbers equal the (baseline + delta) values — relative to avoid static
        // state collisions with other tests.
        var queue = new ErrorQueue(new PiiRedactor());
        var baseline = ErrorPipelineMetrics.Snapshot();

        ErrorPipelineMetrics.IncrementEnqueued(1);
        ErrorPipelineMetrics.IncrementDropped(2);
        ErrorPipelineMetrics.IncrementPersisted(3);
        ErrorPipelineMetrics.IncrementPersistFailed(4);
        ErrorPipelineMetrics.IncrementSuppressed(5);

        var logger = new TestLogger<ErrorPipelineSummaryReporter>();
        var reporter = NewReporter(queue, logger);
        await reporter.TickAsync(CancellationToken.None);

        var line = logger.Entries.Single();
        line.Message.Should().Contain($"Enqueued: {baseline.Enqueued + 1}");
        line.Message.Should().Contain($"Dropped: {baseline.Dropped + 2}");
        line.Message.Should().Contain($"Persisted: {baseline.Persisted + 3}");
        line.Message.Should().Contain($"Failed: {baseline.PersistFailed + 4}");
        line.Message.Should().Contain($"Suppressed: {baseline.Suppressed + 5}");
    }

    // ---------------------------------------------------------------------------------
    // Real-timer smoke test — shortened interval, assert at least two ticks fired.
    // ---------------------------------------------------------------------------------

    [Fact]
    public async Task ExecuteAsync_RealTimerAtShortInterval_FiresMultipleTicks()
    {
        // End-to-end plumbing check: we start the reporter with a 50ms interval, wait a bit,
        // and assert we saw at least 2 log lines. Not a deep timing test — just proves the
        // hosted-service plumbing actually fires ticks.
        var queue = new ErrorQueue(new PiiRedactor());
        var logger = new TestLogger<ErrorPipelineSummaryReporter>();
        var reporter = NewReporter(
            queue,
            logger,
            options: new ErrorCaptureOptions { SummaryIntervalSeconds = 0.05 });

        await reporter.StartAsync(CancellationToken.None);
        await Task.Delay(200);
        await reporter.StopAsync(CancellationToken.None);

        logger.Entries.Count(e => e.Message.Contains("Enqueued"))
            .Should().BeGreaterThanOrEqualTo(2);
    }

    // ---------------------------------------------------------------------------------
    // A minimal ILogger test double. We deliberately avoid pulling in a bigger logging
    // framework — a couple of lines of custom code is cheaper and easier to reason about
    // than adding yet another test dependency.
    // ---------------------------------------------------------------------------------

    private sealed record LogEntry(LogLevel Level, string Message);

    private sealed class TestLogger<T> : ILogger<T>
    {
        private readonly List<LogEntry> _entries = new();
        private readonly object _sync = new();

        public IReadOnlyList<LogEntry> Entries
        {
            get
            {
                lock (_sync)
                {
                    return _entries.ToList();
                }
            }
        }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_sync)
            {
                _entries.Add(new LogEntry(logLevel, formatter(state, exception)));
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
