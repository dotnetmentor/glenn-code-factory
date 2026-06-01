using Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Source.Features.Users.Models;
using Source.Features.Workspaces.Commands;
using Source.Features.Workspaces.Models;
using Source.Features.Workspaces.Services;

namespace Api.Tests.Features.Workspaces;

public class CreateWorkspaceHandlerTests : HandlerTestBase
{
    private readonly CreateWorkspaceHandler _handler;

    public CreateWorkspaceHandlerTests()
    {
        var slugGen = new WorkspaceSlugGenerator(Context);
        _handler = new CreateWorkspaceHandler(Context, slugGen);
    }

    [Fact]
    public async Task Creates_workspace_owner_membership_and_event()
    {
        var owner = await SeedUser("owner-1");

        var result = await _handler.Handle(
            new CreateWorkspaceCommand(owner.Id, "Acme", Slug: null, SlugSeed: "acme.io"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Slug.Should().Be("acme-io");
        result.Value.Name.Should().Be("Acme");

        var workspace = await Context.Workspaces.Include(w => w.Memberships)
            .SingleAsync(w => w.Id == result.Value.Id);
        workspace.OwnerId.Should().Be(owner.Id);
        workspace.Memberships.Should().HaveCount(1);
        workspace.Memberships.Single().Role.Should().Be(WorkspaceRole.Owner);
        workspace.DomainEvents.Should().NotBeEmpty("a WorkspaceCreated event should be raised");
    }

    [Fact]
    public async Task Slug_collision_appends_numeric_suffix()
    {
        var owner1 = await SeedUser("u1");
        var owner2 = await SeedUser("u2");

        var first = await _handler.Handle(
            new CreateWorkspaceCommand(owner1.Id, "Acme", Slug: "acme", SlugSeed: null),
            CancellationToken.None);

        var second = await _handler.Handle(
            new CreateWorkspaceCommand(owner2.Id, "Acme", Slug: "acme", SlugSeed: null),
            CancellationToken.None);

        first.IsSuccess.Should().BeTrue();
        second.IsSuccess.Should().BeTrue();
        first.Value.Slug.Should().Be("acme");
        second.Value.Slug.Should().Be("acme-2");
    }

    [Fact]
    public async Task Fails_when_owner_user_does_not_exist()
    {
        var result = await _handler.Handle(
            new CreateWorkspaceCommand("ghost", "Acme", null, null),
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("Owner");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Fails_when_name_blank(string? name)
    {
        var owner = await SeedUser("u-blank");

        var result = await _handler.Handle(
            new CreateWorkspaceCommand(owner.Id, name!, null, "seed"),
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("Name");
    }

    private async Task<User> SeedUser(string id)
    {
        var user = new User { Id = id, UserName = $"{id}@x.com", Email = $"{id}@x.com" };
        Context.Users.Add(user);
        await Context.SaveChangesAsync();
        return user;
    }
}
