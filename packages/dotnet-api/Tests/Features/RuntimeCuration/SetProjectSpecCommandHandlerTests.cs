using System.Text.Json;
using Api.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Source.Features.Projects.Models;
using Source.Features.RuntimeBootstrap.Contracts;
using Source.Features.RuntimeCuration.Commands;
using Source.Features.RuntimeLifecycle.Models;
using Source.Features.RuntimePresets.Contracts;
using Source.Features.RuntimePresets.Services;
using Source.Shared.Results;

namespace Api.Tests.Features.RuntimeCuration;

/// <summary>
/// Handler-level coverage for <see cref="SetProjectSpecCommandHandler"/>.
/// Mirrors <see cref="ApproveProposalCommandHandlerTests"/>'s in-memory rig.
/// Unlike Approve / Edit there's no proposal row, no project-group fan-out,
/// no AgentHub side, and — by design — no RuntimeHub push either. The user
/// is authoring fresh, not reviewing a diff against running services, so the
/// handler intentionally skips pushing deltas to live daemons; runtimes
/// converge on their next cold-boot / wake via GetBootstrapQuery.
///
/// <para>V3 spec shape: each service references a preset by <c>kind</c> +
/// supplies its parameter <c>values</c>. The handler delegates preset existence
/// / type checks to <see cref="IPresetExpander"/>; structural invariants
/// (unique <c>name</c>, required <c>kind</c>) are caught by
/// <see cref="RuntimeSpecV3.Validate"/> before the expander runs.</para>
/// </summary>
public class SetProjectSpecCommandHandlerTests : HandlerTestBase
{
    private readonly Mock<IPresetExpander> _expander = new();

