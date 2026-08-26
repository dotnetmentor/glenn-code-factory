using Api.Tests.Infrastructure;
using Hangfire;
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
/// Unit tests for <see cref="HeartbeatWatcherJob"/>. The bootstrap mirrors
/// <see cref="RuntimeReconcilerJobTests"/>: a wired
/// <see cref="ApplicationDbContext"/> with the
/// <see cref="DomainEventInterceptor"/> + MediatR registered, plus
/// <c>AddSignalR</c> so the auto-discovered <c>BroadcastRuntimeStateChangedHandler</c>
/// (which depends on <c>IHubContext</c>) can be activated by DI.
///
/// <para>Tests target <see cref="HeartbeatWatcherJob.ScanOnce"/> directly. The
/// public <see cref="HeartbeatWatcherJob.Run"/> contains a 12 x 5 s loop that
/// would block tests for the better part of a minute.</para>
/// </summary>
public class HeartbeatWatcherJobTests : IDisposable
{
    private readonly ServiceProvider _provider;
    private readonly ApplicationDbContext _db;
    // FakeClock starts pinned at DateTime.UtcNow so existing tests that seed
    // LastHeartbeatAt with `DateTime.UtcNow.AddSeconds(-45)` still get a -45s
    // delta against the job's clock (the two reads sit within microseconds of
    // each other). Tests that need to fast-forward past the 5-minute bootstrap
    // window call `_clock.Advance(TimeSpan.FromMinutes(N))` after seeding.
    private readonly FakeClock _clock = new(DateTime.UtcNow);

