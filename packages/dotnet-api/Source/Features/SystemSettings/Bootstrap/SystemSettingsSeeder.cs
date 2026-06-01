using Microsoft.EntityFrameworkCore;
using Source.Features.SystemSettings.Models;
using Source.Features.SystemSettings.Services;
using Source.Infrastructure;

namespace Source.Features.SystemSettings.Bootstrap;

/// <summary>
/// One-shot startup hosted service that ports the static <see cref="SystemSettingsCatalog"/>
/// into the DB on first boot of an environment, picking up any current values from
/// <see cref="IConfiguration"/> (e.g. the <c>GitHub:</c> block in <c>appsettings.json</c>).
///
/// <para><b>Idempotent.</b> Each row is checked individually; subsequent boots find
/// every row already present and exit with "Seeded 0 new...".</para>
///
/// <para><b>Doesn't overwrite.</b> Once a row exists, the seeder never updates it —
/// the DB is authoritative. To clear a value the operator uses the admin UI.</para>
/// </summary>
public class SystemSettingsSeeder : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SystemSettingsSeeder> _logger;
    private readonly IHostEnvironment _env;

    public SystemSettingsSeeder(
        IServiceScopeFactory scopeFactory,
        ILogger<SystemSettingsSeeder> logger,
        IHostEnvironment env)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _env = env;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Integration tests run on the EF Core InMemory provider — the same skip the
        // Program.cs migration block uses. Without this guard the seeder runs against
        // a fresh in-memory DB on every test host boot, which is harmless but noisy.
        if (_env.IsEnvironment("Testing"))
        {
            return;
        }

        try
        {
            await SeedAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            // A seeder failure should never block API startup — log loudly and move on.
            // The admin UI will surface "no value" rows; the operator re-enters them.
            _logger.LogError(ex, "SystemSettingsSeeder failed; continuing startup.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// The actual seed loop, exposed so tests can drive it without spinning up a host.
    /// </summary>
    public async Task SeedAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        var cipher = scope.ServiceProvider.GetRequiredService<ISystemSettingsCipher>();

        var existingKeys = await db.Set<SystemSetting>()
            .Select(s => s.Key)
            .ToListAsync(ct);
        var existingSet = new HashSet<string>(existingKeys, StringComparer.Ordinal);

        var inserted = 0;
        var skipped = 0;

        foreach (var category in SystemSettingsCatalog.Categories)
        {
            foreach (var def in category.Settings)
            {
                if (existingSet.Contains(def.Key))
                {
                    skipped++;
                    continue;
                }

                var initial = config[def.Key];
                var hasInitial = !string.IsNullOrEmpty(initial);

                var row = new SystemSetting
                {
                    Key = def.Key,
                    Category = def.Category,
                    Description = def.Description,
                    IsSecret = def.IsSecret,
                    UpdatedBy = null,
                    Value = hasInitial
                        ? (def.IsSecret ? cipher.Encrypt(initial!) : initial)
                        : null,
                };
                db.Set<SystemSetting>().Add(row);
                inserted++;
            }
        }

        if (inserted > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        _logger.LogInformation(
            "Seeded {Inserted} new SystemSettings rows; {Skipped} already present.",
            inserted, skipped);
    }
}
