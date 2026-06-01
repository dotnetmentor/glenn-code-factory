using Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Source.Features.Users.Models;
using Source.Features.Workspaces.Commands;
using Source.Features.Workspaces.Models;
using Source.Features.Workspaces.Queries;
using Source.Infrastructure.Workspaces;

namespace Api.Tests.Features.Workspaces;

/// <summary>
/// Unit tests for the invite handlers. Covers token generation, expiry behaviour, accept-flow
/// invariants (email match, single-use), and the pending-only filter on the list query.
/// </summary>
public class InviteHandlersTests : HandlerTestBase
{
    private static FakeWorkspaceContext CtxFor(Workspace w, string userId, WorkspaceRole role = WorkspaceRole.Owner)
        => new(w.Id, w.Slug, role, isSuperAdmin: false, userId);

    // -----------------------------------------------------------------------
    // CreateInvite
    // -----------------------------------------------------------------------

    [Fact]
    public async Task CreateInvite_generates_unique_token_and_sets_7day_expiry()
    {
        var (ws, owner) = await SeedSoloWorkspace();
        var handler = new CreateInviteHandler(Context, CtxFor(ws, owner.Id));

        var before = DateTime.UtcNow;
        var result = await handler.Handle(new CreateInviteCommand("invitee@example.com", WorkspaceRole.Member), CancellationToken.None);
        var after = DateTime.UtcNow;

        result.IsSuccess.Should().BeTrue();
        result.Value.Token.Should().NotBeNullOrEmpty();
        result.Value.Token.Length.Should().BeGreaterThan(20, "32-byte tokens base64url-encode to >40 chars");

        // Token should be URL-safe (no '+', '/', '=').
        result.Value.Token.Should().NotContainAny("+", "/", "=");

        result.Value.ExpiresAt.Should().BeOnOrAfter(before.AddDays(7).AddSeconds(-1));
        result.Value.ExpiresAt.Should().BeOnOrBefore(after.AddDays(7).AddSeconds(1));
    }

    [Fact]
    public async Task CreateInvite_two_invites_for_different_emails_have_distinct_tokens()
    {
        var (ws, owner) = await SeedSoloWorkspace();
        var handler = new CreateInviteHandler(Context, CtxFor(ws, owner.Id));

        var a = await handler.Handle(new CreateInviteCommand("a@example.com", WorkspaceRole.Member), CancellationToken.None);
        var b = await handler.Handle(new CreateInviteCommand("b@example.com", WorkspaceRole.Member), CancellationToken.None);

        a.IsSuccess.Should().BeTrue();
        b.IsSuccess.Should().BeTrue();
        a.Value.Token.Should().NotBe(b.Value.Token);
    }

    [Fact]
    public async Task CreateInvite_refuses_duplicate_pending_invite_for_same_email()
    {
        var (ws, owner) = await SeedSoloWorkspace();
        var handler = new CreateInviteHandler(Context, CtxFor(ws, owner.Id));

        var first = await handler.Handle(new CreateInviteCommand("dup@example.com", WorkspaceRole.Member), CancellationToken.None);
        first.IsSuccess.Should().BeTrue();

        var second = await handler.Handle(new CreateInviteCommand("dup@example.com", WorkspaceRole.Admin), CancellationToken.None);
        second.IsSuccess.Should().BeFalse();
        second.Error.Should().Contain("pending invite already exists");
    }

