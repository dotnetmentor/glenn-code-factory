using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Source.Features.Users.Models;
using Source.Features.Workspaces.Commands;
using Source.Features.Workspaces.Models;
using Source.Features.Workspaces.Queries;
using Source.Infrastructure;

namespace Api.Tests.Features.Workspaces;

/// <summary>
/// End-to-end HTTP tests for the membership + invite endpoints introduced in P1.5.
/// Exercises the role-gating (Admin), last-Owner protection at the HTTP boundary, and the
/// full invite-and-accept loop across two distinct user identities.
/// </summary>
[Collection(HangfireTestCollection.Name)]
public class MembersAndInvitesEndpointsTests : IntegrationTestBase
{
    private const string Password = "Password123!";

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private async Task<(HttpClient Client, string UserId, string Email, string PrimaryWorkspaceSlug)> RegisterUserAsync(string? localPart = null)
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

        var cookies = response.Headers.GetValues("Set-Cookie");
        var authCookie = cookies.First(c => c.StartsWith("auth-token=", StringComparison.Ordinal));
        var cookieValue = authCookie.Split(';')[0];

        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", cookieValue);

        using var scope = CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var user = (await userManager.FindByEmailAsync(email))!;
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var ws = await db.Workspaces.SingleAsync(w => w.OwnerId == user.Id);

