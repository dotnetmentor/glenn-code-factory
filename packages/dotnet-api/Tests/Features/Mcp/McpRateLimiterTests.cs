using Api.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Source.Features.Mcp.Framework;

namespace Api.Tests.Features.Mcp;

/// <summary>
/// Unit coverage for <see cref="McpRateLimiter"/> — the in-memory token-bucket
/// gate consulted by <see cref="McpControllerBase"/> on every MCP call. We
/// inject a <see cref="FakeClock"/> so refill / sweep behaviour is
/// deterministic; nothing here uses real wall-clock time.
///
/// <para>Tests focus on the contract surface: bucket lifecycle, refill maths,
/// per-key independence, sweep eviction. The limiter's interaction with the
/// controller (audit row, envelope shape) is covered in
/// <see cref="McpControllerBaseTests"/>.</para>
/// </summary>
public class McpRateLimiterTests
{
    private static McpRateLimiter Create(FakeClock clock) =>
        new McpRateLimiter(clock, NullLogger<McpRateLimiter>.Instance);

    [Fact]
    public void TryAcquire_FreshBucket_FirstCall_IsAllowedAndCountsAsOne()
    {
        var clock = new FakeClock();
        var limiter = Create(clock);

        var decision = limiter.TryAcquire(Guid.NewGuid(), "kanban", "tools/list", capacity: 60, refillPerSecond: 1.0);

        decision.Allowed.Should().BeTrue();
        decision.RetryAfterMs.Should().Be(0);
        limiter.BucketCount.Should().Be(1, "the first call lazily creates exactly one bucket");
    }

    [Fact]
    public void TryAcquire_ExhaustsCapacity_ThenDeniesWithRetryHint()
    {
        var clock = new FakeClock();
        var limiter = Create(clock);
        var rt = Guid.NewGuid();

        // Drain a full 60-token bucket without advancing the clock — no refill.
        for (int i = 0; i < 60; i++)
        {
            var d = limiter.TryAcquire(rt, "k", "m", 60, 1.0);
            d.Allowed.Should().BeTrue($"call {i + 1} of 60 should fit in the burst budget");
        }

        var denied = limiter.TryAcquire(rt, "k", "m", 60, 1.0);
        denied.Allowed.Should().BeFalse("the 61st call exceeds the burst");
        // Refill rate 1/s ⇒ deficit of 1 token ⇒ 1000 ms.
        denied.RetryAfterMs.Should().Be(1000);
    }

    [Fact]
    public void TryAcquire_AfterClockAdvance_RefillsTokens()
    {
        var clock = new FakeClock();
        var limiter = Create(clock);
        var rt = Guid.NewGuid();

        for (int i = 0; i < 60; i++)
            limiter.TryAcquire(rt, "k", "m", 60, 1.0);
        limiter.TryAcquire(rt, "k", "m", 60, 1.0).Allowed.Should().BeFalse();

        // Advance one second — exactly one token regenerates at 1 rps.
        clock.Advance(TimeSpan.FromSeconds(1));
        var afterRefill = limiter.TryAcquire(rt, "k", "m", 60, 1.0);
        afterRefill.Allowed.Should().BeTrue();

        // The next call is back to denial — only one token regenerated.
        limiter.TryAcquire(rt, "k", "m", 60, 1.0).Allowed.Should().BeFalse();
    }

    [Fact]
    public void TryAcquire_DifferentKeys_AreIndependent()
    {
        var clock = new FakeClock();
        var limiter = Create(clock);
        var rtA = Guid.NewGuid();
        var rtB = Guid.NewGuid();

        // Drain runtime A.
        for (int i = 0; i < 60; i++)
            limiter.TryAcquire(rtA, "k", "m", 60, 1.0);
        limiter.TryAcquire(rtA, "k", "m", 60, 1.0).Allowed.Should().BeFalse();

        // Runtime B's bucket is untouched.
        limiter.TryAcquire(rtB, "k", "m", 60, 1.0).Allowed.Should().BeTrue();

        // Same runtime, different server: also independent.
        limiter.TryAcquire(rtA, "other-server", "m", 60, 1.0).Allowed.Should().BeTrue();

        // Same runtime + server, different method: also independent.
        limiter.TryAcquire(rtA, "k", "other-method", 60, 1.0).Allowed.Should().BeTrue();
    }

