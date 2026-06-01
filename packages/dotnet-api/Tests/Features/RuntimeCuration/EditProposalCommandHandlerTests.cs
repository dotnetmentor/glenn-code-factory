using System.Text.Json;
using Api.Tests.Infrastructure;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Source.Features.Projects.Models;
using Source.Features.RuntimeBootstrap.Contracts;
using Source.Features.RuntimeCuration.Commands;
using Source.Features.RuntimeCuration.Models;
using Source.Features.RuntimeLifecycle.Models;
using Source.Features.RuntimePresets.Contracts;
using Source.Features.RuntimePresets.Services;
using Source.Features.SignalR.Hubs;
using Source.Shared.Results;

namespace Api.Tests.Features.RuntimeCuration;

/// <summary>
/// Handler-level coverage for <see cref="EditProposalCommandHandler"/>.
///
/// <para>V3 inputs: the user-edited body is a <see cref="RuntimeSpecV3"/>
/// (preset-based); the handler re-runs it through
/// <see cref="IPresetExpander"/> to produce the daemon-bound V2 wire shape
/// used in the delta push. The expander is mocked here so unit tests don't
/// need real preset rows in the DB; preset-level errors surface through
/// <c>_expander.Setup(...).ReturnsAsync(Result.Failure(...))</c>.</para>
/// </summary>
public class EditProposalCommandHandlerTests : HandlerTestBase
{
    private readonly Mock<IHubClients<IAgentClient>> _agentClients = new();
    private readonly Mock<IHubContext<AgentHub, IAgentClient>> _agentHub = new();
    private readonly Mock<IAgentClient> _agentGroupClient = new();

    private readonly Mock<IHubClients<IRuntimeClient>> _runtimeClients = new();
    private readonly Mock<IHubContext<RuntimeHub, IRuntimeClient>> _runtimeHub = new();
    private readonly Mock<IRuntimeClient> _runtimeGroupClient = new();

    private readonly Mock<IPresetExpander> _expander = new();

    public EditProposalCommandHandlerTests()
    {
        _agentHub.SetupGet(h => h.Clients).Returns(_agentClients.Object);
        _agentClients.Setup(c => c.Group(It.IsAny<string>())).Returns(_agentGroupClient.Object);

        _runtimeHub.SetupGet(h => h.Clients).Returns(_runtimeClients.Object);
        _runtimeClients.Setup(c => c.Group(It.IsAny<string>())).Returns(_runtimeGroupClient.Object);

        // Default expansion: a single redis V2 service. Tests that need a
        // specific expansion to drive delta computation override this inline.
        _expander
            .Setup(e => e.ExpandAsync(It.IsAny<RuntimeSpecV3>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(new RuntimeSpecV2
            {
                Services = new List<ServiceSpec>
                {
                    new() { Name = "redis", Command = "redis-server" },
                },
            }));
    }

    private EditProposalCommandHandler CreateHandler() => new(
        Context,
        _runtimeHub.Object,
        _agentHub.Object,
        _expander.Object,
        NullLogger<EditProposalCommandHandler>.Instance);

    private async Task<RuntimeProposal> SeedAsync(
        RuntimeProposalStatus status = RuntimeProposalStatus.Pending,
        string? projectSpec = null)
    {
        var pid = Guid.NewGuid();

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
            ProposedSpec = """{"version":3,"services":[{"kind":"bash-raw","name":"mongodb","values":{}}]}""",
            Reason = "agent suggested mongo",
        };
        Context.RuntimeProposals.Add(proposal);
        await Context.SaveChangesAsync();
        return proposal;
    }

    private static RuntimeSpecV3 SpecWith(params ServiceInstance[] services) => new()
    {
        Services = services.ToList(),
    };

    private static ServiceInstance Service(string kind, string name) => new()
    {
        Kind = kind,
        Name = name,
        Values = new Dictionary<string, JsonElement>(),
    };

    // ----------------------------------------------------------------------

