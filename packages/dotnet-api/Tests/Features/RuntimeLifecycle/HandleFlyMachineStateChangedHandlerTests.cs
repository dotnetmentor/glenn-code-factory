using Hangfire;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Source.Features.FlyManagement.Events;
using Source.Features.RuntimeLifecycle.Events;
using Source.Features.RuntimeLifecycle.Models;
using Source.Infrastructure;
using Source.Infrastructure.Interceptors;

namespace Api.Tests.Features.RuntimeLifecycle;

/// <summary>
/// Unit tests for <c>HandleFlyMachineStateChangedHandler</c>. We build a real DI
/// container with <see cref="ApplicationDbContext"/> + <see cref="DomainEventInterceptor"/>
/// + MediatR (mirroring <see cref="RuntimeProvisionerJobTests"/>) so that:
///
/// <list type="bullet">
///   <item>publishing <see cref="FlyMachineStateChanged"/> via <see cref="IMediator"/>
///         actually invokes the handler under test;</item>
///   <item>the resulting <see cref="RuntimeStateChanged"/> domain event flows through
///         <c>PersistRuntimeStateEventHandler</c>, exercising the audit-row contract
///         end-to-end (the last test asserts on this).</item>
/// </list>
/// </summary>
public class HandleFlyMachineStateChangedHandlerTests : IDisposable
{
    private readonly ServiceProvider _provider;
    private readonly ApplicationDbContext _db;
    private readonly IMediator _mediator;

