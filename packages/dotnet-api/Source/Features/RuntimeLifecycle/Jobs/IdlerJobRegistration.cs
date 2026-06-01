using Hangfire;

namespace Source.Features.RuntimeLifecycle.Jobs;

/// <summary>
/// Central place to wire up the <see cref="IdlerJob"/> as a Hangfire recurring
/// job. Mirrors <see cref="HeartbeatWatcherJobRegistration"/> exactly so the two
/// idioms stay aligned.
///
/// <para>Called from <c>HangfireStartupService</c> once the server is up.
/// Extracted into its own static class so it can be exercised directly from
/// tests without spinning up the full host.</para>
///
/// <para><b>Why <see cref="Cron.Minutely"/> for a 5-second scan.</b> Hangfire's
/// smallest built-in cadence is one minute. The idler fires once a minute and
/// the <see cref="IdlerJob.Run"/> body fans out internally as 12 x 5-second
/// iterations. One Hangfire registration, sub-minute effective frequency.</para>
/// </summary>
public static class IdlerJobRegistration
{
    public const string JobId = "runtime-idler";

    /// <summary>
    /// Cron expression: every minute. The job's inner loop handles the
    /// sub-minute scan cadence; see <see cref="IdlerJob"/>.
    /// </summary>
    public static readonly string CronExpression = Cron.Minutely();

    public static void Register(IRecurringJobManager recurringJobManager)
    {
        recurringJobManager.AddOrUpdate<IdlerJob>(
            JobId,
            job => job.Run(JobCancellationToken.Null),
            CronExpression,
            new RecurringJobOptions
            {
                TimeZone = TimeZoneInfo.Utc,
            });
    }
}
