using Source.Infrastructure.AuthorizationServices;
using Source.Infrastructure.DevSeed;

namespace Source.Infrastructure.Extensions;

public static class SeedDataExtensions
{
    /// <summary>
    /// Seeds roles in all environments (required for authorization to work)
    /// </summary>
    public static async Task<WebApplication> SeedRoles(this WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var roleSeeder = scope.ServiceProvider.GetRequiredService<RoleSeederService>();

        // Seed all roles (User, Admin, SuperAdmin) - required in all environments
        await roleSeeder.SeedRolesAsync();

        return app;
    }

    /// <summary>
    /// Ensures the bootstrap SuperAdmin account exists (all environments). Must run AFTER
    /// <see cref="SeedRoles"/> so the SuperAdmin role is present. Self-registration is closed,
    /// so this seed is the only way a fresh deployment has a usable login.
    /// </summary>
    public static async Task<WebApplication> SeedSuperAdmin(this WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var superAdminSeeder = scope.ServiceProvider.GetRequiredService<SuperAdminSeederService>();

        await superAdminSeeder.SeedSuperAdminAsync();

        return app;
    }

    /// <summary>
    /// Seeds development-only data (test users, tenants, sample data, etc.)
    /// Uses MediatR commands to ensure all business logic runs.
    /// </summary>
    public static async Task<WebApplication> SeedDevelopmentData(this WebApplication app)
    {
        app.Logger.LogInformation("Starting development data seeding...");

        using var scope = app.Services.CreateScope();
        var devSeedService = scope.ServiceProvider.GetRequiredService<DevSeedService>();

        // Run the dev seed service synchronously during startup
        await devSeedService.StartAsync(CancellationToken.None);

        return app;
    }

    /// <summary>
    /// Registers development seed service (call in Program.cs before building)
    /// </summary>
    public static IServiceCollection AddDevSeedServices(this IServiceCollection services)
    {
        services.AddScoped<DevSeedService>();
        return services;
    }
}
