using System.Net;
using System.Net.Http.Json;
using Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Source.Features.Users.Models;
using Source.Features.Workspaces.Models;
using Source.Infrastructure;
using Source.Infrastructure.AuthorizationModels;

namespace Api.Tests.Features.Authentication;

/// <summary>
/// End-to-end test for the /api/auth/register flow's bootstrap effects:
/// - User row created.
/// - WorkspaceUser app role granted.
/// - Initial workspace created with a slug derived from the email's local-part.
/// - Owner-role membership row written.
/// </summary>
[Collection(HangfireTestCollection.Name)]
public class RegisterCreatesWorkspaceTests : IntegrationTestBase
{
    [Fact]
    public async Task Register_creates_user_workspace_owner_membership_and_role()
    {
        await SeedRolesAsync();

        var email = $"jane.doe.{Guid.NewGuid():N}@example.com";

        var response = await Client.PostAsJsonAsync("/api/auth/register", new
        {
            email,
            password = "Password123!",
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        // The User row exists and has the WorkspaceUser role.
        using var scope = CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var user = await userManager.FindByEmailAsync(email);
        user.Should().NotBeNull();
        var roles = await userManager.GetRolesAsync(user!);
        roles.Should().Contain(RoleConstants.WorkspaceUser);

        // A workspace exists, owned by this user, with an Owner-role membership.
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var workspaces = await db.Workspaces.Where(w => w.OwnerId == user!.Id).ToListAsync();
        workspaces.Should().HaveCount(1);
        var ws = workspaces.Single();
        ws.Slug.Should().StartWith("jane-doe", "the slug should derive from the email local-part");

        var membership = await db.WorkspaceMemberships
            .SingleAsync(m => m.WorkspaceId == ws.Id && m.UserId == user!.Id);
        membership.Role.Should().Be(WorkspaceRole.Owner);
    }

    [Fact]
    public async Task Register_picks_unique_slug_when_local_part_collides()
    {
        await SeedRolesAsync();

        var seed = $"shared.{Guid.NewGuid():N}";

        var first = await Client.PostAsJsonAsync("/api/auth/register", new
        {
            email = $"{seed}@a.com",
            password = "Password123!",
        });
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        var second = await Client.PostAsJsonAsync("/api/auth/register", new
        {
            email = $"{seed}@b.com", // same local-part — slug must auto-suffix
            password = "Password123!",
        });
        second.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var slugs = await db.Workspaces.Select(w => w.Slug).ToListAsync();

        var sanitised = Source.Features.Workspaces.Services.WorkspaceSlugGenerator.Sanitize(seed);
        slugs.Should().Contain(sanitised);
        slugs.Should().Contain($"{sanitised}-2", "the second register call should land on the -2 suffix");
    }
}
