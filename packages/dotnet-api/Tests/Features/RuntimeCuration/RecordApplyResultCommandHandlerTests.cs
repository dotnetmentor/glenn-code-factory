using Api.Tests.Infrastructure;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Source.Features.RuntimeCuration.Commands;
using Source.Features.RuntimeCuration.Models;
using Source.Features.RuntimeLifecycle.Models;
using Source.Features.SignalR.Hubs;

namespace Api.Tests.Features.RuntimeCuration;

public class RecordApplyResultCommandHandlerTests : HandlerTestBase
{
    private readonly Mock<IHubClients<IAgentClient>> _agentClients = new();
    private readonly Mock<IHubContext<AgentHub, IAgentClient>> _agentHub = new();
    private readonly Mock<IAgentClient> _agentGroupClient = new();

    public RecordApplyResultCommandHandlerTests()
    {
        _agentHub.SetupGet(h => h.Clients).Returns(_agentClients.Object);
        _agentClients.Setup(c => c.Group(It.IsAny<string>())).Returns(_agentGroupClient.Object);
    }

    private RecordApplyResultCommandHandler CreateHandler() => new(
        Context,
        _agentHub.Object,
        NullLogger<RecordApplyResultCommandHandler>.Instance);

    private async Task<RuntimeProposal> SeedAsync(
        RuntimeProposalStatus status = RuntimeProposalStatus.Approved,
        string? appliedSpec = """{"languages":["node@22"],"services":["postgres"]}""")
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
            ProposedSpec = """{"languages":["node@22"],"services":["postgres"]}""",
            AppliedSpec = appliedSpec,
            DecidedBy = "user-42",
            DecidedAt = DateTime.UtcNow,
        };
        Context.RuntimeProposals.Add(proposal);
        await Context.SaveChangesAsync();
        return proposal;
    }

    // ----------------------------------------------------------------------

    [Fact]
    public async Task SuccessAck_FlipsToApplied_ClearsErrorMessage_Broadcasts()
    {
        var proposal = await SeedAsync();
        // Simulate a previous failure leaving an ErrorMessage on the row — a
        // success ack must clear it.
        Context.RuntimeProposals.Single(p => p.Id == proposal.Id).ErrorMessage = "previous error";
        await Context.SaveChangesAsync();
        Context.ChangeTracker.Clear();

        RuntimeProposalUpdatedPayload? captured = null;
        _agentGroupClient
            .Setup(c => c.RuntimeProposalUpdated(It.IsAny<RuntimeProposalUpdatedPayload>()))
            .Callback<RuntimeProposalUpdatedPayload>(p => captured = p)
            .Returns(Task.CompletedTask);

        var handler = CreateHandler();
        var result = await handler.Handle(
            new RecordApplyResultCommand(
                RuntimeId: proposal.RuntimeId,
                ProjectId: proposal.ProjectId,
                Payload: new RuntimeSpecDeltaApplyResultPayload(
                    ProposalId: proposal.Id,
                    Success: true,
                    Error: null)),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        Context.ChangeTracker.Clear();
        var reloaded = Context.RuntimeProposals.Single(p => p.Id == proposal.Id);
        reloaded.Status.Should().Be(RuntimeProposalStatus.Applied);
        reloaded.ErrorMessage.Should().BeNull();

        captured.Should().NotBeNull();
        captured!.Status.Should().Be(RuntimeProposalStatus.Applied);
    }

    [Fact]
    public async Task FailureAck_FlipsToFailed_PersistsErrorMessage_Broadcasts()
    {
        var proposal = await SeedAsync();

        var handler = CreateHandler();
        var result = await handler.Handle(
            new RecordApplyResultCommand(
                RuntimeId: proposal.RuntimeId,
                ProjectId: proposal.ProjectId,
                Payload: new RuntimeSpecDeltaApplyResultPayload(
                    ProposalId: proposal.Id,
                    Success: false,
                    Error: "mise install failed: network unreachable")),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        Context.ChangeTracker.Clear();
        var reloaded = Context.RuntimeProposals.Single(p => p.Id == proposal.Id);
        reloaded.Status.Should().Be(RuntimeProposalStatus.Failed);
        reloaded.ErrorMessage.Should().Be("mise install failed: network unreachable");

        _agentGroupClient.Verify(
            c => c.RuntimeProposalUpdated(It.Is<RuntimeProposalUpdatedPayload>(p =>
                p.Status == RuntimeProposalStatus.Failed &&
                p.ErrorMessage == "mise install failed: network unreachable")),
            Times.Once);
    }

    [Fact]
    public async Task UnknownProposal_IsIdempotent_LogsAndReturnsSuccess()
    {
        // Daemon may replay an ack across a server restart that lost the row —
        // we don't want to crash the hub on a stale ack.
        var handler = CreateHandler();
        var result = await handler.Handle(
            new RecordApplyResultCommand(
                RuntimeId: Guid.NewGuid(),
                ProjectId: Guid.NewGuid(),
                Payload: new RuntimeSpecDeltaApplyResultPayload(
                    ProposalId: Guid.NewGuid(),
                    Success: true,
                    Error: null)),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue("idempotent: no row → no-op success");
        _agentGroupClient.Verify(
            c => c.RuntimeProposalUpdated(It.IsAny<RuntimeProposalUpdatedPayload>()),
            Times.Never);
    }

    [Fact]
    public async Task RuntimeMismatch_LogsAndReturnsSuccess_WithoutMutation()
    {
        // Defensive: a stale daemon's ack referencing a peer's proposal id.
        // Must not crash; must not write; must not broadcast.
        var proposal = await SeedAsync();

        var handler = CreateHandler();
        var result = await handler.Handle(
            new RecordApplyResultCommand(
                RuntimeId: Guid.NewGuid(), // wrong runtime
                ProjectId: proposal.ProjectId,
                Payload: new RuntimeSpecDeltaApplyResultPayload(
                    ProposalId: proposal.Id,
                    Success: true,
                    Error: null)),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        Context.ChangeTracker.Clear();
        var reloaded = Context.RuntimeProposals.Single(p => p.Id == proposal.Id);
        reloaded.Status.Should().Be(RuntimeProposalStatus.Approved,
            "row must not be mutated on a cross-runtime ack");

        _agentGroupClient.Verify(
            c => c.RuntimeProposalUpdated(It.IsAny<RuntimeProposalUpdatedPayload>()),
            Times.Never);
    }
}
