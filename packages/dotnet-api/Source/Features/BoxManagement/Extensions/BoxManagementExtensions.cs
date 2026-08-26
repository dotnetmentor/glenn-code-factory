using System.Net;
using Microsoft.Extensions.Http.Resilience;
using Polly;
using Source.Features.BoxManagement.Configuration;

namespace Source.Features.BoxManagement.Extensions;

/// <summary>
/// Wires up the BoxManagement feature: the typed <see cref="BoxClient"/> and the
/// <see cref="IBoxOptionsAccessor"/> reading from
/// <see cref="Source.Features.SystemSettings.Services.ISystemSettingsService"/>.
/// Mirrors <see cref="Source.Features.GitHub.Extensions.GithubFeatureExtensions"/>.
///
/// <para><b>Resilience.</b> The HttpClient is wrapped in a
/// <c>Microsoft.Extensions.Http.Resilience</c> pipeline (Polly v8) with four
/// strategies stacked outer-to-inner:
/// <list type="number">
///   <item>Concurrency limiter — caps in-flight requests at 20 with a queue of 10
///         so a thundering herd of runtime spin-ups can't exhaust sockets or
///         amplify a Box outage.</item>
///   <item>Circuit breaker — opens after a 50% failure ratio over a 1-minute window
///         (min 5 calls); fail-fast for 30s, then half-open.</item>
///   <item>Retry — 3 attempts, exponential backoff with jitter, base 1s. Retries on
///         5xx, 429 and transport errors, honouring <c>Retry-After</c> on 429.
///         Deliberately does NOT retry 409 <c>box_starting</c> here — that can take
///         longer than this pipeline should block; callers own that wait loop.</item>
///   <item>Per-attempt timeout — 30s, inside the retry so each attempt gets a fresh
///         budget. <see cref="HttpClient.Timeout"/> is 60s to cover the worst case.</item>
/// </list>
/// </para>
///
/// <para><b>No BaseAddress.</b> Unlike the old Fly client, the API base URL comes
/// from SystemSettings per request (<see cref="BoxClient"/> builds absolute URIs) so
/// an operator can repoint the API host without a restart.</para>
/// </summary>
public static class BoxManagementExtensions
{
    public static IServiceCollection AddBoxManagement(this IServiceCollection services)
    {
        // Scoped — same lifetime as ISystemSettingsService. Each request gets a fresh
        // accessor that reads through the singleton SystemSettingsCache.
        services.AddScoped<IBoxOptionsAccessor, BoxOptionsAccessor>();

        services.AddHttpClient<BoxClient>(client =>
            {
                client.Timeout = TimeSpan.FromSeconds(60);
            })
            .AddResilienceHandler("box-resilience", builder =>
            {
                builder.AddConcurrencyLimiter(permitLimit: 20, queueLimit: 10);

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
                        // falls back to exponential backoff + jitter.
                        if (args.Outcome.Result?.Headers.RetryAfter?.Delta is { } delta)
                        {
                            return new ValueTask<TimeSpan?>(delta);
                        }
                        return new ValueTask<TimeSpan?>((TimeSpan?)null);
                    },
                });

                builder.AddTimeout(TimeSpan.FromSeconds(30));
            });

        return services;
    }
}
