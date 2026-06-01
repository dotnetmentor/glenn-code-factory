using Api.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Source.Features.Cloudflare.Models;
using Source.Features.Projects.Commands.DeleteProject;
using Source.Features.Projects.Models;
using Source.Features.Workspaces.Models;

namespace Api.Tests.Features.Projects;

/// <summary>
/// cloudflare-tunnel-preview Phase 3: soft-deleting a project must flip every
/// subdomain still assigned to one of its branches to
/// <see cref="SubdomainStatus.Releasing"/>. Phase 4 picks up Releasing rows
/// and tears them down on Cloudflare. The destroy-and-never-reuse invariant
/// means the rows do NOT return to <see cref="SubdomainStatus.Available"/>.
///
/// <para>Branch deletion isn't a separate command in v1 — branches are not
/// soft-deletable on their own. The project tombstone is the logical delete
/// for every branch underneath it, so wiring the release-on-delete behaviour
/// here is the correct seam.</para>
/// </summary>
public class DeleteProjectHandlerSubdomainReleaseTests : HandlerTestBase
{
    private readonly Guid _workspaceId = Guid.NewGuid();
    private readonly string _adminUserId = Guid.NewGuid().ToString();

    private async Task<Project> SeedProjectWithBranchAsync(Guid? branchId = null)
    {
        // Workspace membership is the authorisation gate the handler runs
        // before touching project state — seed an Admin so the project
        // tombstone path is reachable in the test.
        Context.WorkspaceMemberships.Add(new WorkspaceMembership
        {
            Id = Guid.NewGuid(),
            WorkspaceId = _workspaceId,
            UserId = _adminUserId,
            Role = WorkspaceRole.Admin,
        });

        var project = new Project
        {
            Id = Guid.NewGuid(),
            WorkspaceId = _workspaceId,
            OwnerUserId = _adminUserId,
            Name = "Test Project",
            GithubRepoOwner = "owner",
            GithubRepoName = "repo",
            GithubInstallationId = Guid.NewGuid(),
        };
        Context.Projects.Add(project);

        var branch = new ProjectBranch
        {
            Id = branchId ?? Guid.NewGuid(),
            ProjectId = project.Id,
            Name = "main",
            IsDefault = true,
        };
        Context.ProjectBranches.Add(branch);

        await Context.SaveChangesAsync();
        return project;
    }

    private static SubdomainAssignment NewAssignment(
        Guid branchId,
        SubdomainStatus status = SubdomainStatus.Assigned) => new()
        {
            Id = Guid.NewGuid(),
            Hostname = $"{Guid.NewGuid():N}".Substring(0, 8) + ".glenncode.ai",
            Subdomain = $"{Guid.NewGuid():N}".Substring(0, 8),
            TunnelId = Guid.NewGuid().ToString(),
            TunnelToken = "encrypted-token",
            Status = status,
            AssignedBranchId = status == SubdomainStatus.Available ? null : branchId,
            AssignedAt = status == SubdomainStatus.Available ? null : DateTime.UtcNow,
        };

    [Fact]
    public async Task Soft_delete_flips_assigned_subdomains_to_releasing()
    {
        var branchId = Guid.NewGuid();
        var project = await SeedProjectWithBranchAsync(branchId);

        var assigned = NewAssignment(branchId);
        Context.SubdomainAssignments.Add(assigned);
        await Context.SaveChangesAsync();

        var handler = new DeleteProjectHandler(
            Context,
            NullLogger<DeleteProjectHandler>.Instance);

        var result = await handler.Handle(
            new DeleteProjectCommand(project.Id, _adminUserId),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        var reloaded = await Context.SubdomainAssignments.FindAsync(assigned.Id);
        reloaded!.Status.Should().Be(SubdomainStatus.Releasing);
        reloaded.AssignedBranchId.Should().BeNull("FK is cleared so the read-side projection stops showing a soft-deleted owner");
        reloaded.AssignedAt.Should().BeNull();
    }

    [Fact]
    public async Task Soft_delete_leaves_already_releasing_rows_alone()
    {
        var branchId = Guid.NewGuid();
        var project = await SeedProjectWithBranchAsync(branchId);

        // A previously-released subdomain (e.g. from a prior delete cycle on
        // a now-dead branch) should not be re-flipped or have its FK
        // mutated — Releasing is terminal until Phase 4 destroys the row.
        var alreadyReleasing = NewAssignment(branchId, SubdomainStatus.Releasing);
        alreadyReleasing.AssignedBranchId = null;
        alreadyReleasing.AssignedAt = null;
        Context.SubdomainAssignments.Add(alreadyReleasing);
        await Context.SaveChangesAsync();

        var handler = new DeleteProjectHandler(
            Context,
            NullLogger<DeleteProjectHandler>.Instance);

        var result = await handler.Handle(
            new DeleteProjectCommand(project.Id, _adminUserId),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        var reloaded = await Context.SubdomainAssignments.FindAsync(alreadyReleasing.Id);
        reloaded!.Status.Should().Be(SubdomainStatus.Releasing);
    }

    [Fact]
    public async Task Soft_delete_with_no_subdomains_succeeds_quietly()
    {
        var project = await SeedProjectWithBranchAsync();

        var handler = new DeleteProjectHandler(
            Context,
            NullLogger<DeleteProjectHandler>.Instance);

        var result = await handler.Handle(
            new DeleteProjectCommand(project.Id, _adminUserId),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }
}
