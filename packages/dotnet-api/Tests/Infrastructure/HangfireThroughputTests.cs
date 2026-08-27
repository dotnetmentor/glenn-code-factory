using Api.Tests.Infrastructure;
using Hangfire;
using Hangfire.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Source.Features.RuntimeLifecycle.Jobs;
using Source.Features.RuntimeTokens.Jobs;
using Source.Infrastructure.Jobs;
using Source.Infrastructure.Services;

namespace Api.Tests.Infrastructure;

/// <summary>
/// Pins the fixes for the queue-starvation incident: the <c>default</c> queue had
/// grown to 231,777 jobs with a 20-day-old head, because three minutely recurring
/// jobs each held a Hangfire worker for ~50 of every 60 seconds on a server sized
/// to two workers. Every ad-hoc "provision this runtime now" kick landed behind
/// that backlog, so a new branch only ever started when a stale copy of the
/// minutely sweep happened to be dequeued — the ~1 minute users experienced.
/// </summary>
public class HangfireThroughputTests
{
    // ------------------------------------------------------------------
    // Per-runtime provisioning lock
    // ------------------------------------------------------------------

    [Fact]
    public void ProvisionOne_LocksPerRuntime_NotGloballyAcrossAllProvisions()
    {
        var method = typeof(RuntimeProvisionerJob).GetMethod(nameof(RuntimeProvisionerJob.ProvisionOne));
        method.Should().NotBeNull();

        var attribute = method!
            .GetCustomAttributes(typeof(DisableConcurrentExecutionAttribute), inherit: false)
            .Cast<DisableConcurrentExecutionAttribute>()
            .SingleOrDefault();

        attribute.Should().NotBeNull("ad-hoc provisioning of one runtime must still exclude itself");
        attribute!.Resource.Should().Be("runtime-provision:{0}",
            "the lock must key on the runtimeId argument — the parameterless overload keys on " +
            "type + method name, which serialises every ad-hoc provision in the system against " +
            "every other one and makes a new branch wait out an unrelated runtime's reboot");
    }

    [Fact]
    public void ProvisionerSweep_KeepsTheGlobalLock()
    {
        // The batch sweep is the one place a global lock is correct: two concurrent
        // sweeps would fork the same Pending rows twice.
        var method = typeof(RuntimeProvisionerJob)
            .GetMethods()
            .Single(m => m.Name == nameof(RuntimeProvisionerJob.Run)
                && m.GetParameters().Length == 1
                && m.GetParameters()[0].ParameterType == typeof(IJobCancellationToken));

        var attribute = method
            .GetCustomAttributes(typeof(DisableConcurrentExecutionAttribute), inherit: false)
            .Cast<DisableConcurrentExecutionAttribute>()
            .SingleOrDefault();

        attribute.Should().NotBeNull();
        attribute!.Resource.Should().BeNull("the sweep excludes all other sweeps, so the default resource is right");
    }

    // ------------------------------------------------------------------
    // Continuous sweeps stay off the Hangfire workers
    // ------------------------------------------------------------------

    [Fact]
    public async Task ContinuousSweep_RunsCyclesRepeatedly_AndSurvivesAFailingCycle()
    {
        var services = new ServiceCollection();
        var job = new CountingJob { ThrowOnCall = 1 };
        services.AddSingleton(job);
        using var provider = services.BuildServiceProvider();

        var sweep = new TestSweepService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<TestSweepService>.Instance);

        await sweep.StartAsync(CancellationToken.None);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (job.Calls < 3 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
            }
        }
        finally
        {
            await sweep.StopAsync(CancellationToken.None);
        }

        job.Calls.Should().BeGreaterThanOrEqualTo(3,
            "the sweep must keep cycling on its own timer — and a cycle that throws " +
            "must not take the sweep (or the host) down with it");
    }

    private sealed class CountingJob
    {
        public int Calls;
        public int ThrowOnCall = -1;

        public Task RunAsync()
        {
            var call = Interlocked.Increment(ref Calls);
            if (call == ThrowOnCall)
            {
                throw new InvalidOperationException("simulated cycle failure");
            }
            return Task.CompletedTask;
        }
    }

    private sealed class TestSweepService : ContinuousSweepService<CountingJob>
    {
        public TestSweepService(IServiceScopeFactory scopeFactory, ILogger<TestSweepService> logger)
            : base(scopeFactory, logger)
        {
        }

        protected override TimeSpan StartupDelay => TimeSpan.Zero;
        protected override TimeSpan MinimumCycle => TimeSpan.FromMilliseconds(10);
        protected override string SweepName => "Test";

        protected override Task RunCycleAsync(CountingJob job, CancellationToken ct) => job.RunAsync();
    }
}

/// <summary>
/// Recurring registrations live in the Hangfire database, so deleting the C#
/// registration call is not enough to stop a job — these verify the startup service
/// actively retires the ones that must stop firing.
/// </summary>
[Collection(HangfireTestCollection.Name)]
public class HangfireStartupServiceTests : HangfireTestBase
{
    [Fact]
    public async Task StartAsync_RetiresLegacyAndMovedRecurringJobs()
    {
        var recurringJobManager = new RecurringJobManager(Storage);

        // Simulate the production database: a registration left over from the
        // Fly-to-Box rename (which had been failing every minute with
        // "Could not load type 'FlyDriftPollerJob'"), plus the three sub-minute
        // sweeps that have since moved in-process.
        recurringJobManager.AddOrUpdate<IdlerJob>(
            "fly-drift-poller", j => j.Run(JobCancellationToken.Null), Cron.Minutely());
        recurringJobManager.AddOrUpdate<HeartbeatWatcherJob>(
            HeartbeatWatcherJobRegistration.JobId, j => j.Run(JobCancellationToken.Null), Cron.Minutely());
        recurringJobManager.AddOrUpdate<IdlerJob>(
            IdlerJobRegistration.JobId, j => j.Run(JobCancellationToken.Null), Cron.Minutely());
        recurringJobManager.AddOrUpdate<RuntimeTokenUsageFlushJob>(
            RuntimeTokenUsageFlushJobRegistration.JobId, j => j.Run(JobCancellationToken.Null), Cron.Minutely());

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Features:EnableHangfire"] = "true" })
            .Build();

        var startup = new HangfireStartupService(recurringJobManager, configuration);
        await startup.StartAsync(CancellationToken.None);

        using var connection = Storage.GetConnection();
        var ids = connection.GetRecurringJobs().Select(j => j.Id).ToList();

        ids.Should().NotContain("fly-drift-poller",
            "the Fly-era registration no longer resolves to a type and must be removed, not just unregistered in code");
        ids.Should().NotContain(HeartbeatWatcherJobRegistration.JobId);
        ids.Should().NotContain(IdlerJobRegistration.JobId);
        ids.Should().NotContain(RuntimeTokenUsageFlushJobRegistration.JobId,
            "the sub-minute sweeps run in-process now; leaving them registered would run them twice " +
            "and keep occupying the workers this change frees");

        ids.Should().Contain("runtime-provisioner", "the discrete, durable jobs stay on Hangfire");
        ids.Should().Contain("box-drift-poller");
    }
}
