using Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Source.Features.Users.Models;
using Source.Features.Workspaces.Commands;
using Source.Features.Workspaces.Models;
using Source.Features.Workspaces.Queries;
using Source.Infrastructure.Workspaces;

namespace Api.Tests.Features.Workspaces;

/// <summary>
/// Unit tests for the membership-management handlers (ChangeMemberRole, RemoveMember, GetWorkspaceMembers).
/// Focuses on the business invariants — last-Owner protection above all — that the integration
/// tests cannot easily assert without contorting fixtures.
/// </summary>
public class MembershipHandlersTests : HandlerTestBase
{
    private static FakeWorkspaceContext CtxFor(Workspace w, string userId, WorkspaceRole role = WorkspaceRole.Owner)
        => new(w.Id, w.Slug, role, isSuperAdmin: false, userId);

    // -----------------------------------------------------------------------
    // ChangeMemberRole
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ChangeMemberRole_promotes_member_to_admin()
    {
        var (ws, owner, member) = await SeedWorkspaceWithMember(memberRole: WorkspaceRole.Member);
        var handler = new ChangeMemberRoleHandler(Context, CtxFor(ws, owner.Id));

        var result = await handler.Handle(new ChangeMemberRoleCommand(member.Id, WorkspaceRole.Admin), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Role.Should().Be(WorkspaceRole.Admin);
        var refreshed = await Context.WorkspaceMemberships.SingleAsync(m => m.UserId == member.Id);
        refreshed.Role.Should().Be(WorkspaceRole.Admin);
    }

    [Fact]
    public async Task ChangeMemberRole_refuses_to_demote_last_owner()
    {
        var (ws, owner, _) = await SeedWorkspaceWithMember(memberRole: WorkspaceRole.Member);
        var handler = new ChangeMemberRoleHandler(Context, CtxFor(ws, owner.Id));

        var result = await handler.Handle(new ChangeMemberRoleCommand(owner.Id, WorkspaceRole.Admin), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("last Owner");

        var unchanged = await Context.WorkspaceMemberships.SingleAsync(m => m.UserId == owner.Id);
        unchanged.Role.Should().Be(WorkspaceRole.Owner, "the role must not have been mutated");
    }

    [Fact]
    public async Task ChangeMemberRole_allows_owner_demotion_when_a_second_owner_exists()
    {
        var (ws, owner1, owner2) = await SeedWorkspaceWithMember(memberRole: WorkspaceRole.Owner);
        var handler = new ChangeMemberRoleHandler(Context, CtxFor(ws, owner1.Id));

        var result = await handler.Handle(new ChangeMemberRoleCommand(owner1.Id, WorkspaceRole.Admin), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var owner1Membership = await Context.WorkspaceMemberships.SingleAsync(m => m.UserId == owner1.Id);
        owner1Membership.Role.Should().Be(WorkspaceRole.Admin);
        // The other owner must still be there.
        var owner2Membership = await Context.WorkspaceMemberships.SingleAsync(m => m.UserId == owner2.Id);
        owner2Membership.Role.Should().Be(WorkspaceRole.Owner);
    }

    [Fact]
    public async Task ChangeMemberRole_promoting_to_owner_updates_workspace_owner_pointer()
    {
        var (ws, owner, member) = await SeedWorkspaceWithMember(memberRole: WorkspaceRole.Admin);
        var handler = new ChangeMemberRoleHandler(Context, CtxFor(ws, owner.Id));

        var result = await handler.Handle(new ChangeMemberRoleCommand(member.Id, WorkspaceRole.Owner), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var refreshed = await Context.Workspaces.SingleAsync(w => w.Id == ws.Id);
        refreshed.OwnerId.Should().Be(member.Id, "promoting to Owner should update the denormalised pointer");
    }

    [Fact]
    public async Task ChangeMemberRole_returns_failure_if_target_not_a_member()
    {
        var (ws, owner, _) = await SeedWorkspaceWithMember();
        var handler = new ChangeMemberRoleHandler(Context, CtxFor(ws, owner.Id));

        var result = await handler.Handle(new ChangeMemberRoleCommand("ghost", WorkspaceRole.Admin), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("not found");
    }

    // -----------------------------------------------------------------------
    // RemoveMember
    // -----------------------------------------------------------------------

    [Fact]
    public async Task RemoveMember_removes_a_regular_member()
    {
        var (ws, owner, member) = await SeedWorkspaceWithMember(memberRole: WorkspaceRole.Member);
        var handler = new RemoveMemberHandler(Context, CtxFor(ws, owner.Id));

        var result = await handler.Handle(new RemoveMemberCommand(member.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var remaining = await Context.WorkspaceMemberships.Where(m => m.WorkspaceId == ws.Id).ToListAsync();
        remaining.Should().ContainSingle().Which.UserId.Should().Be(owner.Id);
    }

    [Fact]
    public async Task RemoveMember_refuses_to_remove_last_owner()
    {
        var (ws, owner, _) = await SeedWorkspaceWithMember(memberRole: WorkspaceRole.Member);
        var handler = new RemoveMemberHandler(Context, CtxFor(ws, owner.Id));

        var result = await handler.Handle(new RemoveMemberCommand(owner.Id), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("last Owner");

        var stillThere = await Context.WorkspaceMemberships.AnyAsync(m => m.UserId == owner.Id);
        stillThere.Should().BeTrue();
    }

    [Fact]
    public async Task RemoveMember_allows_removing_an_owner_when_multiple_owners_exist()
    {
        var (ws, owner1, owner2) = await SeedWorkspaceWithMember(memberRole: WorkspaceRole.Owner);
        var handler = new RemoveMemberHandler(Context, CtxFor(ws, owner1.Id));

        var result = await handler.Handle(new RemoveMemberCommand(owner2.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var remaining = await Context.WorkspaceMemberships.Where(m => m.WorkspaceId == ws.Id).ToListAsync();
        remaining.Should().ContainSingle().Which.UserId.Should().Be(owner1.Id);
    }

    [Fact]
    public async Task RemoveMember_returns_failure_if_target_not_a_member()
    {
        var (ws, owner, _) = await SeedWorkspaceWithMember();
        var handler = new RemoveMemberHandler(Context, CtxFor(ws, owner.Id));

        var result = await handler.Handle(new RemoveMemberCommand("ghost"), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("not found");
    }

    // -----------------------------------------------------------------------
    // GetWorkspaceMembers
    // -----------------------------------------------------------------------

    [Fact]
    public async Task GetWorkspaceMembers_returns_all_members_with_owner_flag()
    {
        var (ws, owner, member) = await SeedWorkspaceWithMember(memberRole: WorkspaceRole.Member);
        var handler = new GetWorkspaceMembersHandler(Context, CtxFor(ws, owner.Id));

        var result = await handler.Handle(new GetWorkspaceMembersQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(2);
        result.Value.Should().ContainSingle(m => m.UserId == owner.Id && m.IsOwner && m.Role == WorkspaceRole.Owner);
        result.Value.Should().ContainSingle(m => m.UserId == member.Id && !m.IsOwner && m.Role == WorkspaceRole.Member);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Seeds a workspace with two members: an Owner and a second user at <paramref name="memberRole"/>.
    /// Returns (workspace, owner, second-user).
    /// </summary>
    private async Task<(Workspace, User, User)> SeedWorkspaceWithMember(WorkspaceRole memberRole = WorkspaceRole.Member)
    {
        var owner = new User { Id = $"owner-{Guid.NewGuid():N}", UserName = "owner@x.com", Email = "owner@x.com" };
        var second = new User { Id = $"member-{Guid.NewGuid():N}", UserName = "second@x.com", Email = "second@x.com" };
        Context.Users.Add(owner);
        Context.Users.Add(second);

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
        Context.WorkspaceMemberships.Add(new WorkspaceMembership
        {
            Id = Guid.NewGuid(),
            WorkspaceId = ws.Id,
            UserId = second.Id,
            Role = memberRole,
        });

        await Context.SaveChangesAsync();
        return (ws, owner, second);
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
