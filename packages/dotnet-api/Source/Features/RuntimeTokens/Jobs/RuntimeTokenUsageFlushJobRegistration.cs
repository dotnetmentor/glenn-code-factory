using Hangfire;

namespace Source.Features.RuntimeTokens.Jobs;

/// <summary>
/// Central place to wire up the <see cref="RuntimeTokenUsageFlushJob"/> as a
/// Hangfire recurring job. Mirrors <c>HeartbeatWatcherJobRegistration</c>
/// exactly so the two idioms stay aligned.
///
/// <para>Called from <c>HangfireStartupService</c> once the server is up.
/// Extracted into its own static class so it can be exercised directly from
/// tests without spinning up the full host.</para>
///
/// <para><b>Why <see cref="Cron.Minutely"/> for a 30-second flush.</b> Hangfire's
/// smallest built-in cadence is one minute. The job fires once a minute and the
/// <see cref="RuntimeTokenUsageFlushJob.Run"/> body fans out internally as
/// 2 x 30-second iterations. One Hangfire registration, sub-minute effective
/// frequency.</para>
/// </summary>
public static class RuntimeTokenUsageFlushJobRegistration
{
    public const string JobId = "runtime-token-usage-flush";

    /// <summary>
    /// Cron expression: every minute. The job's inner loop handles the
    /// sub-minute flush cadence; see <see cref="RuntimeTokenUsageFlushJob"/>.
    /// </summary>
    public static readonly string CronExpression = Cron.Minutely();

    public static void Register(IRecurringJobManager recurringJobManager)
    {
        recurringJobManager.AddOrUpdate<RuntimeTokenUsageFlushJob>(
            JobId,
            job => job.Run(JobCancellationToken.Null),
            CronExpression,
            new RecurringJobOptions
            {
                TimeZone = TimeZoneInfo.Utc,
            });
    }
}
