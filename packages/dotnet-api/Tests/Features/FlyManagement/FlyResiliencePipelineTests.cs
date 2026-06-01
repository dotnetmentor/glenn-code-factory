using System.Net;
using System.Text;
using Api.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Polly.CircuitBreaker;
using Polly.RateLimiting;
using Source.Features.FlyManagement;
using Source.Features.FlyManagement.Configuration;
using Source.Features.FlyManagement.Extensions;
using Source.Infrastructure;

namespace Api.Tests.Features.FlyManagement;

/// <summary>
/// Card 7 coverage: the resilience pipeline configured by
/// <see cref="FlyManagementExtensions.AddFlyManagement"/>. Every test wires a real
/// <see cref="FlyClient"/> through the same DI registration we use in production,
/// substitutes the innermost <see cref="HttpMessageHandler"/> with a deterministic
/// scripted handler, and asserts on the observable behaviour of the outer pipeline:
/// retry semantics, circuit-breaker open/fail-fast, and the concurrency limiter
/// rejecting beyond capacity.
///
/// <para>Why we go through DI rather than constructing a Polly pipeline by hand: it
/// guarantees the tests stay honest if the production wiring changes. If a future
/// edit drops the retry strategy or rearranges the order, these tests fail
/// immediately rather than silently testing a hand-rolled pipeline that no longer
/// matches reality.</para>
///
/// <para>What's not covered here: circuit-breaker half-open recovery after the
/// 30-second break duration. That requires either real wall-clock time (slow) or
/// a <c>FakeTimeProvider</c> integration with Polly that the v8 wrapper does not
/// yet expose at the resilience-handler level. Documented as a known gap; the
/// open-the-circuit and fail-fast semantics are what catch the bug we care about.
/// </para>
/// </summary>
public class FlyResiliencePipelineTests : IDisposable
{
    private static readonly FlyOptions DefaultOptions = new()
    {
        ApiToken = "fly_pat_secret_xyz",
        OrgSlug = "personal",
        AppName = "test-app",
        DefaultRegion = "arn",
    };

    private readonly ApplicationDbContext _db = TestDbContextFactory.Create();
    private ServiceProvider? _provider;

    public void Dispose()
    {
        _provider?.Dispose();
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Build a real DI container with <see cref="FlyManagementExtensions.AddFlyManagement"/>
    /// applied, then swap the typed client's primary handler for the supplied scripted
    /// handler. The resilience pipeline still wraps the outside, so all four strategies
    /// (concurrency, circuit breaker, retry, timeout) are exercised end-to-end.
    /// </summary>
    private FlyClient BuildClient(HttpMessageHandler primaryHandler)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(_db);

        services.AddFlyManagement();

        // Override the IFlyOptionsAccessor registered by AddFlyManagement (Scoped).
        // Last registration wins when GetService<T> resolves a single instance.
        services.AddScoped<IFlyOptionsAccessor>(_ => new StubFlyOptionsAccessor(DefaultOptions));

        // Replace the inner-most handler. AddFlyManagement registers FlyClient via
        // AddHttpClient<FlyClient>; configuring the primary handler for that named
        // client splices our scripted handler in below the resilience pipeline.
        services.AddHttpClient<FlyClient>()
            .ConfigurePrimaryHttpMessageHandler(() => primaryHandler);

        _provider = services.BuildServiceProvider();
        var scope = _provider.CreateScope();
        return scope.ServiceProvider.GetRequiredService<FlyClient>();
    }

    /// <summary>
    /// Scripted handler that returns a queued sequence of responses. Throws on overflow
    /// so a test that under-mocks gets an obvious failure rather than a hang.
    /// Captures the count of calls actually made for assertion.
    /// </summary>
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> _responses;
        public int CallCount { get; private set; }
        public List<DateTimeOffset> CallTimestamps { get; } = new();

