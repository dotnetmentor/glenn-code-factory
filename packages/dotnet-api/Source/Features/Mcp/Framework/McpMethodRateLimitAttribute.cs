namespace Source.Features.Mcp.Framework;

/// <summary>
/// Per-method override for the MCP rate limiter. Stamp this on a concrete
/// MCP controller's action to override the framework default
/// (<see cref="McpRateLimiter.DefaultCapacity"/> /
/// <see cref="McpRateLimiter.DefaultRefillPerSecond"/> — 60 calls / minute
/// sustained, burst to 60). Read by <see cref="McpControllerBase"/> via
/// reflection (with a per-(controllerType, methodName) cache) on first
/// invocation.
///
/// <para><b>Why an attribute and not config.</b> The rate-limit budget is a
/// piece of the controller's contract — it lives next to the action that
/// owns it so a maintainer reading the controller knows the budget without
/// having to chase configuration. Mirrors <see cref="McpServerAttribute"/>'s
/// rationale.</para>
///
/// <para><b>Token-bucket semantics.</b>
/// <see cref="Capacity"/> is the burst ceiling — the bucket starts full at
/// this value, and refilling beyond it is clamped. <see cref="RefillPerSecond"/>
/// is the steady-state replenishment rate. A method tagged
/// <c>[McpMethodRateLimit(capacity: 5, refillPerSecond: 0.5)]</c> tolerates
/// a burst of 5 calls back-to-back, then sustains one call every two
/// seconds.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class McpMethodRateLimitAttribute : Attribute
{
    /// <summary>
    /// Burst ceiling in tokens. Bucket starts full at this value; refills
    /// are clamped to this maximum. Must be positive.
    /// </summary>
    public int Capacity { get; }

    /// <summary>
    /// Steady-state replenishment rate in tokens / second. Fractional values
    /// are supported (e.g. <c>0.5</c> = one token every two seconds). Must
    /// be positive.
    /// </summary>
    public double RefillPerSecond { get; }

    public McpMethodRateLimitAttribute(int capacity, double refillPerSecond)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be positive.");
        if (refillPerSecond <= 0)
            throw new ArgumentOutOfRangeException(nameof(refillPerSecond), refillPerSecond, "RefillPerSecond must be positive.");

        Capacity = capacity;
        RefillPerSecond = refillPerSecond;
    }
}
