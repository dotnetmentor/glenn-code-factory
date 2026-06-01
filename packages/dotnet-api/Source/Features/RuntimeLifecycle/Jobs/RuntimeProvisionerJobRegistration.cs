using Hangfire;

namespace Source.Features.RuntimeLifecycle.Jobs;

/// <summary>
/// Central place to wire up the <see cref="RuntimeProvisionerJob"/> as a Hangfire
/// recurring job. Mirrors <c>ErrorLogRetentionJobRegistration</c> exactly so the two
/// idioms stay aligned.
///
/// <para>Called from <c>HangfireStartupService</c> once the server is up. Extracted
/// into its own static class so it can be exercised directly from tests without
/// spinning up the full host.</para>
/// </summary>
public static class RuntimeProvisionerJobRegistration
{
    public const string JobId = "runtime-provisioner";

    /// <summary>
    /// Cron expression: every minute. The provisioner is the bottleneck between a user
    /// requesting a runtime and the machine actually booting, so we want it to drain
    /// the queue as quickly as Hangfire schedules will allow without spinning a
    /// dedicated worker. The job is idempotent and self-throttles via
    /// <see cref="RuntimeProvisionerJob.BatchSize"/>.
    /// </summary>
    public static readonly string CronExpression = Cron.Minutely();

    public static void Register(IRecurringJobManager recurringJobManager)
    {
        recurringJobManager.AddOrUpdate<RuntimeProvisionerJob>(
            JobId,
            job => job.Run(JobCancellationToken.Null),
            CronExpression,
            new RecurringJobOptions
            {
                TimeZone = TimeZoneInfo.Utc,
            });
    }
}
