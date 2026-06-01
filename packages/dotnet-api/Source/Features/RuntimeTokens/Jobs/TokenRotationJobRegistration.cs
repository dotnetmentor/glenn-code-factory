using Hangfire;

namespace Source.Features.RuntimeTokens.Jobs;

/// <summary>
/// Hangfire registration for <see cref="TokenRotationJob"/>. Mirrors
/// <c>HeartbeatWatcherJobRegistration</c>. Daily cadence — see job class for
/// rationale (lookahead vs overlap math).
/// </summary>
public static class TokenRotationJobRegistration
{
    public const string JobId = "runtime-token-rotation";

    /// <summary>Daily at 02:30 UTC, well off-peak vs the janitor (03:00 UTC).</summary>
    public static readonly string CronExpression = "30 2 * * *";

    public static void Register(IRecurringJobManager recurringJobManager)
    {
        recurringJobManager.AddOrUpdate<TokenRotationJob>(
            JobId,
            job => job.Run(JobCancellationToken.Null),
            CronExpression,
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
    }
}
