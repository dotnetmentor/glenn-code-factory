using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Source.Features.RuntimeBootstrap.Models;
using Source.Features.RuntimeLifecycle.Events;
using Source.Features.RuntimeLifecycle.Jobs;
using Source.Features.RuntimeLifecycle.Models;
using Source.Infrastructure;
using Source.Infrastructure.Interceptors;

namespace Api.Tests.Features.RuntimeLifecycle;

/// <summary>
/// Unit tests for <see cref="RuntimeJanitorJob"/>. The janitor talks only to
/// Postgres (no Fly), so the bootstrap is simpler than the reconciler's: we
/// build a wired <see cref="ApplicationDbContext"/> with the
/// <see cref="DomainEventInterceptor"/> + MediatR registered (for parity with
/// the reconciler bootstrap and to keep audit-row writes flowing if any
/// transitions ever fire during a test).
/// </summary>
public class RuntimeJanitorJobTests : IDisposable
{
    private readonly ServiceProvider _provider;
    private readonly ApplicationDbContext _db;

    public RuntimeJanitorJobTests()
    {
        var dbName = Guid.NewGuid().ToString();
        var services = new ServiceCollection();

        services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));
        services.AddHttpContextAccessor();

        services.AddMediatR(cfg =>
            cfg.RegisterServicesFromAssembly(typeof(RuntimeStateChanged).Assembly));

        // ScheduleRespawnHandler is auto-discovered and depends on IBackgroundJobClient.
        // The janitor never produces a Crashed transition, but DI must still be able
        // to construct the handler at startup.
        services.AddSingleton<IBackgroundJobClient>(new Mock<IBackgroundJobClient>().Object);

        services.AddScoped<DomainEventInterceptor>();

        services.AddDbContext<ApplicationDbContext>((sp, options) =>
        {
            options.UseInMemoryDatabase(dbName);
            options.AddInterceptors(sp.GetRequiredService<DomainEventInterceptor>());
        });

        _provider = services.BuildServiceProvider();
        _db = _provider.GetRequiredService<ApplicationDbContext>();
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _provider.Dispose();
        GC.SuppressFinalize(this);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private RuntimeJanitorJob CreateJob() =>
        new(_db, NullLogger<RuntimeJanitorJob>.Instance);

    /// <summary>
    /// Seed a runtime with explicit lifecycle fields, bypassing the audit-field
    /// auto-stamp by overwriting <c>DeletedAt</c> after the initial save. The
    /// <c>ISoftDelete</c> hook in <c>ApplicationDbContext</c> stamps
    /// <c>DeletedAt = UtcNow</c> when <c>IsDeleted</c> flips on save, which
    /// would clobber any back-dated value we passed in. Saving twice (insert,
    /// then back-date) is the cleanest way to set up a test row that looks
    /// like it was deleted N days ago.
    /// </summary>
    private async Task<ProjectRuntime> SeedRuntimeAsync(
        RuntimeState state,
        DateTime? deletedAt = null)
    {
        var runtime = new ProjectRuntime
        {
            ProjectId = Guid.NewGuid(),
            Region = "arn",
            VolumeSizeGb = 1,
            State = state,
            FlyMachineId = "mach_" + Guid.NewGuid().ToString("N")[..8],
        };

        if (deletedAt.HasValue)
        {
            runtime.IsDeleted = true;
        }

        _db.ProjectRuntimes.Add(runtime);
        await _db.SaveChangesAsync();

        if (deletedAt.HasValue)
        {
            // Back-date DeletedAt directly in the DB without re-running the
            // soft-delete hook: detach + reattach + Modified.
            runtime.DeletedAt = deletedAt.Value;
            _db.Entry(runtime).Property(r => r.DeletedAt).IsModified = true;
            await _db.SaveChangesAsync();
        }

        return runtime;
    }

    private async Task SeedStateEventAsync(Guid runtimeId, RuntimeState? from, RuntimeState to)
    {
        _db.RuntimeStateEvents.Add(new RuntimeStateEvent
        {
            RuntimeId = runtimeId,
            FromState = from,
            ToState = to,
            Reason = "test:seed",
            TriggeredBy = "test",
        });
        await _db.SaveChangesAsync();
    }

    private async Task SeedBootstrapRunAsync(Guid runtimeId)
    {
        _db.BootstrapRuns.Add(new BootstrapRun
        {
            RuntimeId = runtimeId,
            StartedAt = DateTime.UtcNow.AddDays(-40),
            EndedAt = DateTime.UtcNow.AddDays(-40).AddMinutes(2),
            FinalStage = BootstrapStage.Connecting,
            Success = true,
            BootstrapVersion = "v1",
        });
        await _db.SaveChangesAsync();
    }

    // ------------------------------------------------------------------
    // Tests
    // ------------------------------------------------------------------

    [Fact]
    public async Task Run_DeletedOlderThan30Days_HardDeletes()
    {
        var runtime = await SeedRuntimeAsync(
            RuntimeState.Deleted,
            deletedAt: DateTime.UtcNow.AddDays(-31));

        await CreateJob().Run(CancellationToken.None);

        // IgnoreQueryFilters: if it's truly hard-deleted even ignore-filters
        // returns null. (If we'd only soft-deleted, this query would still
        // find it.)
        var found = await _db.ProjectRuntimes
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.Id == runtime.Id);

        found.Should().BeNull("a Deleted runtime older than 30 days must be hard-deleted");
    }

    [Fact]
    public async Task Run_DeletedWithin30Days_LeavesAlone()
    {
        var runtime = await SeedRuntimeAsync(
            RuntimeState.Deleted,
            deletedAt: DateTime.UtcNow.AddDays(-29));

        await CreateJob().Run(CancellationToken.None);

        var found = await _db.ProjectRuntimes
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.Id == runtime.Id);

        found.Should().NotBeNull(
            "a Deleted runtime still inside the 30-day window must survive");
        found!.State.Should().Be(RuntimeState.Deleted);
    }

    [Fact]
    public async Task Run_OtherStates_NotAffected()
    {
        // Seed runtimes in a variety of non-Deleted states. Some have a back-dated
        // DeletedAt to prove we're really keying off the State column, not the
        // soft-delete metadata.
        var pending = await SeedRuntimeAsync(RuntimeState.Pending);
        var online = await SeedRuntimeAsync(RuntimeState.Online);
        var crashed = await SeedRuntimeAsync(
            RuntimeState.Crashed,
            deletedAt: DateTime.UtcNow.AddDays(-90));
        var failed = await SeedRuntimeAsync(
            RuntimeState.Failed,
            deletedAt: DateTime.UtcNow.AddDays(-365));

        await CreateJob().Run(CancellationToken.None);

        var ids = new[] { pending.Id, online.Id, crashed.Id, failed.Id };
        var survivors = await _db.ProjectRuntimes
            .IgnoreQueryFilters()
            .Where(r => ids.Contains(r.Id))
            .ToListAsync();

        survivors.Should().HaveCount(4,
            "only State=Deleted rows are eligible — all other states must be ignored regardless of DeletedAt");
    }

    [Fact]
    public async Task Run_PreservesAuditRows()
    {
        // Core forensic-trail test: the runtime row goes, the audit rows stay.
        var runtime = await SeedRuntimeAsync(
            RuntimeState.Deleted,
            deletedAt: DateTime.UtcNow.AddDays(-31));

        await SeedStateEventAsync(runtime.Id, RuntimeState.Online, RuntimeState.Suspending);
        await SeedStateEventAsync(runtime.Id, RuntimeState.Suspending, RuntimeState.Suspended);
        await SeedStateEventAsync(runtime.Id, RuntimeState.Suspended, RuntimeState.Deleting);
        await SeedStateEventAsync(runtime.Id, RuntimeState.Deleting, RuntimeState.Deleted);
        await SeedBootstrapRunAsync(runtime.Id);

        await CreateJob().Run(CancellationToken.None);

        var runtimeFound = await _db.ProjectRuntimes
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.Id == runtime.Id);
        runtimeFound.Should().BeNull("the eligible runtime must be hard-deleted");

        var stateEvents = await _db.RuntimeStateEvents
            .Where(e => e.RuntimeId == runtime.Id)
            .ToListAsync();
        stateEvents.Should().HaveCount(4,
            "audit rows must outlive the runtime — no FK cascade is configured precisely to keep this trail");

        var bootstrapRuns = await _db.BootstrapRuns
            .Where(b => b.RuntimeId == runtime.Id)
            .ToListAsync();
        bootstrapRuns.Should().HaveCount(1,
            "BootstrapRuns are append-only audit and must survive runtime hard-delete");
    }

    [Fact]
    public async Task Run_EmptyTable_NoOp()
    {
        // No runtimes at all. Job must be a clean no-op.
        var act = async () => await CreateJob().Run(CancellationToken.None);

        await act.Should().NotThrowAsync();

        (await _db.ProjectRuntimes.IgnoreQueryFilters().CountAsync())
            .Should().Be(0);
        (await _db.RuntimeStateEvents.CountAsync())
            .Should().Be(0);
    }

    [Fact]
    public async Task Run_MixedBatch()
    {
        // 2 eligible (old + Deleted)
        var oldDeleted1 = await SeedRuntimeAsync(
            RuntimeState.Deleted,
            deletedAt: DateTime.UtcNow.AddDays(-31));
        var oldDeleted2 = await SeedRuntimeAsync(
            RuntimeState.Deleted,
            deletedAt: DateTime.UtcNow.AddDays(-90));

        // 1 ineligible by date (recent + Deleted)
        var recentDeleted = await SeedRuntimeAsync(
            RuntimeState.Deleted,
            deletedAt: DateTime.UtcNow.AddDays(-10));

        // 1 ineligible by state (old + Crashed — DeletedAt is set but State isn't Deleted)
        var oldCrashed = await SeedRuntimeAsync(
            RuntimeState.Crashed,
            deletedAt: DateTime.UtcNow.AddDays(-365));

        await CreateJob().Run(CancellationToken.None);

        var allIds = new[] { oldDeleted1.Id, oldDeleted2.Id, recentDeleted.Id, oldCrashed.Id };
        var survivors = await _db.ProjectRuntimes
            .IgnoreQueryFilters()
            .Where(r => allIds.Contains(r.Id))
            .Select(r => r.Id)
            .ToListAsync();

        survivors.Should().BeEquivalentTo(new[] { recentDeleted.Id, oldCrashed.Id },
            "only the two old + Deleted rows should be hard-deleted; recent-Deleted and old-Crashed survive");
    }

    // ------------------------------------------------------------------
    // [DisableConcurrentExecution] presence — guards against accidental removal.
    // ------------------------------------------------------------------

    [Fact]
    public void Run_HasDisableConcurrentExecutionAttribute()
    {
        var method = typeof(RuntimeJanitorJob).GetMethod(nameof(RuntimeJanitorJob.Run))!;
        var attr = method.GetCustomAttributes(typeof(Hangfire.DisableConcurrentExecutionAttribute), inherit: false);
        attr.Should().NotBeEmpty(
            "two Hangfire workers must not race on the same janitor pass — the attribute is the lock");
    }
}
