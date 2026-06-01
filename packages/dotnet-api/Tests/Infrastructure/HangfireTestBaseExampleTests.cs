using Hangfire;
using Hangfire.Common;
using Hangfire.Server;
using Hangfire.States;

namespace Api.Tests.Infrastructure;

/// <summary>
/// Smoke tests that prove <see cref="HangfireTestBase"/> wires up correctly:
///   - A no-op job reaches the Succeeded state.
///   - A throwing job reaches the Failed state.
///   - Globally-registered server filters are actually invoked during execution.
/// </summary>
[Collection(HangfireTestCollection.Name)]
public class HangfireTestBaseExampleTests : HangfireTestBase
{
    // Used by GlobalFilter_IsInvoked_DuringJobExecution.
    private static int _filterInvocationCount;
    private static readonly object _filterLock = new();

    public override Task InitializeAsync()
    {
        // Reset shared counter — parallel xUnit test classes don't run in parallel by default
        // within the same collection, so this is safe for our purposes.
        lock (_filterLock) { _filterInvocationCount = 0; }

        RegisterGlobalFilter(new CountingServerFilter());
        return base.InitializeAsync();
    }

    [Fact]
    public async Task EnqueueTrivialJob_Succeeds()
    {
        TrivialJob.Reset();

        var finalState = await EnqueueAndWait<TrivialJob>(j => j.Run(), TimeSpan.FromSeconds(5));

        finalState.Should().Be(SucceededState.StateName);
        TrivialJob.ExecutionCount.Should().Be(1, "the worker should have executed the job exactly once");
    }

    [Fact]
    public async Task EnqueueThrowingJob_EndsInFailedState()
    {
        var finalState = await EnqueueAndWait<ThrowingJob>(j => j.Run(), TimeSpan.FromSeconds(5));

        // Hangfire retries failed jobs by default, but the immediate post-execution state is Failed
        // (it transitions to Scheduled for retry afterwards). Accept either Failed or Scheduled —
        // what we care about is that the exception surfaced through Hangfire's pipeline.
        finalState.Should().BeOneOf(FailedState.StateName, ScheduledState.StateName);
    }

    [Fact]
    public async Task GlobalFilter_IsInvoked_DuringJobExecution()
    {
        TrivialJob.Reset();

        var finalState = await EnqueueAndWait<TrivialJob>(j => j.Run(), TimeSpan.FromSeconds(5));

        finalState.Should().Be(SucceededState.StateName);
        _filterInvocationCount.Should().BeGreaterThan(0,
            "the globally-registered CountingServerFilter should have fired during job execution");
    }

    // ---- Job classes used by the tests above ----

    public class TrivialJob
    {
        public static int ExecutionCount;

        public static void Reset() => Interlocked.Exchange(ref ExecutionCount, 0);

        public void Run()
        {
            Interlocked.Increment(ref ExecutionCount);
        }
    }

    public class ThrowingJob
    {
        public void Run() => throw new InvalidOperationException("boom");
    }

    /// <summary>
    /// A minimal IServerFilter that increments a counter each time the Hangfire pipeline
    /// wraps a job execution. Proves that global filter registration in HangfireTestBase
    /// actually flows through to the running server.
    /// </summary>
    private class CountingServerFilter : IServerFilter
    {
        public void OnPerforming(PerformingContext filterContext)
        {
            lock (_filterLock) { _filterInvocationCount++; }
        }

        public void OnPerformed(PerformedContext filterContext)
        {
        }
    }
}