    [Fact]
    public async Task CreateInvite_refuses_inviting_an_existing_member()
    {
        var (ws, owner) = await SeedSoloWorkspace();
        var existing = new User { Id = "existing-1", UserName = "exist@example.com", Email = "exist@example.com" };
        Context.Users.Add(existing);
        Context.WorkspaceMemberships.Add(new WorkspaceMembership
        {
            Id = Guid.NewGuid(), WorkspaceId = ws.Id, UserId = existing.Id, Role = WorkspaceRole.Member,
        });
        await Context.SaveChangesAsync();

        var handler = new CreateInviteHandler(Context, CtxFor(ws, owner.Id));
        var result = await handler.Handle(new CreateInviteCommand("exist@example.com", WorkspaceRole.Member), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("already a member");
    }

    // -----------------------------------------------------------------------
    // GetWorkspaceInvites
    // -----------------------------------------------------------------------

    [Fact]
    public async Task GetWorkspaceInvites_returns_only_pending_un_expired()
    {
        var (ws, owner) = await SeedSoloWorkspace();
        var now = DateTime.UtcNow;

        Context.WorkspaceInvites.Add(MakeInvite(ws.Id, "pending@x.com", expiresAt: now.AddDays(3))); // pending
        Context.WorkspaceInvites.Add(MakeInvite(ws.Id, "expired@x.com", expiresAt: now.AddDays(-1))); // expired
        Context.WorkspaceInvites.Add(MakeInvite(ws.Id, "accepted@x.com", expiresAt: now.AddDays(3), acceptedAt: now.AddHours(-1))); // accepted
        await Context.SaveChangesAsync();

        var handler = new GetWorkspaceInvitesHandler(Context, CtxFor(ws, owner.Id));
        var result = await handler.Handle(new GetWorkspaceInvitesQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().ContainSingle();
        result.Value[0].Email.Should().Be("pending@x.com");
    }

    // -----------------------------------------------------------------------
    // RevokeInvite
    // -----------------------------------------------------------------------

    [Fact]
    public async Task RevokeInvite_deletes_a_pending_invite()
    {
        var (ws, owner) = await SeedSoloWorkspace();
        var invite = MakeInvite(ws.Id, "x@x.com", expiresAt: DateTime.UtcNow.AddDays(2));
        Context.WorkspaceInvites.Add(invite);
        await Context.SaveChangesAsync();

        var handler = new RevokeInviteHandler(Context, CtxFor(ws, owner.Id));
        var result = await handler.Handle(new RevokeInviteCommand(invite.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var stillThere = await Context.WorkspaceInvites.AnyAsync(i => i.Id == invite.Id);
        stillThere.Should().BeFalse();
    }

    [Fact]
    public async Task RevokeInvite_refuses_already_accepted_invite()
    {
        var (ws, owner) = await SeedSoloWorkspace();
        var accepted = MakeInvite(ws.Id, "x@x.com", expiresAt: DateTime.UtcNow.AddDays(2), acceptedAt: DateTime.UtcNow);
        Context.WorkspaceInvites.Add(accepted);
        await Context.SaveChangesAsync();

        var handler = new RevokeInviteHandler(Context, CtxFor(ws, owner.Id));
        var result = await handler.Handle(new RevokeInviteCommand(accepted.Id), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("already been accepted");
    }

    // -----------------------------------------------------------------------
    // AcceptInvite (top-level — no workspace context)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task AcceptInvite_creates_membership_and_marks_invite_accepted()
    {
        var (ws, _) = await SeedSoloWorkspace();
        var invitee = await SeedUser("invitee@example.com");
        var invite = MakeInvite(ws.Id, invitee.Email!, expiresAt: DateTime.UtcNow.AddDays(2));
        Context.WorkspaceInvites.Add(invite);
        await Context.SaveChangesAsync();

        var handler = BuildAcceptHandler();
        var result = await handler.Handle(new AcceptInviteCommand(invitee.Id, invite.Token), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Slug.Should().Be(ws.Slug);
        result.Value.Role.Should().Be(WorkspaceRole.Member);

        // Membership created.
        var membership = await Context.WorkspaceMemberships
            .SingleAsync(m => m.WorkspaceId == ws.Id && m.UserId == invitee.Id);
        membership.Role.Should().Be(WorkspaceRole.Member);

        // Invite marked accepted.
        var refreshedInvite = await Context.WorkspaceInvites.SingleAsync(i => i.Id == invite.Id);
        refreshedInvite.AcceptedAt.Should().NotBeNull();
        refreshedInvite.AcceptedByUserId.Should().Be(invitee.Id);
    }

    [Fact]
    public async Task AcceptInvite_rejects_email_mismatch()
    {
        var (ws, _) = await SeedSoloWorkspace();
        var stranger = await SeedUser("stranger@example.com");
        var invite = MakeInvite(ws.Id, "intended@example.com", expiresAt: DateTime.UtcNow.AddDays(2));
        Context.WorkspaceInvites.Add(invite);
        await Context.SaveChangesAsync();

        var handler = BuildAcceptHandler();
        var result = await handler.Handle(new AcceptInviteCommand(stranger.Id, invite.Token), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("different email");

        var membership = await Context.WorkspaceMemberships.AnyAsync(m => m.WorkspaceId == ws.Id && m.UserId == stranger.Id);
        membership.Should().BeFalse("the stranger must not have been added");
    }

    [Fact]
    public async Task AcceptInvite_rejects_expired_invite()
    {
        var (ws, _) = await SeedSoloWorkspace();
        var invitee = await SeedUser("late@example.com");
        var invite = MakeInvite(ws.Id, invitee.Email!, expiresAt: DateTime.UtcNow.AddDays(-1));
        Context.WorkspaceInvites.Add(invite);
        await Context.SaveChangesAsync();

        var handler = BuildAcceptHandler();
        var result = await handler.Handle(new AcceptInviteCommand(invitee.Id, invite.Token), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("expired");
    }

    [Fact]
    public async Task AcceptInvite_rejects_already_accepted_invite()
    {
        var (ws, _) = await SeedSoloWorkspace();
        var invitee = await SeedUser("again@example.com");
        var invite = MakeInvite(ws.Id, invitee.Email!, expiresAt: DateTime.UtcNow.AddDays(2), acceptedAt: DateTime.UtcNow.AddHours(-1));
        Context.WorkspaceInvites.Add(invite);
        await Context.SaveChangesAsync();

        var handler = BuildAcceptHandler();
        var result = await handler.Handle(new AcceptInviteCommand(invitee.Id, invite.Token), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("already been accepted");
    }

    [Fact]
    public async Task AcceptInvite_rejects_unknown_token()
    {
        var (ws, _) = await SeedSoloWorkspace();
        var invitee = await SeedUser("nobody@example.com");

        var handler = BuildAcceptHandler();
        var result = await handler.Handle(new AcceptInviteCommand(invitee.Id, "totally-not-a-token-12345"), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("not found");
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private async Task<(Workspace, User)> SeedSoloWorkspace()
    {
        var owner = await SeedUser("owner@example.com");
        var ws = new Workspace
        {
            Id = Guid.NewGuid(),
            Slug = $"ws-{Guid.NewGuid():N}".Substring(0, 20),
            Name = "Acme",
            OwnerId = owner.Id,
        };
        Context.Workspaces.Add(ws);
        Context.WorkspaceMemberships.Add(new WorkspaceMembership
        {
            Id = Guid.NewGuid(),
            WorkspaceId = ws.Id,
            UserId = owner.Id,
            Role = WorkspaceRole.Owner,
        });
        await Context.SaveChangesAsync();
        return (ws, owner);
    }

    private async Task<User> SeedUser(string email)
    {
        var user = new User { Id = $"u-{Guid.NewGuid():N}", UserName = email, Email = email };
        Context.Users.Add(user);
        await Context.SaveChangesAsync();
        return user;
    }

    private static WorkspaceInvite MakeInvite(Guid workspaceId, string email, DateTime expiresAt, DateTime? acceptedAt = null)
        => new()
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            Email = email.ToLowerInvariant(),
            Role = WorkspaceRole.Member,
            InvitedById = "owner-test",
            Token = $"tok-{Guid.NewGuid():N}",
            ExpiresAt = expiresAt,
            AcceptedAt = acceptedAt,
            AcceptedByUserId = acceptedAt is null ? null : "some-user",
        };

    /// <summary>
    /// Build an <see cref="AcceptInviteHandler"/> with a real <see cref="UserManager{TUser}"/>
    /// hooked up to the in-memory <see cref="Context"/>. We need a real UserManager because the
    /// handler calls <c>FindByIdAsync</c>.
    /// </summary>
    private AcceptInviteHandler BuildAcceptHandler()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Context);
        services.AddIdentity<User, Microsoft.AspNetCore.Identity.IdentityRole>()
            .AddEntityFrameworkStores<Source.Infrastructure.ApplicationDbContext>()
            .AddDefaultTokenProviders();

        // Identity expects an IHttpContextAccessor in some code paths; the in-memory tests do not
        // exercise those, but the DI container needs the registration to succeed.
        services.AddHttpContextAccessor();

        var sp = services.BuildServiceProvider();
        var userManager = sp.GetRequiredService<UserManager<User>>();
        return new AcceptInviteHandler(Context, userManager);
    }

    /// <summary>Stub <see cref="IWorkspaceContext"/> — handlers under test only read it.</summary>
    private sealed class FakeWorkspaceContext : IWorkspaceContext
    {
        public FakeWorkspaceContext(Guid id, string slug, WorkspaceRole role, bool isSuperAdmin, string userId)
        {
            Id = id;
            Slug = slug;
            Role = role;
            IsSuperAdmin = isSuperAdmin;
            UserId = userId;
            IsResolved = true;
        }

        public bool IsResolved { get; }
        public Guid Id { get; }
        public string Slug { get; }
        public WorkspaceRole Role { get; }
        public bool IsSuperAdmin { get; }
        public string UserId { get; }
    }
}
