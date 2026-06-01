using Source.Shared;

namespace Api.Tests.Infrastructure;

/// <summary>
/// Test double for <see cref="IClock"/> that returns a fixed time (or whatever the test sets).
///
/// Tests have two ways to control time:
///   - Set <see cref="UtcNow"/> directly to a specific instant.
///   - Call <see cref="Advance"/> with a <see cref="TimeSpan"/> to move forward.
/// </summary>
public class FakeClock : IClock
{
    public DateTime UtcNow { get; set; }

    public FakeClock(DateTime utcNow)
    {
        UtcNow = utcNow;
    }

    public FakeClock() : this(new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc))
    {
    }

    /// <summary>
    /// Advance the clock forward by the supplied amount. Convenience sugar over
    /// <c>UtcNow += delta</c> that reads better at call sites.
    /// </summary>
    public void Advance(TimeSpan delta)
    {
        UtcNow = UtcNow.Add(delta);
    }
}
