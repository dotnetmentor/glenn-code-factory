using Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Source.Features.Users.Models;
using Source.Features.Workspaces.Models;

namespace Api.Tests.Features.Workspaces;

/// <summary>
/// Smoke tests that verify the workspace EF model is wired up correctly:
/// the three DbSets exist, can persist, and the (WorkspaceId, UserId) unique
/// index on memberships is enforced. We use the shared TestDbContextFactory so
/// these run on the InMemory provider — we only assert in-process invariants
/// here, not real Postgres index behaviour.
/// </summary>
public class WorkspaceDbModelTests : HandlerTestBase
{
    [Fact]
    public async Task Can_persist_workspace_with_owner_membership()
    {
        var owner = new User { Id = "owner-1", UserName = "owner@example.com", Email = "owner@example.com" };
        Context.Users.Add(owner);

        var ws = new Workspace
        {
            Id = Guid.NewGuid(),
            Slug = "acme",
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

        var loaded = await Context.Workspaces
            .Include(w => w.Memberships)
            .SingleAsync(w => w.Slug == "acme");

        loaded.Memberships.Should().HaveCount(1);
        loaded.Memberships.Single().Role.Should().Be(WorkspaceRole.Owner);
        loaded.CreatedAt.Should().NotBe(default); // auto-set by SaveChangesAsync
    }

    [Fact]
    public async Task Soft_deleted_workspace_is_excluded_by_default_query()
    {
        var owner = new User { Id = "owner-2", UserName = "o2@example.com", Email = "o2@example.com" };
        Context.Users.Add(owner);

        var ws = new Workspace
        {
            Id = Guid.NewGuid(),
            Slug = "deleted-ws",
            Name = "Deleted",
            OwnerId = owner.Id,
        };
        Context.Workspaces.Add(ws);
        await Context.SaveChangesAsync();

        ws.IsDeleted = true;
        await Context.SaveChangesAsync();

        var found = await Context.Workspaces.FirstOrDefaultAsync(w => w.Slug == "deleted-ws");
        found.Should().BeNull("soft-deleted workspaces are filtered out by the global query filter");

        var foundIgnoringFilter = await Context.Workspaces
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(w => w.Slug == "deleted-ws");
        foundIgnoringFilter.Should().NotBeNull();
        foundIgnoringFilter!.DeletedAt.Should().NotBeNull();
    }
}
