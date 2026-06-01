using Source.Features.GitHub.Configuration;
using Source.Features.GitHub.Services;

namespace Source.Features.GitHub.Extensions;

/// <summary>
/// Wires up the GitHub integration feature: the named HTTP client, the options accessor
/// (DB-backed via <see cref="Source.Features.SystemSettings.Services.ISystemSettingsService"/>),
/// and the core services. Mirrors <c>WorkspacesFeatureExtensions</c>.
///
/// <para>Note: <see cref="GithubOptions"/> is no longer bound from <c>IConfiguration</c>.
/// As of SS.2 the values live in the SystemSettings DB store and are accessed via
/// <see cref="IGithubOptionsAccessor"/>. The <c>GitHub:</c> block in
/// <c>appsettings.json</c> is now seeded into the DB on first boot only.</para>
/// </summary>
public static class GithubFeatureExtensions
{
    public static IServiceCollection AddGithubFeature(this IServiceCollection services, IConfiguration configuration)
    {
        // Memory cache backs the installation-token service. Idempotent — safe to call here.
        services.AddMemoryCache();

        // Named HttpClient for all GitHub API calls. Default base address is the REST API root;
        // the OAuth code-exchange call overrides this with an absolute URL because that endpoint
        // lives on github.com, not api.github.com.
        services.AddHttpClient(GithubApiClient.HttpClientName, client =>
        {
            client.BaseAddress = new Uri("https://api.github.com/");
            client.DefaultRequestHeaders.UserAgent.ParseAdd("glenn-platform");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        });

        // Scoped accessor — matches the lifetime of ISystemSettingsService. Each request gets
        // a fresh accessor that reads through the singleton SystemSettingsCache.
        services.AddScoped<IGithubOptionsAccessor, GithubOptionsAccessor>();

        services.AddScoped<IGithubAppTokenService, GithubAppTokenService>();
        services.AddScoped<IGithubApiClient, GithubApiClient>();
        services.AddScoped<IGithubUserTokenService, GithubUserTokenService>();
        services.AddScoped<IGithubWebhookValidator, GithubWebhookValidator>();
        services.AddScoped<IGithubInstallStateService, GithubInstallStateService>();
        services.AddScoped<IGithubRepositorySyncService, GithubRepositorySyncService>();

        return services;
    }
}
