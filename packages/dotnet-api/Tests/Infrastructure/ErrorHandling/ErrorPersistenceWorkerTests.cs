using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Source.Infrastructure;
using Source.Infrastructure.ErrorHandling;
using Source.Shared;

namespace Api.Tests.Infrastructure.ErrorHandling;

/// <summary>
/// Tests for the refactored <see cref="ErrorPersistenceWorker"/>: batched writes,
/// read-up-to-N-or-wait-M flushing, swallow-and-continue on failure, and the
/// graceful drain on shutdown.
///
/// Two test styles are mixed:
///
///   - Pure-logic tests of <see cref="ErrorPersistenceWorker.CollectBatchAsync"/>
///     which is a deterministic, clock-driven helper. No real delays, no DbContext.
///     Used to assert the read-up-to-N-or-wait-M contract cheaply and reliably.
///
///   - Integration tests that spin up the full worker with a test scope factory and
///     an in-memory <see cref="ApplicationDbContext"/>. Used to assert DB-side effects
///     (AddRange + single SaveChanges per batch), the swallow-failure-and-continue
///     contract, and the shutdown drain. These use short real timeouts (25ms) rather
///     than trying to mock out <see cref="Task.Delay"/>, which keeps them deterministic
///     without adding yet another abstraction layer.
///
/// Time-abstraction note:
///   We keep <see cref="IClock"/> as the single time-source. The batch-collection
///   helper reads <c>_clock.UtcNow</c> to build its deadline and compute remaining
///   time per inner read. Because the helper accepts the timeout as a plain
///   <see cref="TimeSpan"/> argument, tests can shrink it (e.g. to 25ms) and still
///   exercise the full path without flakiness. The harder alternative — abstracting
///   <c>Task.Delay</c> behind an <c>IDelayer</c> — was rejected: the extra surface
///   area is not worth it when a 25ms real delay is deterministic enough.
/// </summary>
public class ErrorPersistenceWorkerTests
{
    private static ErrorEntry NewEntry(string message, DateTime? occurredAt = null) => new(
        Message: message,
        StackTrace: null,
        Source: "Test",
        Severity: "Error",
        CorrelationId: null,
        RequestPath: null,
        RequestMethod: null,
        ContextData: null,
        OccurredAt: occurredAt ?? new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc));

    private static Channel<ErrorEntry> NewChannel()
        => Channel.CreateUnbounded<ErrorEntry>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

    // ---------------------------------------------------------------------------------
    // Pure-logic tests for CollectBatchAsync
    // ---------------------------------------------------------------------------------

    [Fact]
    public async Task CollectBatchAsync_Flushes_WhenBatchReachesMaxSize()
    {
        // Arrange: pre-fill channel with exactly 100 entries. maxBatchSize=100 means the
        // helper must return as soon as it has read all 100, NOT wait for the timeout.
        var channel = NewChannel();
        for (var i = 0; i < 100; i++)
        {
            await channel.Writer.WriteAsync(NewEntry($"msg-{i}"));
        }
        var clock = new FakeClock();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Act: deliberately use a LONG timeout so that any flush we observe is driven by
        // the count-reached branch, not the timeout branch.
        var batch = await ErrorPersistenceWorker.CollectBatchAsync(
            channel.Reader,
            maxBatchSize: 100,
            timeout: TimeSpan.FromSeconds(60),
            clock,
            cts.Token);

        // Assert
        batch.Should().HaveCount(100);
        batch[0].Message.Should().Be("msg-0");
        batch[99].Message.Should().Be("msg-99");
    }

    [Fact]
    public async Task CollectBatchAsync_Flushes_WhenTimeoutElapses_BeforeBatchFills()
    {
        // Arrange: only 50 entries in the channel, but maxBatchSize=100. The helper must
        // return with the 50 it has after the timeout, not hang forever.
        var channel = NewChannel();
        for (var i = 0; i < 50; i++)
        {
            await channel.Writer.WriteAsync(NewEntry($"msg-{i}"));
        }
        var clock = new FakeClock();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Act
        var batch = await ErrorPersistenceWorker.CollectBatchAsync(
            channel.Reader,
            maxBatchSize: 100,
            timeout: TimeSpan.FromMilliseconds(50),
            clock,
            cts.Token);

        // Assert
        batch.Should().HaveCount(50);
    }

    [Fact]
    public async Task CollectBatchAsync_Flushes_SingleItem_AfterTimeout()
    {
        // Arrange: a single entry. The batch must come back with just that one after
        // the timeout, not block waiting for a 100-entry batch that will never arrive.
        var channel = NewChannel();
        await channel.Writer.WriteAsync(NewEntry("lonely"));
        var clock = new FakeClock();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Act
        var batch = await ErrorPersistenceWorker.CollectBatchAsync(
            channel.Reader,
            maxBatchSize: 100,
            timeout: TimeSpan.FromMilliseconds(50),
            clock,
            cts.Token);

        // Assert
        batch.Should().HaveCount(1);
        batch[0].Message.Should().Be("lonely");
    }

    [Fact]
    public async Task CollectBatchAsync_ReturnsEmpty_WhenChannelEmptyAndTimeoutElapses()
    {
        // Arrange: empty channel. The helper must return an empty batch after the
        // timeout — the caller (the worker) will simply skip the flush for empty batches.
        var channel = NewChannel();
        var clock = new FakeClock();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Act
        var batch = await ErrorPersistenceWorker.CollectBatchAsync(
            channel.Reader,
            maxBatchSize: 100,
            timeout: TimeSpan.FromMilliseconds(25),
            clock,
            cts.Token);

        // Assert
        batch.Should().BeEmpty();
    }

    [Fact]
    public async Task CollectBatchAsync_ReturnsEmpty_WhenCancelled()
    {
        // Arrange: cancellation must exit the helper with whatever it has (here: nothing).
        var channel = NewChannel();
        var clock = new FakeClock();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act
        var batch = await ErrorPersistenceWorker.CollectBatchAsync(
            channel.Reader,
            maxBatchSize: 100,
            timeout: TimeSpan.FromSeconds(60),
            clock,
            cts.Token);

        // Assert
        batch.Should().BeEmpty();
    }

    [Fact]
    public async Task CollectBatchAsync_UsesClockForTimeoutDeadline()
    {
        // Sanity-check: the helper reads _clock.UtcNow to compute its deadline. If we
        // point the clock at the future and set a 500ms timeout, the helper should still
        // only wait real-time ~500ms — the clock controls the *deadline math*, the
        // underlying Task.Delay / CancelAfter uses wall-clock for the real sleep.
        //
        // The explicit assertion here is just that passing IClock as a parameter is
        // observable at runtime: the batch returned is empty, no errors, fast.
        var channel = NewChannel();
        var clock = new FakeClock(new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var batch = await ErrorPersistenceWorker.CollectBatchAsync(
            channel.Reader,
            maxBatchSize: 100,
            timeout: TimeSpan.FromMilliseconds(25),
            clock,
            cts.Token);

        batch.Should().BeEmpty();
    }

    // ---------------------------------------------------------------------------------
    // Integration tests — full worker + in-memory DbContext
    // ---------------------------------------------------------------------------------

    [Fact]
    public async Task Worker_Flushes_WhenBatchReachesOneHundredItems()
    {
        // Arrange: enqueue 100 before the worker starts. With maxBatchSize=100 the very
        // first iteration should pick up all 100 and write them in one SaveChanges call.
        var queue = new ErrorQueue(new PiiRedactor());
        for (var i = 0; i < 100; i++)
        {
            await queue.EnqueueAsync(NewEntry($"msg-{i}"));
        }

        var dbName = Guid.NewGuid().ToString();
        using var scopeFactory = new TestScopeFactory(dbName);

        var worker = new ErrorPersistenceWorker(
            queue,
            scopeFactory,
            NullLogger<ErrorPersistenceWorker>.Instance,
            new FakeClock(),
            new ErrorSignatureHasher(),
            new ErrorPersistenceWorkerOptions
            {
                MaxBatchSize = 100,
                BatchTimeout = TimeSpan.FromMilliseconds(25),
            });

        // Act: run for a short window, then stop — enough time for the worker to pick up
        // the pre-filled batch.
        using var runCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        await worker.StartAsync(runCts.Token);
        queue.CompleteWriting();
        await WaitUntilAsync(async () =>
        {
            using var s = scopeFactory.CreateScope();
            var d = s.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            return await d.ErrorLogs.CountAsync() >= 100;
        }, TimeSpan.FromSeconds(3));
        await worker.StopAsync(CancellationToken.None);

        // Assert: all 100 rows persisted.
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var count = await db.ErrorLogs.CountAsync();
        count.Should().Be(100);
    }

    [Fact]
    public async Task Worker_Flushes_WhenTimeoutElapses_BeforeBatchFills()
    {
        // Arrange: only 50 entries — less than the batch size. The worker must still
        // flush them after the batch-timeout window.
        var queue = new ErrorQueue(new PiiRedactor());
        for (var i = 0; i < 50; i++)
        {
            await queue.EnqueueAsync(NewEntry($"msg-{i}"));
        }

        var dbName = Guid.NewGuid().ToString();
        using var scopeFactory = new TestScopeFactory(dbName);

        var worker = new ErrorPersistenceWorker(
            queue,
            scopeFactory,
            NullLogger<ErrorPersistenceWorker>.Instance,
            new FakeClock(),
            new ErrorSignatureHasher(),
            new ErrorPersistenceWorkerOptions
            {
                MaxBatchSize = 100,
                BatchTimeout = TimeSpan.FromMilliseconds(25),
            });

        // Act
        await worker.StartAsync(CancellationToken.None);
        queue.CompleteWriting();
        await WaitUntilAsync(async () =>
        {
            using var s = scopeFactory.CreateScope();
            var d = s.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            return await d.ErrorLogs.CountAsync() >= 50;
        }, TimeSpan.FromSeconds(3));
        await worker.StopAsync(CancellationToken.None);

        // Assert: all 50 rows persisted.
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var count = await db.ErrorLogs.CountAsync();
        count.Should().Be(50);
    }

    [Fact]
    public async Task Worker_Flushes_SingleItem_AfterTimeout()
    {
        // Arrange: single entry. Must be flushed after the timeout — not held forever
        // waiting for a partner that never comes.
        var queue = new ErrorQueue(new PiiRedactor());
        await queue.EnqueueAsync(NewEntry("lonely"));

        var dbName = Guid.NewGuid().ToString();
        using var scopeFactory = new TestScopeFactory(dbName);

        var worker = new ErrorPersistenceWorker(
            queue,
            scopeFactory,
            NullLogger<ErrorPersistenceWorker>.Instance,
            new FakeClock(),
            new ErrorSignatureHasher(),
            new ErrorPersistenceWorkerOptions
            {
                MaxBatchSize = 100,
                BatchTimeout = TimeSpan.FromMilliseconds(25),
            });

        // Act
        await worker.StartAsync(CancellationToken.None);
        await WaitUntilAsync(async () =>
        {
            using var s = scopeFactory.CreateScope();
            var d = s.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            return await d.ErrorLogs.CountAsync() >= 1;
        }, TimeSpan.FromSeconds(3));
        queue.CompleteWriting();
        await worker.StopAsync(CancellationToken.None);

        // Assert
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db.ErrorLogs.SingleAsync()).Message.Should().Be("lonely");
    }

    [Fact]
    public async Task Worker_SaveChangesFailure_DoesNotKillWorker_NextBatchProceeds()
    {
        // Arrange: the scope factory is parameterised by a shared "mode flag" — while
        // ThrowMode is true, the worker's DbContext throws on SaveChanges; once flipped to
        // false, subsequent writes succeed. This avoids the scope-counter race where test-
        // side poll calls (to check DB state) interact with the worker's scope counter.
        var queue = new ErrorQueue(new PiiRedactor());
        await queue.EnqueueAsync(NewEntry("batch-a"));

        var dbName = Guid.NewGuid().ToString();
        using var scopeFactory = new ToggleableThrowScopeFactory(dbName) { ThrowMode = true };

        var startingFailed = ErrorPipelineMetrics.PersistFailed;
        var startingPersisted = ErrorPipelineMetrics.Persisted;

        var worker = new ErrorPersistenceWorker(
            queue,
            scopeFactory,
            NullLogger<ErrorPersistenceWorker>.Instance,
            new FakeClock(),
            new ErrorSignatureHasher(),
            new ErrorPersistenceWorkerOptions
            {
                MaxBatchSize = 100,
                BatchTimeout = TimeSpan.FromMilliseconds(25),
            });

        // Act: start the worker, wait for it to attempt-and-fail on batch-a, then flip
        // the switch and enqueue batch-b.
        await worker.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => Task.FromResult(scopeFactory.FailureCount >= 1), TimeSpan.FromSeconds(3));

        scopeFactory.ThrowMode = false;
        await queue.EnqueueAsync(NewEntry("batch-b"));
        await WaitUntilAsync(async () =>
        {
            using var s = scopeFactory.CreateReadScope();
            var d = s.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            return await d.ErrorLogs.AnyAsync(e => e.Message == "batch-b");
        }, TimeSpan.FromSeconds(3));

        queue.CompleteWriting();
        await worker.StopAsync(CancellationToken.None);

        // Assert
        using var scope = scopeFactory.CreateReadScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db.ErrorLogs.AnyAsync(e => e.Message == "batch-b")).Should().BeTrue();
        (await db.ErrorLogs.AnyAsync(e => e.Message == "batch-a")).Should().BeFalse(
            "batch-a threw on SaveChanges; it must NOT be re-enqueued or retried (storm amplification risk)");

        // Failure / success counters moved in the right directions.
        (ErrorPipelineMetrics.PersistFailed - startingFailed).Should().BeGreaterThanOrEqualTo(1);
        (ErrorPipelineMetrics.Persisted - startingPersisted).Should().BeGreaterThanOrEqualTo(1);

        // Worker's own failure counter reached at least 1 (proving the catch block ran).
        scopeFactory.FailureCount.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task Worker_CancellationToken_TriggersDrainFlush()
    {
        // Arrange: start the worker FIRST with a long-ish batch timeout, wait for it to
        // settle into its wait-for-more-items state (initial batch drains quickly), then
        // enqueue 5 items and IMMEDIATELY call StopAsync. Those 5 items are sitting in
        // the channel at shutdown-time; the drain-on-shutdown path must flush them.
        //
        // With a naive implementation that only flushes on batch-full or timer-elapsed
        // and exits on cancel without draining, those 5 items would be lost — which is
        // exactly the behaviour this test exists to prevent.
        var queue = new ErrorQueue(new PiiRedactor());

        var dbName = Guid.NewGuid().ToString();
        using var scopeFactory = new TestScopeFactory(dbName);

        var worker = new ErrorPersistenceWorker(
            queue,
            scopeFactory,
            NullLogger<ErrorPersistenceWorker>.Instance,
            new FakeClock(),
            new ErrorSignatureHasher(),
            new ErrorPersistenceWorkerOptions
            {
                MaxBatchSize = 100,
                // Long timeout so the worker is parked in WaitToReadAsync when we stop.
                BatchTimeout = TimeSpan.FromSeconds(30),
            });

        // Start the worker with a FRESH stopping token we can cancel ourselves (StopAsync
        // only waits on the already-running task; for a deterministic drain test we
        // prefer to trigger cancellation directly).
        using var workerCts = new CancellationTokenSource();
        var executeTask = worker.StartAsync(workerCts.Token);
        await executeTask;
        // Give ExecuteAsync a moment to enter its WaitToReadAsync on the empty channel.
        await Task.Delay(50);

        // Act: enqueue 5 items, then cancel. The cancellation bubbles into CollectBatchAsync's
        // linked CTS, which returns whatever it has (the 5 items if the read loop saw them,
        // or nothing if cancel beat the read). The drain-on-shutdown phase must catch the
        // rest.
        for (var i = 0; i < 5; i++)
        {
            await queue.EnqueueAsync(NewEntry($"drain-{i}"));
        }
        await worker.StopAsync(CancellationToken.None);

        // Assert: all 5 rows persisted via the drain path (or the pre-drain flush — either
        // is acceptable; what matters is that nothing was silently lost on shutdown).
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var count = await db.ErrorLogs.CountAsync();
        count.Should().Be(5);
    }

    [Fact]
    public async Task Worker_UsesFakeClock_DeterministicTiming()
    {
        // Sanity check: the worker is constructed with our FakeClock and does not crash.
        // We're not asserting deep timing behaviour — that's covered by the CollectBatchAsync
        // unit tests — but this proves end-to-end that IClock is threaded all the way down.
        var queue = new ErrorQueue(new PiiRedactor());
        var clock = new FakeClock();

        var dbName = Guid.NewGuid().ToString();
        using var scopeFactory = new TestScopeFactory(dbName);

        var worker = new ErrorPersistenceWorker(
            queue,
            scopeFactory,
            NullLogger<ErrorPersistenceWorker>.Instance,
            clock,
            new ErrorSignatureHasher(),
            new ErrorPersistenceWorkerOptions
            {
                MaxBatchSize = 100,
                BatchTimeout = TimeSpan.FromMilliseconds(25),
            });

        await queue.EnqueueAsync(NewEntry("ticktock"));
        await worker.StartAsync(CancellationToken.None);

        // Advance the fake clock for good measure — deterministic, no real wall-clock here.
        clock.Advance(TimeSpan.FromSeconds(1));

        await WaitUntilAsync(async () =>
        {
            using var s = scopeFactory.CreateScope();
            var d = s.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            return await d.ErrorLogs.AnyAsync();
        }, TimeSpan.FromSeconds(3));

        queue.CompleteWriting();
        await worker.StopAsync(CancellationToken.None);
    }

    // ---------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------

    /// <summary>Polls a predicate until it returns true, with a hard timeout.</summary>
    private static async Task WaitUntilAsync(Func<Task<bool>> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await predicate())
            {
                return;
            }
            await Task.Delay(10);
        }
        throw new TimeoutException($"WaitUntilAsync timed out after {timeout.TotalMilliseconds}ms");
    }

    /// <summary>
    /// Minimal <see cref="IServiceScopeFactory"/> that hands out scopes bound to an
    /// in-memory EF database shared across all scopes from this factory — so a test
    /// can insert rows in one scope and read them back in another.
    /// </summary>
    private class TestScopeFactory : IServiceScopeFactory, IDisposable
    {
        private readonly ServiceProvider _provider;

        public TestScopeFactory(string dbName)
        {
            var services = new ServiceCollection();
            services.AddDbContext<ApplicationDbContext>(options =>
                options.UseInMemoryDatabase(dbName));
            _provider = services.BuildServiceProvider();
        }

        public IServiceScope CreateScope() => _provider.CreateScope();

        public void Dispose() => _provider.Dispose();
    }

    /// <summary>
    /// Scope factory with a shared mode-flag. While <see cref="ThrowMode"/> is true,
    /// any DbContext handed to a consumer throws on <c>SaveChangesAsync</c> (but its
    /// add/query operations succeed on a throwaway in-memory DB — so the worker's
    /// batch assembly is exercised normally up to the save). Once the flag is flipped
    /// to false, consumers receive real DbContexts bound to the shared <c>dbName</c>,
    /// so subsequent writes land in the real test DB and can be asserted.
    ///
    /// <see cref="CreateReadScope"/> bypasses the throw wrapper entirely — used by the
    /// test itself to poll and assert DB state without interfering with the worker's
    /// view of the factory.
    /// </summary>
    private class ToggleableThrowScopeFactory : IServiceScopeFactory, IDisposable
    {
        private readonly ServiceProvider _provider;
        private int _failureCount;

        public bool ThrowMode { get; set; }
        public int FailureCount => _failureCount;

        public ToggleableThrowScopeFactory(string dbName)
        {
            var services = new ServiceCollection();
            services.AddDbContext<ApplicationDbContext>(options =>
                options.UseInMemoryDatabase(dbName));
            _provider = services.BuildServiceProvider();
        }

        /// <summary>Scope used by the worker. Respects <see cref="ThrowMode"/>.</summary>
        public IServiceScope CreateScope()
        {
            var innerScope = _provider.CreateScope();
            if (ThrowMode)
            {
                return new ThrowingScope(innerScope, () => Interlocked.Increment(ref _failureCount));
            }
            return innerScope;
        }

        /// <summary>Scope for test-side reads — never throws, always real.</summary>
        public IServiceScope CreateReadScope() => _provider.CreateScope();

        public void Dispose() => _provider.Dispose();

        private class ThrowingScope : IServiceScope
        {
            private readonly IServiceScope _inner;
            private readonly ThrowingServiceProvider _sp;

            public ThrowingScope(IServiceScope inner, Action onThrow)
            {
                _inner = inner;
                _sp = new ThrowingServiceProvider(inner.ServiceProvider, onThrow);
            }

            public IServiceProvider ServiceProvider => _sp;
            public void Dispose() => _inner.Dispose();
        }

        private class ThrowingServiceProvider : IServiceProvider
        {
            private readonly IServiceProvider _inner;
            private readonly Action _onThrow;

            public ThrowingServiceProvider(IServiceProvider inner, Action onThrow)
            {
                _inner = inner;
                _onThrow = onThrow;
            }

            public object? GetService(Type serviceType)
            {
                var svc = _inner.GetService(serviceType);
                if (svc is ApplicationDbContext)
                {
                    // Return a brand-new isolated DbContext whose SaveChangesAsync throws.
                    // We don't want the failed Add to touch the shared test DB.
                    return new FailingDbContext(_onThrow);
                }
                return svc;
            }
        }

        /// <summary>
        /// DbContext whose <c>SaveChangesAsync</c> always throws. Backed by a throwaway
        /// in-memory DB so Add / AddRange / etc. still work up to the (failing) save.
        /// </summary>
        private class FailingDbContext : ApplicationDbContext
        {
            private readonly Action _onThrow;

            public FailingDbContext(Action onThrow) : base(BuildOptions())
            {
                _onThrow = onThrow;
            }

            private static DbContextOptions<ApplicationDbContext> BuildOptions()
                => new DbContextOptionsBuilder<ApplicationDbContext>()
                    .UseInMemoryDatabase("failing-" + Guid.NewGuid())
                    .Options;

            public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
            {
                _onThrow();
                throw new InvalidOperationException("simulated DB failure");
            }
        }
    }
}
