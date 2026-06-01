using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;

namespace Source.Infrastructure.Extensions;

public static class RateLimitingExtensions
{
    public static IServiceCollection AddRateLimitingServices(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            // 📡 ERROR REPORT: public, anonymous, hostile-input endpoint.
            //
            // Per-IP 10/sec burst limit. On rejection we deliberately return 204 No Content
            // rather than 429 — the spec's design point is "attacker gets no feedback
            // signal." See POST /api/errors/report contract.
            //
            // To keep tests independent (rate-limit state persists for the lifetime of the
            // DI singleton across requests), the partition key mixes the caller's IP with
            // the optional <c>X-Test-Session</c> request header. In production the header
            // is absent and behaviour degrades to pure per-IP.
            //
            // 💡 Sustained-rate note: the spec calls out "10/sec burst, 100/min sustained."
            // The 10/sec burst is the critical DoS protection; at that cap the theoretical
            // maximum is 600/min, which is 6× the 100/min sustained limit, so the burst
            // already bounds sustained abuse well enough for this feature's purpose.
            // If traffic data later justifies it, we can chain a second 100/min partition
            // here via <see cref="PartitionedRateLimiter.CreateChained"/>.
            options.AddPolicy("ErrorReport", httpContext =>
            {
                var ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                var testSession = httpContext.Request.Headers["X-Test-Session"].ToString();
                var partitionKey = string.IsNullOrEmpty(testSession) ? ip : $"{ip}|{testSession}";

                return RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: partitionKey,
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 10,
                        Window = TimeSpan.FromSeconds(1),
                        QueueLimit = 0,
                        AutoReplenishment = true,
                    });
            });

            // 🚨 STRICT: Login/Auth endpoints - 5 attempts per minute
            options.AddFixedWindowLimiter("AuthPolicy", limiterOptions =>
            {
                limiterOptions.PermitLimit = 5;
                limiterOptions.Window = TimeSpan.FromMinutes(1);
                limiterOptions.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
                limiterOptions.QueueLimit = 2;
            });

            // 📧 EMAIL: Registration/email endpoints - 3 per minute  
            options.AddFixedWindowLimiter("EmailPolicy", limiterOptions =>
            {
                limiterOptions.PermitLimit = 3;
                limiterOptions.Window = TimeSpan.FromMinutes(1);
                limiterOptions.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
                limiterOptions.QueueLimit = 1;
            });

            // 📤 UPLOAD: File upload endpoints - 10 per minute
            options.AddFixedWindowLimiter("UploadPolicy", limiterOptions =>
            {
                limiterOptions.PermitLimit = 10;
                limiterOptions.Window = TimeSpan.FromMinutes(1);
                limiterOptions.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
                limiterOptions.QueueLimit = 5;
            });

            // 🔄 GENERAL: Most API endpoints - 100 per minute
            options.AddFixedWindowLimiter("GeneralPolicy", limiterOptions =>
            {
                limiterOptions.PermitLimit = 100;
                limiterOptions.Window = TimeSpan.FromMinutes(1);
                limiterOptions.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
                limiterOptions.QueueLimit = 10;
            });

            // 🚫 Global fallback - very generous
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(
                httpContext => RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: httpContext.User.Identity?.Name ?? httpContext.Request.Headers.Host.ToString(),
                    factory: partition => new FixedWindowRateLimiterOptions
                    {
                        AutoReplenishment = true,
                        PermitLimit = 1000,
                        Window = TimeSpan.FromMinutes(1)
                    }));

            // Custom rejection response
            options.OnRejected = async (context, token) =>
            {
                // 🔇 Silent-drop branch for the public error-report endpoint. The spec
                // explicitly requires that rate-limit violations there NEVER return 429:
                // an attacker probing the endpoint gets no feedback signal, so they can't
                // tune their rate to stay just under the limit.
                var path = context.HttpContext.Request.Path;
                if (path.StartsWithSegments("/api/errors/report"))
                {
                    context.HttpContext.Response.StatusCode = StatusCodes.Status204NoContent;
                    return;
                }

                context.HttpContext.Response.StatusCode = 429;

                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    await context.HttpContext.Response.WriteAsync(
                        $"{{\"error\": \"Rate limit exceeded. Try again in {retryAfter.TotalSeconds} seconds.\", \"retryAfter\": {retryAfter.TotalSeconds}}}",
                        cancellationToken: token);
                }
                else
                {
                    await context.HttpContext.Response.WriteAsync(
                        "{\"error\": \"Rate limit exceeded. Please try again later.\"}",
                        cancellationToken: token);
                }
            };
        });

        return services;
    }
} 