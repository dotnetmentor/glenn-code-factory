using Microsoft.AspNetCore.Identity;
using Source.Features.Users.Models;
using Source.Infrastructure.AuthorizationModels;

namespace Source.Infrastructure.AuthorizationServices;

/// <summary>
/// Idempotent startup seeder that guarantees a bootstrap SuperAdmin account exists in
/// every environment. Self-registration is closed (see <c>SendOtpHandler</c>), so without
/// this seed there would be no way to log into a freshly deployed instance.
///
/// <para>The user is created with <see cref="User.EmailConfirmed"/> = true and the
/// <see cref="RoleConstants.SuperAdmin"/> role. The <c>ExistingUserWorkspaceBackfill</c>
/// hosted service then grants the <see cref="RoleConstants.WorkspaceUser"/> role and a
/// primary workspace on the same boot, so the seeded admin lands with full access.</para>
///
/// <para>The email is read from <c>Bootstrap:SuperAdminEmail</c> (env var
/// <c>Bootstrap__SuperAdminEmail</c>). If unset, seeding is skipped — set it in
/// <c>.env</c> locally or in your deployment env vars before first boot.
/// Running this twice is a no-op: an existing user simply has the role ensured.</para>
/// </summary>
public class SuperAdminSeederService
{
    private readonly UserManager<User> _userManager;
    private readonly RoleManager<IdentityRole> _roleManager;
    private readonly IConfiguration _configuration;
    private readonly ILogger<SuperAdminSeederService> _logger;

    public SuperAdminSeederService(
        UserManager<User> userManager,
        RoleManager<IdentityRole> roleManager,
        IConfiguration configuration,
        ILogger<SuperAdminSeederService> logger)
    {
        _userManager = userManager;
        _roleManager = roleManager;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task SeedSuperAdminAsync()
    {
        var email = _configuration["Bootstrap:SuperAdminEmail"];
        if (string.IsNullOrWhiteSpace(email))
        {
            _logger.LogWarning(
                "Bootstrap:SuperAdminEmail is not configured — skipping SuperAdmin seed. " +
                "Set Bootstrap__SuperAdminEmail (e.g. in .env) to bootstrap the first admin.");
            return;
        }

        _logger.LogInformation("🔐 Ensuring bootstrap SuperAdmin exists: {Email}", email);

        if (!await _roleManager.RoleExistsAsync(RoleConstants.SuperAdmin))
        {
            _logger.LogError("❌ SuperAdmin role missing — role seeding must run before SuperAdmin seeding.");
            return;
        }

        var user = await _userManager.FindByEmailAsync(email);
        if (user is null)
        {
            user = new User
            {
                UserName = email,
                Email = email,
                EmailConfirmed = true,
                Credits = 500
            };

            var createResult = await _userManager.CreateAsync(user);
            if (!createResult.Succeeded)
            {
                var errors = string.Join(", ", createResult.Errors.Select(e => e.Description));
                _logger.LogError("❌ Failed to create bootstrap SuperAdmin {Email}: {Errors}", email, errors);
                return;
            }

            _logger.LogInformation("✅ Created bootstrap SuperAdmin user: {Email}", email);
        }

        if (!await _userManager.IsInRoleAsync(user, RoleConstants.SuperAdmin))
        {
            var roleResult = await _userManager.AddToRoleAsync(user, RoleConstants.SuperAdmin);
            if (roleResult.Succeeded)
            {
                _logger.LogInformation("✅ Granted SuperAdmin role to: {Email}", email);
            }
            else
            {
                var errors = string.Join(", ", roleResult.Errors.Select(e => e.Description));
                _logger.LogError("❌ Failed to grant SuperAdmin role to {Email}: {Errors}", email, errors);
            }
        }
        else
        {
            _logger.LogDebug("Bootstrap SuperAdmin {Email} already has the SuperAdmin role.", email);
        }
    }
}
