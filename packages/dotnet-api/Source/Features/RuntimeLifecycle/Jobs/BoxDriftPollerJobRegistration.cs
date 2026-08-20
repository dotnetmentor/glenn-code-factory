using Hangfire;

namespace Source.Features.RuntimeLifecycle.Jobs;

/// <summary>
/// Hangfire wiring for the <see cref="BoxDriftPollerJob"/>. Mirrors the other
/// recurring-job registrations in this folder so the idioms stay aligned:
/// a public const <see cref="JobId"/>, a static <see cref="CronExpression"/>,
/// and a single <see cref="Register"/> entry point called from
/// <c>HangfireStartupService</c>.
///
/// <para><b>Cadence.</b> <see cref="Cron.Minutely"/> — Hangfire's smallest
/// granularity. The card spec asks for "60 seconds per runtime"; a Minutely
/// recurring job that scans every runtime in one pass satisfies it without
/// the per-runtime scheduling overhead a fan-out registration would carry.</para>
/// </summary>
public static class BoxDriftPollerJobRegistration
{
    public const string JobId = "box-drift-poller";

    /// <summary>
    /// Cron expression: every minute. <see cref="BoxDriftPollerJob.Run"/>
    /// performs the full scan on each tick.
    /// </summary>
    public static readonly string CronExpression = Cron.Minutely();

    public static void Register(IRecurringJobManager recurringJobManager)
    {
        recurringJobManager.AddOrUpdate<BoxDriftPollerJob>(
            JobId,
            job => job.Run(JobCancellationToken.Null),
            CronExpression,
            new RecurringJobOptions
            {
                TimeZone = TimeZoneInfo.Utc,
            });
    }
}
