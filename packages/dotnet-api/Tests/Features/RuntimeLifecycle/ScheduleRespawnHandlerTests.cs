using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Source.Features.RuntimeLifecycle.Events;
using Source.Features.RuntimeLifecycle.Jobs;
using Source.Features.RuntimeLifecycle.Models;
using Source.Infrastructure;
using Source.Infrastructure.Interceptors;

namespace Api.Tests.Features.RuntimeLifecycle;

/// <summary>
/// Unit tests for <c>ScheduleRespawnHandler</c>. We register the handler via
/// MediatR and publish <see cref="RuntimeStateChanged"/> through the standard
/// pipeline, mirroring how the production code reaches it (through
/// <c>DomainEventInterceptor.SavedChangesAsync</c>).
///
/// <para>Hangfire's <see cref="IBackgroundJobClient"/> is mocked. The
/// extension method <c>Schedule&lt;T&gt;(Expression, TimeSpan)</c> ultimately
/// calls <see cref="IBackgroundJobClient.Create(Job, IState)"/> with a
/// <see cref="ScheduledState"/>; the tests assert on the captured
/// <see cref="ScheduledState.EnqueueAt"/> to confirm the backoff schedule.</para>
/// </summary>
public class ScheduleRespawnHandlerTests : IDisposable
{
    private readonly ServiceProvider _provider;
    private readonly ApplicationDbContext _db;
    private readonly IMediator _mediator;
    private readonly Mock<IBackgroundJobClient> _backgroundJobs;