    public SetProjectSpecCommandHandlerTests()
    {
        // Default: expander always succeeds with an empty V2 expansion. The
        // handler discards the value anyway (no daemon push on this path); the
        // tests that need to assert validation-failure-from-expander override
        // this setup inline.
        _expander
            .Setup(e => e.ExpandAsync(It.IsAny<RuntimeSpecV3>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(new RuntimeSpecV2()));
    }

    private SetProjectSpecCommandHandler CreateHandler() => new(
        Context,
        _expander.Object,
        NullLogger<SetProjectSpecCommandHandler>.Instance);

    private async Task<(Project Project, ProjectRuntime? Runtime)> SeedAsync(
        string? projectSpec = null,
        int specVersion = 1,
        bool createRuntime = true)
    {
        var pid = Guid.NewGuid();
        var project = new Project
        {
            Id = pid,
            WorkspaceId = Guid.NewGuid(),
            OwnerUserId = "owner-" + Guid.NewGuid().ToString("N"),
            Name = "Test Project",
            GithubRepoOwner = "owner",
            GithubRepoName = "repo",
            Spec = projectSpec,
            SpecVersion = specVersion,
        };
        Context.Projects.Add(project);

        ProjectRuntime? runtime = null;
        if (createRuntime)
        {
            runtime = new ProjectRuntime
            {
                Id = Guid.NewGuid(),
                ProjectId = pid,
                Region = "arn",
            };
            Context.ProjectRuntimes.Add(runtime);
        }

        await Context.SaveChangesAsync();
        return (project, runtime);
    }

    /// <summary>
    /// V3 spec builder: each service is a <c>(kind, name)</c> pair pointing at
    /// a preset slug. The <c>values</c> dict is empty by default — the expander
    /// is mocked, so the test never actually exercises parameter substitution.
    /// </summary>
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
    public async Task HappyPath_FirstTimeSeed_PersistsSpec_BumpsVersion()
    {
        // Empty project (Spec=null) + valid spec → spec persists wholesale,
        // SpecVersion 1 → 2. No daemon push — that's the whole point of the
        // direct-write path; live runtimes pick the spec up on next bootstrap.
        var (project, _) = await SeedAsync(projectSpec: null);

        var spec = SpecWith(Service("postgres-15", "postgres"));

        var handler = CreateHandler();
        var result = await handler.Handle(
            new SetProjectSpecCommand(project.Id, spec),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.SpecVersion.Should().Be(2,
            "SpecVersion bumps by one on each direct-write (1 → 2)");
        result.Value.UpdatedAt.Should().NotBe(default);

        // Persisted spec round-trip — body matches RuntimeSpecV3.ToJson() shape.
        Context.ChangeTracker.Clear();
        var reloaded = Context.Projects.Single(p => p.Id == project.Id);
        reloaded.Spec.Should().Be(spec.ToJson());
        reloaded.SpecVersion.Should().Be(2);
    }

    [Fact]
    public async Task ExistingSpec_Overwrite_ReplacesWholesale_NoDaemonPush()
    {
        // Project already has postgres; new spec is postgres + redis. The
        // project row's Spec is replaced byte-for-byte by the new body. No
        // delta is pushed to the live runtime — the user pasted this in
        // without seeing what's actually running, so the daemon keeps its
        // current state until it reads the project spec on next bootstrap.
        var current = """{"version":3,"services":[{"kind":"postgres-15","name":"postgres","values":{}}]}""";
        var (project, _) = await SeedAsync(projectSpec: current, specVersion: 3);

        var newSpec = SpecWith(
            Service("postgres-15", "postgres"),
            Service("bash-raw", "redis"));

        var handler = CreateHandler();
        var result = await handler.Handle(
            new SetProjectSpecCommand(project.Id, newSpec),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.SpecVersion.Should().Be(4, "3 → 4 on overwrite");

        Context.ChangeTracker.Clear();
        var reloaded = Context.Projects.Single(p => p.Id == project.Id);
        reloaded.Spec.Should().Be(newSpec.ToJson(),
            "the supplied spec replaces the project's persisted spec wholesale");
        reloaded.SpecVersion.Should().Be(4);
    }

    [Fact]
    public async Task ValidationFailure_DuplicateServiceName_ReturnsValidatorCode_ProjectUntouched()
    {
        // Two services with the same name — RuntimeSpecV3.Validate() rejects
        // with the stable code prefix `service_name_duplicate:`. We surface it
        // verbatim and the project row is left exactly as we seeded it (no
        // version bump, no spec mutation).
        var (project, _) = await SeedAsync(projectSpec: null, specVersion: 1);

        var badSpec = SpecWith(
            Service("postgres-15", "postgres"),
            Service("bash-raw", "postgres"));

        var handler = CreateHandler();
        var result = await handler.Handle(
            new SetProjectSpecCommand(project.Id, badSpec),
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().StartWith("service_name_duplicate");

        Context.ChangeTracker.Clear();
        var reloaded = Context.Projects.Single(p => p.Id == project.Id);
        reloaded.Spec.Should().BeNull("validation rejected → no write happens");
        reloaded.SpecVersion.Should().Be(1, "version untouched on validation failure");
    }

    [Fact]
    public async Task ValidationFailure_MissingKind_ReturnsServiceKindRequired()
    {
        // Service with no kind — validator returns `service_kind_required`.
        // (V3 replaced V2's "service_command_required" since the command line
        //  comes from the preset's template, not the spec.)
        var (project, _) = await SeedAsync();

        var badSpec = new RuntimeSpecV3
        {
            Services = new List<ServiceInstance>
            {
                new() { Kind = "", Name = "api", Values = new Dictionary<string, JsonElement>() },
            },
        };

        var handler = CreateHandler();
        var result = await handler.Handle(
            new SetProjectSpecCommand(project.Id, badSpec),
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().StartWith("service_kind_required");
    }

    [Fact]
    public async Task ExpanderFailure_PresetNotFound_SurfacesErrorCode()
    {
        // Structural validation passes, but the expander resolves no preset
        // for the supplied kind. The handler must surface the expander's
        // snake_case code verbatim and leave the project untouched.
        var (project, _) = await SeedAsync();

        _expander
            .Setup(e => e.ExpandAsync(It.IsAny<RuntimeSpecV3>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<RuntimeSpecV2>("preset_not_found:unknown-preset"));

        var spec = SpecWith(Service("unknown-preset", "x"));

        var handler = CreateHandler();
        var result = await handler.Handle(
            new SetProjectSpecCommand(project.Id, spec),
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("preset_not_found:unknown-preset");

        Context.ChangeTracker.Clear();
        var reloaded = Context.Projects.Single(p => p.Id == project.Id);
        reloaded.Spec.Should().BeNull("expander failure → no write happens");
        reloaded.SpecVersion.Should().Be(1);
    }

    [Fact]
    public async Task ProjectNotFound_ReturnsNotFound()
    {
        var spec = SpecWith(Service("postgres-15", "postgres"));

        var handler = CreateHandler();
        var result = await handler.Handle(
            new SetProjectSpecCommand(Guid.NewGuid(), spec),
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("not_found");
    }

    [Fact]
    public async Task NoLiveRuntime_StillPersistsSpec()
    {
        // Freshly-seeded project with no ProjectRuntime row yet. The spec write
        // must succeed (so bootstrap picks it up on the next cold boot).
        var (project, _) = await SeedAsync(createRuntime: false);

        var spec = SpecWith(Service("postgres-15", "postgres"));

        var handler = CreateHandler();
        var result = await handler.Handle(
            new SetProjectSpecCommand(project.Id, spec),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.SpecVersion.Should().Be(2);

        Context.ChangeTracker.Clear();
        Context.Projects.Single(p => p.Id == project.Id).Spec.Should().Be(spec.ToJson());
    }

    [Fact]
    public async Task LiveRuntimeExists_StillNoPush_RuntimeKeepsRunningOldSpec()
    {
        // A live ProjectRuntime is in the table. The direct-write path still
        // does NOT touch it — the runtime keeps running whatever it was
        // running, and will read the new project spec on its next bootstrap.
        // This is the lazier-than-Approve contract: paste-spec is the user
        // authoring fresh JSON without reviewing a diff against running
        // services, so disrupting the daemon mid-work would be a footgun.
        var (project, runtime) = await SeedAsync(createRuntime: true);

        var spec = SpecWith(Service("postgres-15", "postgres"));

        var handler = CreateHandler();
        var result = await handler.Handle(
            new SetProjectSpecCommand(project.Id, spec),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        // Project row updated.
        Context.ChangeTracker.Clear();
        Context.Projects.Single(p => p.Id == project.Id).Spec.Should().Be(spec.ToJson());

        // Runtime row untouched — its state, region etc. are still whatever
        // we seeded. (No assertion on "Spec" on ProjectRuntime: the column
        // was dropped in the project-level-spec migration.)
        var reloadedRuntime = Context.ProjectRuntimes.Single(r => r.Id == runtime!.Id);
        reloadedRuntime.Region.Should().Be("arn");
    }

    [Fact]
    public async Task SoftDeletedRuntime_HasNoEffectOnSpecWrite()
    {
        // A soft-deleted ProjectRuntime row is filtered by the global query
        // filter. Either way the handler doesn't push, but the spec write
        // still has to succeed.
        var (project, runtime) = await SeedAsync();
        runtime!.IsDeleted = true;
        runtime.DeletedAt = DateTime.UtcNow;
        await Context.SaveChangesAsync();

        var spec = SpecWith(Service("postgres-15", "postgres"));

        var handler = CreateHandler();
        var result = await handler.Handle(
            new SetProjectSpecCommand(project.Id, spec),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        Context.ChangeTracker.Clear();
        Context.Projects.Single(p => p.Id == project.Id).Spec.Should().Be(spec.ToJson());
    }
}
