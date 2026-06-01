using Source.Features.Mcp.Framework;

namespace Source.Features.Mcp.Jobs;

/// <summary>
/// Hangfire entry point that prunes idle buckets from the in-memory
/// <see cref="McpRateLimiter"/>. The sweep itself is synchronous and fast
/// (single iteration over the dictionary) — this job is a thin wrapper
/// around <see cref="McpRateLimiter.Sweep"/> that adapts it to Hangfire's
/// async invocation contract.
///
/// <para>Wired up by <see cref="McpRateLimiterSweepJobRegistration"/> from
/// <c>HangfireStartupService</c>. Cadence is hourly because the bucket
/// idle-TTL is one hour — running more often wouldn't change the
/// observational output.</para>
/// </summary>
public class McpRateLimiterSweepJob
{
    private readonly McpRateLimiter _limiter;
    private readonly ILogger<McpRateLimiterSweepJob> _logger;

    public McpRateLimiterSweepJob(McpRateLimiter limiter, ILogger<McpRateLimiterSweepJob> logger)
    {
        _limiter = limiter;
        _logger = logger;
    }

    /// <summary>
    /// Hangfire entry point. Returns <see cref="Task.CompletedTask"/> after
    /// the synchronous sweep — Hangfire requires an async signature even
    /// for fundamentally synchronous work.
    /// </summary>
    public Task Run(CancellationToken ct = default)
    {
        _limiter.Sweep();
        _logger.LogDebug(
            "McpRateLimiterSweepJob completed; live bucket count = {Count}",
            _limiter.BucketCount);
        return Task.CompletedTask;
    }
}
