using Hangfire;

namespace Source.Features.RuntimeLifecycle.Jobs;

/// <summary>
/// Central place to wire up the <see cref="RuntimeReconcilerJob"/> as a Hangfire
/// recurring job. Mirrors <see cref="RuntimeProvisionerJobRegistration"/> exactly so
/// the two idioms stay aligned.
///
/// <para>Called from <c>HangfireStartupService</c> once the server is up. Extracted
/// into its own static class so it can be exercised directly from tests without
/// spinning up the full host.</para>
/// </summary>
public static class RuntimeReconcilerJobRegistration
{
    public const string JobId = "runtime-reconciler";

    /// <summary>
    /// Cron expression: every minute. The reconciler is the safety net that closes
    /// drift between our state graph and Fly's reality; running every minute keeps
    /// any stuck runtime from sitting in the wrong state for longer than a single
    /// support call's worth of patience. The job is idempotent — a run with no
    /// drift to fix exits cheaply.
    /// </summary>
    public static readonly string CronExpression = Cron.Minutely();

    public static void Register(IRecurringJobManager recurringJobManager)
    {
        recurringJobManager.AddOrUpdate<RuntimeReconcilerJob>(
            JobId,
            job => job.Run(JobCancellationToken.Null),
            CronExpression,
            new RecurringJobOptions
            {
                TimeZone = TimeZoneInfo.Utc,
            });
    }
}
