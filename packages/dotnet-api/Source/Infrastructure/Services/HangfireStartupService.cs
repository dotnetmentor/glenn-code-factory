using Hangfire;
using Source.Features.Conversations.Jobs;
using Source.Features.Mcp.Jobs;
using Source.Features.RuntimeLifecycle.Jobs;
using Source.Features.RuntimeTokens.Jobs;
using Source.Infrastructure.ErrorHandling;

namespace Source.Infrastructure.Services;

public class HangfireStartupService : IHostedService
{
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
        HeartbeatWatcherJobRegistration.Register(_recurringJobManager);
        FlyDriftPollerJobRegistration.Register(_recurringJobManager);
        IdlerJobRegistration.Register(_recurringJobManager);
        OrphanSessionJanitorJobRegistration.Register(_recurringJobManager);
        ReconcileStaleSessionsJobRegistration.Register(_recurringJobManager);
        TokenRotationJobRegistration.Register(_recurringJobManager);
        RuntimeTokenUsageFlushJobRegistration.Register(_recurringJobManager);
        McpRateLimiterSweepJobRegistration.Register(_recurringJobManager);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
