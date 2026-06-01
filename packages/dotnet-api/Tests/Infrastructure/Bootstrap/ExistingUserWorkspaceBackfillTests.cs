using Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Source.Features.Users.Models;
using Source.Features.Workspaces.Models;
using Source.Infrastructure;
using Source.Infrastructure.AuthorizationModels;
using Source.Infrastructure.Bootstrap;

namespace Api.Tests.Infrastructure.Bootstrap;

/// <summary>
/// Integration tests for <see cref="ExistingUserWorkspaceBackfill"/>. We use the
/// <see cref="IntegrationTestBase"/> in-memory test host so the backfill runs against
/// a real <see cref="UserManager{TUser}"/>, real MediatR pipeline (so
/// <c>CreateWorkspaceCommand</c> handler actually runs and persists rows), and a real
/// <see cref="ApplicationDbContext"/>. Mocking those would be brittle and would
/// effectively re-implement the production behaviour we are trying to validate.
/// </summary>
[Collection(HangfireTestCollection.Name)]
public class ExistingUserWorkspaceBackfillTests : IntegrationTestBase
{
    /// <summary>
    /// Build the backfill against the test host's scope factory. We construct the
    /// service directly (rather than registering it as a hosted service) so the test
    /// can call <c>RunAsync</c> deterministically and assert on its side-effects.
    /// </summary>
    private ExistingUserWorkspaceBackfill BuildBackfill()
    {
        var scopeFactory = Services.GetRequiredService<IServiceScopeFactory>();
        return new ExistingUserWorkspaceBackfill(scopeFactory, NullLogger<ExistingUserWorkspaceBackfill>.Instance);
    }

    /// <summary>
    /// Insert a user directly via UserManager — bypasses RegisterUserHandler so the
    /// user has neither the WorkspaceUser role nor a primary workspace, mimicking the
    /// pre-P1.2 production state we are backfilling.
    /// </summary>
    private async Task<User> SeedRawUserAsync(string emailLocal)
    {
        await SeedRolesAsync();

        using var scope = CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var user = new User
        {
            UserName = $"{emailLocal}@example.com",
            Email = $"{emailLocal}@example.com",
            EmailConfirmed = true,
        };
        var result = await userManager.CreateAsync(user, "Password123!");
        result.Succeeded.Should().BeTrue("seeding the raw user must succeed");
        return user;
    }

    [Fact]
    public async Task Happy_path_users_with_no_memberships_get_workspace_and_role()
    {
        // Arrange — three pre-P1.2 users with neither a workspace nor the role.
        var alice = await SeedRawUserAsync("alice");
        var bob = await SeedRawUserAsync("bob");
        var carol = await SeedRawUserAsync("carol");

        // Act
        var backfill = BuildBackfill();
        await backfill.RunAsync(CancellationToken.None);

        // Assert — every user now has exactly one membership (Owner role) and the WorkspaceUser app role.
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();

        foreach (var seeded in new[] { alice, bob, carol })
        {
            var user = await userManager.FindByIdAsync(seeded.Id);
            user.Should().NotBeNull();

            var hasRole = await userManager.IsInRoleAsync(user!, RoleConstants.WorkspaceUser);
            hasRole.Should().BeTrue($"{seeded.Email} must have the WorkspaceUser role after backfill");

            var memberships = await db.WorkspaceMemberships
                .Where(m => m.UserId == seeded.Id)
                .ToListAsync();
            memberships.Should().HaveCount(1, $"{seeded.Email} must have exactly one membership after backfill");
            memberships[0].Role.Should().Be(WorkspaceRole.Owner);

            var workspace = await db.Workspaces.SingleAsync(w => w.Id == memberships[0].WorkspaceId);
            workspace.OwnerId.Should().Be(seeded.Id);
        }
    }

