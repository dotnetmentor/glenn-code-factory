using Hangfire;
using Source.Features.Conversations.Jobs;
using Source.Features.Mcp.Jobs;
using Source.Features.RuntimeLifecycle.Jobs;
using Source.Features.RuntimeTokens.Jobs;
using Source.Infrastructure.ErrorHandling;

namespace Source.Infrastructure.Services;

public class HangfireStartupService : IHostedService
{
    /// <summary>
    /// Recurring registrations that must be actively removed from the Hangfire
    /// database, because deleting the C# registration call does nothing on its own —
    /// the schedule lives in the <c>hangfire.set</c>/<c>hangfire.hash</c> rows and
    /// keeps firing forever otherwise.
    ///
    /// <list type="bullet">
    ///   <item><c>fly-drift-poller</c> — renamed to <c>box-drift-poller</c> during the
    ///     Fly-to-Box migration. The old row survived the rename and had been failing
    ///     every single minute since with
    ///     <c>Could not load type 'FlyDriftPollerJob'</c>.</item>
    ///   <item>The three sub-minute sweeps, which now run in-process on their own
    ///     timers (see <c>ContinuousSweepService</c>). Left registered they would run
    ///     twice — once here, once on the timer — and keep occupying the Hangfire
    ///     workers this change exists to free.</item>
    /// </list>
    /// </summary>
    private static readonly string[] RetiredRecurringJobIds =
    [
        "fly-drift-poller",
        HeartbeatWatcherJobRegistration.JobId,
        IdlerJobRegistration.JobId,
        RuntimeTokenUsageFlushJobRegistration.JobId,
    ];

    private readonly IRecurringJobManager _recurringJobManager;
    private readonly IConfiguration _configuration;

    public HangfireStartupService(IRecurringJobManager recurringJobManager, IConfiguration configuration)
    {
        _recurringJobManager = recurringJobManager;
        _configuration = configuration;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var enableHangfire = _configuration.GetValue<bool>("Features:EnableHangfire", true);

        if (!enableHangfire)
        {
            return Task.CompletedTask;
        }

        // Register recurring jobs. Each registration is idempotent (AddOrUpdate),
        // so restarts are safe.
        ErrorLogRetentionJobRegistration.Register(_recurringJobManager);
        RuntimeProvisionerJobRegistration.Register(_recurringJobManager);
        RuntimeReconcilerJobRegistration.Register(_recurringJobManager);
        RuntimeJanitorJobRegistration.Register(_recurringJobManager);
        BoxDriftPollerJobRegistration.Register(_recurringJobManager);
        BoxTtlExtenderJobRegistration.Register(_recurringJobManager);
        OrphanSessionJanitorJobRegistration.Register(_recurringJobManager);
        ReconcileStaleSessionsJobRegistration.Register(_recurringJobManager);
        TokenRotationJobRegistration.Register(_recurringJobManager);
        McpRateLimiterSweepJobRegistration.Register(_recurringJobManager);

        // Recurring registrations live in the Hangfire database, so one that is no
        // longer registered here does NOT go away on its own — it keeps firing on
        // its old schedule until something removes it. Both kinds of leftover are
        // swept here.
        foreach (var jobId in RetiredRecurringJobIds)
        {
            _recurringJobManager.RemoveIfExists(jobId);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
