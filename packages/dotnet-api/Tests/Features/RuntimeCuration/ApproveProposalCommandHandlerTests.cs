using System.Text.Json;
using Api.Tests.Infrastructure;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Source.Features.Projects.Models;
using Source.Features.RuntimeCuration.Commands;
using Source.Features.RuntimeCuration.Models;
using Source.Features.RuntimeCuration.Services;
using Source.Features.RuntimeLifecycle.Models;
using Source.Features.SignalR.Hubs;

namespace Api.Tests.Features.RuntimeCuration;

/// <summary>
/// Handler-level coverage for <see cref="ApproveProposalCommandHandler"/>.
/// Mirrors <see cref="CreateRuntimeProposalCommandHandlerTests"/>'s in-memory
/// rig + typed-hub mocking; adds a second hub mock for the daemon-bound
/// <see cref="IRuntimeClient"/> push.
///
/// <para>V2 semantics: the approved spec REPLACES the project's persisted
/// spec wholesale (no additive merge). The delta is computed against the
/// project's prior spec for the daemon push to the proposal's originating
/// runtime; other runtimes converge lazily via the next bootstrap call.
/// Spec storage moved from ProjectRuntime to Project — see
/// <c>project-level-runtime-spec</c>.</para>
/// </summary>
public class ApproveProposalCommandHandlerTests : HandlerTestBase
{
    // Frontend fan-out — IAgentClient on AgentHub.
    private readonly Mock<IHubClients<IAgentClient>> _agentClients = new();
    private readonly Mock<IHubContext<AgentHub, IAgentClient>> _agentHub = new();
    private readonly Mock<IAgentClient> _agentGroupClient = new();

    // Daemon push — IRuntimeClient on RuntimeHub.
    private readonly Mock<IHubClients<IRuntimeClient>> _runtimeClients = new();
    private readonly Mock<IHubContext<RuntimeHub, IRuntimeClient>> _runtimeHub = new();
    private readonly Mock<IRuntimeClient> _runtimeGroupClient = new();

    public ApproveProposalCommandHandlerTests()
    {
        _agentHub.SetupGet(h => h.Clients).Returns(_agentClients.Object);
        _agentClients.Setup(c => c.Group(It.IsAny<string>())).Returns(_agentGroupClient.Object);

        _runtimeHub.SetupGet(h => h.Clients).Returns(_runtimeClients.Object);
        _runtimeClients.Setup(c => c.Group(It.IsAny<string>())).Returns(_runtimeGroupClient.Object);
    }

    private ApproveProposalCommandHandler CreateHandler() => new(
        Context,
        _runtimeHub.Object,
        _agentHub.Object,
        new CurrentExpandedSpecResolver(Context),
        NullLogger<ApproveProposalCommandHandler>.Instance);