    public ScheduleRespawnHandlerTests()
    {
        var dbName = Guid.NewGuid().ToString();
        var services = new ServiceCollection();

        services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));
        services.AddHttpContextAccessor();

        // SignalR services satisfy the auto-discovered BroadcastRuntimeStateChangedHandler.
        services.AddSignalR();

        services.AddMediatR(cfg =>
            cfg.RegisterServicesFromAssembly(typeof(RuntimeStateChanged).Assembly));

        _backgroundJobs = new Mock<IBackgroundJobClient>();
        services.AddSingleton<IBackgroundJobClient>(_backgroundJobs.Object);

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

    private async Task<ProjectRuntime> SeedRuntimeAsync(
        RuntimeState state = RuntimeState.Crashed,
        int respawnRetries = 0,
        string? flyMachineId = "mach_test")
    {
        var runtime = new ProjectRuntime
        {
            ProjectId = Guid.NewGuid(),
            Region = "arn",
            VolumeSizeGb = 1,
            State = state,
            RespawnRetries = respawnRetries,
            FlyMachineId = flyMachineId,
            FlyVolumeId = "vol_test",
            ImageDigest = "sha256:" + new string('a', 64),
        };
        _db.ProjectRuntimes.Add(runtime);
        await _db.SaveChangesAsync();
        return runtime;
    }

    /// <summary>
    /// Insert raw <see cref="RuntimeStateEvent"/> rows (no domain-event side
    /// effects) so we can stage prior crashes for the escalation test cases.
    /// We backdate <see cref="RuntimeStateEvent.CreatedAt"/> manually because
    /// the audit interceptor stamps it on insert.
    /// </summary>
    private async Task SeedAuditRowAsync(Guid runtimeId, DateTime createdAt, RuntimeState toState = RuntimeState.Crashed)
    {
        var row = new RuntimeStateEvent
        {
            Id = Guid.NewGuid(),
            RuntimeId = runtimeId,
            FromState = RuntimeState.Online,
            ToState = toState,
            Reason = "test:seed",
            TriggeredBy = "test",
        };
        _db.RuntimeStateEvents.Add(row);
        await _db.SaveChangesAsync();

        // Override the auto-stamped CreatedAt.
        row.CreatedAt = createdAt;
        await _db.SaveChangesAsync();
    }

    private static RuntimeStateChanged CrashedEvent(Guid runtimeId, Guid? projectId = null)
        => new(
            runtimeId: runtimeId,
            projectId: projectId ?? Guid.NewGuid(),
            branchId: Guid.NewGuid(),
            fromState: RuntimeState.Online,
            toState: RuntimeState.Crashed,
            reason: "test:crash",
            triggeredBy: "test",
            metadata: null);

    /// <summary>
    /// Captures the <see cref="ScheduledState"/> from a single
    /// <see cref="IBackgroundJobClient.Create"/> call; returns null if Create
    /// was never invoked.
    /// </summary>
    private (Job Job, ScheduledState State)? CapturedSchedule()
    {
        Job? capturedJob = null;
        IState? capturedState = null;

        _backgroundJobs.Invocations
            .Where(i => i.Method.Name == nameof(IBackgroundJobClient.Create))
            .ToList()
            .ForEach(inv =>
            {
                capturedJob = inv.Arguments[0] as Job;
                capturedState = inv.Arguments[1] as IState;
            });

        if (capturedJob is null || capturedState is not ScheduledState scheduled)
        {
            return null;
        }

        return (capturedJob, scheduled);
    }

    // ------------------------------------------------------------------
    // Tests
    // ------------------------------------------------------------------

    [Fact]
    public async Task Handle_NonCrashedTransition_DoesNothing()
    {
        var runtime = await SeedRuntimeAsync(state: RuntimeState.Online, respawnRetries: 0);

        // Publish an Online transition (not Crashed) — handler must no-op.
        await _mediator.Publish(new RuntimeStateChanged(
            runtimeId: runtime.Id,
            projectId: runtime.ProjectId,
            branchId: runtime.BranchId,
            fromState: RuntimeState.Bootstrapping,
            toState: RuntimeState.Online,
            reason: "daemon:ready",
            triggeredBy: "daemon",
            metadata: null));

        _backgroundJobs.Verify(
            x => x.Create(It.IsAny<Job>(), It.IsAny<IState>()),
            Times.Never,
            "non-Crashed transitions must not schedule a respawn");

        var refreshed = await _db.ProjectRuntimes.AsNoTracking().SingleAsync(r => r.Id == runtime.Id);
        refreshed.State.Should().Be(RuntimeState.Online, "no escalation either");
    }

    [Fact]
    public async Task Handle_FirstCrash_SchedulesWith3sDelay()
    {
        var runtime = await SeedRuntimeAsync(state: RuntimeState.Crashed, respawnRetries: 0);
        // Seed exactly the audit row that the *real* PersistRuntimeStateEventHandler
        // would have written before us (single Crashed row, fresh).
        await SeedAuditRowAsync(runtime.Id, DateTime.UtcNow);

        var before = DateTime.UtcNow;
        await _mediator.Publish(CrashedEvent(runtime.Id));
        var after = DateTime.UtcNow;

        var captured = CapturedSchedule();
        captured.Should().NotBeNull("a first crash must schedule a respawn");
        captured!.Value.Job.Type.Should().Be<RespawnRuntimeJob>(
            "the scheduled job must be RespawnRuntimeJob");

        // EnqueueAt should be ~3 seconds out (with a generous tolerance for clock noise).
        var delay = captured.Value.State.EnqueueAt - before;
        delay.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(2));
        delay.Should().BeLessThanOrEqualTo(after - before + TimeSpan.FromSeconds(4));
    }

    [Fact]
    public async Task Handle_SecondCrash_SchedulesWith30sDelay()
    {
        var runtime = await SeedRuntimeAsync(state: RuntimeState.Crashed, respawnRetries: 1);
        await SeedAuditRowAsync(runtime.Id, DateTime.UtcNow);

        var before = DateTime.UtcNow;
        await _mediator.Publish(CrashedEvent(runtime.Id));
        var after = DateTime.UtcNow;

        var captured = CapturedSchedule();
        captured.Should().NotBeNull();

        var delay = captured!.Value.State.EnqueueAt - before;
        delay.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(29));
        delay.Should().BeLessThanOrEqualTo(after - before + TimeSpan.FromSeconds(31));
    }

    [Fact]
    public async Task Handle_ThirdCrash_SchedulesWith300sDelay()
    {
        // RespawnRetries=2 → 5 minute backoff. Only 1 audit row → no escalation.
        var runtime = await SeedRuntimeAsync(state: RuntimeState.Crashed, respawnRetries: 2);
        await SeedAuditRowAsync(runtime.Id, DateTime.UtcNow);

        var before = DateTime.UtcNow;
        await _mediator.Publish(CrashedEvent(runtime.Id));
        var after = DateTime.UtcNow;

        var captured = CapturedSchedule();
        captured.Should().NotBeNull();

        var delay = captured!.Value.State.EnqueueAt - before;
        delay.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(299));
        delay.Should().BeLessThanOrEqualTo(after - before + TimeSpan.FromSeconds(301));
    }

    [Fact]
    public async Task Handle_ThirdCrashWithin1Hour_EscalatesToFailed()
    {
        // The escalation policy: 3 Crashed audit rows within 1 hour → Failed.
        // Seed two prior recent crashes plus the current one (3 total).
        var runtime = await SeedRuntimeAsync(state: RuntimeState.Crashed, respawnRetries: 2);
        await SeedAuditRowAsync(runtime.Id, DateTime.UtcNow.AddMinutes(-30));
        await SeedAuditRowAsync(runtime.Id, DateTime.UtcNow.AddMinutes(-10));
        // The current crash's audit row — what the real persistence handler
        // would have written immediately before us.
        await SeedAuditRowAsync(runtime.Id, DateTime.UtcNow);

        await _mediator.Publish(CrashedEvent(runtime.Id));

        // Runtime escalated to Failed.
        var refreshed = await _db.ProjectRuntimes.AsNoTracking().SingleAsync(r => r.Id == runtime.Id);
        refreshed.State.Should().Be(RuntimeState.Failed,
            "3 crashes within an hour must escalate to Failed");

        // No respawn scheduled.
        _backgroundJobs.Verify(
            x => x.Create(It.IsAny<Job>(), It.IsAny<IState>()),
            Times.Never,
            "an escalated runtime must not also be scheduled for respawn");

        // The escalation transition itself must have produced an audit row.
        var failedRows = await _db.RuntimeStateEvents.AsNoTracking()
            .Where(e => e.RuntimeId == runtime.Id && e.ToState == RuntimeState.Failed)
            .ToListAsync();
        failedRows.Should().HaveCount(1);
        failedRows.Single().Reason.Should().StartWith("respawn:exhausted");
        failedRows.Single().TriggeredBy.Should().Be("watcher:respawn");
    }

    [Fact]
    public async Task Handle_FourthCrashOlderThan1Hour_StillSchedules()
    {
        // Five prior crashes but all > 1 hour ago — outside the escalation window.
        // Plus the current crash (fresh). Recent count = 1, no escalation.
        var runtime = await SeedRuntimeAsync(state: RuntimeState.Crashed, respawnRetries: 0);
        for (var i = 0; i < 5; i++)
        {
            await SeedAuditRowAsync(runtime.Id, DateTime.UtcNow.AddHours(-2 - i));
        }
        await SeedAuditRowAsync(runtime.Id, DateTime.UtcNow);

        await _mediator.Publish(CrashedEvent(runtime.Id));

        // Runtime stays Crashed (not escalated) and a respawn is scheduled.
        var refreshed = await _db.ProjectRuntimes.AsNoTracking().SingleAsync(r => r.Id == runtime.Id);
        refreshed.State.Should().Be(RuntimeState.Crashed,
            "old crashes outside the 1h window must not trigger escalation");

        var captured = CapturedSchedule();
        captured.Should().NotBeNull("the runtime is still respawn-eligible");
    }

    [Fact]
    public async Task Handle_RuntimeNoLongerExists_NoOp()
    {
        // The event references a runtime id that's not in the DB (hard-deleted).
        // Handler must not throw or schedule.
        var ghostId = Guid.NewGuid();

        var act = async () => await _mediator.Publish(CrashedEvent(ghostId));

        await act.Should().NotThrowAsync(
            "a missing runtime row is a recoverable race, not a crash");

        _backgroundJobs.Verify(
            x => x.Create(It.IsAny<Job>(), It.IsAny<IState>()),
            Times.Never,
            "no runtime to respawn means no schedule");
    }
}
