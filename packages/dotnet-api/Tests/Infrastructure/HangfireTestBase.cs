using System.Linq.Expressions;
using Hangfire;
using Hangfire.Common;
using Hangfire.InMemory;
using Hangfire.Logging;
using Hangfire.States;
using Hangfire.Storage;
using Hangfire.Storage.Monitoring;

namespace Api.Tests.Infrastructure;

/// <summary>
/// Base class for tests that need a live Hangfire <see cref="BackgroundJobServer"/> running
/// against in-process storage (<c>Hangfire.InMemory</c>). Each test fixture gets a fresh
/// storage instance and a freshly started server, torn down in <see cref="DisposeAsync"/>.
///
/// Use <see cref="RegisterGlobalFilter"/> before calling the base <see cref="InitializeAsync"/>
/// (i.e. from an overridden <c>InitializeAsync</c> that calls <c>base.InitializeAsync()</c> last)
/// to attach global filters the same way production does.
/// </summary>
public abstract class HangfireTestBase : IAsyncLifetime
{
    private BackgroundJobServer? _server;
    private InMemoryStorage? _storage;
    private BackgroundJobClient? _client;
    private readonly List<object> _globalFilters = new();

    /// <summary>
    /// Storage for the current test fixture. Available after <see cref="InitializeAsync"/>.
    /// </summary>
    protected JobStorage Storage => _storage
        ?? throw new InvalidOperationException("Storage is not initialized yet. Did you call base.InitializeAsync()?");

    /// <summary>
    /// A <see cref="BackgroundJobClient"/> bound to this fixture's storage. Use this instead of
    /// the static <c>BackgroundJob.Enqueue</c> API so tests are isolated when xUnit runs them in parallel.
    /// </summary>
    protected BackgroundJobClient Client => _client
        ?? throw new InvalidOperationException("Client is not initialized yet. Did you call base.InitializeAsync()?");

    /// <summary>
    /// Register a Hangfire global filter to be applied before the server starts. Call this from
    /// an override of <see cref="InitializeAsync"/> BEFORE calling <c>base.InitializeAsync()</c>.
    /// </summary>
    protected void RegisterGlobalFilter(object filter)
    {
        if (_server is not null)
        {
            throw new InvalidOperationException(
                "Global filters must be registered before the Hangfire server starts. " +
                "Register them before calling base.InitializeAsync().");
        }
        _globalFilters.Add(filter);
    }

    public virtual Task InitializeAsync()
    {
        // Hangfire's LibLog provider is statically cached. If a prior test booted
        // ASP.NET's AspNetCoreLogProvider (which caches an IServiceProvider) and then
        // disposed its host, subsequent InMemoryStorage construction will ObjectDisposed
        // on the stale factory. Force a safe noop provider so the static state is always
        // valid within this fixture.
        LogProvider.SetCurrentLogProvider(NoOpLogProvider.Instance);

        // Hangfire resolves job instances via the static JobActivator.Current. If a prior
        // IntegrationTestBase run installed an ASP.NET-scoped activator and then disposed
        // its IServiceProvider, every job we enqueue here would throw ObjectDisposedException
        // when Hangfire tries to instantiate it. Reset to the default reflection-based
        // activator so our fixture is independent of any prior host's service container.
        JobActivator.Current = new JobActivator();

        _storage = new InMemoryStorage();
        _client = new BackgroundJobClient(_storage);

        // Hangfire relies on a static JobStorage.Current for many of its internal paths;
        // point it at our fixture's storage so monitoring APIs and server internals work.
        JobStorage.Current = _storage;

        // Clear any filters leaked from prior tests before re-adding ours.
        // Fixtures that don't register filters still start clean (no cross-test bleed).
        GlobalJobFilters.Filters.Clear();
        foreach (var filter in _globalFilters)
        {
            GlobalJobFilters.Filters.Add(filter);
        }

        _server = new BackgroundJobServer(new BackgroundJobServerOptions
        {
            WorkerCount = 2,
            Queues = new[] { "default" },
            SchedulePollingInterval = TimeSpan.FromMilliseconds(100),
            HeartbeatInterval = TimeSpan.FromSeconds(30),
        }, _storage);

        return Task.CompletedTask;
    }