    [Fact]
    public async Task Idempotent_running_twice_in_a_row_is_a_noop_on_the_second_run()
    {
        // Arrange
        await SeedRawUserAsync("dave");
        await SeedRawUserAsync("eve");

        var backfill = BuildBackfill();

        // First run — provisions both users.
        await backfill.RunAsync(CancellationToken.None);

        // Snapshot state after the first run so we can assert nothing changes on the second.
        int workspaceCountAfterFirst;
        int membershipCountAfterFirst;
        using (var scope = CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            workspaceCountAfterFirst = await db.Workspaces.CountAsync();
            membershipCountAfterFirst = await db.WorkspaceMemberships.CountAsync();
        }

        // Act — second run should be a no-op.
        await backfill.RunAsync(CancellationToken.None);

        // Assert — counts are identical; no extra workspaces/memberships were created.
        using var scope2 = CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var workspaceCountAfterSecond = await db2.Workspaces.CountAsync();
        var membershipCountAfterSecond = await db2.WorkspaceMemberships.CountAsync();

        workspaceCountAfterSecond.Should().Be(workspaceCountAfterFirst,
            "the second run must not create any additional workspaces");
        membershipCountAfterSecond.Should().Be(membershipCountAfterFirst,
            "the second run must not create any additional memberships");
    }

    [Fact]
    public async Task Mixed_state_user_with_membership_no_role_user_with_role_no_membership_user_with_neither()
    {
        // Arrange — three users, each in a different broken state.
        await SeedRolesAsync();

        // (A) has a membership but no role — should get the role added, no new workspace.
        var withMembership = await SeedRawUserAsync("withmembership");
        Guid existingWorkspaceId;
        using (var scope = CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var workspace = new Workspace
            {
                Id = Guid.NewGuid(),
                Slug = "withmembership-existing",
                Name = "Existing",
                OwnerId = withMembership.Id,
            };
            db.Workspaces.Add(workspace);
            db.WorkspaceMemberships.Add(new WorkspaceMembership
            {
                Id = Guid.NewGuid(),
                WorkspaceId = workspace.Id,
                UserId = withMembership.Id,
                Role = WorkspaceRole.Owner,
            });
            await db.SaveChangesAsync();
            existingWorkspaceId = workspace.Id;
        }

        // (B) has the role but no membership — should get a workspace created, role unchanged.
        var withRole = await SeedRawUserAsync("withrole");
        using (var scope = CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            var user = await userManager.FindByIdAsync(withRole.Id);
            (await userManager.AddToRoleAsync(user!, RoleConstants.WorkspaceUser)).Succeeded.Should().BeTrue();
        }

        // (C) has neither — should get both.
        var withNothing = await SeedRawUserAsync("withnothing");

        // Act
        await BuildBackfill().RunAsync(CancellationToken.None);

        // Assert
        using var verifyScope = CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var verifyUm = verifyScope.ServiceProvider.GetRequiredService<UserManager<User>>();

        // (A) membership-no-role: role granted, still exactly one (pre-existing) workspace.
        var aUser = await verifyUm.FindByIdAsync(withMembership.Id);
        (await verifyUm.IsInRoleAsync(aUser!, RoleConstants.WorkspaceUser)).Should().BeTrue();
        var aMemberships = await verifyDb.WorkspaceMemberships.Where(m => m.UserId == withMembership.Id).ToListAsync();
        aMemberships.Should().HaveCount(1, "user with pre-existing membership must not get a second workspace");
        aMemberships[0].WorkspaceId.Should().Be(existingWorkspaceId);

        // (B) role-no-membership: still has the role, now has exactly one membership.
        var bUser = await verifyUm.FindByIdAsync(withRole.Id);
        (await verifyUm.IsInRoleAsync(bUser!, RoleConstants.WorkspaceUser)).Should().BeTrue();
        var bMemberships = await verifyDb.WorkspaceMemberships.Where(m => m.UserId == withRole.Id).ToListAsync();
        bMemberships.Should().HaveCount(1);
        bMemberships[0].Role.Should().Be(WorkspaceRole.Owner);

        // (C) neither: now has both.
        var cUser = await verifyUm.FindByIdAsync(withNothing.Id);
        (await verifyUm.IsInRoleAsync(cUser!, RoleConstants.WorkspaceUser)).Should().BeTrue();
        var cMemberships = await verifyDb.WorkspaceMemberships.Where(m => m.UserId == withNothing.Id).ToListAsync();
        cMemberships.Should().HaveCount(1);
        cMemberships[0].Role.Should().Be(WorkspaceRole.Owner);
    }
}
