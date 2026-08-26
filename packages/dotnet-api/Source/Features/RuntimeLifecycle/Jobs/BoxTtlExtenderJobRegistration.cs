using Hangfire;

namespace Source.Features.RuntimeLifecycle.Jobs;

/// <summary>
/// Central place to wire up the <see cref="BoxTtlExtenderJob"/> as a Hangfire
/// recurring job. Mirrors <see cref="IdlerJobRegistration"/> so the idioms stay
/// aligned. Called from <c>HangfireStartupService</c> once the server is up.
///
/// <para><b>Cadence math.</b> Every 30 minutes against the default 6-hour TTL
/// leaves ~11 missed passes of headroom before a healthy box would self-archive
/// — see the job's own doc comment for the fail-safe reasoning.</para>
/// </summary>
public static class BoxTtlExtenderJobRegistration
{
    public const string JobId = "box-ttl-extender";

    /// <summary>Cron expression: every 30 minutes.</summary>
    public static readonly string CronExpression = "*/30 * * * *";

    public static void Register(IRecurringJobManager recurringJobManager)
    {
        recurringJobManager.AddOrUpdate<BoxTtlExtenderJob>(
            JobId,
            job => job.Run(JobCancellationToken.Null),
            CronExpression,
            new RecurringJobOptions
            {
                TimeZone = TimeZoneInfo.Utc,
            });
    }
}
