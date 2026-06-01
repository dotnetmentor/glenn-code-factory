using Hangfire;

namespace Source.Infrastructure.ErrorHandling;

/// <summary>
/// Central place to wire up the <see cref="ErrorLogRetentionJob"/> as a Hangfire
/// recurring job. Called from <see cref="Source.Infrastructure.Services.HangfireStartupService"/>
/// once the server is up.
///
/// Extracted into its own class so it can be exercised directly from tests
/// without spinning up the full <c>HangfireStartupService</c>.
/// </summary>
public static class ErrorLogRetentionJobRegistration
{
    public const string JobId = "error-log-retention";

    /// <summary>
    /// Cron expression: daily at 03:00 UTC. Kept off-peak so any lock contention
    /// overlaps minimally with user traffic.
    /// </summary>
    public const string CronExpression = "0 3 * * *";

    public static void Register(IRecurringJobManager recurringJobManager)
    {
        recurringJobManager.AddOrUpdate<ErrorLogRetentionJob>(
            JobId,
            job => job.ExecuteAsync(CancellationToken.None),
            CronExpression,
            new RecurringJobOptions
            {
                TimeZone = TimeZoneInfo.Utc,
            });
    }
}
