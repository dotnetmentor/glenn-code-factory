using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Source.Features.ProjectTemplates.Models;
using Source.Features.RuntimePresets.Contracts;
using Source.Features.Users.Models;
using Source.Infrastructure.AuthorizationModels;

namespace Source.Infrastructure.DevSeed;

/// <summary>
/// Development seed service that seeds test users and curated project starters.
/// Runs on startup in Development mode only.
/// Uses hardcoded OTPs and passwords for deterministic E2E testing.
/// </summary>
public class DevSeedService : IHostedService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<DevSeedService> _logger;

    public DevSeedService(IServiceProvider serviceProvider, ILogger<DevSeedService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("=== DEV SEED SERVICE STARTING ===");

        using var scope = _serviceProvider.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        try
        {
            // Seed test users
            await SeedUsers(userManager, roleManager, cancellationToken);

            // Seed curated project templates (Starters). Runs AFTER users so any
            // future user-scoped seed logic upstream is in place first.
            await SeedProjectTemplatesAsync(db, cancellationToken);

            _logger.LogInformation("=== DEV SEED SERVICE COMPLETED SUCCESSFULLY ===");
            LogTestCredentials();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during dev seed.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task SeedUsers(
        UserManager<User> userManager,
        RoleManager<IdentityRole> roleManager,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Seeding test users...");

        foreach (var userDef in DevSeedData.TestUsers)
        {
            var existingUser = await userManager.FindByEmailAsync(userDef.Email);
            if (existingUser != null)
            {
                // Ensure existing user has a password set
                var hasPassword = await userManager.HasPasswordAsync(existingUser);
                if (!hasPassword)
                {
                    var addPwResult = await userManager.AddPasswordAsync(existingUser, userDef.Password);
                    if (addPwResult.Succeeded)
                    {
                        _logger.LogInformation("Added password to existing user: {Email}", userDef.Email);
                    }
                    else
                    {
                        var errors = string.Join(", ", addPwResult.Errors.Select(e => e.Description));
                        _logger.LogWarning("Failed to add password to {Email}: {Errors}", userDef.Email, errors);
                    }
                }

                _logger.LogDebug("User {Email} already exists, skipping creation", userDef.Email);
                continue;
            }

            var user = new User
            {
                UserName = userDef.Email,
                Email = userDef.Email,
                EmailConfirmed = true,
                FirstName = userDef.FirstName,
                LastName = userDef.LastName,
            };

            var result = await userManager.CreateAsync(user);
            if (!result.Succeeded)
            {
                var errors = string.Join(", ", result.Errors.Select(e => e.Description));
                _logger.LogError("Failed to create user {Email}: {Errors}", userDef.Email, errors);
                continue;
            }

            _logger.LogInformation("Created user: {Email} ({FirstName} {LastName})",
                userDef.Email, userDef.FirstName, userDef.LastName);

            // Set password for the user
            var passwordResult = await userManager.AddPasswordAsync(user, userDef.Password);
            if (passwordResult.Succeeded)
            {
                _logger.LogInformation("Password set for user: {Email}", userDef.Email);
            }
            else
            {
                var errors = string.Join(", ", passwordResult.Errors.Select(e => e.Description));
                _logger.LogWarning("Failed to set password for {Email}: {Errors}", userDef.Email, errors);
            }

            // Assign SuperAdmin role if needed
            if (userDef.IsSuperAdmin)
            {
                var roleName = RoleConstants.SuperAdmin;
                if (await roleManager.RoleExistsAsync(roleName))
                {
                    var roleResult = await userManager.AddToRoleAsync(user, roleName);
                    if (roleResult.Succeeded)
                    {
                        _logger.LogInformation("Assigned SuperAdmin role to {Email}", userDef.Email);
                    }
                }
            }

            // Set OTP for testing
            user.OtpCode = userDef.OtpCode;
            user.OtpExpiresAt = DateTime.UtcNow.AddYears(10);
            user.OtpAttempts = 0;
            await userManager.UpdateAsync(user);
        }
    }

    /// <summary>
    /// Seed the curated global <c>ProjectTemplates</c> catalogue.
    /// Idempotent by <c>Slug</c>: on re-run, existing rows are left untouched
    /// (admin edits to name / icon / sort order / archive status survive) and
    /// only missing slugs are inserted. <c>CreatedAt</c> / <c>UpdatedAt</c> are
    /// stamped by the DbContext's audit interceptor on save.
    ///
    /// <para>Inline <c>RuntimeSpec</c> JSON is validated against
    /// <see cref="RuntimeSpecV2"/> before insert — a malformed seed crashes
    /// startup loudly rather than silently shipping a broken row that the
    /// project-create flow would later trip over.</para>
    /// </summary>
    private async Task SeedProjectTemplatesAsync(ApplicationDbContext db, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Seeding project starters...");

        foreach (var seed in DevSeedData.Starters)
        {
            // Idempotency check: re-runs skip slugs that already exist. We
            // intentionally include soft-deleted rows here (IgnoreQueryFilters)
            // so an admin's archive isn't undone by the next boot.
            var exists = await db.ProjectTemplates
                .IgnoreQueryFilters()
                .AnyAsync(t => t.Slug == seed.Slug, cancellationToken);

            if (exists)
            {
                _logger.LogDebug("Starter '{Slug}' already exists, skipping", seed.Slug);
                continue;
            }

            // Validate the inline runtime spec before insert — bad JSON should
            // crash startup, not silently land in the catalogue.
            if (!string.IsNullOrWhiteSpace(seed.RuntimeSpec))
            {
                var parsed = RuntimeSpecV3.TryParse(seed.RuntimeSpec);
                if (parsed is null)
                {
                    throw new InvalidOperationException(
                        $"Seed starter '{seed.Slug}' has invalid RuntimeSpec JSON.");
                }
                var validated = parsed.Validate();
                if (!validated.IsSuccess)
                {
                    throw new InvalidOperationException(
                        $"Seed starter '{seed.Slug}' failed V3 validation: {validated.Error}");
                }
            }

            var entity = new ProjectTemplate
            {
                Id = Guid.NewGuid(),
                Slug = seed.Slug,
                Name = seed.Name,
                Description = seed.Description,
                IconKey = seed.IconKey,
                SourceRepoOwner = seed.SourceRepoOwner,
                SourceRepoName = seed.SourceRepoName,
                RuntimeSpec = seed.RuntimeSpec,
                IsActive = seed.IsActive,
                IsDefault = seed.IsDefault,
                SortOrder = seed.SortOrder,
                // CreatedAt / UpdatedAt auto-stamped by the IAuditable interceptor.
            };

            db.ProjectTemplates.Add(entity);
            _logger.LogInformation("Seeded starter: {Slug} ({Name})", seed.Slug, seed.Name);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private void LogTestCredentials()
    {
        _logger.LogInformation("");
        _logger.LogInformation("========================================");
        _logger.LogInformation("     DEV TEST CREDENTIALS");
        _logger.LogInformation("========================================");
        _logger.LogInformation("");
        _logger.LogInformation("Use these emails to log in (OTP or password):");
        _logger.LogInformation("");

        foreach (var user in DevSeedData.TestUsers)
        {
            var roleInfo = user.IsSuperAdmin ? "SuperAdmin" : "User";
            _logger.LogInformation("  {Email,-25} OTP: {Otp}  Password: {Password}  ({Role})",
                user.Email, user.OtpCode, user.Password, roleInfo);
        }

        _logger.LogInformation("");
        _logger.LogInformation("========================================");
        _logger.LogInformation("");
    }
}