        return (client, user.Id, email, ws.Slug);
    }

    /// <summary>Promote a second user into Alice's workspace at the given role (direct DB seed).</summary>
    private async Task SeedMembershipAsync(string slug, string userId, WorkspaceRole role)
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var ws = await db.Workspaces.SingleAsync(w => w.Slug == slug);
        db.WorkspaceMemberships.Add(new WorkspaceMembership
        {
            Id = Guid.NewGuid(),
            WorkspaceId = ws.Id,
            UserId = userId,
            Role = role,
        });
        await db.SaveChangesAsync();
    }

    // -----------------------------------------------------------------------
    // GET /api/workspaces/{slug}/members
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Get_members_returns_list_for_member_role()
    {
        var alice = await RegisterUserAsync();
        var bob = await RegisterUserAsync();
        await SeedMembershipAsync(alice.PrimaryWorkspaceSlug, bob.UserId, WorkspaceRole.Member);

        var response = await bob.Client.GetAsync($"/api/workspaces/{alice.PrimaryWorkspaceSlug}/members");
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        var body = await response.Content.ReadFromJsonAsync<List<WorkspaceMemberItem>>(JsonOpts);
        body.Should().NotBeNull();
        body!.Should().HaveCount(2);
        body.Should().ContainSingle(m => m.UserId == alice.UserId && m.IsOwner);
        body.Should().ContainSingle(m => m.UserId == bob.UserId && !m.IsOwner && m.Role == WorkspaceRole.Member);
    }

    [Fact]
    public async Task Get_members_403_for_non_member()
    {
        var alice = await RegisterUserAsync();
        var bob = await RegisterUserAsync();

        var response = await bob.Client.GetAsync($"/api/workspaces/{alice.PrimaryWorkspaceSlug}/members");
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // -----------------------------------------------------------------------
    // PUT /api/workspaces/{slug}/members/{userId}
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Put_member_role_admin_can_promote_member_to_admin()
    {
        var alice = await RegisterUserAsync();
        var bob = await RegisterUserAsync();
        await SeedMembershipAsync(alice.PrimaryWorkspaceSlug, bob.UserId, WorkspaceRole.Member);

        var response = await alice.Client.PutAsJsonAsync(
            $"/api/workspaces/{alice.PrimaryWorkspaceSlug}/members/{bob.UserId}",
            new { role = "Admin" });

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<ChangeMemberRoleResponse>(JsonOpts);
        body!.Role.Should().Be(WorkspaceRole.Admin);
    }

    [Fact]
    public async Task Put_member_role_member_role_is_403()
    {
        var alice = await RegisterUserAsync();
        var bob = await RegisterUserAsync();
        await SeedMembershipAsync(alice.PrimaryWorkspaceSlug, bob.UserId, WorkspaceRole.Member);

        // Bob is only a Member — he can't change roles, even his own.
        var response = await bob.Client.PutAsJsonAsync(
            $"/api/workspaces/{alice.PrimaryWorkspaceSlug}/members/{bob.UserId}",
            new { role = "Admin" });
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Put_member_role_refuses_demoting_last_owner()
    {
        var alice = await RegisterUserAsync();

        var response = await alice.Client.PutAsJsonAsync(
            $"/api/workspaces/{alice.PrimaryWorkspaceSlug}/members/{alice.UserId}",
            new { role = "Admin" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("last Owner");
    }

    // -----------------------------------------------------------------------
    // DELETE /api/workspaces/{slug}/members/{userId}
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Delete_member_admin_can_remove_member()
    {
        var alice = await RegisterUserAsync();
        var bob = await RegisterUserAsync();
        await SeedMembershipAsync(alice.PrimaryWorkspaceSlug, bob.UserId, WorkspaceRole.Member);

        var response = await alice.Client.DeleteAsync($"/api/workspaces/{alice.PrimaryWorkspaceSlug}/members/{bob.UserId}");
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Scope the assertion to *Alice's* workspace — Bob still owns his own primary workspace
        // (auto-created at registration), so a global membership lookup would still find him.
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var aliceWs = await db.Workspaces.SingleAsync(w => w.Slug == alice.PrimaryWorkspaceSlug);
        var stillInAliceWs = await db.WorkspaceMemberships
            .AnyAsync(m => m.WorkspaceId == aliceWs.Id && m.UserId == bob.UserId);
        stillInAliceWs.Should().BeFalse();
    }

    [Fact]
    public async Task Delete_member_refuses_removing_last_owner()
    {
        var alice = await RegisterUserAsync();

        var response = await alice.Client.DeleteAsync($"/api/workspaces/{alice.PrimaryWorkspaceSlug}/members/{alice.UserId}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("last Owner");
    }

    // -----------------------------------------------------------------------
    // POST /api/workspaces/{slug}/invites
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Post_invite_admin_creates_pending_invite_with_token()
    {
        var alice = await RegisterUserAsync();

        var response = await alice.Client.PostAsJsonAsync(
            $"/api/workspaces/{alice.PrimaryWorkspaceSlug}/invites",
            new { email = "newbie@example.com", role = "Member" });

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<CreateInviteResponse>(JsonOpts);
        body.Should().NotBeNull();
        body!.Token.Should().NotBeNullOrEmpty();
        body.Email.Should().Be("newbie@example.com");
        body.Role.Should().Be(WorkspaceRole.Member);
        body.ExpiresAt.Should().BeAfter(DateTime.UtcNow);
    }

    [Fact]
    public async Task Post_invite_403_for_member()
    {
        var alice = await RegisterUserAsync();
        var bob = await RegisterUserAsync();
        await SeedMembershipAsync(alice.PrimaryWorkspaceSlug, bob.UserId, WorkspaceRole.Member);

        var response = await bob.Client.PostAsJsonAsync(
            $"/api/workspaces/{alice.PrimaryWorkspaceSlug}/invites",
            new { email = "x@example.com", role = "Member" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Get_invites_lists_pending_only()
    {
        var alice = await RegisterUserAsync();

        // Create one invite.
        var create = await alice.Client.PostAsJsonAsync(
            $"/api/workspaces/{alice.PrimaryWorkspaceSlug}/invites",
            new { email = "pending@example.com", role = "Member" });
        create.StatusCode.Should().Be(HttpStatusCode.Created);

        var list = await alice.Client.GetAsync($"/api/workspaces/{alice.PrimaryWorkspaceSlug}/invites");
        list.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await list.Content.ReadFromJsonAsync<List<WorkspaceInviteItem>>(JsonOpts);
        body.Should().ContainSingle().Which.Email.Should().Be("pending@example.com");
    }

    [Fact]
    public async Task Delete_invite_removes_pending_invite()
    {
        var alice = await RegisterUserAsync();

        var create = await alice.Client.PostAsJsonAsync(
            $"/api/workspaces/{alice.PrimaryWorkspaceSlug}/invites",
            new { email = "revoke@example.com", role = "Member" });
        var created = await create.Content.ReadFromJsonAsync<CreateInviteResponse>(JsonOpts);

        var revoke = await alice.Client.DeleteAsync(
            $"/api/workspaces/{alice.PrimaryWorkspaceSlug}/invites/{created!.Id}");
        revoke.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Listing should now be empty.
        var list = await alice.Client.GetFromJsonAsync<List<WorkspaceInviteItem>>(
            $"/api/workspaces/{alice.PrimaryWorkspaceSlug}/invites", JsonOpts);
        list.Should().BeEmpty();
    }

    // -----------------------------------------------------------------------
    // POST /api/invites/accept (top-level)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Accept_invite_attaches_membership_when_email_matches()
    {
        var alice = await RegisterUserAsync();
        var bob = await RegisterUserAsync();

        // Alice invites Bob using *Bob's actual email*.
        var create = await alice.Client.PostAsJsonAsync(
            $"/api/workspaces/{alice.PrimaryWorkspaceSlug}/invites",
            new { email = bob.Email, role = "Admin" });
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await create.Content.ReadFromJsonAsync<CreateInviteResponse>(JsonOpts);

        // Bob accepts.
        var accept = await bob.Client.PostAsJsonAsync("/api/invites/accept", new { token = created!.Token });
        accept.StatusCode.Should().Be(HttpStatusCode.OK, await accept.Content.ReadAsStringAsync());
        var body = await accept.Content.ReadFromJsonAsync<AcceptInviteResponse>(JsonOpts);
        body.Should().NotBeNull();
        body!.Slug.Should().Be(alice.PrimaryWorkspaceSlug);
        body.Role.Should().Be(WorkspaceRole.Admin);

        // Verify Bob now sees the workspace in his picker.
        var picker = await bob.Client.GetFromJsonAsync<List<MyWorkspaceItem>>("/api/me/workspaces", JsonOpts);
        picker.Should().Contain(w => w.Slug == alice.PrimaryWorkspaceSlug);
    }

    [Fact]
    public async Task Accept_invite_400_on_email_mismatch()
    {
        var alice = await RegisterUserAsync();
        var bob = await RegisterUserAsync();

        var create = await alice.Client.PostAsJsonAsync(
            $"/api/workspaces/{alice.PrimaryWorkspaceSlug}/invites",
            new { email = "different-person@example.com", role = "Member" });
        var created = await create.Content.ReadFromJsonAsync<CreateInviteResponse>(JsonOpts);

        // Bob (different email) tries to accept.
        var accept = await bob.Client.PostAsJsonAsync("/api/invites/accept", new { token = created!.Token });
        accept.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await accept.Content.ReadAsStringAsync();
        body.Should().Contain("different email");
    }

    [Fact]
    public async Task Accept_invite_requires_authentication()
    {
        var alice = await RegisterUserAsync();
        var bob = await RegisterUserAsync();

        var create = await alice.Client.PostAsJsonAsync(
            $"/api/workspaces/{alice.PrimaryWorkspaceSlug}/invites",
            new { email = bob.Email, role = "Member" });
        var created = await create.Content.ReadFromJsonAsync<CreateInviteResponse>(JsonOpts);

        var anon = Factory.CreateClient();
        var accept = await anon.PostAsJsonAsync("/api/invites/accept", new { token = created!.Token });
        accept.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
