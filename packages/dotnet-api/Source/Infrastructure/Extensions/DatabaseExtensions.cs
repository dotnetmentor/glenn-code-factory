using Microsoft.EntityFrameworkCore;
using Npgsql;
using Source.Infrastructure;
using Source.Infrastructure.Database;
using Source.Infrastructure.ErrorHandling;
using Source.Infrastructure.Interceptors;

namespace Source.Infrastructure.Extensions;

public static class DatabaseExtensions
{
    public static IServiceCollection AddDatabaseServices(this IServiceCollection services, IConfiguration configuration)
    {
        // Resolve from DATABASE_URL (set by the deployment host) or config, normalizing
        // managed-host URI form (postgres://...) into Npgsql keyword form. See
        // PostgresConnectionString for the why.
        var connectionString = PostgresConnectionString.Resolve(configuration);

        // Configure NpgsqlDataSourceBuilder with dynamic JSON support for JSONB columns
        var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
        dataSourceBuilder.EnableDynamicJson(); // Required for Npgsql 8.0+ to read JSONB as List<Guid>, etc.
        var dataSource = dataSourceBuilder.Build();

        services.AddHttpContextAccessor();
        services.AddScoped<DomainEventInterceptor>();
        services.AddScoped<ChangeTrackingInterceptor>();

        // ErrorCaptureSaveChangesInterceptor only depends on the singleton ErrorQueue and a
        // singleton-safe ILogger<T>, so register it as a singleton — cheaper than scoped and
        // avoids the provider needing to resolve it per DbContext. AddInterceptors will pull
        // the same instance into every DbContext constructed by the factory.
        services.AddSingleton<ErrorCaptureSaveChangesInterceptor>();

        services.AddDbContext<ApplicationDbContext>((sp, options) =>
        {
            options.UseNpgsql(dataSource, npgsqlOptions =>
            {
                npgsqlOptions.EnableRetryOnFailure(maxRetryCount: 3);
                npgsqlOptions.CommandTimeout(30);
            });
            options.AddInterceptors(
                sp.GetRequiredService<DomainEventInterceptor>(),
                sp.GetRequiredService<ChangeTrackingInterceptor>(),
                sp.GetRequiredService<ErrorCaptureSaveChangesInterceptor>());
        });

        return services;
    }
    
    public static async Task<WebApplication> MigrateDatabase(this WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await context.Database.MigrateAsync();
        return app;
    }
} 