    public virtual async Task DisposeAsync()
    {
        if (_server is not null)
        {
            // Give running jobs a moment to finish before forcing shutdown.
            _server.SendStop();
            using var shutdownCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await _server.WaitForShutdownAsync(shutdownCts.Token);
            }
            catch
            {
                // Swallow — we're tearing down.
            }
            _server.Dispose();
            _server = null;
        }

        GlobalJobFilters.Filters.Clear();
        _storage = null;
    }

    /// <summary>
    /// Enqueues a fire-and-forget job and polls its state until it reaches a terminal state
    /// (Succeeded / Failed / Deleted) or the <paramref name="timeout"/> elapses.
    /// Returns the final state name (or the last observed state if it times out).
    /// </summary>
    protected async Task<string> EnqueueAndWait<T>(Expression<Action<T>> methodCall, TimeSpan timeout)
    {
        var jobId = Client.Enqueue(methodCall);
        return await WaitForTerminalState(jobId, timeout);
    }

    /// <summary>
    /// Enqueues a fire-and-forget job and polls its state until it reaches a terminal state
    /// or the <paramref name="timeout"/> elapses. Overload for parameterless static calls.
    /// </summary>
    protected async Task<string> EnqueueAndWait(Expression<Action> methodCall, TimeSpan timeout)
    {
        var jobId = Client.Enqueue(methodCall);
        return await WaitForTerminalState(jobId, timeout);
    }

    /// <summary>
    /// Returns the state history (oldest first) for a given job ID.
    /// </summary>
    protected IEnumerable<StateHistoryDto> GetJobHistory(string jobId)
    {
        var monitoringApi = Storage.GetMonitoringApi();
        var details = monitoringApi.JobDetails(jobId);
        return details?.History ?? Enumerable.Empty<StateHistoryDto>();
    }

    /// <summary>
    /// Returns the current (most recent) state name for a given job ID.
    /// </summary>
    protected string? GetCurrentState(string jobId)
    {
        var monitoringApi = Storage.GetMonitoringApi();
        var details = monitoringApi.JobDetails(jobId);
        return details?.History?.FirstOrDefault()?.StateName;
    }

    private async Task<string> WaitForTerminalState(string jobId, TimeSpan timeout)
    {
        var monitoringApi = Storage.GetMonitoringApi();
        var deadline = DateTime.UtcNow + timeout;
        string? lastState = null;

        while (DateTime.UtcNow < deadline)
        {
            var details = monitoringApi.JobDetails(jobId);
            var currentState = details?.History?.FirstOrDefault()?.StateName;

            if (currentState is not null)
            {
                lastState = currentState;
                if (currentState == SucceededState.StateName
                    || currentState == FailedState.StateName
                    || currentState == DeletedState.StateName)
                {
                    return currentState;
                }
            }

            await Task.Delay(50);
        }

        return lastState ?? "Unknown";
    }

    /// <summary>
    /// A minimal <see cref="ILogProvider"/> that drops everything. Used to shield tests
    /// from Hangfire's statically-cached <c>AspNetCoreLogProvider</c>, which can retain
    /// a disposed <c>ILoggerFactory</c> across test host lifecycles.
    /// </summary>
    private sealed class NoOpLogProvider : ILogProvider
    {
        public static readonly NoOpLogProvider Instance = new();
        public ILog GetLogger(string name) => NoOpLog.Instance;

        private sealed class NoOpLog : ILog
        {
            public static readonly NoOpLog Instance = new();
            public bool Log(LogLevel logLevel, Func<string>? messageFunc, Exception? exception = null) => false;
        }
    }
}
