using Hangfire;
using Hangfire.Common;
using Hangfire.PostgreSql;
using Hangfire.States;
using Source.Features.RuntimeLifecycle.Jobs;
using Source.Infrastructure.Database;
using Source.Infrastructure.ErrorHandling;
using Source.Infrastructure.Jobs;
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
            config.UsePostgreSqlStorage(
                options => options.UseNpgsqlConnection(connectionString),
                new PostgreSqlStorageOptions
                {
                    // Defaults are 15s polling with long polling OFF, which puts a
                    // 0-15s floor under every ad-hoc Enqueue — including the
                    // "provision this runtime NOW" kick a new branch fires, where
                    // that delay is the user watching a spinner. Long polling uses
                    // Postgres LISTEN/NOTIFY, so a worker wakes on the notification
                    // rather than on a timer: pickup drops to milliseconds AND the
                    // idle query load goes down.
                    EnableLongPolling = true,

                    // Belt and braces behind LISTEN/NOTIFY. Notifications don't
                    // survive every deployment topology (a connection pooler in
                    // transaction mode swallows them), and this is the fallback that
                    // decides how bad that gets: 1s instead of 15s.
                    QueuePollInterval = TimeSpan.FromSeconds(1),
                });
        });

        services.AddHangfireServer(options =>
        {
            // ProcessorCount * 2 reads like a sensible default and is wrong here:
            // the production host has 1 vCPU, so it yielded TWO workers — 120
            // worker-seconds per minute — while the minutely recurring jobs alone
            // demanded ~150. The queue grew without bound (231,777 jobs, 20-day-old
            // head) until the long-running sweeps moved out to
            // ContinuousSweepService. These jobs are I/O-bound — Postgres round
            // trips and HTTP calls to Box — so worker count should track expected
            // concurrent I/O, not cores. The floor is what matters; the ceiling
            // keeps a big host from opening an unreasonable number of connections.
            options.WorkerCount = configuration.GetValue<int?>("Hangfire:WorkerCount")
                ?? Math.Clamp(Environment.ProcessorCount * 2, 8, 20);
            options.Queues = new[] { "default", "critical", "background" };
        });

        // Recurring-job classes resolved from DI by Hangfire's JobActivator.
        services.AddScoped<ErrorLogRetentionJob>();
        services.AddScoped<RuntimeProvisionerJob>();
        services.AddScoped<RuntimeReconcilerJob>();
        services.AddScoped<RuntimeJanitorJob>();
        services.AddScoped<HeartbeatWatcherJob>();
        services.AddScoped<BoxDriftPollerJob>();
        services.AddScoped<BoxTtlExtenderJob>();
        services.AddScoped<IdlerJob>();
        services.AddScoped<RespawnRuntimeJob>();

        services.AddHostedService<HangfireStartupService>();

        // Sweeps that need a sub-minute cadence run in-process on their own timers
        // instead of occupying Hangfire workers for ~50 of every 60 seconds. See
        // ContinuousSweepService for the throughput incident that forced this.
        services.AddHostedService<HeartbeatWatcherSweepService>();
        services.AddHostedService<IdlerSweepService>();
        services.AddHostedService<RuntimeTokenUsageFlushSweepService>();

        // Standing guardrail: drops queue entries abandoned long enough to be
        // meaningless, so a throughput dip can never leave weeks of backlog in
        // front of new work.
        services.AddHostedService<HangfireQueueMaintenanceService>();

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