using Hangfire;

namespace Source.Features.Conversations.Jobs;

/// <summary>
/// Central place to wire up the <see cref="OrphanSessionJanitorJob"/> as a
/// Hangfire recurring job. Mirrors
/// <see cref="Source.Features.RuntimeLifecycle.Jobs.HeartbeatWatcherJobRegistration"/>
/// exactly so the two idioms stay aligned.
///
/// <para>Called from <c>HangfireStartupService</c> once the server is up.
/// Extracted into its own static class so it can be exercised directly from
/// tests without spinning up the full host.</para>
///
/// <para><b>Cadence.</b> Every minute. Orphan reaping doesn't have to be
/// instant — by the time we declare a runtime Crashed/Failed the user is
/// already seeing a runtime-level error, and the per-session "Failed
/// (runtime_unavailable)" is a tidy-up signal more than a primary user signal.
/// Hangfire's smallest built-in cadence is one minute; that's plenty.</para>
/// </summary>
public static class OrphanSessionJanitorJobRegistration
{
    public const string JobId = "orphan-session-janitor";

    /// <summary>
    /// Cron expression: every minute. The job's batched scan handles whatever
    /// orphan volume an outage produces.
    /// </summary>
    public static readonly string CronExpression = Cron.Minutely();

    public static void Register(IRecurringJobManager recurringJobManager)
    {
        recurringJobManager.AddOrUpdate<OrphanSessionJanitorJob>(
            JobId,
            job => job.Run(JobCancellationToken.Null),
            CronExpression,
            new RecurringJobOptions
            {
                TimeZone = TimeZoneInfo.Utc,
            });
    }
}