        public ScriptedHandler(IEnumerable<Func<HttpResponseMessage>> responses)
        {
            _responses = new Queue<Func<HttpResponseMessage>>(responses);
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            CallTimestamps.Add(DateTimeOffset.UtcNow);
            if (_responses.Count == 0)
            {
                throw new InvalidOperationException(
                    $"ScriptedHandler exhausted after {CallCount} calls — test under-mocked.");
            }
            return Task.FromResult(_responses.Dequeue()());
        }
    }

    private static HttpResponseMessage Response(HttpStatusCode status, string body = "{}")
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Response429WithRetryAfter(TimeSpan delta)
    {
        var resp = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        resp.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(delta);
        return resp;
    }

    // ----------------------------------------------------------------------
    // Retry
    // ----------------------------------------------------------------------

    [Fact]
    public async Task Retry_503ThenSuccess_ReturnsSuccessAfterRetries()
    {
        // 503, 503, 503, then 200 — retry budget is 3, so the 4th call (1 initial + 3
        // retries) wins. Body deliberately matches the FlyApp shape so the eventual
        // success path can deserialise.
        const string okBody = """{"id":"app_xyz","name":"test-app","org_slug":"personal","status":"deployed","created_at":"2026-05-08T10:00:00Z"}""";
        var handler = new ScriptedHandler(new Func<HttpResponseMessage>[]
        {
            () => Response(HttpStatusCode.ServiceUnavailable),
            () => Response(HttpStatusCode.ServiceUnavailable),
            () => Response(HttpStatusCode.ServiceUnavailable),
            () => Response(HttpStatusCode.OK, okBody),
        });

        var client = BuildClient(handler);

        var app = await client.GetAppAsync(CancellationToken.None);

        app.Should().NotBeNull();
        app.Name.Should().Be("test-app");
        handler.CallCount.Should().Be(4, "1 initial attempt + 3 retries before the 200 lands");
    }

    [Fact]
    public async Task Retry_429WithRetryAfter_RespectsHeader()
    {
        // 429 with Retry-After: 1s, then 200. We assert the gap between the two HTTP
        // calls is at least ~1s so the DelayGenerator clearly took precedence over the
        // 1s exponential base. Using 1s rather than 2s keeps the test fast — the
        // assertion is "did we wait at all per the header" not "did we wait exactly N".
        const string okBody = """{"id":"app_xyz","name":"test-app","org_slug":"personal","status":"deployed","created_at":"2026-05-08T10:00:00Z"}""";
        var handler = new ScriptedHandler(new Func<HttpResponseMessage>[]
        {
            () => Response429WithRetryAfter(TimeSpan.FromSeconds(1)),
            () => Response(HttpStatusCode.OK, okBody),
        });

        var client = BuildClient(handler);

        await client.GetAppAsync(CancellationToken.None);

        handler.CallCount.Should().Be(2);
        var gap = handler.CallTimestamps[1] - handler.CallTimestamps[0];
        gap.Should().BeGreaterThanOrEqualTo(
            TimeSpan.FromMilliseconds(900), // small slack for clock granularity
            "the Retry-After: 1s header should drive at least ~1s of backoff");
    }

    [Fact]
    public async Task Retry_TransportException_RetriesUntilSuccess()
    {
        // Two HttpRequestExceptions then a clean 200. Verifies the retry strategy
        // catches transport-level failures, not just bad status codes.
        const string okBody = """{"id":"app_xyz","name":"test-app","org_slug":"personal","status":"deployed","created_at":"2026-05-08T10:00:00Z"}""";
        var handler = new ScriptedHandler(new Func<HttpResponseMessage>[]
        {
            () => throw new HttpRequestException("connection reset"),
            () => throw new HttpRequestException("connection reset"),
            () => Response(HttpStatusCode.OK, okBody),
        });

        var client = BuildClient(handler);

        var app = await client.GetAppAsync(CancellationToken.None);

        app.Name.Should().Be("test-app");
        handler.CallCount.Should().Be(3);
    }

    // ----------------------------------------------------------------------
    // Circuit breaker
    // ----------------------------------------------------------------------

    [Fact]
    public async Task CircuitBreaker_RepeatedServerErrors_OpensCircuit_RejectsImmediately()
    {
        // Configured: FailureRatio = 0.5, MinimumThroughput = 5, SamplingDuration = 1min.
        // Each GetAppAsync is one logical operation that retries up to 4 times on 5xx,
        // so a steady stream of 503s blows past the throughput threshold quickly. After
        // the circuit opens, subsequent calls must fail fast with BrokenCircuitException
        // _without_ touching the handler.
        var handler = new ScriptedHandler(Enumerable.Repeat<Func<HttpResponseMessage>>(
            () => Response(HttpStatusCode.ServiceUnavailable),
            count: 1000));

        var client = BuildClient(handler);

        // Drive enough failures through the pipeline to trip the breaker. Each call
        // fans out to 4 handler invocations (1 + 3 retries) and counts as multiple
        // outcomes from the breaker's POV, so 5 logical calls is plenty.
        for (var i = 0; i < 10; i++)
        {
            try { await client.GetAppAsync(CancellationToken.None); }
            catch { /* expected — either FlyApiException or BrokenCircuitException */ }
        }

        // At this point the circuit MUST be open. The next call should fail fast
        // without invoking the handler at all. Snapshot CallCount to prove it.
        var preCount = handler.CallCount;

        Func<Task> nextCall = () => client.GetAppAsync(CancellationToken.None);

        await nextCall.Should().ThrowAsync<BrokenCircuitException>(
            "the circuit is open after a sustained run of 5xx outcomes");

        handler.CallCount.Should().Be(
            preCount,
            "fail-fast must short-circuit before reaching the inner HTTP handler");
    }

    // ----------------------------------------------------------------------
    // Concurrency limiter
    // ----------------------------------------------------------------------

    [Fact]
    public async Task ConcurrencyLimiter_ExceedingPermitAndQueue_RejectsExtraCalls()
    {
        // Configured: permitLimit = 20, queueLimit = 10. So 30 concurrent calls fit;
        // the 31st (and any beyond) must be rejected with RateLimiterRejectedException.
        //
        // We bypass FlyClient.GetAppAsync (which serialises through a shared DbContext
        // and would prevent true concurrency) and exercise the resilience-wrapped
        // HttpClient directly — that's the only piece this test cares about anyway.
        // The inner handler blocks on a manual gate so we can stack ~40 in-flight
        // requests and watch the limiter reject the surplus.
        var gate = new TaskCompletionSource();
        var handler = new HoldingHandler(gate.Task);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(_db);
        services.AddFlyManagement();
        services.AddScoped<IFlyOptionsAccessor>(_ => new StubFlyOptionsAccessor(DefaultOptions));
        services.AddHttpClient<FlyClient>()
            .ConfigurePrimaryHttpMessageHandler(() => handler);
        _provider = services.BuildServiceProvider();

        var factory = _provider.GetRequiredService<IHttpClientFactory>();
        var http = factory.CreateClient(nameof(FlyClient));

        var rejections = 0;
        var tasks = new List<Task>();
        for (var i = 0; i < 40; i++)
        {
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, "apps/test-app");
                    using var _ = await http.SendAsync(req, CancellationToken.None);
                }
                catch (RateLimiterRejectedException)
                {
                    Interlocked.Increment(ref rejections);
                }
                catch
                {
                    // Anything else (HttpRequestException once the gate is released,
                    // BrokenCircuitException, etc.) isn't what this test gates on.
                }
            }));
        }

        // Let the limiter sort through the burst before sampling. 500ms is generous —
        // rejection is a synchronous outcome from the limiter's POV.
        await Task.Delay(500);

        rejections.Should().BeGreaterThan(
            0,
            "with permitLimit=20 and queueLimit=10, calls past the 30th must be rejected");

        // Release every parked call so the test can finish and the handler doesn't
        // leak background tasks.
        gate.SetResult();
        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Handler that blocks every request on a gate task until released, returning a
    /// generic 200 once unblocked. Used by the concurrency-limiter test to hold many
    /// requests in flight at once so we can observe the rejection threshold.
    /// </summary>
    private sealed class HoldingHandler : HttpMessageHandler
    {
        private readonly Task _gate;
        public HoldingHandler(Task gate) => _gate = gate;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"id":"app_xyz","name":"test-app","org_slug":"personal","status":"deployed","created_at":"2026-05-08T10:00:00Z"}""",
                    Encoding.UTF8,
                    "application/json"),
            };
        }
    }
}
