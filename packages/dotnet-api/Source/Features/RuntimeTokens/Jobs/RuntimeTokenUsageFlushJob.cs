using Hangfire;
using Source.Features.RuntimeTokens.Services;

namespace Source.Features.RuntimeTokens.Jobs;

/// <summary>
/// Recurring Hangfire job that drains the in-memory
/// <see cref="RuntimeTokenUsageRecorder"/> accumulator and writes the deltas
/// (LastUsedAt + RequestCount) to <c>RuntimeTokenIssues</c>. Wired in
/// <see cref="RuntimeTokenUsageFlushJobRegistration"/>.
///
/// <para><b>Cadence.</b> Hangfire's smallest cron unit is one minute. The job
/// fires once a minute and the body fans out internally as 2 x 30-second
/// iterations so the effective flush cadence is 30 s — one Hangfire
/// registration, sub-minute frequency. Mirrors the
/// <c>HeartbeatWatcherJob.LoopIterations</c> idiom.</para>
///
/// <para><b>Concurrency.</b> Decorated with
/// <see cref="DisableConcurrentExecutionAttribute"/> with a 60-second timeout
/// so two Hangfire workers can't double-flush on the same minute.</para>
///
/// <para><b>Failure isolation.</b> Per-iteration <c>try/catch</c> ensures one
/// failed flush doesn't kill the remaining iteration in the same minute. The
/// recorder's accumulator is preserved across the failure — the next flush
/// picks up everything that didn't make it.</para>
/// </summary>
public class RuntimeTokenUsageFlushJob
{
    /// <summary>How many times the inner loop fires per Hangfire invocation.</summary>
    private const int LoopIterations = 2;

    /// <summary>Sleep between inner-loop iterations.</summary>
    private const int LoopIntervalSeconds = 30;

    private readonly RuntimeTokenUsageRecorder _recorder;
    private readonly ILogger<RuntimeTokenUsageFlushJob> _logger;

    public RuntimeTokenUsageFlushJob(
        RuntimeTokenUsageRecorder recorder,
        ILogger<RuntimeTokenUsageFlushJob> logger)
    {
        _recorder = recorder;
        _logger = logger;
    }

    /// <summary>
    /// Hangfire entry point. Wraps the inner <see cref="Run(CancellationToken)"/>
    /// in a linked <see cref="CancellationTokenSource"/> with a hard 50-second
    /// budget so the job can never hold the
    /// <see cref="DisableConcurrentExecutionAttribute"/> lock past the 60-second
    /// TTL — even if the flush call hangs forever. When the CTS trips, control
    /// returns, Hangfire releases the lock, and the next tick acquires on
    /// schedule.
    ///
    /// <para><see cref="AutomaticRetry"/> with <c>Attempts = 0</c> stops Hangfire
    /// from auto-requeuing a partially-cancelled run on top of the next scheduled
    /// tick.</para>
    /// </summary>
    [DisableConcurrentExecution(timeoutInSeconds: 60)]
    [AutomaticRetry(Attempts = 0)]
    public async Task Run(IJobCancellationToken hangfireCt)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(hangfireCt.ShutdownToken);
        cts.CancelAfter(TimeSpan.FromSeconds(50));
        await Run(cts.Token);
    }

    public async Task Run(CancellationToken ct = default)
    {
        for (int i = 0; i < LoopIterations && !ct.IsCancellationRequested; i++)
        {
            try
            {
                await _recorder.FlushAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "RuntimeTokenUsageFlushJob iteration {Iteration} failed; accumulator preserved for next flush",
                    i);
            }

            if (i < LoopIterations - 1)
            {
                await Task.Delay(TimeSpan.FromSeconds(LoopIntervalSeconds), ct);
            }
        }
    }
}