    public HeartbeatWatcherJobTests()
    {
        var dbName = Guid.NewGuid().ToString();
        var services = new ServiceCollection();

        services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));
        services.AddHttpContextAccessor();

        // SignalR services satisfy the auto-discovered BroadcastRuntimeStateChangedHandler,
        // which depends on IHubContext<AgentHub, IAgentClient>. The hub never fires
        // in tests (no connected clients) but DI must be able to construct the handler.
        services.AddSignalR();

        services.AddMediatR(cfg =>
            cfg.RegisterServicesFromAssembly(typeof(RuntimeStateChanged).Assembly));

        // ScheduleRespawnHandler is auto-discovered by MediatR and depends on
        // IBackgroundJobClient. The hub never schedules anything in tests that
        // don't intend to crash a runtime, but DI must be able to construct it.
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

    /// <summary>
    /// The watcher's OWN job client (branch 3's rescue enqueue) — distinct from
    /// the container-registered mock that satisfies ScheduleRespawnHandler.
    /// </summary>
    private readonly Mock<IBackgroundJobClient> _watcherBackgroundJobs = new();

    private HeartbeatWatcherJob CreateJob() =>
        new(_db, _clock, _watcherBackgroundJobs.Object, NullLogger<HeartbeatWatcherJob>.Instance);

    /// <summary>
    /// Seed a runtime in the requested state with an explicit
    /// <see cref="ProjectRuntime.LastHeartbeatAt"/>. The audit-field auto-stamp
    /// doesn't touch <c>LastHeartbeatAt</c>, so a single save is enough.
    /// </summary>
    private async Task<ProjectRuntime> SeedRuntimeAsync(
        RuntimeState state,
        DateTime? lastHeartbeatAt,
        DateTime? lastBootstrapActivityAt = null)
    {
        var runtime = new ProjectRuntime
        {
            ProjectId = Guid.NewGuid(),
            Region = "arn",
            VolumeSizeGb = 1,
            State = state,
            BoxId = "box_" + Guid.NewGuid().ToString("N")[..8],
            LastHeartbeatAt = lastHeartbeatAt,
            LastBootstrapActivityAt = lastBootstrapActivityAt,
        };
        _db.ProjectRuntimes.Add(runtime);
        await _db.SaveChangesAsync();
        return runtime;
    }


    // ------------------------------------------------------------------
    // Tests
    // ------------------------------------------------------------------

    [Fact]
    public async Task ScanOnce_OnlineRuntimePastThreshold_TransitionsToCrashed()
    {
        // 45 seconds of silence is well past the 30-second threshold.
        var runtime = await SeedRuntimeAsync(
            RuntimeState.Online,
            lastHeartbeatAt: DateTime.UtcNow.AddSeconds(-90));

        await CreateJob().ScanOnce(CancellationToken.None);

        var refreshed = await _db.ProjectRuntimes.AsNoTracking().SingleAsync(r => r.Id == runtime.Id);
        refreshed.State.Should().Be(RuntimeState.Crashed,
            "an Online runtime silent past the threshold must be flagged Crashed");

        var events = await _db.RuntimeStateEvents.AsNoTracking()
            .Where(e => e.RuntimeId == runtime.Id)
            .ToListAsync();
        events.Should().HaveCount(1, "the Crashed transition must produce exactly one audit row");

        var audit = events.Single();
        audit.FromState.Should().Be(RuntimeState.Online);
        audit.ToState.Should().Be(RuntimeState.Crashed);
        audit.Reason.Should().Be("heartbeat:missed");
        audit.TriggeredBy.Should().Be("watcher:heartbeat");
        audit.Metadata.Should().NotBeNull();
        audit.Metadata!.Should().Contain("secondsSilent",
            "metadata must include the silent-window length so operators can diagnose flakiness");
        audit.Metadata!.Should().Contain("lastHeartbeatAt");
    }

    [Fact]
    public async Task ScanOnce_OnlineRuntimeWithinThreshold_NotTouched()
    {
        // 20 seconds is below the 30-second threshold — fresh enough to leave alone.
        var runtime = await SeedRuntimeAsync(
            RuntimeState.Online,
            lastHeartbeatAt: DateTime.UtcNow.AddSeconds(-20));

        await CreateJob().ScanOnce(CancellationToken.None);

        var refreshed = await _db.ProjectRuntimes.AsNoTracking().SingleAsync(r => r.Id == runtime.Id);
        refreshed.State.Should().Be(RuntimeState.Online,
            "a heartbeat inside the threshold means the runtime is still healthy");

        (await _db.RuntimeStateEvents.CountAsync(e => e.RuntimeId == runtime.Id))
            .Should().Be(0, "no transition means no audit row");
    }

    [Fact]
    public async Task ScanOnce_BootstrappingRuntimePastThreshold_TransitionsToCrashed()
    {
        // Bootstrapping runtimes are also expected to heartbeat once the daemon
        // is alive. A silent Bootstrapping daemon is just as broken as a silent
        // Online one.
        var runtime = await SeedRuntimeAsync(
            RuntimeState.Bootstrapping,
            lastHeartbeatAt: DateTime.UtcNow.AddSeconds(-90));

        await CreateJob().ScanOnce(CancellationToken.None);

        var refreshed = await _db.ProjectRuntimes.AsNoTracking().SingleAsync(r => r.Id == runtime.Id);
        refreshed.State.Should().Be(RuntimeState.Crashed,
            "Bootstrapping daemons are scanned too — Bootstrapping -> Crashed is a legal edge");

        var audit = await _db.RuntimeStateEvents.AsNoTracking()
            .SingleAsync(e => e.RuntimeId == runtime.Id);
        audit.FromState.Should().Be(RuntimeState.Bootstrapping);
        audit.ToState.Should().Be(RuntimeState.Crashed);
        audit.Reason.Should().Be("heartbeat:missed");
    }

    [Fact]
    public async Task ScanOnce_WakingRuntimePastThreshold_TransitionsToCrashed()
    {
        // Waking runtimes also expect heartbeats — Waking -> Crashed is legal.
        var runtime = await SeedRuntimeAsync(
            RuntimeState.Waking,
            lastHeartbeatAt: DateTime.UtcNow.AddSeconds(-90));

        await CreateJob().ScanOnce(CancellationToken.None);

        var refreshed = await _db.ProjectRuntimes.AsNoTracking().SingleAsync(r => r.Id == runtime.Id);
        refreshed.State.Should().Be(RuntimeState.Crashed);

        var audit = await _db.RuntimeStateEvents.AsNoTracking()
            .SingleAsync(e => e.RuntimeId == runtime.Id);
        audit.FromState.Should().Be(RuntimeState.Waking);
        audit.ToState.Should().Be(RuntimeState.Crashed);
    }

    [Fact]
    public async Task ScanOnce_SuspendedRuntime_NotTouched()
    {
        // Suspended daemons don't heartbeat — they're stopped on purpose. Even
        // an ancient LastHeartbeatAt is irrelevant: the row must not be touched.
        var runtime = await SeedRuntimeAsync(
            RuntimeState.Suspended,
            lastHeartbeatAt: DateTime.UtcNow.AddDays(-7));

        await CreateJob().ScanOnce(CancellationToken.None);

        var refreshed = await _db.ProjectRuntimes.AsNoTracking().SingleAsync(r => r.Id == runtime.Id);
        refreshed.State.Should().Be(RuntimeState.Suspended,
            "Suspended runtimes are intentionally silent and out of scope for the watcher");

        (await _db.RuntimeStateEvents.CountAsync(e => e.RuntimeId == runtime.Id))
            .Should().Be(0);
    }

    [Fact]
    public async Task ScanOnce_BootingRuntime_NotTouched()
    {
        // Booting hasn't connected the daemon yet — there is nothing to heartbeat.
        // Even with a (nonsensical) ancient LastHeartbeatAt, the watcher must skip.
        var runtime = await SeedRuntimeAsync(
            RuntimeState.Booting,
            lastHeartbeatAt: DateTime.UtcNow.AddSeconds(-300));

        await CreateJob().ScanOnce(CancellationToken.None);

        var refreshed = await _db.ProjectRuntimes.AsNoTracking().SingleAsync(r => r.Id == runtime.Id);
        refreshed.State.Should().Be(RuntimeState.Booting,
            "Booting runtimes have no daemon yet and are out of scope");

        (await _db.RuntimeStateEvents.CountAsync(e => e.RuntimeId == runtime.Id))
            .Should().Be(0);
    }

    [Fact]
    public async Task ScanOnce_RuntimeWithNullLastHeartbeatAt_NotTouched()
    {
        // Online state but no heartbeat yet (just transitioned in). The query's
        // LastHeartbeatAt != null filter must skip these — flagging them Crashed
        // would falsely murder a runtime that simply hasn't sent its first beat.
        var runtime = await SeedRuntimeAsync(
            RuntimeState.Online,
            lastHeartbeatAt: null);

        await CreateJob().ScanOnce(CancellationToken.None);

        var refreshed = await _db.ProjectRuntimes.AsNoTracking().SingleAsync(r => r.Id == runtime.Id);
        refreshed.State.Should().Be(RuntimeState.Online,
            "null LastHeartbeatAt means 'no beat seen yet' — never a crash signal");

        (await _db.RuntimeStateEvents.CountAsync(e => e.RuntimeId == runtime.Id))
            .Should().Be(0);
    }

    [Fact]
    public async Task ScanOnce_MultipleStaleRuntimes_AllFlagged()
    {
        // 3 stale Online + 1 fresh Online + 1 stale Suspended.
        // Expect exactly 3 transitions: the stale Onlines. The fresh Online and
        // the stale Suspended must remain untouched. Threshold is 30s — all three
        // "stale" values are comfortably past it (45/60/120 s), and the "fresh"
        // value (-20s) is comfortably inside.
        var stale1 = await SeedRuntimeAsync(RuntimeState.Online, DateTime.UtcNow.AddSeconds(-90));
        var stale2 = await SeedRuntimeAsync(RuntimeState.Online, DateTime.UtcNow.AddSeconds(-90));
        var stale3 = await SeedRuntimeAsync(RuntimeState.Online, DateTime.UtcNow.AddSeconds(-120));
        var fresh = await SeedRuntimeAsync(RuntimeState.Online, DateTime.UtcNow.AddSeconds(-20));
        var suspended = await SeedRuntimeAsync(RuntimeState.Suspended, DateTime.UtcNow.AddSeconds(-300));

        await CreateJob().ScanOnce(CancellationToken.None);

        async Task<RuntimeState> StateOf(Guid id) =>
            (await _db.ProjectRuntimes.AsNoTracking().SingleAsync(r => r.Id == id)).State;

        (await StateOf(stale1.Id)).Should().Be(RuntimeState.Crashed);
        (await StateOf(stale2.Id)).Should().Be(RuntimeState.Crashed);
        (await StateOf(stale3.Id)).Should().Be(RuntimeState.Crashed);
        (await StateOf(fresh.Id)).Should().Be(RuntimeState.Online);
        (await StateOf(suspended.Id)).Should().Be(RuntimeState.Suspended);

        var allEvents = await _db.RuntimeStateEvents.AsNoTracking().ToListAsync();
        allEvents.Should().HaveCount(3,
            "exactly three stale Onlines should produce three audit rows");
        allEvents.Should().OnlyContain(e =>
            e.Reason == "heartbeat:missed" && e.TriggeredBy == "watcher:heartbeat");

        var flaggedIds = allEvents.Select(e => e.RuntimeId).ToHashSet();
        flaggedIds.Should().BeEquivalentTo(new[] { stale1.Id, stale2.Id, stale3.Id });
    }

    [Fact]
    public async Task ScanOnce_EmptyTable_NoOp()
    {
        // No runtimes at all. Job must be a clean no-op — no throw, no audit.
        var act = async () => await CreateJob().ScanOnce(CancellationToken.None);

        await act.Should().NotThrowAsync();

        (await _db.ProjectRuntimes.CountAsync()).Should().Be(0);
        (await _db.RuntimeStateEvents.CountAsync()).Should().Be(0);
    }

    // ------------------------------------------------------------------
    // Branch 2: bootstrap-timeout (never-heartbeated mid-boot runtimes).
    //
    // The original heartbeat-cutoff branch filters `LastHeartbeatAt != null`,
    // so it can't see runtimes that died before the daemon ever connected.
    // The reconciler only acts on Fly drift, so a healthy Fly VM with a dead
    // daemon process slips past both watchers. The bootstrap-timeout branch
    // closes that gap by treating "in Booting/Bootstrapping/Waking with no
    // heartbeat for >5 min" as crashed.
    // ------------------------------------------------------------------

    [Fact]
    public async Task ScanOnce_StuckBootstrapping_NeverHeartbeated_PastTimeout_FlaggedCrashed()
    {
        // Bootstrapping row with no heartbeat at all. Seed sets UpdatedAt to
        // real-now; we then advance the FakeClock 10 minutes forward so the
        // job sees the row as 10 minutes stale — well past the 5-minute
        // bootstrap timeout. The daemon process likely died inside an
        // otherwise-healthy Fly VM before sending its first beat; both other
        // watchers miss it.
        var runtime = await SeedRuntimeAsync(
            RuntimeState.Bootstrapping,
            lastHeartbeatAt: null);
        _clock.Advance(TimeSpan.FromMinutes(15));

        await CreateJob().ScanOnce(CancellationToken.None);

        var refreshed = await _db.ProjectRuntimes.AsNoTracking().SingleAsync(r => r.Id == runtime.Id);
        refreshed.State.Should().Be(RuntimeState.Crashed,
            "a never-heartbeated Bootstrapping runtime past the 5-min timeout must be flagged Crashed");

        var audit = await _db.RuntimeStateEvents.AsNoTracking()
            .SingleAsync(e => e.RuntimeId == runtime.Id);
        audit.FromState.Should().Be(RuntimeState.Bootstrapping);
        audit.ToState.Should().Be(RuntimeState.Crashed);
        audit.Reason.Should().Be("bootstrap:timeout");
        audit.TriggeredBy.Should().Be("watcher:bootstrap_timeout");
        audit.Metadata.Should().NotBeNull();
        audit.Metadata!.Should().Contain("secondsSilent",
            "metadata must include how long the runtime sat in the mid-boot state for diagnostics");
        audit.Metadata!.Should().Contain("previousState");
    }

    [Fact]
    public async Task ScanOnce_StuckBootstrapping_NeverHeartbeated_WithinTimeout_NotFlagged()
    {
        // Bootstrapping row with no heartbeat. Seed sets UpdatedAt to real-now;
        // we advance the FakeClock only 30 seconds — comfortably inside the
        // 5-minute bootstrap window. A normal cold boot can legitimately sit
        // here for a minute or two; we MUST NOT panic-crash it.
        var runtime = await SeedRuntimeAsync(
            RuntimeState.Bootstrapping,
            lastHeartbeatAt: null);
        _clock.Advance(TimeSpan.FromSeconds(30));

        await CreateJob().ScanOnce(CancellationToken.None);

        var refreshed = await _db.ProjectRuntimes.AsNoTracking().SingleAsync(r => r.Id == runtime.Id);
        refreshed.State.Should().Be(RuntimeState.Bootstrapping,
            "a fresh mid-boot runtime well inside the 5-min window must be left alone");

        (await _db.RuntimeStateEvents.CountAsync(e => e.RuntimeId == runtime.Id))
            .Should().Be(0, "no transition means no audit row");
    }

    [Fact]
    public async Task ScanOnce_Booting_NeverHeartbeated_PastTimeout_FlaggedCrashed()
    {
        // Booting is also covered by the bootstrap-timeout branch even though
        // the original heartbeat-cutoff branch excludes it (no daemon yet).
        // A 10-minute-old Booting row with no heartbeat means the boot never
        // progressed — likely a dead bootstrap process inside the VM.
        var runtime = await SeedRuntimeAsync(
            RuntimeState.Booting,
            lastHeartbeatAt: null);
        _clock.Advance(TimeSpan.FromMinutes(15));

        await CreateJob().ScanOnce(CancellationToken.None);

        var refreshed = await _db.ProjectRuntimes.AsNoTracking().SingleAsync(r => r.Id == runtime.Id);
        refreshed.State.Should().Be(RuntimeState.Crashed,
            "Booting -> Crashed is a legal edge; the bootstrap-timeout branch flips it");

        var audit = await _db.RuntimeStateEvents.AsNoTracking()
            .SingleAsync(e => e.RuntimeId == runtime.Id);
        audit.FromState.Should().Be(RuntimeState.Booting);
        audit.ToState.Should().Be(RuntimeState.Crashed);
        audit.Reason.Should().Be("bootstrap:timeout");
    }

    [Fact]
    public async Task ScanOnce_Waking_NeverHeartbeated_PastTimeout_FlaggedCrashed()
    {
        // Waking is the post-suspend cold-boot variant — same daemon download +
        // bootstrap cycle, same failure mode. A 10-minute-old Waking row with
        // no heartbeat means the resume-from-suspend stalled on the daemon
        // process side.
        var runtime = await SeedRuntimeAsync(
            RuntimeState.Waking,
            lastHeartbeatAt: null);
        _clock.Advance(TimeSpan.FromMinutes(15));

        await CreateJob().ScanOnce(CancellationToken.None);

        var refreshed = await _db.ProjectRuntimes.AsNoTracking().SingleAsync(r => r.Id == runtime.Id);
        refreshed.State.Should().Be(RuntimeState.Crashed);

        var audit = await _db.RuntimeStateEvents.AsNoTracking()
            .SingleAsync(e => e.RuntimeId == runtime.Id);
        audit.FromState.Should().Be(RuntimeState.Waking);
        audit.ToState.Should().Be(RuntimeState.Crashed);
        audit.Reason.Should().Be("bootstrap:timeout");
    }

    [Fact]
    public async Task ScanOnce_Online_NeverHeartbeated_NotFlaggedByNewBranch()
    {
        // Online is OUTSIDE the bootstrap-timeout branch's scope — that branch
        // governs only Booting / Bootstrapping / Waking. An Online runtime with
        // LastHeartbeatAt == null is also outside the original heartbeat-cutoff
        // branch (its filter requires LastHeartbeatAt != null), so the
        // watcher correctly leaves it alone. (Once Online actually heartbeats,
        // the original 30-second cutoff applies.)
        var runtime = await SeedRuntimeAsync(
            RuntimeState.Online,
            lastHeartbeatAt: null);
        _clock.Advance(TimeSpan.FromMinutes(10));

        await CreateJob().ScanOnce(CancellationToken.None);

        var refreshed = await _db.ProjectRuntimes.AsNoTracking().SingleAsync(r => r.Id == runtime.Id);
        refreshed.State.Should().Be(RuntimeState.Online,
            "the bootstrap-timeout branch must not touch Online — that's the heartbeat-cutoff branch's domain");

        (await _db.RuntimeStateEvents.CountAsync(e => e.RuntimeId == runtime.Id))
            .Should().Be(0);
    }

    [Fact]
    public async Task ScanOnce_MidBootRow_WithRecentHeartbeat_LeftToOriginalBranch()
    {
        // A Bootstrapping runtime whose state-age would otherwise match the
        // bootstrap-timeout filter (10 minutes elapsed) but that DOES have a
        // recent heartbeat. The new branch must not poach it — its filter
        // requires LastHeartbeatAt == null — and the original heartbeat-cutoff
        // branch must also leave it alone (the heartbeat is within the 30s
        // threshold). End result: row untouched. This guards against the two
        // branches accidentally overlapping on rows that have started
        // heartbeating mid-boot but happen to still be in Bootstrapping
        // (the bootstrap took a while, but the daemon is alive and reporting).
        var runtime = await SeedRuntimeAsync(
            RuntimeState.Bootstrapping,
            lastHeartbeatAt: null);
        _clock.Advance(TimeSpan.FromMinutes(10));
        // Land a fresh heartbeat (5s before the advanced clock). Note that
        // SaveChangesAsync's audit override WILL bump UpdatedAt back to real-now
        // (≈ seed-time), but that only strengthens the test: UpdatedAt is now
        // ~10 minutes older than _clock.UtcNow, which means the bootstrap-
        // timeout filter would absolutely match if LastHeartbeatAt were null.
        // It isn't, so the new branch leaves the row alone.
        runtime.LastHeartbeatAt = _clock.UtcNow.AddSeconds(-5);
        await _db.SaveChangesAsync();

        await CreateJob().ScanOnce(CancellationToken.None);

        var refreshed = await _db.ProjectRuntimes.AsNoTracking().SingleAsync(r => r.Id == runtime.Id);
        refreshed.State.Should().Be(RuntimeState.Bootstrapping,
            "a runtime with a recent heartbeat must not be flagged by the bootstrap-timeout branch");

        (await _db.RuntimeStateEvents.CountAsync(e => e.RuntimeId == runtime.Id))
            .Should().Be(0,
            "neither the heartbeat-cutoff branch nor the bootstrap-timeout branch should fire here");
    }

    [Fact]
    public async Task ScanOnce_Booting_WithStaleLastBootstrapActivityAt_AfterFreshBootTransition_NotFlagged()
    {
        // Reproduces the respawn-loop bug: a runtime that previously reached Online
        // left LastBootstrapActivityAt hours in the past. Without clearing that
        // timestamp on entry to Booting, the bootstrap-silence branch treats the
        // fresh machine as silent for hours and Crashes it seconds after creation.
        var runtime = await SeedRuntimeAsync(
            RuntimeState.Crashed,
            lastHeartbeatAt: null,
            lastBootstrapActivityAt: DateTime.UtcNow.AddHours(-5));

        var transition = runtime.TransitionTo(
            RuntimeState.Booting,
            reason: "respawn:created",
            triggeredBy: "system:respawn");
        transition.IsSuccess.Should().BeTrue();
        await _db.SaveChangesAsync();

        _clock.Advance(TimeSpan.FromSeconds(30));

        await CreateJob().ScanOnce(CancellationToken.None);

        var refreshed = await _db.ProjectRuntimes.AsNoTracking().SingleAsync(r => r.Id == runtime.Id);
        refreshed.State.Should().Be(RuntimeState.Booting,
            "a freshly respawned Booting runtime must not instant-timeout on stale bootstrap activity");
    }

    // ------------------------------------------------------------------
    // [DisableConcurrentExecution] presence — guards against accidental removal.
    // ------------------------------------------------------------------

    [Fact]
    public void Run_HasDisableConcurrentExecutionAttribute()
    {
        // The class has two `Run` overloads — the Hangfire entry point
        // (`Run(IJobCancellationToken)`) and the inner CT loop
        // (`Run(CancellationToken)`). The attribute lives on the Hangfire
        // entry point — that's the one Hangfire reflects over when scheduling.
        // Disambiguate by parameter type to avoid AmbiguousMatchException.
        var method = typeof(HeartbeatWatcherJob).GetMethod(
            nameof(HeartbeatWatcherJob.Run),
            new[] { typeof(Hangfire.IJobCancellationToken) })!;
        var attr = method.GetCustomAttributes(typeof(Hangfire.DisableConcurrentExecutionAttribute), inherit: false);
        attr.Should().NotBeEmpty(
            "two Hangfire workers must not race on the same heartbeat-scan minute — the attribute is the lock");
    }

    // ------------------------------------------------------------------
    // Branch 3: Crashed-orphan rescue (lost scheduled respawn)
    // ------------------------------------------------------------------

    /// <summary>Seed the state-event audit row branch 3 dates a crash by.</summary>
    private async Task SeedCrashEventAsync(Guid runtimeId)
    {
        _db.RuntimeStateEvents.Add(new RuntimeStateEvent
        {
            RuntimeId = runtimeId,
            FromState = RuntimeState.Bootstrapping,
            ToState = RuntimeState.Crashed,
            Reason = "heartbeat:missed",
            TriggeredBy = "watcher:heartbeat",
        });
        await _db.SaveChangesAsync();
    }

    [Fact]
    public async Task ScanOnce_ReenqueuesRespawnForRuntimeStuckInCrashedPastRescueWindow()
    {
        // A live daemon keeps heartbeating a Crashed row (its RuntimeReady is
        // refused) — the heartbeat must NOT shield the row from rescue.
        var runtime = await SeedRuntimeAsync(RuntimeState.Crashed, lastHeartbeatAt: _clock.UtcNow);
        await SeedCrashEventAsync(runtime.Id);

        // Past the 10-minute rescue window with no further transition: the
        // scheduled RespawnRuntimeJob was evidently lost.
        _clock.Advance(TimeSpan.FromMinutes(11));

        await CreateJob().ScanOnce();

        _watcherBackgroundJobs.Verify(
            x => x.Create(
                It.Is<Hangfire.Common.Job>(j => j.Type == typeof(RespawnRuntimeJob)),
                It.IsAny<Hangfire.States.IState>()),
            Times.Once,
            "a runtime stuck in Crashed past the rescue window must get its respawn re-enqueued");

        var refreshed = await _db.ProjectRuntimes.AsNoTracking().SingleAsync(r => r.Id == runtime.Id);
        refreshed.State.Should().Be(RuntimeState.Crashed,
            "the watcher only enqueues the respawn — RespawnRuntimeJob owns the Crashed → Booting transition");
    }

    [Fact]
    public async Task ScanOnce_LeavesRecentlyCrashedRuntimeToItsScheduledRespawn()
    {
        var runtime = await SeedRuntimeAsync(RuntimeState.Crashed, lastHeartbeatAt: null);
        await SeedCrashEventAsync(runtime.Id);

        // Inside the window: ScheduleRespawnHandler's backoff (≤5 min) still
        // owns this crash — the rescue must not double-fire.
        _clock.Advance(TimeSpan.FromMinutes(3));

        await CreateJob().ScanOnce();

        _watcherBackgroundJobs.Verify(
            x => x.Create(It.IsAny<Hangfire.Common.Job>(), It.IsAny<Hangfire.States.IState>()),
            Times.Never,
            "a freshly-crashed runtime is the scheduled respawn's job, not the rescue's");
    }

    [Fact]
    public async Task ScanOnce_RescuesEachStuckRuntimeOnlyOncePerInvocation()
    {
        var runtime = await SeedRuntimeAsync(RuntimeState.Crashed, lastHeartbeatAt: null);
        await SeedCrashEventAsync(runtime.Id);
        _clock.Advance(TimeSpan.FromMinutes(11));

        var job = CreateJob();
        await job.ScanOnce();
        await job.ScanOnce(); // same invocation loops ScanOnce 12× per minute

        _watcherBackgroundJobs.Verify(
            x => x.Create(It.IsAny<Hangfire.Common.Job>(), It.IsAny<Hangfire.States.IState>()),
            Times.Once,
            "the 12×-per-minute inner loop must not enqueue duplicate rescues within one invocation");
    }
}
