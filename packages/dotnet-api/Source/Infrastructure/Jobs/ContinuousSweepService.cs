using System.Diagnostics;

namespace Source.Infrastructure.Jobs;

/// <summary>
/// Base for the platform's continuous in-process sweeps — the ones that need a
/// cadence FASTER than the one-minute floor a cron expression can express
/// (heartbeat watching at 5s, idle detection at 5s, token-usage flushing at 30s).
///
/// <para><b>Why these are not Hangfire recurring jobs any more.</b> The old shape
/// was a minutely cron job whose body looped internally (12 x 5s), which meant a
/// single job occupied a Hangfire worker for ~50 of every 60 seconds. With three
/// such jobs that is ~150 worker-seconds of demand per minute, while the server —
/// sized <c>ProcessorCount * 2</c> on a 1-vCPU host — supplied only 120. Demand
/// permanently exceeded capacity, so the <c>default</c> queue grew without bound:
/// it reached 231,777 jobs with a 20-day-old head before this was found. Every
/// ad-hoc <c>Enqueue</c> (the "provision this runtime NOW" kick) landed behind
/// that backlog and effectively never ran, which is why a fresh branch waited for
/// the minutely sweep instead of starting in seconds.</para>
///
/// <para>A sweep is a periodic scan of live database state. It carries no payload,
/// nothing needs to survive a restart, and running it twice is harmless — so it
/// wants a timer, not a durable job queue. Hosting it here keeps Hangfire's
/// workers free for what actually belongs there: discrete, durable, retryable
/// units of work like provisioning and respawning a specific runtime.</para>
///
/// <para>The job classes themselves are unchanged and still own their own pacing
/// (their <c>Run(CancellationToken)</c> loops N times with an internal delay).
/// This service just calls that loop back-to-back for the lifetime of the process,
/// with <see cref="MinimumCycle"/> as a guard so a cycle that fails instantly
/// cannot become a hot loop.</para>
/// </summary>
/// <typeparam name="TJob">
/// The sweep job. Resolved per cycle via
/// <see cref="ActivatorUtilities.GetServiceOrCreateInstance{T}(IServiceProvider)"/> —
/// matching how Hangfire's own AspNetCore activator resolved these types, so jobs
/// that were never explicitly registered in DI keep working.
/// </typeparam>
public abstract class ContinuousSweepService<TJob> : BackgroundService where TJob : class
{
    /// <summary>
    /// Floor on how often a cycle may restart. The job loops pace themselves; this
    /// only matters when a cycle returns or throws immediately (e.g. the database
    /// is unreachable), where it turns a spin into a slow retry.
    /// </summary>
    protected virtual TimeSpan MinimumCycle => TimeSpan.FromSeconds(5);

    /// <summary>
    /// Delay before the first cycle so application startup (migrations, warmup)
    /// isn't competing with a sweep for the connection pool.
    /// </summary>
    protected virtual TimeSpan StartupDelay => TimeSpan.FromSeconds(10);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger _logger;

    protected ContinuousSweepService(IServiceScopeFactory scopeFactory, ILogger logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>Human-readable name for logs.</summary>
    protected abstract string SweepName { get; }

    /// <summary>Run one cycle. Implementations delegate to the job's own loop.</summary>
    protected abstract Task RunCycleAsync(TJob job, CancellationToken ct);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("{Sweep}: continuous sweep service starting.", SweepName);

        try
        {
            await Task.Delay(StartupDelay, stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                var started = Stopwatch.GetTimestamp();

                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var job = ActivatorUtilities.GetServiceOrCreateInstance<TJob>(scope.ServiceProvider);
                    await RunCycleAsync(job, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // One bad cycle must never take the sweep — or the host — down.
                    _logger.LogError(ex, "{Sweep}: cycle failed; continuing.", SweepName);
                }

                var elapsed = Stopwatch.GetElapsedTime(started);
                if (elapsed < MinimumCycle)
                {
                    await Task.Delay(MinimumCycle - elapsed, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }

        _logger.LogInformation("{Sweep}: continuous sweep service stopped.", SweepName);
    }
}
