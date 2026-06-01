using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Source.Features.GitHub.Configuration;
using Source.Features.SystemSettings;
using Source.Features.SystemSettings.Services;
using Source.Infrastructure;

namespace Api.Tests.Infrastructure;

/// <summary>
/// Base class for end-to-end integration tests that spin up the full ASP.NET Core pipeline
/// via <see cref="WebApplicationFactory{TEntryPoint}"/>.
///
/// Each test instance gets:
///   - A fresh EF Core in-memory database (unique name per factory build) so tests are isolated.
///   - Hangfire disabled via config (<c>Features:EnableHangfire = false</c>) — no real Postgres,
///     no BackgroundJobServer running during HTTP tests.
///   - <see cref="ASPNETCORE_ENVIRONMENT"/> forced to <c>Testing</c>.
///
/// Use <see cref="WithService{T}"/> to swap any registered service for a test double (e.g.
/// replace <see cref="Source.Shared.IClock"/> with a <see cref="FakeClock"/>). Overrides must
/// be applied BEFORE the first access to <see cref="Client"/> or <see cref="Services"/>.
/// </summary>
public abstract class IntegrationTestBase : IDisposable
{
    private readonly string _dbName = Guid.NewGuid().ToString();
    private readonly List<Action<IServiceCollection>> _serviceOverrides = new();
    private WebApplicationFactory<Program>? _factory;
    private HttpClient? _client;

    /// <summary>
    /// The <see cref="HttpClient"/> configured against the in-memory test server.
    /// Accessing this triggers factory creation; register service overrides before first access.
    /// </summary>
    protected HttpClient Client => _client ??= BuildFactory().CreateClient();

    /// <summary>
    /// The root <see cref="IServiceProvider"/> of the test host. Use for resolving DI services
    /// (e.g. <see cref="ApplicationDbContext"/> via <see cref="CreateScope"/>).
    /// </summary>
    protected IServiceProvider Services => BuildFactory().Services;

    /// <summary>
    /// The underlying <see cref="WebApplicationFactory{TEntryPoint}"/>. Prefer <see cref="Client"/>
    /// and <see cref="Services"/> for most cases.
    /// </summary>
    protected WebApplicationFactory<Program> Factory => BuildFactory();

    /// <summary>
    /// Register an override to replace a service registration in the test host. Must be called
    /// before the first access to <see cref="Client"/> or <see cref="Services"/>.
    /// </summary>
    protected void WithService<TService>(TService instance) where TService : class
    {
        if (_factory is not null)
        {
            throw new InvalidOperationException(
                $"Cannot register service override for {typeof(TService).Name} after the test host has been built. " +
                "Call WithService<T>() before accessing Client or Services.");
        }

        _serviceOverrides.Add(services =>
        {
            services.RemoveAll<TService>();
            services.AddSingleton(instance);
        });
    }

    /// <summary>
    /// Register an arbitrary modification to the test host's <see cref="IServiceCollection"/>.
    /// Useful when a single override-call needs to replace multiple registrations (for example,
    /// swapping in a mock and post-configuring an options object). Must be called BEFORE
    /// the first access to <see cref="Client"/> or <see cref="Services"/>.
    /// </summary>
    protected void WithServiceFactory(Action<IServiceCollection> apply)
    {
        if (_factory is not null)
        {
            throw new InvalidOperationException(
                "Cannot register service override after the test host has been built. " +
                "Call WithServiceFactory(...) before accessing Client or Services.");
        }

        _serviceOverrides.Add(apply);
    }

    /// <summary>
    /// Creates a DI scope. Useful for resolving scoped services like <see cref="ApplicationDbContext"/>.
    /// </summary>
    protected IServiceScope CreateScope() => Services.CreateScope();

    /// <summary>
    /// Seeds the <see cref="Source.Infrastructure.AuthorizationModels.ApplicationRole"/> rows
    /// into the test DB. Tests that exercise role-gated endpoints (signup, RequireWorkspaceRole,
    /// etc.) should call this once before driving the API. Production seeds at startup; we skip
    /// that in the Testing environment so each test gets an isolated DB and seeds on demand.
    /// </summary>
    protected async Task SeedRolesAsync()
    {
        using var scope = CreateScope();
        var seeder = scope.ServiceProvider
            .GetRequiredService<Source.Infrastructure.AuthorizationServices.RoleSeederService>();
        await seeder.SeedRolesAsync();
    }

