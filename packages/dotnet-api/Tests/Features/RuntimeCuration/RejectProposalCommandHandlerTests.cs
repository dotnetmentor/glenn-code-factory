using Api.Tests.Infrastructure;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Source.Features.RuntimeCuration.Commands;
using Source.Features.RuntimeCuration.Models;
using Source.Features.RuntimeLifecycle.Models;
using Source.Features.SignalR.Hubs;

namespace Api.Tests.Features.RuntimeCuration;

public class RejectProposalCommandHandlerTests : HandlerTestBase
{
    private readonly Mock<IHubClients<IAgentClient>> _agentClients = new();
    private readonly Mock<IHubContext<AgentHub, IAgentClient>> _agentHub = new();
    private readonly Mock<IAgentClient> _agentGroupClient = new();

    public RejectProposalCommandHandlerTests()
    {
        _agentHub.SetupGet(h => h.Clients).Returns(_agentClients.Object);
        _agentClients.Setup(c => c.Group(It.IsAny<string>())).Returns(_agentGroupClient.Object);
    }

    private RejectProposalCommandHandler CreateHandler() => new(
        Context,
        _agentHub.Object,
        NullLogger<RejectProposalCommandHandler>.Instance);

    private async Task<RuntimeProposal> SeedAsync(
        RuntimeProposalStatus status = RuntimeProposalStatus.Pending)
    {
        var pid = Guid.NewGuid();
        var runtime = new ProjectRuntime
        {
            Id = Guid.NewGuid(),
            ProjectId = pid,
            Region = "arn",
        };
        Context.ProjectRuntimes.Add(runtime);

        var proposal = new RuntimeProposal
        {
            Id = Guid.NewGuid(),
            ProjectId = pid,
            RuntimeId = runtime.Id,
            Status = status,
            ProposedSpec = """{"languages":["node"],"services":[]}""",
        };
        Context.RuntimeProposals.Add(proposal);
        await Context.SaveChangesAsync();
        return proposal;
    }

    [Fact]
    public async Task HappyPath_FlipsToRejected_BroadcastsOnly_NoDaemonPush()
    {
        var proposal = await SeedAsync();

        // We DON'T configure a runtime hub mock — verify Reject never even
        // resolves an IRuntimeClient. The handler doesn't take one anyway.
        RuntimeProposalUpdatedPayload? captured = null;
        _agentGroupClient
            .Setup(c => c.RuntimeProposalUpdated(It.IsAny<RuntimeProposalUpdatedPayload>()))
            .Callback<RuntimeProposalUpdatedPayload>(p => captured = p)
            .Returns(Task.CompletedTask);

        var handler = CreateHandler();
        var result = await handler.Handle(
            new RejectProposalCommand(proposal.ProjectId, proposal.Id, "user-42"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(RuntimeProposalStatus.Rejected);
        result.Value.AppliedSpec.Should().BeNull("Reject never sets AppliedSpec");

        Context.ChangeTracker.Clear();
        var reloaded = Context.RuntimeProposals.Single(p => p.Id == proposal.Id);
        reloaded.Status.Should().Be(RuntimeProposalStatus.Rejected);
        reloaded.DecidedBy.Should().Be("user-42");
        reloaded.DecidedAt.Should().NotBeNull();
        reloaded.AppliedSpec.Should().BeNull();

        _agentClients.Verify(c => c.Group($"project-{proposal.ProjectId}"), Times.Once);
        captured.Should().NotBeNull();
        captured!.Status.Should().Be(RuntimeProposalStatus.Rejected);
    }

    [Fact]
    public async Task AlreadyDecided_ReturnsAlreadyDecided()
    {
        var proposal = await SeedAsync(status: RuntimeProposalStatus.Approved);

        var handler = CreateHandler();
        var result = await handler.Handle(
            new RejectProposalCommand(proposal.ProjectId, proposal.Id, "user-42"),
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("already_decided");

        _agentGroupClient.Verify(
            c => c.RuntimeProposalUpdated(It.IsAny<RuntimeProposalUpdatedPayload>()), Times.Never);
    }
}
