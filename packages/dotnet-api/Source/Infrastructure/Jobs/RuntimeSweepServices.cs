using Source.Features.RuntimeLifecycle.Jobs;
using Source.Features.RuntimeTokens.Jobs;

namespace Source.Infrastructure.Jobs;

/// <summary>
/// Watches for runtimes whose daemon has gone silent, at the 5-second cadence
/// <see cref="HeartbeatWatcherJob"/> paces internally. Moved off Hangfire — see
/// <see cref="ContinuousSweepService{TJob}"/> for why.
/// </summary>
public sealed class HeartbeatWatcherSweepService : ContinuousSweepService<HeartbeatWatcherJob>
{
    public HeartbeatWatcherSweepService(
        IServiceScopeFactory scopeFactory,
        ILogger<HeartbeatWatcherSweepService> logger)
        : base(scopeFactory, logger)
    {
    }

    protected override string SweepName => "HeartbeatWatcher";

    protected override Task RunCycleAsync(HeartbeatWatcherJob job, CancellationToken ct) => job.Run(ct);
}

/// <summary>
/// Suspends runtimes that have gone idle. Same cadence and rationale as
/// <see cref="HeartbeatWatcherSweepService"/>.
/// </summary>
public sealed class IdlerSweepService : ContinuousSweepService<IdlerJob>
{
    public IdlerSweepService(
        IServiceScopeFactory scopeFactory,
        ILogger<IdlerSweepService> logger)
        : base(scopeFactory, logger)
    {
    }

    protected override string SweepName => "Idler";

    protected override Task RunCycleAsync(IdlerJob job, CancellationToken ct) => job.Run(ct);
}

/// <summary>
/// Drains the in-memory runtime-token usage accumulator to the database on the
/// 30-second cadence <see cref="RuntimeTokenUsageFlushJob"/> paces internally.
/// </summary>
public sealed class RuntimeTokenUsageFlushSweepService : ContinuousSweepService<RuntimeTokenUsageFlushJob>
{
    public RuntimeTokenUsageFlushSweepService(
        IServiceScopeFactory scopeFactory,
        ILogger<RuntimeTokenUsageFlushSweepService> logger)
        : base(scopeFactory, logger)
    {
    }

    protected override string SweepName => "RuntimeTokenUsageFlush";

    protected override Task RunCycleAsync(RuntimeTokenUsageFlushJob job, CancellationToken ct) => job.Run(ct);
}