    /// <summary>
    /// Persist the supplied <see cref="GithubOptions"/> values into the test SystemSettings store
    /// by writing every <c>GitHub:*</c> row through <see cref="ISystemSettingsService.SetAsync"/>.
    /// Mirrors what the production seeder does at startup (which we skip in the Testing
    /// environment), and replaces the old <c>services.PostConfigure&lt;GithubOptions&gt;(...)</c>
    /// approach now that GitHub options come from the DB instead of <c>IConfiguration</c>.
    ///
    /// <para>Walks the <see cref="SystemSettingsCatalog"/> so <c>IsSecret</c> stays authoritative
    /// from the catalog — callers don't have to know which fields are secret.</para>
    ///
    /// <para>Each property's catalog metadata determines encryption; missing/empty properties on
    /// <paramref name="options"/> just produce empty rows.</para>
    /// </summary>
    protected async Task SeedGithubSystemSettingsAsync(GithubOptions options)
    {
        using var scope = CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ISystemSettingsService>();

        // Pull values off the POCO by reflection so we can pair them with their catalog defs.
        var props = typeof(GithubOptions).GetProperties()
            .Where(p => p.CanRead && p.PropertyType == typeof(string))
            .ToDictionary(p => p.Name, StringComparer.Ordinal);

        foreach (var def in SystemSettingsCatalog.AllSettings.Where(d => d.Category == GithubOptions.SectionName))
        {
            // Key is "GitHub:AppId" → property name "AppId".
            var propName = def.Key.Split(':', 2)[1];
            if (!props.TryGetValue(propName, out var prop)) continue;

            var value = (string?)prop.GetValue(options);
            await service.SetAsync(def.Key, value, def.IsSecret);
        }
    }

    private WebApplicationFactory<Program> BuildFactory()
    {
        if (_factory is not null)
        {
            return _factory;
        }

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");

                // Disable Hangfire before any services load — production code checks this flag.
                builder.ConfigureAppConfiguration((_, config) =>
                {
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Features:EnableHangfire"] = "false",
                        // Point the connection string at something harmless; InMemory DB ignores it,
                        // but DatabaseExtensions currently builds an NpgsqlDataSource from config.
                        // We'll replace the DbContext entirely below.
                        ["ConnectionStrings:DefaultConnection"] = "Host=localhost;Database=test;Username=test;Password=test",
                        // Stable 32-byte key (base64) for SystemSettingsCipher in the Testing
                        // environment. Production resolves this from real config; the dev fallback
                        // in SystemSettingsExtensions only kicks in for Development, so without this
                        // entry the cipher constructor throws and any code path that resolves
                        // ISystemSettingsService (now including IGithubOptionsAccessor) returns 500.
                        ["SystemSettings:EncryptionKey"] = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA="
                    });
                });

                builder.ConfigureTestServices(services =>
                {
                    // Force a stable test encryption key for SystemSettingsCipher. The minimal
                    // hosting model snapshots `builder.Configuration` at AddSystemSettingsFeature
                    // call time, so InMemoryCollection added via ConfigureAppConfiguration is too
                    // late to affect that read. PostConfigure is the correct hook — it runs at
                    // IOptions resolution time, after all Configure callbacks.
                    services.PostConfigure<SystemSettingsCipherOptions>(opts =>
                    {
                        opts.EncryptionKey = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";
                    });

                    // Replace the production DbContext registration with EF Core InMemory,
                    // scoped to this factory instance so each test gets an isolated store.
                    // Use a dedicated EF internal service provider to avoid the
                    // "multiple database providers registered" error when both Npgsql and
                    // InMemory end up in the same container.
                    services.RemoveAll<DbContextOptions<ApplicationDbContext>>();
                    services.RemoveAll<DbContextOptions>();
                    services.RemoveAll<ApplicationDbContext>();

                    var efProvider = new ServiceCollection()
                        .AddEntityFrameworkInMemoryDatabase()
                        .BuildServiceProvider();

                    services.AddDbContext<ApplicationDbContext>(options =>
                    {
                        options.UseInMemoryDatabase(_dbName);
                        options.UseInternalServiceProvider(efProvider);
                    });

                    // Apply any per-test service overrides registered via WithService<T>().
                    foreach (var apply in _serviceOverrides)
                    {
                        apply(services);
                    }
                });
            });

        return _factory;
    }

    public void Dispose()
    {
        _client?.Dispose();
        _factory?.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Small helpers used by <see cref="IntegrationTestBase"/> to prune existing registrations.
/// </summary>
internal static class ServiceCollectionExtensions
{
    public static void RemoveAll<T>(this IServiceCollection services)
    {
        for (var i = services.Count - 1; i >= 0; i--)
        {
            if (services[i].ServiceType == typeof(T))
            {
                services.RemoveAt(i);
            }
        }
    }
}
