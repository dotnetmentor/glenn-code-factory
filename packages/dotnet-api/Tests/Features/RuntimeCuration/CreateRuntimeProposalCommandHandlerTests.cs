using System.Text.Json;
using Api.Tests.Infrastructure;
using MediatR;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
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
/// Handler-level coverage for the daemon-facing
/// <c>POST /api/runtimes/{id}/proposals</c> entry point — the curation flow's
/// front door. Exercises validation, persistence, and SignalR fan-out. The
/// controller-level claim cross-check lives in
/// <see cref="RuntimeProposalsControllerTests"/>.
///
/// <para>V3 spec shape: each service references a preset by <c>kind</c> +
/// supplies its parameter <c>values</c>. Structural validation enforces
/// required kind/name and unique service names — see
/// <see cref="RuntimeSpecV3.Validate"/>. Preset existence + parameter typing
/// surface from the mocked <see cref="IPresetExpander"/>.</para>
/// </summary>
public class CreateRuntimeProposalCommandHandlerTests : HandlerTestBase
{
    private readonly Mock<IHubClients<IAgentClient>> _clients = new();
    private readonly Mock<IHubContext<AgentHub, IAgentClient>> _hub = new();
    private readonly Mock<IAgentClient> _groupClient = new();
    private readonly Mock<IPresetExpander> _expander = new();
    // Drives the auto-apply approve path when repair consent is armed. These
    // tests seed runtimes WITHOUT consent armed, so Send is never invoked.
    private readonly Mock<IMediator> _mediator = new();

