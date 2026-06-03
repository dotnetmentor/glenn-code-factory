using Api.Tests.Infrastructure;
using Source.Features.Projects.Models;
using Source.Features.ProjectSecrets.Models;
using Source.Features.ProjectSecrets.Queries;
using Source.Features.RuntimeBootstrap.Contracts;
using Source.Features.RuntimeCuration.Models;
using Source.Features.RuntimeCuration.Services;

namespace Api.Tests.Features.ProjectSecrets;

/// <summary>
/// Coverage for <see cref="GetProjectEnvStatusSummaryQueryHandler"/> — the
/// cross-branch rollup that powers the sidebar branch dots and the
/// settings-cog badge. Required is project-level (same for every branch); only
/// the branch-effective present set differs, so a key set project-wide
/// satisfies every branch while a branch override only satisfies that branch.
/// </summary>
public class GetProjectEnvStatusSummaryQueryHandlerTests
{
    private readonly string _dbName = Guid.NewGuid().ToString();

    private static ProjectSecret Secret(Guid projectId, Guid? branchId, string key) => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = projectId,
        BranchId = branchId,
        Key = key,
        Ciphertext = Array.Empty<byte>(),
        Nonce = Array.Empty<byte>(),
    };

    private static ProjectBranch Branch(Guid projectId, Guid id, string name) => new()
    {
        Id = id,
        ProjectId = projectId,
        Name = name,
    };

    private static string SpecWithRequired(params string[] keys)
    {
        var spec = new RuntimeSpecV2
        {
            Services = new List<ServiceSpec>
            {
                new()
                {
                    Name = "api",
                    Command = "dotnet run",
                    RequiredEnv = keys
                        .Select(k => new RequiredEnvVar { Key = k, Required = true })
                        .ToList(),
                },
            },
        };
        return spec.ToJson();
    }

    [Fact]
    public async Task Rolls_up_missing_required_vars_per_branch()
    {
        var projectId = Guid.NewGuid();
        var branchMissing = Guid.NewGuid();
        var branchSatisfied = Guid.NewGuid();

        await using (var seed = TestDbContextFactory.Create(_dbName))
        {
            seed.RuntimeProposals.Add(new RuntimeProposal
            {
                Id = Guid.NewGuid(),
                ProjectId = projectId,
                RuntimeId = Guid.NewGuid(),
                Status = RuntimeProposalStatus.Approved,
                ProposedSpec = "{}",
                ExpandedSpec = SpecWithRequired("API_KEY", "QUEUE_URL"),
                DecidedAt = DateTime.UtcNow,
            });

            // API_KEY set project-wide → satisfied on every branch.
            seed.ProjectSecrets.Add(Secret(projectId, null, "API_KEY"));
            // QUEUE_URL overridden ONLY on branchSatisfied.
            seed.ProjectSecrets.Add(Secret(projectId, branchSatisfied, "QUEUE_URL"));

            seed.ProjectBranches.Add(Branch(projectId, branchMissing, "feature/x"));
            seed.ProjectBranches.Add(Branch(projectId, branchSatisfied, "main"));
            await seed.SaveChangesAsync();
        }

        await using var ctx = TestDbContextFactory.Create(_dbName);
        var handler = new GetProjectEnvStatusSummaryQueryHandler(
            ctx, new CurrentExpandedSpecResolver(ctx));

        var result = await handler.Handle(
            new GetProjectEnvStatusSummaryQuery(projectId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var summary = result.Value;
        summary.RequiredCount.Should().Be(2);
        summary.BranchesWithMissing.Should().Be(1);

        var missing = summary.Branches.Single(b => b.BranchId == branchMissing);
        missing.MissingCount.Should().Be(1);
        missing.MissingKeys.Should().Equal("QUEUE_URL");

        var satisfied = summary.Branches.Single(b => b.BranchId == branchSatisfied);
        satisfied.MissingCount.Should().Be(0);
        satisfied.MissingKeys.Should().BeEmpty();
    }

    [Fact]
    public async Task No_terminal_proposal_yields_empty_summary()
    {
        var projectId = Guid.NewGuid();
        await using (var seed = TestDbContextFactory.Create(_dbName))
        {
            seed.ProjectBranches.Add(Branch(projectId, Guid.NewGuid(), "main"));
            await seed.SaveChangesAsync();
        }

        await using var ctx = TestDbContextFactory.Create(_dbName);
        var handler = new GetProjectEnvStatusSummaryQueryHandler(
            ctx, new CurrentExpandedSpecResolver(ctx));

        var result = await handler.Handle(
            new GetProjectEnvStatusSummaryQuery(projectId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.RequiredCount.Should().Be(0);
        result.Value.BranchesWithMissing.Should().Be(0);
        result.Value.Branches.Should().BeEmpty();
    }
}
