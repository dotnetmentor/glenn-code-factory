using System.Collections.Concurrent;
using Source.Shared;

namespace Source.Features.Mcp.Framework;

/// <summary>
/// In-memory token-bucket rate limiter consulted by <see cref="McpControllerBase"/>
/// before each MCP method dispatch. Buckets are keyed by
/// <c>(runtimeId, serverName, method)</c> so a noisy method on one runtime
/// doesn't starve a quiet method on another.
///
/// <para><b>Single-instance only.</b> The bucket store is a process-local
/// <see cref="ConcurrentDictionary{TKey, TValue}"/>. When (if) the API scales
/// horizontally, this needs to move to Redis (atomic Lua script + TTL keys)
/// — the dictionary semantics here can't be made coherent across multiple
/// API instances. We accept the limitation today because (a) we run a single
/// instance and (b) the rate limiter is a backstop, not a security control:
/// the JWT auth layer already gates the door. A future card adds Redis if /
/// when we scale out.</para>
///
/// <para><b>Bucket lifecycle.</b> Buckets are lazily created on first call
/// (<see cref="ConcurrentDictionary{TKey, TValue}.GetOrAdd(TKey, Func{TKey, TValue})"/>)
/// and pruned by the periodic <see cref="Sweep"/> pass — entries idle for
/// more than <see cref="IdleTtl"/> are evicted, and if the dictionary still
/// exceeds <see cref="MaxEntries"/>, the LRU keys are dropped. The sweep is
/// driven by a Hangfire recurring job
/// (<c>McpRateLimiterSweepJobRegistration</c>) so the limiter itself stays
/// dependency-free.</para>
///
/// <para><b>Concurrency model.</b> Two layers of synchronisation:
/// <list type="bullet">
///   <item>The dictionary is concurrent — <c>GetOrAdd</c> handles the
///         race between two callers creating the same bucket.</item>
///   <item>The bucket's refill + decrement is guarded by a per-bucket
///         <see cref="object"/> lock so two callers on the same key see a
///         consistent token count. Lock granularity is per-bucket, never
///         the whole dictionary, so unrelated keys don't contend.</item>
/// </list></para>
///
/// <para><b>Why no business-logic exceptions.</b> A rate-limit denial is a
/// normal outcome — it returns as <see cref="RateLimitDecision.Allowed"/> =
/// <c>false</c> with a <see cref="RateLimitDecision.RetryAfterMs"/> hint,
/// never as a thrown exception. Mirrors the <c>Result&lt;T&gt;</c> convention
/// elsewhere in the codebase.</para>
/// </summary>
public sealed class McpRateLimiter
{
    /// <summary>
    /// Default token-bucket capacity used when an action carries no
    /// <see cref="McpMethodRateLimitAttribute"/>. 60 tokens = a minute of
    /// sustained 1 rps with a full-minute burst. Tuned for the MVP — most
    /// MCP calls are exploratory tool use from an agent, not high-throughput
    /// batch APIs.
    /// </summary>
    public const int DefaultCapacity = 60;

    /// <summary>
    /// Default replenishment rate used when an action carries no
    /// <see cref="McpMethodRateLimitAttribute"/>. 1 token / second pairs
    /// with <see cref="DefaultCapacity"/> = 60 to give sustained 60 calls
    /// per minute.
    /// </summary>
    public const double DefaultRefillPerSecond = 1.0;

    /// <summary>
    /// Hard ceiling on dictionary size. <see cref="Sweep"/> evicts LRU keys
    /// past this. 10k = enough for ~10 runtimes x ~10 servers x ~100 methods
    /// without churn; tighter ceiling would risk thrashing in steady state.
    /// </summary>
    private const int MaxEntries = 10_000;

    /// <summary>
    /// Idle window after which a bucket is pruned. One hour is comfortably
    /// longer than any plausible refill window for the default budget — a
    /// bucket idle this long has fully refilled to <see cref="DefaultCapacity"/>
    /// anyway, so dropping it is observationally invisible.
    /// </summary>
    private static readonly TimeSpan IdleTtl = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<(Guid RuntimeId, string Server, string Method), TokenBucket> _buckets = new();
    private readonly IClock _clock;
    private readonly ILogger<McpRateLimiter> _logger;

    public McpRateLimiter(IClock clock, ILogger<McpRateLimiter> logger)
    {
        _clock = clock;
        _logger = logger;
    }

    /// <summary>Live bucket count. Test hook + observability surface.</summary>
    public int BucketCount => _buckets.Count;

