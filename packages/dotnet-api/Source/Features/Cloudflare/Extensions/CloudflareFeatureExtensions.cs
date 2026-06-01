using Source.Features.Cloudflare.Configuration;
using Source.Features.Cloudflare.Services;

namespace Source.Features.Cloudflare.Extensions;

/// <summary>
/// Wires up the Cloudflare feature: the typed
/// <see cref="CloudflareApiClient"/> bound to
/// <c>https://api.cloudflare.com/client/v4/</c>, and the
/// <see cref="ICloudflareOptionsAccessor"/> that reads credentials from
/// <see cref="Source.Features.SystemSettings.Services.ISystemSettingsService"/>.
/// Mirrors <see cref="Source.Features.FlyManagement.Extensions.FlyManagementExtensions"/>.
///
/// <para>No resilience pipeline at this phase. The Cloudflare API is called
/// during admin-triggered batch creates only (not in a runtime hot path), so
/// the existing per-request <c>HttpClient.Timeout</c> + the handler's "log and
/// continue" partial-failure semantics are enough. A Polly pipeline can be
/// layered on later if 429-handling becomes a concern.</para>
/// </summary>
public static class CloudflareFeatureExtensions
{
    public static IServiceCollection AddCloudflareFeature(this IServiceCollection services)
    {
        // Scoped — same lifetime as ISystemSettingsService. Each request gets
        // a fresh accessor reading through the singleton SystemSettingsCache.
        services.AddScoped<ICloudflareOptionsAccessor, CloudflareOptionsAccessor>();

        // Typed HttpClient. BaseAddress points at the Cloudflare API v4 root.
        // 30s timeout is generous — the four endpoints we call are all
        // sub-second in practice.
        services.AddHttpClient<CloudflareApiClient>(client =>
        {
            client.BaseAddress = new Uri("https://api.cloudflare.com/client/v4/");
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        return services;
    }
}
