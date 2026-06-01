using Hangfire;

namespace Source.Features.Mcp.Jobs;

/// <summary>
/// Central place to wire up the <see cref="McpRateLimiterSweepJob"/> as a
/// Hangfire recurring job. Mirrors <see cref="Source.Features.RuntimeLifecycle.Jobs.HeartbeatWatcherJobRegistration"/>
/// exactly so the two idioms stay aligned.
///
/// <para>Called from <c>HangfireStartupService</c> once the server is up.
/// Extracted into its own static class so it can be exercised directly
/// from tests without spinning up the full host.</para>
///
/// <para><b>Why hourly.</b> The bucket idle-TTL inside
/// <see cref="Source.Features.Mcp.Framework.McpRateLimiter"/> is one hour;
/// scanning more often than that wouldn't surface anything new. The sweep
/// is also a backstop — even if it never ran, the dictionary's
/// <see cref="Source.Features.Mcp.Framework.McpRateLimiter"/> ceiling caps
/// memory growth.</para>
/// </summary>
public static class McpRateLimiterSweepJobRegistration
{
    public const string JobId = "mcp-rate-limiter-sweep";

    /// <summary>
    /// Cron expression: top of every hour. Matches the bucket idle-TTL so
    /// stale entries are dropped within roughly one TTL window.
    /// </summary>
    public static readonly string CronExpression = Cron.Hourly();

    public static void Register(IRecurringJobManager recurringJobManager)
    {
        recurringJobManager.AddOrUpdate<McpRateLimiterSweepJob>(
            JobId,
            job => job.Run(CancellationToken.None),
            CronExpression,
            new RecurringJobOptions
            {
                TimeZone = TimeZoneInfo.Utc,
            });
    }
}