    private async Task<(Project Project, ProjectRuntime Runtime, RuntimeProposal Proposal)> SeedAsync(
        string proposedSpec = """{"version":2,"services":[{"name":"postgres","command":"postgres -D /data"}]}""",
        string? projectSpec = null,
        Guid? projectId = null,
        RuntimeProposalStatus status = RuntimeProposalStatus.Pending,
        string? expandedSpec = null)
    {
        var pid = projectId ?? Guid.NewGuid();

        // Spec now lives on Project (per `project-level-runtime-spec`).
        var project = new Project
        {
            Id = pid,
            WorkspaceId = Guid.NewGuid(),
            OwnerUserId = "owner-" + Guid.NewGuid().ToString("N"),
            Name = "Test Project",
            GithubRepoOwner = "owner",
            GithubRepoName = "repo",
            Spec = projectSpec,
            SpecVersion = 1,
        };
        Context.Projects.Add(project);

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
            ProposedSpec = proposedSpec,
            ExpandedSpec = expandedSpec,
            Reason = "test",
        };
        Context.RuntimeProposals.Add(proposal);
        await Context.SaveChangesAsync();
        return (project, runtime, proposal);
    }

    // ----------------------------------------------------------------------

    [Fact]
    public async Task HappyPath_StampsRow_ReplacesSpec_PushesDelta_AndBroadcasts()
    {
        // Runtime has postgres; proposal adds redis. The approved spec
        // replaces the runtime spec wholesale; the delta surfaces only redis
        // as new.
        var proposed = """{"version":2,"services":[{"name":"postgres","command":"postgres -D /data"},{"name":"redis","command":"redis-server"}]}""";
        var current = """{"version":2,"services":[{"name":"postgres","command":"postgres -D /data"}]}""";
        var (project, runtime, proposal) = await SeedAsync(
            proposedSpec: proposed, projectSpec: current, expandedSpec: proposed);

        // Seed a PRIOR terminal-write proposal on the SAME project so the
        // resolver returns the "current" (postgres-only) expansion. The handler
        // computes the delta against this, excluding the proposal under approval.
        Context.RuntimeProposals.Add(new RuntimeProposal
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            RuntimeId = runtime.Id,
            Status = RuntimeProposalStatus.Approved,
            ProposedSpec = current,
            ExpandedSpec = current,
            Reason = "prior",
            DecidedBy = "user-1",
            DecidedAt = DateTime.UtcNow.AddMinutes(-5),
        });
        await Context.SaveChangesAsync();

        ApplyRuntimeSpecDeltaPayload? capturedDelta = null;
        _runtimeGroupClient
            .Setup(c => c.ApplyRuntimeSpecDelta(It.IsAny<ApplyRuntimeSpecDeltaPayload>()))
            .Callback<ApplyRuntimeSpecDeltaPayload>(p => capturedDelta = p)
            .Returns(Task.CompletedTask);

        RuntimeProposalUpdatedPayload? capturedUpdate = null;
        _agentGroupClient
            .Setup(c => c.RuntimeProposalUpdated(It.IsAny<RuntimeProposalUpdatedPayload>()))
            .Callback<RuntimeProposalUpdatedPayload>(p => capturedUpdate = p)
            .Returns(Task.CompletedTask);

        var handler = CreateHandler();
        var result = await handler.Handle(
            new ApproveProposalCommand(proposal.ProjectId, proposal.Id, "user-42"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(RuntimeProposalStatus.Approved);
        result.Value.AppliedSpec.Should().Be(proposal.ProposedSpec);
        result.Value.DecidedBy.Should().Be("user-42");

        Context.ChangeTracker.Clear();
        var reloadedProposal = Context.RuntimeProposals.Single(p => p.Id == proposal.Id);
        reloadedProposal.Status.Should().Be(RuntimeProposalStatus.Approved);
        reloadedProposal.AppliedSpec.Should().Be(proposal.ProposedSpec);
        reloadedProposal.DecidedBy.Should().Be("user-42");
        reloadedProposal.DecidedAt.Should().NotBeNull();

        // Project spec replaced verbatim by the proposed spec; SpecVersion bumped by one.
        var reloadedProject = Context.Projects.Single(p => p.Id == project.Id);
        reloadedProject.Spec.Should().Be(proposed,
            "V2 semantics: the proposed spec replaces the project's persisted spec wholesale");
        reloadedProject.SpecVersion.Should().Be(2,
            "SpecVersion is bumped by one on each approval (1 → 2)");

        // Daemon push — only redis is new; postgres unchanged.
        _runtimeClients.Verify(c => c.Group($"runtime-{runtime.Id}"), Times.Once);
        capturedDelta.Should().NotBeNull();
        capturedDelta!.ProposalId.Should().Be(proposal.Id);
        capturedDelta.Delta.NewOrChangedServices.Should().HaveCount(1);
        capturedDelta.Delta.NewOrChangedServices[0].Name.Should().Be("redis");
        capturedDelta.Delta.RemovedServices.Should().BeEmpty();

        // Project-group fan-out — Approved status visible to other tabs.
        _agentClients.Verify(c => c.Group($"project-{proposal.ProjectId}"), Times.Once);
        capturedUpdate.Should().NotBeNull();
        capturedUpdate!.ProposalId.Should().Be(proposal.Id);
        capturedUpdate.Status.Should().Be(RuntimeProposalStatus.Approved);
        capturedUpdate.AppliedSpec.Should().Be(proposal.ProposedSpec);
    }

    [Fact]
    public async Task CrossProject_ProposalLookupFails_ReturnsNotFound()
    {
        var (_, _, proposal) = await SeedAsync(projectId: Guid.NewGuid());

        var handler = CreateHandler();
        var result = await handler.Handle(
            new ApproveProposalCommand(
                ProjectId: Guid.NewGuid(), // wrong project
                ProposalId: proposal.Id,
                ActorUserId: "user-42"),
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("not_found");

        // No SignalR side-effects.
        _runtimeGroupClient.Verify(
            c => c.ApplyRuntimeSpecDelta(It.IsAny<ApplyRuntimeSpecDeltaPayload>()), Times.Never);
        _agentGroupClient.Verify(
            c => c.RuntimeProposalUpdated(It.IsAny<RuntimeProposalUpdatedPayload>()), Times.Never);
    }

    [Fact]
    public async Task AlreadyApproved_ReturnsAlreadyDecided()
    {
        var (_, _, proposal) = await SeedAsync(status: RuntimeProposalStatus.Approved);

        var handler = CreateHandler();
        var result = await handler.Handle(
            new ApproveProposalCommand(proposal.ProjectId, proposal.Id, "user-42"),
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("already_decided");
        _runtimeGroupClient.Verify(
            c => c.ApplyRuntimeSpecDelta(It.IsAny<ApplyRuntimeSpecDeltaPayload>()), Times.Never);
    }

    [Fact]
    public async Task AlreadyRejected_ReturnsAlreadyDecided()
    {
        var (_, _, proposal) = await SeedAsync(status: RuntimeProposalStatus.Rejected);

        var handler = CreateHandler();
        var result = await handler.Handle(
            new ApproveProposalCommand(proposal.ProjectId, proposal.Id, "user-42"),
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("already_decided");
    }

    [Fact]
    public async Task EmptyDelta_ProposalAlreadyCovered_StillSucceedsWithNoChanges()
    {
        // Runtime already has every entry in the proposal — delta is empty
        // but we still flip to Approved and push (the daemon ack-only no-ops
        // it). The persisted runtime spec is now the proposed spec, byte-for-byte.
        var spec = """{"version":2,"services":[{"name":"postgres","command":"postgres -D /data"}]}""";
        var (project, runtime, proposal) = await SeedAsync(proposedSpec: spec, projectSpec: spec);

        ApplyRuntimeSpecDeltaPayload? capturedDelta = null;
        _runtimeGroupClient
            .Setup(c => c.ApplyRuntimeSpecDelta(It.IsAny<ApplyRuntimeSpecDeltaPayload>()))
            .Callback<ApplyRuntimeSpecDeltaPayload>(p => capturedDelta = p)
            .Returns(Task.CompletedTask);

        var handler = CreateHandler();
        var result = await handler.Handle(
            new ApproveProposalCommand(proposal.ProjectId, proposal.Id, "user-42"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        capturedDelta.Should().NotBeNull();
        capturedDelta!.Delta.NewOrChangedServices.Should().BeEmpty();
        capturedDelta.Delta.RemovedServices.Should().BeEmpty();
        capturedDelta.Delta.HasChanges.Should().BeFalse();

        // Project spec round-trip stable (still bumped by one on approval).
        Context.ChangeTracker.Clear();
        var reloadedProject = Context.Projects.Single(p => p.Id == project.Id);
        reloadedProject.Spec.Should().Be(spec);
        reloadedProject.SpecVersion.Should().Be(2);
    }
}
