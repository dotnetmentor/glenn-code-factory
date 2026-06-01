using Hangfire;
using Hangfire.Common;
using Hangfire.PostgreSql;
using Hangfire.States;
using Source.Features.RuntimeLifecycle.Jobs;
using Source.Infrastructure.Database;
using Source.Infrastructure.ErrorHandling;
using Source.Infrastructure.Services;
using Source.Infrastructure.Security;

namespace Source.Infrastructure.Extensions;

public static class HangfireExtensions
{
    /// <summary>
    /// Registers a no-op <see cref="IBackgroundJobClient"/>. The swagger-generation
    /// pass starts the host with <c>SWAGGER_GENERATION_MODE=true</c> and skips Hangfire
    /// entirely (no Postgres connection in CI), but MediatR still auto-discovers handlers
    /// like <c>ScheduleRespawnHandler</c> which depend on <see cref="IBackgroundJobClient"/>.
    /// Without a registration, container validation throws on startup. The stub never runs
    /// — swagger gen just builds the OpenAPI document and exits.
    /// </summary>
    public static IServiceCollection AddNoOpBackgroundJobClient(this IServiceCollection services)
    {
        services.AddSingleton<IBackgroundJobClient, NoOpBackgroundJobClient>();
        return services;
    }

    public static IServiceCollection AddHangfireServices(this IServiceCollection services, IConfiguration configuration)
    {
        var enableHangfire = configuration.GetValue<bool>("Features:EnableHangfire", true);

        // The error-capture job filter is useful even when Hangfire itself is disabled —
        // tests and DI consumers expect it to resolve. Register it unconditionally in DI,
        // and register a lightweight hosted service that installs it into GlobalJobFilters
        // on startup regardless of whether the Hangfire server is running. Without this,
        // any job that does get enqueued (e.g. by tests that enable Hangfire later) would
        // bypass the error pipeline.
        services.AddSingleton<ErrorCaptureJobFilter>();
        services.AddHostedService<ErrorCaptureJobFilterRegistrar>();

        if (!enableHangfire)
        {
            return services;
        }

        // Resolve from DATABASE_URL (set by the deployment host) or config, normalizing
        // managed-host URI form (postgres://...) into Npgsql keyword form. See
        // PostgresConnectionString for the why.
        var connectionString = PostgresConnectionString.Resolve(configuration);

        services.AddHangfire(config =>
        {
            config.UsePostgreSqlStorage(options =>
                options.UseNpgsqlConnection(connectionString));
        });

        services.AddHangfireServer(options =>
        {
            options.WorkerCount = Math.Min(Environment.ProcessorCount * 2, 8);
            options.Queues = new[] { "default", "critical", "background" };
        });

        // Recurring-job classes resolved from DI by Hangfire's JobActivator.
        services.AddScoped<ErrorLogRetentionJob>();
        services.AddScoped<RuntimeProvisionerJob>();
        services.AddScoped<RuntimeReconcilerJob>();
        services.AddScoped<RuntimeJanitorJob>();
        services.AddScoped<HeartbeatWatcherJob>();
        services.AddScoped<FlyDriftPollerJob>();
        services.AddScoped<IdlerJob>();
        services.AddScoped<RespawnRuntimeJob>();

        services.AddHostedService<HangfireStartupService>();

        return services;
    }

    public static IApplicationBuilder UseHangfire(this IApplicationBuilder app, IWebHostEnvironment environment)
    {
        app.UseHangfireDashboard("/api/hangfire", new DashboardOptions
        {
            Authorization = new[] { new HangfireAuthorizationFilter() }
        });

        return app;
    }
}

/// <summary>
/// Stand-in <see cref="IBackgroundJobClient"/> for the swagger-generation host, which
/// runs without Hangfire wired up. Every method is a no-op — the swagger pass never
/// actually enqueues anything; the registration only exists so DI validation passes
/// when MediatR auto-discovers handlers (e.g. <c>ScheduleRespawnHandler</c>) that take
/// an <see cref="IBackgroundJobClient"/> in their constructor.
/// </summary>
internal sealed class NoOpBackgroundJobClient : IBackgroundJobClient
{
    public string Create(Job job, IState state) => string.Empty;
    public bool ChangeState(string jobId, IState state, string expectedState) => false;
}