using Microsoft.Extensions.Options;
using Source.Shared;

namespace Source.Infrastructure.ErrorHandling;

/// <summary>
/// Hosted background service that periodically emits a single <c>LogInformation</c>
/// line summarising the error-capture pipeline: all five monotonic counters from
/// <see cref="ErrorPipelineMetrics"/> plus the current queue depth read off the
/// <see cref="ErrorQueue"/>.
///
/// <para><b>Why a log line instead of a metrics endpoint?</b> Operators already have logs.
/// Dropping one summary line per minute means a simple grep tells you whether the pipeline
/// is healthy ("Enqueued climbing, Persisted climbing, Dropped flat") or under pressure
/// ("Dropped climbing — queue is overflowing"). The counters are also exposed on the
/// <c>GET /api/error-logs/pipeline-stats</c> endpoint for anything that wants JSON.</para>
///
/// <para><b>Time handling.</b> The tick body is driven deterministically off
/// <see cref="IClock"/> and is testable without real delays via the internal
/// <see cref="TickAsync"/> method; the outer loop still uses <see cref="PeriodicTimer"/>
/// because there's no clean way to drive a real-world sleep from a fake clock. Tests
/// call <c>TickAsync</c> directly for content assertions, and one smoke test spins up
/// the full hosted service with a 50ms interval to prove the plumbing is wired.</para>
/// </summary>
public class ErrorPipelineSummaryReporter : BackgroundService
{
    private readonly ErrorQueue _queue;
    private readonly IClock _clock;
    private readonly ILogger<ErrorPipelineSummaryReporter> _logger;
    private readonly IOptions<ErrorCaptureOptions> _options;

    public ErrorPipelineSummaryReporter(
        ErrorQueue queue,
        IClock clock,
        ILogger<ErrorPipelineSummaryReporter> logger,
        IOptions<ErrorCaptureOptions> options)
    {
        _queue = queue;
        _clock = clock;
        _logger = logger;
        _options = options;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalSeconds = _options.Value.SummaryIntervalSeconds;
        if (intervalSeconds <= 0)
        {
            // Disabled by configuration — run a no-op loop so the host shuts down cleanly.
            _logger.LogInformation(
                "ErrorPipelineSummaryReporter disabled (SummaryIntervalSeconds={Interval})",
                intervalSeconds);
            return;
        }

        var interval = TimeSpan.FromSeconds(intervalSeconds);
        using var timer = new PeriodicTimer(interval);

        _logger.LogInformation(
            "ErrorPipelineSummaryReporter started (interval={IntervalMs}ms)",
            (int)interval.TotalMilliseconds);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await TickAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Expected on shutdown.
        }

        _logger.LogInformation("ErrorPipelineSummaryReporter stopped");
    }

    /// <summary>
    /// Emit one summary line. Exposed as <c>internal</c> so unit tests can drive it directly
    /// without spinning up the timer — the test project has <c>InternalsVisibleTo</c> on the
    /// production assembly.
    /// </summary>
    internal Task TickAsync(CancellationToken cancellationToken)
    {
        // The clock isn't strictly required for the summary — we read counters, not
        // timestamps — but accepting IClock keeps this service consistent with the rest
        // of the pipeline and leaves room for future "reported at" rendering if useful.
        _ = _clock.UtcNow;

        var snap = ErrorPipelineMetrics.Snapshot();
        var depth = _queue.ApproximateDepth;

        _logger.LogInformation(
            "ErrorPipeline stats — Enqueued: {Enqueued}, Dropped: {Dropped}, Persisted: {Persisted}, Failed: {Failed}, Suppressed: {Suppressed}, QueueDepth: {QueueDepth}",
            snap.Enqueued,
            snap.Dropped,
            snap.Persisted,
            snap.PersistFailed,
            snap.Suppressed,
            depth);

        return Task.CompletedTask;
    }
}