    [Fact]
    public async Task HappyPath_AppliesEditedSpec_PushesDeltaToRuntimeGroup()
    {
        // User edits the proposal to swap mongo for a postgres+redis pair.
        // AppliedSpec captures the user-edited V3 verbatim; the expander
        // mock produces a redis-only V2 expansion which becomes the daemon-
        // bound ExpandedSpec.
        var proposal = await SeedAsync();

        ApplyRuntimeSpecDeltaPayload? capturedDelta = null;
        _runtimeGroupClient
            .Setup(c => c.ApplyRuntimeSpecDelta(It.IsAny<ApplyRuntimeSpecDeltaPayload>()))
            .Callback<ApplyRuntimeSpecDeltaPayload>(p => capturedDelta = p)
            .Returns(Task.CompletedTask);

        var editedSpec = SpecWith(
            Service("postgres-15", "postgres"),
            Service("bash-raw", "redis"));

        var handler = CreateHandler();
        var result = await handler.Handle(
            new EditProposalCommand(
                ProjectId: proposal.ProjectId,
                ProposalId: proposal.Id,
                EditedSpec: editedSpec,
                ActorUserId: "user-42"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(RuntimeProposalStatus.Edited);

        Context.ChangeTracker.Clear();
        var reloaded = Context.RuntimeProposals.Single(p => p.Id == proposal.Id);
        reloaded.Status.Should().Be(RuntimeProposalStatus.Edited);
        reloaded.AppliedSpec.Should().NotBe(reloaded.ProposedSpec,
            "Edited stamps the user-edited body, not the daemon's");

        // Persisted AppliedSpec is V3 (source of truth).
        using (var doc = JsonDocument.Parse(reloaded.AppliedSpec!))
        {
            doc.RootElement.GetProperty("version").GetInt32().Should().Be(3);
            var services = doc.RootElement.GetProperty("services").EnumerateArray().ToList();
            services.Should().HaveCount(2);
            services.Select(s => s.GetProperty("kind").GetString())
                .Should().ContainInOrder("postgres-15", "bash-raw");
        }

        // ExpandedSpec is the V2 the expander mock returned (redis only).
        reloaded.ExpandedSpec.Should().NotBeNullOrEmpty();
        using (var doc = JsonDocument.Parse(reloaded.ExpandedSpec!))
        {
            var services = doc.RootElement.GetProperty("services").EnumerateArray().ToList();
            services.Should().ContainSingle();
            services[0].GetProperty("name").GetString().Should().Be("redis");
        }

        // Delta push fired against the runtime group.
        capturedDelta.Should().NotBeNull();
        capturedDelta!.ProposalId.Should().Be(proposal.Id);
    }

    [Fact]
    public async Task ServiceWithMissingKind_FailsBeforeAnyMutation()
    {
        // V3 structural validation rejects a service with no kind before the
        // expander is invoked.
        var proposal = await SeedAsync();

        var handler = CreateHandler();
        var result = await handler.Handle(
            new EditProposalCommand(
                ProjectId: proposal.ProjectId,
                ProposalId: proposal.Id,
                EditedSpec: new RuntimeSpecV3
                {
                    Services = new List<ServiceInstance>
                    {
                        new() { Kind = "", Name = "postgres", Values = new Dictionary<string, JsonElement>() },
                    },
                },
                ActorUserId: "user-42"),
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().StartWith("service_kind_required");

        // Pending → Pending; nothing was persisted.
        Context.ChangeTracker.Clear();
        var reloaded = Context.RuntimeProposals.Single(p => p.Id == proposal.Id);
        reloaded.Status.Should().Be(RuntimeProposalStatus.Pending);
        reloaded.AppliedSpec.Should().BeNull();

        _runtimeGroupClient.Verify(
            c => c.ApplyRuntimeSpecDelta(It.IsAny<ApplyRuntimeSpecDeltaPayload>()), Times.Never);
        _agentGroupClient.Verify(
            c => c.RuntimeProposalUpdated(It.IsAny<RuntimeProposalUpdatedPayload>()), Times.Never);
    }

    [Fact]
    public async Task DuplicateServiceNames_FailBeforeAnyMutation()
    {
        var proposal = await SeedAsync();

        var handler = CreateHandler();
        var result = await handler.Handle(
            new EditProposalCommand(
                ProjectId: proposal.ProjectId,
                ProposalId: proposal.Id,
                EditedSpec: SpecWith(
                    Service("postgres-15", "postgres"),
                    Service("bash-raw", "postgres")),
                ActorUserId: "user-42"),
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().StartWith("service_name_duplicate");
    }

    [Fact]
    public async Task ExpanderFailure_PresetNotFound_FailsBeforeAnyMutation()
    {
        // Structural validation passes; the expander rejects the spec because
        // the slug isn't in the DB. No persistence, no push.
        var proposal = await SeedAsync();
        _expander
            .Setup(e => e.ExpandAsync(It.IsAny<RuntimeSpecV3>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<RuntimeSpecV2>("preset_not_found:unknown-preset"));

        var handler = CreateHandler();
        var result = await handler.Handle(
            new EditProposalCommand(
                ProjectId: proposal.ProjectId,
                ProposalId: proposal.Id,
                EditedSpec: SpecWith(Service("unknown-preset", "x")),
                ActorUserId: "user-42"),
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("preset_not_found:unknown-preset");

        Context.ChangeTracker.Clear();
        var reloaded = Context.RuntimeProposals.Single(p => p.Id == proposal.Id);
        reloaded.Status.Should().Be(RuntimeProposalStatus.Pending);
        reloaded.AppliedSpec.Should().BeNull();

        _runtimeGroupClient.Verify(
            c => c.ApplyRuntimeSpecDelta(It.IsAny<ApplyRuntimeSpecDeltaPayload>()), Times.Never);
    }

    [Fact]
    public async Task AlreadyEdited_ReturnsAlreadyDecided()
    {
        var proposal = await SeedAsync(status: RuntimeProposalStatus.Edited);

        var handler = CreateHandler();
        var result = await handler.Handle(
            new EditProposalCommand(
                ProjectId: proposal.ProjectId,
                ProposalId: proposal.Id,
                EditedSpec: SpecWith(Service("postgres-15", "postgres")),
                ActorUserId: "user-42"),
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("already_decided");
    }
}
