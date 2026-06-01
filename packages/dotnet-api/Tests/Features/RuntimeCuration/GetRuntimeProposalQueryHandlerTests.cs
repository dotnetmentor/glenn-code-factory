using Api.Tests.Infrastructure;
using Source.Features.RuntimeCuration.Models;
using Source.Features.RuntimeCuration.Queries;

namespace Api.Tests.Features.RuntimeCuration;

/// <summary>
/// Handler-level coverage for <see cref="GetRuntimeProposalQueryHandler"/> —
/// the project-scoped single-proposal lookup. Mirrors the kanban GetCard
/// handler tests for the cross-project not_found semantics.
/// </summary>
public class GetRuntimeProposalQueryHandlerTests : HandlerTestBase
{
    private async Task<RuntimeProposal> SeedAsync(Guid projectId)
    {
        var proposal = new RuntimeProposal
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            RuntimeId = Guid.NewGuid(),
            Status = RuntimeProposalStatus.Pending,
            ProposedSpec = """{"languages":["node@22"],"services":["postgres"]}""",
            Reason = "test",
        };
        Context.RuntimeProposals.Add(proposal);
        await Context.SaveChangesAsync();
        return proposal;
    }

    [Fact]
    public async Task HappyPath_ReturnsDtoForOwnedProposal()
    {
        var projectId = Guid.NewGuid();
        var proposal = await SeedAsync(projectId);

        var handler = new GetRuntimeProposalQueryHandler(Context);
        var result = await handler.Handle(
            new GetRuntimeProposalQuery(projectId, proposal.Id),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Id.Should().Be(proposal.Id);
        result.Value.ProjectId.Should().Be(projectId);
        result.Value.RuntimeId.Should().Be(proposal.RuntimeId);
        result.Value.ProposedSpec.Should().Be(proposal.ProposedSpec);
        result.Value.Status.Should().Be(RuntimeProposalStatus.Pending);
    }

    [Fact]
    public async Task CrossProject_ReturnsNotFoundInsteadOfLeakingExistence()
    {
        var projectA = Guid.NewGuid();
        var projectB = Guid.NewGuid();
        var inB = await SeedAsync(projectB);

        var handler = new GetRuntimeProposalQueryHandler(Context);
        var result = await handler.Handle(
            new GetRuntimeProposalQuery(projectA, inB.Id), // wrong project
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("not_found");
    }

    [Fact]
    public async Task MissingProposalId_ReturnsNotFound()
    {
        var handler = new GetRuntimeProposalQueryHandler(Context);
        var result = await handler.Handle(
            new GetRuntimeProposalQuery(Guid.NewGuid(), Guid.NewGuid()),
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("not_found");
    }
}
