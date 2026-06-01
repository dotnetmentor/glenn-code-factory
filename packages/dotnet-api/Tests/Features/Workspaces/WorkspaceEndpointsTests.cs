using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Source.Features.Authentication.Commands;
using Source.Features.Users.Models;
using Source.Features.Workspaces.Commands;
using Source.Features.Workspaces.Models;
using Source.Features.Workspaces.Queries;
using Source.Infrastructure;

namespace Api.Tests.Features.Workspaces;

/// <summary>
/// End-to-end HTTP tests for the workspace endpoints introduced in P1.4:
///   * POST   /api/workspaces           — create
///   * GET    /api/workspaces/{slug}    — read (Member)
///   * PUT    /api/workspaces/{slug}    — rename (Owner)
///   * DELETE /api/workspaces/{slug}    — soft-delete (Owner)
///   * GET    /api/me/workspaces        — picker list
///
/// We register users via /api/auth/register to obtain real JWT-in-cookie auth, then drive
/// HttpClient instances seeded with the right cookie.
/// </summary>
[Collection(HangfireTestCollection.Name)]
public class WorkspaceEndpointsTests : IntegrationTestBase
{
    private const string Password = "Password123!";

    /// <summary>
    /// Mirrors the production JSON setup: the API uses <see cref="JsonStringEnumConverter"/>
    /// for enums, so deserialization on the test side has to as well.
    /// </summary>
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Registers a fresh user and returns both a usable HttpClient (auth cookie attached) and the
    /// userId. Each call mints a unique email so tests stay isolated within the same in-memory DB.
    /// The /register endpoint also auto-creates a primary workspace for the user — we surface its
    /// slug too because most tests want to operate on it.
    /// </summary>
    private async Task<(HttpClient Client, string UserId, string PrimaryWorkspaceSlug, string Email)> RegisterUserAsync(string? localPart = null)
    {
        await SeedRolesAsync();

        var emailLocal = localPart ?? $"user-{Guid.NewGuid():N}";
        var email = $"{emailLocal}@example.com";

        var registerClient = Factory.CreateClient();
        var response = await registerClient.PostAsJsonAsync("/api/auth/register", new
        {
            email,
            password = Password,
        });
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        // Pull out the auth-token cookie that /register set.
        var cookies = response.Headers.GetValues("Set-Cookie");
        var authCookie = cookies.FirstOrDefault(c => c.StartsWith("auth-token=", StringComparison.Ordinal));
        authCookie.Should().NotBeNull("registration must set the auth-token cookie");
        var cookieValue = authCookie!.Split(';')[0]; // "auth-token=...."

        // Build a real HttpClient that re-sends that cookie on every request.
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", cookieValue);

        // Resolve the userId + primary workspace slug from the DB.
        using var scope = CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var user = await userManager.FindByEmailAsync(email);
        user.Should().NotBeNull();

        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var workspace = await db.Workspaces.SingleAsync(w => w.OwnerId == user!.Id);

        return (client, user!.Id, workspace.Slug, email);
    }

