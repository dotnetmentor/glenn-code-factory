using Api.Tests.Infrastructure;
using Source.Features.Users.Models;
using Source.Features.Workspaces.Models;
using Source.Features.Workspaces.Services;

namespace Api.Tests.Features.Workspaces;

public class WorkspaceSlugGeneratorTests : HandlerTestBase
{
    [Theory]
    [InlineData("john.doe@example.com", "john-doe")]
    [InlineData("Andreas Müller", "andreas-muller")]
    [InlineData("  spaces  inside  ", "spaces-inside")]
    [InlineData("ALL_CAPS_USER", "all-caps-user")]
    [InlineData("emoji 🎉 stripped", "emoji-stripped")]
    [InlineData("---leading-and-trailing---", "leading-and-trailing")]
    [InlineData("123-numbers-ok", "123-numbers-ok")]
    public void Sanitize_produces_expected_slug(string input, string expected)
    {
        WorkspaceSlugGenerator.Sanitize(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("@@@")]
    public void Sanitize_returns_empty_for_unrecoverable_input(string input)
    {
        WorkspaceSlugGenerator.Sanitize(input).Should().Be(string.Empty);
    }

    [Fact]
    public async Task Generate_returns_base_slug_when_unused()
    {
        var generator = new WorkspaceSlugGenerator(Context);

        var slug = await generator.GenerateAsync("john.doe@example.com");

        slug.Should().Be("john-doe");
    }

    [Fact]
    public async Task Generate_appends_numeric_suffix_on_collision()
    {
        // Pre-seed "john-doe" so the generator must pick "john-doe-2".
        await SeedWorkspace("john-doe");

        var generator = new WorkspaceSlugGenerator(Context);
        var slug = await generator.GenerateAsync("john.doe@example.com");

        slug.Should().Be("john-doe-2");
    }

    [Fact]
    public async Task Generate_keeps_incrementing_until_unique()
    {
        await SeedWorkspace("alex");
        await SeedWorkspace("alex-2");
        await SeedWorkspace("alex-3");

        var generator = new WorkspaceSlugGenerator(Context);
        var slug = await generator.GenerateAsync("alex");

        slug.Should().Be("alex-4");
    }

    [Fact]
    public async Task Generate_falls_back_to_workspace_when_seed_sanitises_to_empty()
    {
        var generator = new WorkspaceSlugGenerator(Context);

        var slug = await generator.GenerateAsync("@@@");

        slug.Should().Be("workspace");
    }

    [Fact]
    public async Task Generate_treats_soft_deleted_slugs_as_taken()
    {
        var ws = await SeedWorkspace("ghost");
        ws.IsDeleted = true;
        await Context.SaveChangesAsync();

        var generator = new WorkspaceSlugGenerator(Context);
        var slug = await generator.GenerateAsync("ghost");

        slug.Should().Be("ghost-2", "soft-deleted slugs must not be silently resurrected");
    }

    private async Task<Workspace> SeedWorkspace(string slug)
    {
        var owner = new User { Id = Guid.NewGuid().ToString(), UserName = $"{slug}@x.com", Email = $"{slug}@x.com" };
        Context.Users.Add(owner);
        var ws = new Workspace { Id = Guid.NewGuid(), Slug = slug, Name = slug, OwnerId = owner.Id };
        Context.Workspaces.Add(ws);
        await Context.SaveChangesAsync();
        return ws;
    }
}
