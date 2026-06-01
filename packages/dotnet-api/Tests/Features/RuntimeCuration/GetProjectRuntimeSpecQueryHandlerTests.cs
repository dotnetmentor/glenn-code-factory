using Api.Tests.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Source.Features.Projects.Models;
using Source.Features.RuntimeCuration.Queries;
using Source.Features.RuntimeLifecycle.Models;

namespace Api.Tests.Features.RuntimeCuration;

/// <summary>
/// Handler-level coverage for <see cref="GetProjectRuntimeSpecQueryHandler"/>.
/// In-memory rig — the JSON parsing path is exercised end-to-end so a
/// malformed body is proven not to throw.
///
/// <para>Post-<c>project-level-runtime-spec</c>: spec is read straight from
/// the <c>Project</c> row (no more "newest runtime wins" lottery). The
/// runtime lookup that lives alongside is purely for SignalR-group targeting
/// in the DTO — it doesn't drive the body.</para>
///
/// <para>V3 DTO shape: the spec is exposed as a typed <see cref="Source.Features.RuntimePresets.Contracts.RuntimeSpecV3"/>
/// (preset-based services list + install/setup strings).</para>
/// </summary>
public class GetProjectRuntimeSpecQueryHandlerTests : HandlerTestBase
{
    private GetProjectRuntimeSpecQueryHandler CreateHandler(
        ILogger<GetProjectRuntimeSpecQueryHandler>? logger = null) =>
            new(Context, logger ?? NullLogger<GetProjectRuntimeSpecQueryHandler>.Instance);

    private async Task<Project> SeedProjectAsync(Guid projectId, string? spec)
    {
        var project = new Project
        {
            Id = projectId,
            WorkspaceId = Guid.NewGuid(),
            OwnerUserId = "owner-" + Guid.NewGuid().ToString("N"),
            Name = "Test Project",
            GithubRepoOwner = "owner",
            GithubRepoName = "repo",
            Spec = spec,
            SpecVersion = 1,
        };
        Context.Projects.Add(project);
        await Context.SaveChangesAsync();
        return project;
    }

