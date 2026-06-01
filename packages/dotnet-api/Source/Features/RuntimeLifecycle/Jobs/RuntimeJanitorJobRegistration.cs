using Hangfire;

namespace Source.Features.RuntimeLifecycle.Jobs;

/// <summary>
/// Central place to wire up the <see cref="RuntimeJanitorJob"/> as a Hangfire
/// recurring job. Mirrors <see cref="RuntimeReconcilerJobRegistration"/> exactly
/// so the two idioms stay aligned.
///
/// <para>Called from <c>HangfireStartupService</c> once the server is up.
/// Extracted into its own static class so it can be exercised directly from
/// tests without spinning up the full host.</para>
/// </summary>
public static class RuntimeJanitorJobRegistration
{
    public const string JobId = "runtime-janitor";

    /// <summary>
    /// Cron expression: daily at 03:00 UTC. The janitor is a forensic-window
    /// cleanup pass — it only matters that it runs roughly once a day, and
    /// 03:00 UTC is comfortably outside European/US business hours so any
    /// long-tail delete batch lands during a low-traffic window.
    /// </summary>
    public static readonly string CronExpression = Cron.Daily(3, 0);

    public static void Register(IRecurringJobManager recurringJobManager)
    {
        recurringJobManager.AddOrUpdate<RuntimeJanitorJob>(
            JobId,
            job => job.Run(JobCancellationToken.Null),
            CronExpression,
            new RecurringJobOptions
            {
                TimeZone = TimeZoneInfo.Utc,
            });
    }
}