    [Fact]
    public void TryAcquire_HonoursPerMethodOverride_SmallCapacityFastRefill()
    {
        var clock = new FakeClock();
        var limiter = Create(clock);
        var rt = Guid.NewGuid();

        // capacity=2, refill=10/s ⇒ 100 ms per token.
        limiter.TryAcquire(rt, "k", "fast", capacity: 2, refillPerSecond: 10.0).Allowed.Should().BeTrue();
        limiter.TryAcquire(rt, "k", "fast", 2, 10.0).Allowed.Should().BeTrue();
        var denied = limiter.TryAcquire(rt, "k", "fast", 2, 10.0);
        denied.Allowed.Should().BeFalse("the 3rd call exceeds the 2-token burst");
        denied.RetryAfterMs.Should().Be(100, "1 token deficit / 10 rps = 100 ms");

        // After advancing 100 ms, the next call succeeds.
        clock.Advance(TimeSpan.FromMilliseconds(100));
        limiter.TryAcquire(rt, "k", "fast", 2, 10.0).Allowed.Should().BeTrue();
    }

    [Fact]
    public void TryAcquire_RefillIsClampedToCapacity()
    {
        var clock = new FakeClock();
        var limiter = Create(clock);
        var rt = Guid.NewGuid();

        // Burn one token, then advance the clock by an hour. Refill should
        // clamp at capacity (60), not balloon to 60 + 3600 = 3660.
        limiter.TryAcquire(rt, "k", "m", 60, 1.0).Allowed.Should().BeTrue();
        clock.Advance(TimeSpan.FromHours(1));

        // We can drain exactly capacity-many tokens, then the next call denies.
        for (int i = 0; i < 60; i++)
            limiter.TryAcquire(rt, "k", "m", 60, 1.0).Allowed.Should().BeTrue($"call {i + 1}/60 inside refilled-and-clamped capacity");

        limiter.TryAcquire(rt, "k", "m", 60, 1.0).Allowed.Should().BeFalse("refill is clamped to capacity");
    }

    [Fact]
    public void Sweep_PrunesIdleBucketsBeyondTtl()
    {
        var clock = new FakeClock();
        var limiter = Create(clock);

        // Touch three buckets at t=0.
        for (int i = 0; i < 3; i++)
            limiter.TryAcquire(Guid.NewGuid(), "k", "m", 60, 1.0);
        limiter.BucketCount.Should().Be(3);

        // Advance > 1 hour idle TTL.
        clock.Advance(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1));
        limiter.Sweep();

        limiter.BucketCount.Should().Be(0, "all buckets idle past 1h are pruned");
    }

    [Fact]
    public void Sweep_KeepsActiveBuckets_PrunesOnlyIdleOnes()
    {
        var clock = new FakeClock();
        var limiter = Create(clock);
        var staleRt = Guid.NewGuid();
        var activeRt = Guid.NewGuid();

        // Stale bucket touched at t=0.
        limiter.TryAcquire(staleRt, "k", "m", 60, 1.0);

        // Advance 30 minutes, touch the active bucket.
        clock.Advance(TimeSpan.FromMinutes(30));
        limiter.TryAcquire(activeRt, "k", "m", 60, 1.0);

        // Advance another 31 minutes (total 61 from stale's last use,
        // 31 from active's last use). Stale crosses the 1h TTL; active doesn't.
        clock.Advance(TimeSpan.FromMinutes(31));
        limiter.Sweep();

        limiter.BucketCount.Should().Be(1, "only the active bucket survives");

        // The active bucket still serves requests — it isn't a fresh creation,
        // so its remaining tokens reflect the prior decrement.
        var d = limiter.TryAcquire(activeRt, "k", "m", 60, 1.0);
        d.Allowed.Should().BeTrue();
    }

    [Fact]
    public void Sweep_LruEviction_TrimsToMaxEntries_WhenIdleTtlCannot()
    {
        // Strategy: populate >MaxEntries buckets all within the IdleTtl window
        // (so the TTL pass keeps them all), staggered LastUsed so the LRU
        // ordering is well-defined, then assert Sweep evicts the oldest down
        // to exactly MaxEntries.
        const int Max = 10_000;
        const int Excess = 5;
        var clock = new FakeClock();
        var limiter = Create(clock);

        // We create Max + Excess buckets. Each bucket is touched at a slightly
        // different clock instant so LastUsed values are unique and ordered.
        for (int i = 0; i < Max + Excess; i++)
        {
            // Advance 1 ms per touch — well inside the 1h TTL.
            clock.Advance(TimeSpan.FromMilliseconds(1));
            limiter.TryAcquire(Guid.NewGuid(), "k", "m", 60, 1.0);
        }

        limiter.BucketCount.Should().Be(Max + Excess, "all buckets created within the TTL window survive");

        limiter.Sweep();

        limiter.BucketCount.Should().Be(Max,
            "LRU pass evicts the {0} oldest entries to bring the dict back to MaxEntries", Excess);
    }
}