    // -----------------------------------------------------------------------
    // POST /api/workspaces
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Post_workspaces_creates_workspace_and_owner_membership()
    {
        var (client, userId, _, _) = await RegisterUserAsync();

        var response = await client.PostAsJsonAsync("/api/workspaces", new
        {
            name = "Acme Inc",
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());

        var body = await response.Content.ReadFromJsonAsync<CreateWorkspaceResponse>();
        body.Should().NotBeNull();
        body!.Name.Should().Be("Acme Inc");
        body.Slug.Should().StartWith("acme-inc");

        // The DB should reflect the new workspace + an Owner membership for the caller.
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var ws = await db.Workspaces.SingleAsync(w => w.Id == body.Id);
        ws.OwnerId.Should().Be(userId);

        var membership = await db.WorkspaceMemberships
            .SingleAsync(m => m.WorkspaceId == ws.Id && m.UserId == userId);
        membership.Role.Should().Be(WorkspaceRole.Owner);
    }

    [Fact]
    public async Task Post_workspaces_requires_authentication()
    {
        // No cookie attached.
        var anonymous = Factory.CreateClient();
        var response = await anonymous.PostAsJsonAsync("/api/workspaces", new { name = "Anon" });
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // -----------------------------------------------------------------------
    // GET /api/me/workspaces
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Get_me_workspaces_returns_only_callers_workspaces()
    {
        // Two users — Alice gets her primary + an extra; Bob gets only his primary.
        var alice = await RegisterUserAsync();
        var bob = await RegisterUserAsync();

        var aliceExtra = await alice.Client.PostAsJsonAsync("/api/workspaces", new { name = "Alice Side Project" });
        aliceExtra.StatusCode.Should().Be(HttpStatusCode.Created);
        var extra = await aliceExtra.Content.ReadFromJsonAsync<CreateWorkspaceResponse>();

        var aliceList = await alice.Client.GetFromJsonAsync<List<MyWorkspaceItem>>("/api/me/workspaces", JsonOpts);
        aliceList.Should().NotBeNull();
        aliceList!.Should().HaveCount(2);
        aliceList.Should().Contain(w => w.Slug == alice.PrimaryWorkspaceSlug && w.Role == WorkspaceRole.Owner);
        aliceList.Should().Contain(w => w.Slug == extra!.Slug && w.Role == WorkspaceRole.Owner);

        var bobList = await bob.Client.GetFromJsonAsync<List<MyWorkspaceItem>>("/api/me/workspaces", JsonOpts);
        bobList.Should().NotBeNull();
        bobList!.Should().HaveCount(1);
        bobList![0].Slug.Should().Be(bob.PrimaryWorkspaceSlug);
        bobList.Should().NotContain(w => w.Slug == alice.PrimaryWorkspaceSlug, "bob must not see alice's workspaces");
    }

    [Fact]
    public async Task Get_me_workspaces_requires_authentication()
    {
        var anonymous = Factory.CreateClient();
        var response = await anonymous.GetAsync("/api/me/workspaces");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // -----------------------------------------------------------------------
    // GET /api/workspaces/{slug}
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Get_by_slug_returns_details_for_member()
    {
        var (client, _, slug, _) = await RegisterUserAsync();

        var response = await client.GetAsync($"/api/workspaces/{slug}");
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        var body = await response.Content.ReadFromJsonAsync<WorkspaceDetailsResponse>(JsonOpts);
        body.Should().NotBeNull();
        body!.Slug.Should().Be(slug);
        body.MemberCount.Should().Be(1);
        body.CallerRole.Should().Be(WorkspaceRole.Owner);
    }

    [Fact]
    public async Task Get_by_slug_returns_403_for_non_member()
    {
        var alice = await RegisterUserAsync();
        var bob = await RegisterUserAsync();

        // Bob is authenticated but has no membership in Alice's workspace.
        var response = await bob.Client.GetAsync($"/api/workspaces/{alice.PrimaryWorkspaceSlug}");
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Get_by_slug_returns_404_for_unknown_slug()
    {
        var (client, _, _, _) = await RegisterUserAsync();

        var response = await client.GetAsync("/api/workspaces/no-such-workspace-1234");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Get_by_slug_requires_authentication()
    {
        var (_, _, slug, _) = await RegisterUserAsync();

        var anonymous = Factory.CreateClient();
        var response = await anonymous.GetAsync($"/api/workspaces/{slug}");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // -----------------------------------------------------------------------
    // PUT /api/workspaces/{slug}  (rename)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Put_renames_workspace_for_owner()
    {
        var (client, _, slug, _) = await RegisterUserAsync();

        var response = await client.PutAsJsonAsync($"/api/workspaces/{slug}", new
        {
            name = "Renamed Co",
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<RenameWorkspaceResponse>();
        body.Should().NotBeNull();
        body!.Name.Should().Be("Renamed Co");
        body.Slug.Should().Be(slug, "slug stays the same when only the name is provided");
    }

    [Fact]
    public async Task Put_rejects_admin_role()
    {
        // Owner Alice creates her workspace; we then promote Bob to Admin in it directly via DB.
        var alice = await RegisterUserAsync();
        var bob = await RegisterUserAsync();

        using (var scope = CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var workspace = await db.Workspaces.SingleAsync(w => w.Slug == alice.PrimaryWorkspaceSlug);
            db.WorkspaceMemberships.Add(new WorkspaceMembership
            {
                Id = Guid.NewGuid(),
                WorkspaceId = workspace.Id,
                UserId = bob.UserId,
                Role = WorkspaceRole.Admin,
            });
            await db.SaveChangesAsync();
        }

        // Bob is an Admin — rename should be Owner-only, so 403.
        var response = await bob.Client.PutAsJsonAsync($"/api/workspaces/{alice.PrimaryWorkspaceSlug}", new
        {
            name = "Hijack",
        });
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Put_rejects_non_member()
    {
        var alice = await RegisterUserAsync();
        var bob = await RegisterUserAsync();

        var response = await bob.Client.PutAsJsonAsync($"/api/workspaces/{alice.PrimaryWorkspaceSlug}", new
        {
            name = "Hijack",
        });
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // -----------------------------------------------------------------------
    // DELETE /api/workspaces/{slug}
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Delete_soft_deletes_workspace_for_owner()
    {
        var (client, _, slug, _) = await RegisterUserAsync();

        var response = await client.DeleteAsync($"/api/workspaces/{slug}");
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Soft-delete: row still exists in storage but is hidden by query filter.
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var visible = await db.Workspaces.SingleOrDefaultAsync(w => w.Slug == slug);
        visible.Should().BeNull("soft-deleted workspaces are filtered out by default");

        var hidden = await db.Workspaces.IgnoreQueryFilters().SingleAsync(w => w.Slug == slug);
        hidden.IsDeleted.Should().BeTrue();
        hidden.DeletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Delete_rejects_admin()
    {
        var alice = await RegisterUserAsync();
        var bob = await RegisterUserAsync();

        using (var scope = CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var workspace = await db.Workspaces.SingleAsync(w => w.Slug == alice.PrimaryWorkspaceSlug);
            db.WorkspaceMemberships.Add(new WorkspaceMembership
            {
                Id = Guid.NewGuid(),
                WorkspaceId = workspace.Id,
                UserId = bob.UserId,
                Role = WorkspaceRole.Admin,
            });
            await db.SaveChangesAsync();
        }

        var response = await bob.Client.DeleteAsync($"/api/workspaces/{alice.PrimaryWorkspaceSlug}");
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