    /// <summary>
    /// Consult the limiter for a single MCP call. Returns a
    /// <see cref="RateLimitDecision"/> with <see cref="RateLimitDecision.Allowed"/>
    /// indicating whether the call may proceed and, when denied, a
    /// <see cref="RateLimitDecision.RetryAfterMs"/> hint computed from the
    /// per-bucket refill rate.
    ///
    /// <para>Atomicity: the refill + decrement happens under a per-bucket
    /// lock. Different buckets never contend.</para>
    /// </summary>
    public RateLimitDecision TryAcquire(
        Guid runtimeId,
        string server,
        string method,
        int capacity,
        double refillPerSecond)
    {
        var key = (runtimeId, server, method);
        var bucket = _buckets.GetOrAdd(key, _ =>
        {
            var now = _clock.UtcNow;
            return new TokenBucket
            {
                Capacity = capacity,
                Tokens = capacity,
                RefillPerSecond = refillPerSecond,
                LastRefill = now,
                LastUsed = now,
            };
        });

        lock (bucket.Lock)
        {
            var now = _clock.UtcNow;

            // Refill — clamp to bucket capacity. We keep elapsed > 0 to be
            // defensive against a non-monotonic clock injected from tests
            // (FakeClock can move backwards if a test asks it to).
            var elapsed = (now - bucket.LastRefill).TotalSeconds;
            if (elapsed > 0)
            {
                bucket.Tokens = Math.Min(bucket.Capacity, bucket.Tokens + elapsed * bucket.RefillPerSecond);
                bucket.LastRefill = now;
            }
            bucket.LastUsed = now;

            if (bucket.Tokens >= 1.0)
            {
                bucket.Tokens -= 1.0;
                return new RateLimitDecision(true, 0);
            }

            // Compute a wait hint: how many ms until tokens >= 1.0 given the
            // bucket's refill rate. Rounded up so the caller never retries
            // a fraction of a millisecond too early.
            var deficit = 1.0 - bucket.Tokens;
            var retryAfterSec = deficit / bucket.RefillPerSecond;
            var retryAfterMs = (int)Math.Ceiling(retryAfterSec * 1000.0);
            return new RateLimitDecision(false, retryAfterMs);
        }
    }

    /// <summary>
    /// Periodic cleanup driven by Hangfire (see <c>McpRateLimiterSweepJob</c>).
    /// Two-pass:
    /// <list type="number">
    ///   <item>Drop buckets idle for longer than <see cref="IdleTtl"/>.</item>
    ///   <item>If the dictionary still exceeds <see cref="MaxEntries"/>,
    ///         evict the oldest <c>LastUsed</c> keys until we're under the
    ///         ceiling.</item>
    /// </list>
    /// Synchronous and fast — single iteration over the dict. Safe to call
    /// while <see cref="TryAcquire"/> is in flight; <see cref="ConcurrentDictionary{TKey, TValue}"/>
    /// guarantees <c>TryRemove</c> + <c>GetOrAdd</c> are linearisable.
    /// </summary>
    public void Sweep()
    {
        var now = _clock.UtcNow;

        // Pass 1: TTL prune. Snapshot the keys first so we don't mutate
        // while iterating.
        var stale = new List<(Guid, string, string)>();
        foreach (var kv in _buckets)
        {
            if (now - kv.Value.LastUsed > IdleTtl)
            {
                stale.Add(kv.Key);
            }
        }
        foreach (var key in stale)
        {
            _buckets.TryRemove(key, out _);
        }

        // Pass 2: LRU evict if still over the ceiling. Sorting a 10k+ list
        // by LastUsed is fine — sweep runs hourly, not per-call.
        if (_buckets.Count > MaxEntries)
        {
            var excess = _buckets.Count - MaxEntries;
            var lru = _buckets
                .OrderBy(kv => kv.Value.LastUsed)
                .Take(excess)
                .Select(kv => kv.Key)
                .ToList();
            foreach (var key in lru)
            {
                _buckets.TryRemove(key, out _);
            }
        }

        _logger.LogDebug(
            "McpRateLimiter sweep: pruned {Stale} stale buckets, post-sweep count = {Count}",
            stale.Count, _buckets.Count);
    }

    /// <summary>
    /// Internal bucket state. Mutated only under <see cref="Lock"/>; the
    /// fields are otherwise free of concurrency guarantees.
    /// </summary>
    private sealed class TokenBucket
    {
        public int Capacity;
        public double Tokens;
        public double RefillPerSecond;
        public DateTime LastRefill;
        public DateTime LastUsed;
        public readonly object Lock = new();
    }
}

/// <summary>
/// Outcome of a single <see cref="McpRateLimiter.TryAcquire"/> call. The
/// limiter never throws — denials are returned as
/// <see cref="Allowed"/> = <c>false</c> with a
/// <see cref="RetryAfterMs"/> hint the caller can surface to the daemon's
/// MCP client (which typically retries with backoff).
/// </summary>
public sealed record RateLimitDecision(bool Allowed, int RetryAfterMs);