    private async Task<ProjectRuntime> SeedRuntimeAsync(
        Guid projectId,
        RuntimeState state = RuntimeState.Online,
        bool deleted = false,
        DateTime? createdAt = null)
    {
        var runtime = new ProjectRuntime
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Region = "arn",
            State = state,
            IsDeleted = deleted,
        };
        Context.ProjectRuntimes.Add(runtime);
        await Context.SaveChangesAsync();
        if (createdAt.HasValue)
        {
            runtime.CreatedAt = createdAt.Value;
            await Context.SaveChangesAsync();
        }
        return runtime;
    }

    // ----------------------------------------------------------------------

    [Fact]
    public async Task ValidSpec_ReturnsParsedV3Body()
    {
        var projectId = Guid.NewGuid();
        await SeedProjectAsync(
            projectId,
            spec: """{"version":3,"services":[{"kind":"postgres-15","name":"postgres","values":{}}]}""");
        var runtime = await SeedRuntimeAsync(projectId);

        var result = await CreateHandler().Handle(
            new GetProjectRuntimeSpecQuery(projectId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.RuntimeId.Should().Be(runtime.Id);
        result.Value.ProjectId.Should().Be(projectId);
        result.Value.State.Should().Be(RuntimeState.Online);
        result.Value.Spec.Services.Should().HaveCount(1);
        result.Value.Spec.Services![0].Kind.Should().Be("postgres-15");
        result.Value.Spec.Services![0].Name.Should().Be("postgres");
        result.Value.SpecUpdatedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task NullSpec_ReturnsEmptyV3Body()
    {
        var projectId = Guid.NewGuid();
        await SeedProjectAsync(projectId, spec: null);
        await SeedRuntimeAsync(projectId);

        var result = await CreateHandler().Handle(
            new GetProjectRuntimeSpecQuery(projectId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Spec.Services.Should().BeNullOrEmpty();
        result.Value.Spec.Install.Should().BeNull();
        result.Value.Spec.Setup.Should().BeNull();
    }

    [Fact]
    public async Task NoRuntime_StillReturnsProjectSpec_WithNullRuntimeId()
    {
        // Post-refactor: spec is on the project, so a project with no live
        // runtimes still has a viewable spec. RuntimeId/State surface null so
        // the frontend can disable "edit / save to catalog" until a runtime
        // exists to push deltas to.
        var projectId = Guid.NewGuid();
        await SeedProjectAsync(
            projectId,
            spec: """{"version":3,"services":[{"kind":"postgres-15","name":"postgres","values":{}}]}""");

        var result = await CreateHandler().Handle(
            new GetProjectRuntimeSpecQuery(projectId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.RuntimeId.Should().BeNull();
        result.Value.State.Should().BeNull();
        result.Value.Spec.Services.Should().HaveCount(1);
        result.Value.Spec.Services![0].Name.Should().Be("postgres");
    }

    [Fact]
    public async Task MultipleRuntimes_PicksMostRecentForSignalRTargeting()
    {
        // Spec body is now project-level so we no longer pick "newest runtime
        // wins" for the spec — but we still surface the most-recent runtime in
        // the DTO so the frontend's Edit/Save-to-Catalog mutation knows which
        // runtime group to delta-push to. Verify that targeting still picks
        // the newest of the project's runtimes.
        var projectId = Guid.NewGuid();
        await SeedProjectAsync(
            projectId,
            spec: """{"version":3,"services":[{"kind":"postgres-15","name":"postgres","values":{}}]}""");

        var t0 = DateTime.UtcNow.AddMinutes(-30);
        await SeedRuntimeAsync(projectId, createdAt: t0);
        var newest = await SeedRuntimeAsync(projectId, createdAt: t0.AddMinutes(20));

        var result = await CreateHandler().Handle(
            new GetProjectRuntimeSpecQuery(projectId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.RuntimeId.Should().Be(newest.Id);
        result.Value.Spec.Services!.Single().Name.Should().Be("postgres");
    }

    [Fact]
    public async Task NoProjectAtAll_ReturnsNotFound()
    {
        var result = await CreateHandler().Handle(
            new GetProjectRuntimeSpecQuery(Guid.NewGuid()), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("not_found");
    }

    [Fact]
    public async Task MalformedJson_ReturnsEmptySpec_AndLogsWarning()
    {
        var projectId = Guid.NewGuid();
        await SeedProjectAsync(projectId, spec: "{not-valid-json");
        await SeedRuntimeAsync(projectId);

        var logger = new TestLogger<GetProjectRuntimeSpecQueryHandler>();
        var result = await CreateHandler(logger).Handle(
            new GetProjectRuntimeSpecQuery(projectId), CancellationToken.None);

        // Read still succeeds — the Spec tab renders empty rather than
        // surfacing a 500 to the user.
        result.IsSuccess.Should().BeTrue();
        result.Value.Spec.Services.Should().BeNullOrEmpty();
        result.Value.Spec.Install.Should().BeNull();
        result.Value.Spec.Setup.Should().BeNull();

        logger.Records.Should().Contain(r =>
            r.Level == LogLevel.Warning && r.Message.Contains("does not parse as V3"));
    }

    [Fact]
    public async Task InstallAndSetup_FlowThroughIntoDto()
    {
        var projectId = Guid.NewGuid();
        await SeedProjectAsync(
            projectId,
            spec: """{"version":3,"install":"apt-get install -y mongodb-org","setup":"npm install","services":[{"kind":"postgres-15","name":"postgres","values":{}}]}""");
        await SeedRuntimeAsync(projectId);

        var result = await CreateHandler().Handle(
            new GetProjectRuntimeSpecQuery(projectId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Spec.Install.Should().Be("apt-get install -y mongodb-org");
        result.Value.Spec.Setup.Should().Be("npm install");
    }

    // ----------------------------------------------------------------------
    // Tiny capturing logger so the warn-on-malformed assertion has something
    // to inspect. Avoid pulling in a heavier logging-test library for one
    // test — the surface is small.
    // ----------------------------------------------------------------------

    private sealed class TestLogger<T> : ILogger<T>
    {
        public List<LogRecord> Records { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Records.Add(new LogRecord(logLevel, formatter(state, exception)));
        }
    }

    private sealed record LogRecord(LogLevel Level, string Message);
}
