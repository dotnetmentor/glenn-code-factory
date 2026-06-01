using System.Net;
using Microsoft.Extensions.Http.Resilience;
using Polly;
using Polly.Retry;
using Source.Features.FlyManagement.Configuration;

namespace Source.Features.FlyManagement.Extensions;

/// <summary>
/// Wires up the FlyManagement feature: the typed <see cref="FlyClient"/> with its
/// <see cref="HttpClient"/> bound to <c>https://api.machines.dev/v1/</c>, and the
/// <see cref="IFlyOptionsAccessor"/> reading from <see cref="Source.Features.SystemSettings.Services.ISystemSettingsService"/>.
/// Mirrors <see cref="Source.Features.GitHub.Extensions.GithubFeatureExtensions"/>.
///
/// <para><b>Resilience (Card 7).</b> The HttpClient is wrapped in a
/// <c>Microsoft.Extensions.Http.Resilience</c> pipeline (Polly v8 under the hood) with
/// four strategies stacked outer-to-inner:
/// <list type="number">
///   <item>Concurrency limiter — caps in-flight requests at 20 with a queue of 10
///         so a thundering herd of runtime spin-ups can't exhaust HttpClient sockets
///         or amplify a Fly outage by piling thousands of sockets into TIME_WAIT.</item>
///   <item>Circuit breaker — opens after a 50% failure ratio over a 1-minute window
///         (min 5 calls). When open, callers fail fast with
///         <c>BrokenCircuitException</c> for 30s, then half-open. Avoids hammering
///         a Fly API that's already known to be down.</item>
///   <item>Retry — 3 attempts, exponential backoff with jitter, base 1s. Retries on
///         5xx, 429, <see cref="HttpRequestException"/>, and timeout. Honours
///         <c>Retry-After</c> on 429 via <see cref="RetryStrategyOptions{T}.DelayGenerator"/>
///         (the resilience package only auto-honours it on the inner timeout strategy).</item>
///   <item>Per-attempt timeout — 30s, sits inside the retry so each attempt gets a
///         fresh 30s budget. The outer <see cref="HttpClient.Timeout"/> is bumped to
///         60s to comfortably cover the worst case (3 retries × 30s plus backoff,
///         though jitter and circuit breaker make that ceiling near-unreachable).</item>
/// </list>
/// </para>
/// </summary>
public static class FlyManagementExtensions
{
    public static IServiceCollection AddFlyManagement(this IServiceCollection services)
    {
        // Scoped — same lifetime as ISystemSettingsService. Each request gets a fresh
        // accessor that reads through the singleton SystemSettingsCache.
        services.AddScoped<IFlyOptionsAccessor, FlyOptionsAccessor>();

        // Typed HttpClient. BaseAddress points at the Machines API root; the timeout is
        // bumped to 60s because the resilience pipeline's per-attempt timeout is 30s and
        // we want HttpClient.Timeout to comfortably exceed that so retries can land.
        // Per-request auth and User-Agent headers are stamped inside FlyClient.SendAsync
        // so a token rotation in SystemSettings takes effect without a process restart.
        services.AddHttpClient<FlyClient>(client =>
            {
                client.BaseAddress = new Uri("https://api.machines.dev/v1/");
                client.Timeout = TimeSpan.FromSeconds(60);
            })
            .AddResilienceHandler("fly-resilience", builder =>
            {
                // 1. Concurrency limiter (outermost) — caps simultaneous in-flight
                //    requests at 20 with a 10-deep queue. Beyond that, callers see
                //    RateLimiterRejectedException immediately.
                builder.AddConcurrencyLimiter(permitLimit: 20, queueLimit: 10);

                // 2. Circuit breaker — opens on persistent server-side failures.
                //    50% failure ratio over 1 minute (min 5 calls) opens the circuit
                //    for 30 seconds, after which it half-opens and probes.
                builder.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
                {
                    FailureRatio = 0.5,
                    MinimumThroughput = 5,
                    SamplingDuration = TimeSpan.FromMinutes(1),
                    BreakDuration = TimeSpan.FromSeconds(30),
                    ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                        .Handle<HttpRequestException>()
                        .HandleResult(r => (int)r.StatusCode >= 500),
                });

                // 3. Retry — 3 attempts with exponential backoff + jitter on transient
                //    failures. The DelayGenerator honours the Retry-After header on 429
                //    so we back off for as long as Fly tells us to (the package wraps
                //    Polly.Core which doesn't auto-honour Retry-After at this layer).
                builder.AddRetry(new HttpRetryStrategyOptions
                {
                    MaxRetryAttempts = 3,
                    BackoffType = DelayBackoffType.Exponential,
                    UseJitter = true,
                    Delay = TimeSpan.FromSeconds(1),
                    ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                        .Handle<HttpRequestException>()
                        .Handle<Polly.Timeout.TimeoutRejectedException>()
                        .HandleResult(r =>
                            (int)r.StatusCode >= 500 ||
                            r.StatusCode == HttpStatusCode.TooManyRequests),
                    DelayGenerator = static args =>
                    {
                        // 429 with a Retry-After delta wins outright; anything else
                        // returns null and falls back to the configured exponential
                        // backoff + jitter.
                        if (args.Outcome.Result?.Headers.RetryAfter?.Delta is { } delta)
                        {
                            return new ValueTask<TimeSpan?>(delta);
                        }
                        return new ValueTask<TimeSpan?>((TimeSpan?)null);
                    },
                });

                // 4. Per-attempt timeout (innermost) — each retry gets a fresh 30s
                //    budget. Beyond this, the attempt is aborted and the retry strategy
                //    decides whether to spin again.
                builder.AddTimeout(TimeSpan.FromSeconds(30));
            });

        return services;
    }
}