    public HandleFlyMachineStateChangedHandlerTests()
    {
        var dbName = Guid.NewGuid().ToString();
        var services = new ServiceCollection();

        services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));
        services.AddHttpContextAccessor();

        // SignalR services satisfy the auto-discovered BroadcastRuntimeStateChangedHandler,
        // which depends on IHubContext<AgentHub, IAgentClient>. The hub never fires
        // in tests (no connected clients) but DI must be able to construct the handler.
        services.AddSignalR();

        // Register handlers from the api assembly — covers HandleFlyMachineStateChangedHandler
        // and PersistRuntimeStateEventHandler so events propagate end-to-end.
        services.AddMediatR(cfg =>
            cfg.RegisterServicesFromAssembly(typeof(RuntimeStateChanged).Assembly));

        // ScheduleRespawnHandler is auto-discovered and depends on IBackgroundJobClient.
        // Tests here exercise Crashed transitions; we install a noop mock so the
        // handler can be constructed and run, but never assert on its scheduling
        // behaviour (that lives in ScheduleRespawnHandlerTests).
        services.AddSingleton<IBackgroundJobClient>(new Mock<IBackgroundJobClient>().Object);

        services.AddScoped<DomainEventInterceptor>();

        services.AddDbContext<ApplicationDbContext>((sp, options) =>
        {
            options.UseInMemoryDatabase(dbName);
            options.AddInterceptors(sp.GetRequiredService<DomainEventInterceptor>());
        });

        _provider = services.BuildServiceProvider();
        _db = _provider.GetRequiredService<ApplicationDbContext>();
        _mediator = _provider.GetRequiredService<IMediator>();
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

    private async Task<Guid> SeedRuntimeAsync(RuntimeState state, string machineId)
    {
        var runtime = new ProjectRuntime
        {
            ProjectId = Guid.NewGuid(),
            Region = "arn",
            VolumeSizeGb = 1,
            State = state,
            FlyMachineId = machineId,
        };
        _db.ProjectRuntimes.Add(runtime);
        await _db.SaveChangesAsync();
        return runtime.Id;
    }

    private static FlyMachineStateChanged Event(
        string machineId,
        string newState,
        string? previousState = null,
        string? flyEventId = null)
        => new(
            MachineId: machineId,
            NewState: newState,
            PreviousState: previousState,
            OccurredAt: DateTime.UtcNow,
            FlyEventId: flyEventId ?? Guid.NewGuid().ToString());

    // ------------------------------------------------------------------
    // Mapping tests — one per legal (Fly state, current state) pair.
    // ------------------------------------------------------------------

    [Fact]
    public async Task Started_FromBooting_TransitionsToBootstrapping()
    {
        var id = await SeedRuntimeAsync(RuntimeState.Booting, "mach_1");

        await _mediator.Publish(Event("mach_1", "started", previousState: "starting"));

        var refreshed = await _db.ProjectRuntimes.AsNoTracking().SingleAsync(r => r.Id == id);
        refreshed.State.Should().Be(RuntimeState.Bootstrapping);
    }

    [Fact]
    public async Task Started_FromWaking_TransitionsToBootstrapping()
    {
        // Daemon-as-downloadable: Waking + fly:started no longer means
        // "Online" — the daemon still has to download + verify the bundle
        // and report back via RuntimeReady. Webhook hands off to
        // Bootstrapping; the Bootstrapping → Online edge is owned exclusively
        // by the daemon's hub call.
        var id = await SeedRuntimeAsync(RuntimeState.Waking, "mach_2");

        await _mediator.Publish(Event("mach_2", "started", previousState: "stopped"));

        var refreshed = await _db.ProjectRuntimes.AsNoTracking().SingleAsync(r => r.Id == id);
        refreshed.State.Should().Be(RuntimeState.Bootstrapping);
    }

    [Fact]
    public async Task Stopped_FromSuspending_TransitionsToSuspended()
    {
        var id = await SeedRuntimeAsync(RuntimeState.Suspending, "mach_3");

        await _mediator.Publish(Event("mach_3", "stopped", previousState: "started"));

        var refreshed = await _db.ProjectRuntimes.AsNoTracking().SingleAsync(r => r.Id == id);
        refreshed.State.Should().Be(RuntimeState.Suspended);
    }

    [Fact]
    public async Task Destroyed_FromDeleting_TransitionsToDeleted()
    {
        var id = await SeedRuntimeAsync(RuntimeState.Deleting, "mach_4");

        await _mediator.Publish(Event("mach_4", "destroyed", previousState: "stopped"));

        var refreshed = await _db.ProjectRuntimes.AsNoTracking().SingleAsync(r => r.Id == id);
        refreshed.State.Should().Be(RuntimeState.Deleted);
    }

    [Fact]
    public async Task Crashed_FromOnline_TransitionsToCrashed_AndIncrementsRespawnRetries()
    {
        var id = await SeedRuntimeAsync(RuntimeState.Online, "mach_5");

        await _mediator.Publish(Event("mach_5", "crashed", previousState: "started"));

        var refreshed = await _db.ProjectRuntimes.AsNoTracking().SingleAsync(r => r.Id == id);
        refreshed.State.Should().Be(RuntimeState.Crashed);
        refreshed.RespawnRetries.Should().Be(1, "the supervisor uses this counter to enforce the retry budget");
    }

    // ------------------------------------------------------------------
    // No-op cases — illegal mappings, unknown machines, orthogonal states.
    // ------------------------------------------------------------------

    [Fact]
    public async Task Crashed_FromCrashed_NoOp()
    {
        // Runtime already in Crashed — Crashed→Crashed is not a legal self-loop in
        // RuntimeStateMachine and not in our mapping table either, so the handler
        // should log + return without mutating.
        var id = await SeedRuntimeAsync(RuntimeState.Crashed, "mach_6");
        var auditBefore = await _db.RuntimeStateEvents.CountAsync(e => e.RuntimeId == id);

        await _mediator.Publish(Event("mach_6", "crashed", previousState: "started"));

        var refreshed = await _db.ProjectRuntimes.AsNoTracking().SingleAsync(r => r.Id == id);
        refreshed.State.Should().Be(RuntimeState.Crashed, "no mapping for crashed-while-Crashed; handler should no-op");
        refreshed.RespawnRetries.Should().Be(0, "no transition means we don't bump the retry counter");

        var auditAfter = await _db.RuntimeStateEvents.CountAsync(e => e.RuntimeId == id);
        auditAfter.Should().Be(auditBefore, "no audit row should be written for a no-op");
    }

    [Fact]
    public async Task UnknownMachineId_NoOp()
    {
        // Seed a runtime with a different machine id so we can prove no rows were
        // touched by the handler (count comparison, not just the seeded one).
        var id = await SeedRuntimeAsync(RuntimeState.Online, "mach_known");
        var stateBefore = (await _db.ProjectRuntimes.AsNoTracking().SingleAsync(r => r.Id == id)).State;
        var auditBefore = await _db.RuntimeStateEvents.CountAsync();

        await _mediator.Publish(Event("mach_unknown", "crashed", previousState: "started"));

        var refreshed = await _db.ProjectRuntimes.AsNoTracking().SingleAsync(r => r.Id == id);
        refreshed.State.Should().Be(stateBefore, "the handler must not mutate any runtime when the machine is unknown");

        (await _db.RuntimeStateEvents.CountAsync()).Should().Be(auditBefore);
    }

    [Fact]
    public async Task OrthogonalState_NoOp()
    {
        // Fly says "started" while runtime is already Online. Not in our mapping table;
        // we don't want a Online→Online no-op or any other orthogonal mutation.
        var id = await SeedRuntimeAsync(RuntimeState.Online, "mach_7");

        await _mediator.Publish(Event("mach_7", "started", previousState: "started"));

        var refreshed = await _db.ProjectRuntimes.AsNoTracking().SingleAsync(r => r.Id == id);
        refreshed.State.Should().Be(RuntimeState.Online);
        (await _db.RuntimeStateEvents.CountAsync(e => e.RuntimeId == id)).Should().Be(0);
    }

    // ------------------------------------------------------------------
    // End-to-end audit row check — verifies the chain
    // FlyMachineStateChanged → ProjectRuntime.TransitionTo → RuntimeStateChanged
    // → PersistRuntimeStateEventHandler → RuntimeStateEvent row.
    // ------------------------------------------------------------------

    [Fact]
    public async Task Crashed_FromOnline_WritesRuntimeStateEventRow()
    {
        var id = await SeedRuntimeAsync(RuntimeState.Online, "mach_8");
        var flyEventId = "fly_evt_abc";

        await _mediator.Publish(Event("mach_8", "crashed", previousState: "started", flyEventId: flyEventId));

        var events = await _db.RuntimeStateEvents.AsNoTracking()
            .Where(e => e.RuntimeId == id)
            .ToListAsync();
        events.Should().HaveCount(1);

        var audit = events.Single();
        audit.FromState.Should().Be(RuntimeState.Online);
        audit.ToState.Should().Be(RuntimeState.Crashed);
        audit.Reason.Should().Be("fly_webhook:machine.crashed");
        audit.TriggeredBy.Should().Be("fly:webhook");
        audit.Metadata.Should().NotBeNullOrWhiteSpace();
        audit.Metadata!.Should().Contain(flyEventId, "the metadata json should carry the FlyEventId for traceability");
        audit.Metadata!.Should().Contain("crashed", "the metadata json should carry the lower-cased Fly state");
    }
}