    public CreateRuntimeProposalCommandHandlerTests()
    {
        _hub.SetupGet(h => h.Clients).Returns(_clients.Object);
        _clients.Setup(c => c.Group(It.IsAny<string>())).Returns(_groupClient.Object);

        // Default: expander succeeds with an empty-services V2 expansion. The
        // ExpandedSpec column lands in the row; we don't care about its body
        // unless a test sets up a specific expansion.
        _expander
            .Setup(e => e.ExpandAsync(It.IsAny<RuntimeSpecV3>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(new RuntimeSpecV2()));
    }

    private CreateRuntimeProposalCommandHandler CreateHandler()
        => new(Context, _hub.Object, _expander.Object, _mediator.Object, NullLogger<CreateRuntimeProposalCommandHandler>.Instance);

    private async Task<ProjectRuntime> SeedRuntimeAsync(Guid? projectId = null, bool isDeleted = false)
    {
        var runtime = new ProjectRuntime
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId ?? Guid.NewGuid(),
            Region = "arn",
            IsDeleted = isDeleted,
        };
        Context.ProjectRuntimes.Add(runtime);
        await Context.SaveChangesAsync();
        return runtime;
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
    public async Task HappyPath_persists_pending_proposal_and_broadcasts_to_project_group()
    {
        var projectId = Guid.NewGuid();
        var runtime = await SeedRuntimeAsync(projectId: projectId);

        RuntimeProposalCreatedPayload? captured = null;
        _groupClient
            .Setup(c => c.RuntimeProposalCreated(It.IsAny<RuntimeProposalCreatedPayload>()))
            .Callback<RuntimeProposalCreatedPayload>(p => captured = p)
            .Returns(Task.CompletedTask);

        var handler = CreateHandler();
        var result = await handler.Handle(
            new CreateRuntimeProposalCommand(
                RuntimeId: runtime.Id,
                ProposedSpec: SpecWith(Service("postgres-15", "postgres")),
                Reason: "marketplace needs DB"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.ProposalId.Should().NotBe(Guid.Empty);

        // DB row — single Pending proposal scoped to the right runtime+project.
        var row = Context.RuntimeProposals.Should().ContainSingle().Subject;
        row.Id.Should().Be(result.Value.ProposalId);
        row.RuntimeId.Should().Be(runtime.Id);
        row.ProjectId.Should().Be(projectId);
        row.Status.Should().Be(RuntimeProposalStatus.Pending);
        row.Reason.Should().Be("marketplace needs DB");
        row.AppliedSpec.Should().BeNull("AppliedSpec is set later by Approve/Edit");
        row.DecidedBy.Should().BeNull();
        row.DecidedAt.Should().BeNull();

        // ProposedSpec is V3 (source of truth); ExpandedSpec is the V2 the
        // expander produced (daemon-bound wire shape).
        row.ProposedSpec.Should().NotBeNullOrEmpty();
        row.ExpandedSpec.Should().NotBeNullOrEmpty();

        using (var doc = JsonDocument.Parse(row.ProposedSpec))
        {
            doc.RootElement.GetProperty("version").GetInt32().Should().Be(3);
            var services = doc.RootElement.GetProperty("services").EnumerateArray().ToList();
            services.Should().HaveCount(1);
            services[0].GetProperty("kind").GetString().Should().Be("postgres-15");
            services[0].GetProperty("name").GetString().Should().Be("postgres");
        }

        // SignalR — single broadcast, project-scoped group, payload mirrors the row.
        _clients.Verify(c => c.Group($"project-{projectId}"), Times.Once);
        _clients.Verify(c => c.Group(It.Is<string>(s => s != $"project-{projectId}")), Times.Never);
        _groupClient.Verify(c => c.RuntimeProposalCreated(It.IsAny<RuntimeProposalCreatedPayload>()), Times.Once);

        captured.Should().NotBeNull();
        captured!.ProposalId.Should().Be(result.Value.ProposalId);
        captured.RuntimeId.Should().Be(runtime.Id);
        captured.ProjectId.Should().Be(projectId);
        captured.Reason.Should().Be("marketplace needs DB");
        // Broadcast carries the V3 source-of-truth shape — that's what the
        // user-facing confirmation card renders.
        captured.ProposedSpec.Should().Be(row.ProposedSpec);
    }

    [Fact]
    public async Task RuntimeNotFound_returns_not_found()
    {
        // No runtime seeded.
        var handler = CreateHandler();
        var result = await handler.Handle(
            new CreateRuntimeProposalCommand(
                RuntimeId: Guid.NewGuid(),
                ProposedSpec: SpecWith(Service("postgres-15", "postgres")),
                Reason: "doesn't matter"),
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("not_found");
        Context.RuntimeProposals.Should().BeEmpty("nothing was persisted");
        _groupClient.Verify(
            c => c.RuntimeProposalCreated(It.IsAny<RuntimeProposalCreatedPayload>()),
            Times.Never);
    }

    [Fact]
    public async Task SoftDeletedRuntime_returns_not_found()
    {
        // The global query filter on ProjectRuntime hides IsDeleted=true rows;
        // a torn-down runtime can't accept new proposals.
        var runtime = await SeedRuntimeAsync(isDeleted: true);

        var handler = CreateHandler();
        var result = await handler.Handle(
            new CreateRuntimeProposalCommand(
                RuntimeId: runtime.Id,
                ProposedSpec: SpecWith(Service("postgres-15", "postgres")),
                Reason: "stale daemon"),
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("not_found");
        Context.RuntimeProposals.Should().BeEmpty();
    }

    [Fact]
    public async Task ServiceWithMissingKind_fails_validation_without_persisting()
    {
        // V3 validator catches a service with no kind before the expander
        // runs — the row never reaches SaveChanges.
        var runtime = await SeedRuntimeAsync();

        var handler = CreateHandler();
        var result = await handler.Handle(
            new CreateRuntimeProposalCommand(
                RuntimeId: runtime.Id,
                ProposedSpec: new RuntimeSpecV3
                {
                    Services = new List<ServiceInstance>
                    {
                        new() { Kind = "", Name = "mongodb", Values = new Dictionary<string, JsonElement>() },
                    },
                },
                Reason: "missing kind"),
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().StartWith("service_kind_required");
        Context.RuntimeProposals.Should().BeEmpty("validation fails before SaveChanges");
        _groupClient.Verify(
            c => c.RuntimeProposalCreated(It.IsAny<RuntimeProposalCreatedPayload>()),
            Times.Never);
    }

    [Fact]
    public async Task DuplicateServiceNames_fail_validation_without_persisting()
    {
        var runtime = await SeedRuntimeAsync();

        var handler = CreateHandler();
        var result = await handler.Handle(
            new CreateRuntimeProposalCommand(
                RuntimeId: runtime.Id,
                ProposedSpec: SpecWith(
                    Service("postgres-15", "postgres"),
                    Service("bash-raw", "postgres")),
                Reason: "duplicate names"),
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().StartWith("service_name_duplicate");
        Context.RuntimeProposals.Should().BeEmpty();
    }

    [Fact]
    public async Task ExpanderFailure_PresetNotFound_returns_error_without_persisting()
    {
        // Structural validation passes, but the expander can't resolve the
        // preset slug — that's the second validation surface and it must
        // short-circuit before the row is written.
        var runtime = await SeedRuntimeAsync();
        _expander
            .Setup(e => e.ExpandAsync(It.IsAny<RuntimeSpecV3>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<RuntimeSpecV2>("preset_not_found:unknown-preset"));

        var handler = CreateHandler();
        var result = await handler.Handle(
            new CreateRuntimeProposalCommand(
                RuntimeId: runtime.Id,
                ProposedSpec: SpecWith(Service("unknown-preset", "x")),
                Reason: "bad preset"),
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("preset_not_found:unknown-preset");
        Context.RuntimeProposals.Should().BeEmpty("expander failure → no persist");
        _groupClient.Verify(
            c => c.RuntimeProposalCreated(It.IsAny<RuntimeProposalCreatedPayload>()),
            Times.Never);
    }

    [Fact]
    public async Task ArbitraryServiceName_is_accepted()
    {
        // V3 doesn't whitelist names; any unique-within-spec value works. The
        // expander mock is a no-op so we don't actually care what slug we use.
        var runtime = await SeedRuntimeAsync();

        var handler = CreateHandler();
        var result = await handler.Handle(
            new CreateRuntimeProposalCommand(
                RuntimeId: runtime.Id,
                ProposedSpec: SpecWith(Service("bash-raw", "mongodb")),
                Reason: "user wants a doc store"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue("freeform service names are allowed");
        Context.RuntimeProposals.Should().ContainSingle();
    }

    [Fact]
    public async Task Install_and_Setup_are_serialized_when_present()
    {
        var runtime = await SeedRuntimeAsync();

        var handler = CreateHandler();
        var result = await handler.Handle(
            new CreateRuntimeProposalCommand(
                RuntimeId: runtime.Id,
                ProposedSpec: new RuntimeSpecV3
                {
                    Install = "apt-get install -y mongodb-org",
                    Setup = "npm install",
                    Services = new List<ServiceInstance>
                    {
                        Service("postgres-15", "postgres"),
                    },
                },
                Reason: "freeform install + setup"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        var row = Context.RuntimeProposals.Single();
        using var doc = JsonDocument.Parse(row.ProposedSpec);
        doc.RootElement.GetProperty("install").GetString().Should().Be("apt-get install -y mongodb-org");
        doc.RootElement.GetProperty("setup").GetString().Should().Be("npm install");
    }

    [Fact]
    public async Task Null_Install_and_Setup_are_omitted_from_spec_json()
    {
        var runtime = await SeedRuntimeAsync();

        var handler = CreateHandler();
        var result = await handler.Handle(
            new CreateRuntimeProposalCommand(
                RuntimeId: runtime.Id,
                ProposedSpec: SpecWith(Service("postgres-15", "postgres")),
                Reason: "no install/setup"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        var row = Context.RuntimeProposals.Single();
        using var doc = JsonDocument.Parse(row.ProposedSpec);
        doc.RootElement.TryGetProperty("install", out _).Should().BeFalse(
            "null install is omitted to keep the on-disk JSON minimal");
        doc.RootElement.TryGetProperty("setup", out _).Should().BeFalse();
    }
}
