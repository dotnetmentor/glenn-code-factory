using Hangfire;

namespace Source.Features.Conversations.Jobs;

/// <summary>
/// Central place to wire up the <see cref="ReconcileStaleSessionsJob"/> as a
/// Hangfire recurring job. Mirrors
/// <see cref="OrphanSessionJanitorJobRegistration"/> exactly so the two idioms
/// stay aligned.
///
/// <para>Called from <c>HangfireStartupService</c> once the server is up.
/// Extracted into its own static class so it can be exercised directly from
/// tests without spinning up the full host.</para>
///
/// <para><b>Cadence.</b> Every minute — the smallest built-in Hangfire cadence.
/// A session stuck on a lost terminal event is reaped within ~1 minute of its
/// grace window elapsing, which is plenty for the live-chat UX.</para>
/// </summary>
public static class ReconcileStaleSessionsJobRegistration
{
    public const string JobId = "reconcile-stale-sessions";

    /// <summary>Cron expression: every minute.</summary>
    public static readonly string CronExpression = Cron.Minutely();

    public static void Register(IRecurringJobManager recurringJobManager)
    {
        recurringJobManager.AddOrUpdate<ReconcileStaleSessionsJob>(
            JobId,
            job => job.Run(JobCancellationToken.Null),
            CronExpression,
            new RecurringJobOptions
            {
                TimeZone = TimeZoneInfo.Utc,
            });
    }
}
