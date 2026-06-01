using Source.Features.RuntimeImages.Services;

namespace Source.Features.RuntimeImages.Extensions;

/// <summary>
/// Wires up the RuntimeImages feature: the named <c>"FlyRegistry"</c>
/// <see cref="HttpClient"/> backing <see cref="IFlyRegistryClient"/>, plus the client
/// itself. Reads the Fly Personal Access Token from
/// <see cref="Source.Features.FlyManagement.Configuration.IFlyOptionsAccessor"/> — already
/// registered by <c>AddFlyManagement()</c>, so call this after it.
///
/// <para>The previous <c>RuntimeImagesOptions</c> / <c>IRuntimeImagesOptionsAccessor</c>
/// pair was deleted along with the <c>X-Publisher-Token</c> CI auth path: the only
/// setting it carried was the publisher token, which has no remaining consumers now that
/// registration is human-driven from the super-admin UI.</para>
/// </summary>
public static class RuntimeImagesExtensions
{
    public static IServiceCollection AddRuntimeImagesFeature(this IServiceCollection services)
    {
        // Named HttpClient bound to the Fly registry root. Auth headers + per-request
        // accept-types are stamped inside FlyRegistryClient.SendAsync so a token rotation
        // in SystemSettings takes effect without a process restart. No resilience pipeline
        // here on purpose — these are read-only, on-demand calls driven by an admin click,
        // not the high-throughput Machines API path that needs Polly retries.
        services.AddHttpClient(FlyRegistryClient.HttpClientName, client =>
        {
            client.BaseAddress = new Uri("https://registry.fly.io");
            client.Timeout = TimeSpan.FromSeconds(15);
        });

        services.AddScoped<IFlyRegistryClient, FlyRegistryClient>();
        return services;
    }
}
