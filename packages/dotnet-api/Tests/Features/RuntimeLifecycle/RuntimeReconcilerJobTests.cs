using System.Net;
using Api.Tests.Features.BoxManagement;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Source.Features.BoxManagement;
using Source.Features.BoxManagement.Configuration;
using Source.Features.RuntimeLifecycle.Events;
using Source.Features.RuntimeLifecycle.Jobs;
using Source.Features.RuntimeLifecycle.Models;
using Source.Infrastructure;
using Source.Infrastructure.Interceptors;

namespace Api.Tests.Features.RuntimeLifecycle;

/// <summary>
/// Unit tests for <see cref="RuntimeReconcilerJob"/>. We construct a real
/// <see cref="BoxClient"/> on top of a scripted <see cref="HttpMessageHandler"/>
/// (mirroring the seam <see cref="RuntimeProvisionerJobTests"/> uses) and build a
/// wired <see cref="ApplicationDbContext"/> with the
/// <see cref="DomainEventInterceptor"/> + MediatR registered so the
/// <c>RuntimeStateChanged</c> event flows through the
/// <c>PersistRuntimeStateEventHandler</c> and audit rows actually land.
/// </summary>
public class RuntimeReconcilerJobTests : IDisposable
{
    private readonly ServiceProvider _provider;
    private readonly ApplicationDbContext _db;

    public RuntimeReconcilerJobTests()
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

        // ScheduleRespawnHandler is auto-discovered and depends on IBackgroundJobClient;
        // Crashed transitions in these tests reach its scheduling path, so a mock is
        // required for DI construction (no-op is fine — we assert DB state, not enqueues).
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

    private static readonly BoxOptions DefaultBoxOptions = new()
    {
        ApiKey = "box_test_key",
        ApiBaseUrl = "https://ascii.dev/api/box/v1",
    };

    private RuntimeReconcilerJob CreateJob(HttpMessageHandler handler)
    {
        // No BaseAddress — BoxClient builds absolute URLs from the accessor.
        var http = new HttpClient(handler, disposeHandler: false);
        var box = new BoxClient(
            http,
            new StubBoxOptionsAccessor(DefaultBoxOptions),
            _db,
            NullLogger<BoxClient>.Instance);
        return new RuntimeReconcilerJob(_db, box, NullLogger<RuntimeReconcilerJob>.Instance);
    }

    private async Task<ProjectRuntime> SeedRuntimeAsync(RuntimeState state, string? boxId)
    {
        var runtime = new ProjectRuntime
        {
            ProjectId = Guid.NewGuid(),
            Region = "de",
            VolumeSizeGb = 1,
            State = state,
            BoxId = boxId,
        };
        _db.ProjectRuntimes.Add(runtime);
        await _db.SaveChangesAsync();
        return runtime;
    }

    /// <summary>
    /// Build a scripted handler that returns the supplied JSON body for a single
    /// <c>ListBoxes</c> call.
    /// </summary>
    private static ScriptedHandler BoxListHandler(string body)
    {
        var handler = new ScriptedHandler();
        handler.Enqueue(HttpStatusCode.OK, body);
        return handler;
    }

    /// <summary>
    /// Format Box's box.list envelope. The lifecycle field on the wire is
    /// `state`, stringly-typed; see <c>BoxStates</c> for the vocabulary
    /// (init/provisioning/provisioned/cloning/ready/idle/running/archiving/archived/error).
    /// </summary>
    private static string BoxListJson(params (string id, string state)[] boxes)
    {
        var items = string.Join(",", boxes.Select(b =>
            $$"""{"id":"{{b.id}}","name":"rt","state":"{{b.state}}","type":"small","region":"de","ttlSeconds":21600,"createdAt":"2026-05-08T10:00:00Z"}"""));
        return $$$"""{"ok":true,"type":"box.list","boxes":[{{{items}}}],"pageInfo":{"hasNextPage":false}}""";
    }

    // ------------------------------------------------------------------
    // Tests
    // ------------------------------------------------------------------

    [Fact]
    public async Task Run_EmptyDbAndEmptyBoxList_NoOp()
    {
        var handler = BoxListHandler(BoxListJson());
        var job = CreateJob(handler);

        await job.Run(CancellationToken.None);

        // ListBoxes was the only call.
        handler.CallCount.Should().Be(1);
        (await _db.ProjectRuntimes.CountAsync()).Should().Be(0);
        (await _db.RuntimeStateEvents.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Run_NoDrift_NoStateChangesNoEvents()
    {
        // DB matches Box: runtime is Online and its box is up (running).
        var runtime = await SeedRuntimeAsync(RuntimeState.Online, "box_ok");
        var handler = BoxListHandler(BoxListJson(("box_ok", "running")));

        var job = CreateJob(handler);
        await job.Run(CancellationToken.None);

        var refreshed = await _db.ProjectRuntimes.AsNoTracking().SingleAsync(r => r.Id == runtime.Id);
        refreshed.State.Should().Be(RuntimeState.Online,
            "DB matched Box so no transition should have fired");

        (await _db.RuntimeStateEvents.CountAsync(e => e.RuntimeId == runtime.Id))
            .Should().Be(0, "no drift means no audit row");
    }

    [Fact]
    public async Task Run_BoxMissing_LiveRuntime_TransitionsToCrashed()
    {
        // Runtime claims a box that Box doesn't know about — classic drift.
        var runtime = await SeedRuntimeAsync(RuntimeState.Online, "box_lost");

        // Box returns an unrelated box id, so ours is missing.
        var handler = BoxListHandler(BoxListJson(("box_other", "running")));

        var job = CreateJob(handler);
        await job.Run(CancellationToken.None);

        var refreshed = await _db.ProjectRuntimes.AsNoTracking().SingleAsync(r => r.Id == runtime.Id);
        refreshed.State.Should().Be(RuntimeState.Crashed,
            "missing box forces a Crashed transition so the supervisor can react");

        var events = await _db.RuntimeStateEvents.AsNoTracking()
            .Where(e => e.RuntimeId == runtime.Id)
            .ToListAsync();
        events.Should().HaveCount(1, "the Crashed transition must produce a single audit row");
        var audit = events.Single();
        audit.FromState.Should().Be(RuntimeState.Online);
        audit.ToState.Should().Be(RuntimeState.Crashed);
        audit.Reason.Should().Be("reconciler:box_missing");
        audit.TriggeredBy.Should().Be("reconciler");
    }

    [Fact]
    public async Task Run_BoxMissing_Suspending_TransitionsToSuspended()
    {
        // Suspending + box gone: someone deleted the box out from under us —
        // treat as suspend-complete.
        var runtime = await SeedRuntimeAsync(RuntimeState.Suspending, "box_gone_susp");
        var handler = BoxListHandler(BoxListJson());

        var job = CreateJob(handler);
        await job.Run(CancellationToken.None);

        var refreshed = await _db.ProjectRuntimes.AsNoTracking().SingleAsync(r => r.Id == runtime.Id);
        refreshed.State.Should().Be(RuntimeState.Suspended);

        var events = await _db.RuntimeStateEvents.AsNoTracking()
            .Where(e => e.RuntimeId == runtime.Id)
            .ToListAsync();
        events.Should().HaveCount(1);
        events.Single().Reason.Should().Be("reconciler:suspend_completed_box_missing");
    }

    [Fact]
    public async Task Run_BoxMissing_Deleting_TransitionsToDeleted()
    {
        var runtime = await SeedRuntimeAsync(RuntimeState.Deleting, "box_gone_del");
        var handler = BoxListHandler(BoxListJson());

        var job = CreateJob(handler);
        await job.Run(CancellationToken.None);

        var refreshed = await _db.ProjectRuntimes.AsNoTracking().SingleAsync(r => r.Id == runtime.Id);
        refreshed.State.Should().Be(RuntimeState.Deleted,
            "Deleting + missing box means the delete completed");

        var events = await _db.RuntimeStateEvents.AsNoTracking()
            .Where(e => e.RuntimeId == runtime.Id)
            .ToListAsync();
        events.Should().HaveCount(1);
        events.Single().Reason.Should().Be("reconciler:delete_completed_box_missing");
    }

    [Fact]
    public async Task Run_BoxMissing_TerminalState_NoOp()
    {
        // Suspended is terminal-ish for the reconciler: a missing box is expected
        // eventually (TTL purge) and must not trigger any transition.
        var runtime = await SeedRuntimeAsync(RuntimeState.Suspended, "box_gone_terminal");
        var handler = BoxListHandler(BoxListJson());

        var job = CreateJob(handler);
        await job.Run(CancellationToken.None);

        var refreshed = await _db.ProjectRuntimes.AsNoTracking().SingleAsync(r => r.Id == runtime.Id);
        refreshed.State.Should().Be(RuntimeState.Suspended);
        (await _db.RuntimeStateEvents.CountAsync(e => e.RuntimeId == runtime.Id)).Should().Be(0);
    }

    [Fact]
    public async Task Run_BoxUpDbBooting_TransitionsToBootstrapping()
    {
        // Box has no webhooks — the reconciler is the ONLY driver of the
        // "VM came up" edge. Booting + up → Bootstrapping; the daemon's
        // RuntimeReady hub call still owns Bootstrapping → Online.
        var runtime = await SeedRuntimeAsync(RuntimeState.Booting, "box_boot");
        var handler = BoxListHandler(BoxListJson(("box_boot", "ready")));

        var job = CreateJob(handler);
        await job.Run(CancellationToken.None);

        var refreshed = await _db.ProjectRuntimes.AsNoTracking().SingleAsync(r => r.Id == runtime.Id);
        refreshed.State.Should().Be(RuntimeState.Bootstrapping,
            "Booting + box up hands off to Bootstrapping; only the daemon may claim Online");

        var events = await _db.RuntimeStateEvents.AsNoTracking()
            .Where(e => e.RuntimeId == runtime.Id)
            .ToListAsync();
        events.Should().HaveCount(1);
        var audit = events.Single();
        audit.FromState.Should().Be(RuntimeState.Booting);
        audit.ToState.Should().Be(RuntimeState.Bootstrapping);
        audit.Reason.Should().Be("reconciler:drift");
        audit.TriggeredBy.Should().Be("reconciler");
        audit.Metadata.Should().NotBeNull();
        audit.Metadata!.Should().Contain("\"boxState\":\"ready\"");
        audit.Metadata!.Should().Contain("\"dbState\":\"Booting\"");
    }

    [Fact]
    public async Task Run_BoxUpDbWaking_TransitionsToBootstrapping()
    {
        // Wake path: box resumed but daemon hasn't confirmed yet.
        var runtime = await SeedRuntimeAsync(RuntimeState.Waking, "box_wake");
        var handler = BoxListHandler(BoxListJson(("box_wake", "running")));

        var job = CreateJob(handler);
        await job.Run(CancellationToken.None);

        var refreshed = await _db.ProjectRuntimes.AsNoTracking().SingleAsync(r => r.Id == runtime.Id);
        refreshed.State.Should().Be(RuntimeState.Bootstrapping,
            "Waking + box up hands off to Bootstrapping");

        var events = await _db.RuntimeStateEvents.AsNoTracking()
            .Where(e => e.RuntimeId == runtime.Id)
            .ToListAsync();
        events.Should().HaveCount(1);
        var audit = events.Single();
        audit.FromState.Should().Be(RuntimeState.Waking);
        audit.ToState.Should().Be(RuntimeState.Bootstrapping);
        audit.Reason.Should().Be("reconciler:drift");
        audit.Metadata!.Should().Contain("\"boxState\":\"running\"");
        audit.Metadata!.Should().Contain("\"dbState\":\"Waking\"");
    }

    [Fact]
    public async Task Run_BoxArchivedDbSuspending_TransitionsToSuspended()
    {
        // The stop we issued landed: Suspending advances to Suspended once Box
        // reports archived.
        var runtime = await SeedRuntimeAsync(RuntimeState.Suspending, "box_susp");
        var handler = BoxListHandler(BoxListJson(("box_susp", "archived")));

        var job = CreateJob(handler);
        await job.Run(CancellationToken.None);

        var refreshed = await _db.ProjectRuntimes.AsNoTracking().SingleAsync(r => r.Id == runtime.Id);
        refreshed.State.Should().Be(RuntimeState.Suspended);

        var events = await _db.RuntimeStateEvents.AsNoTracking()
            .Where(e => e.RuntimeId == runtime.Id)
            .ToListAsync();
        events.Should().HaveCount(1);
        events.Single().Reason.Should().Be("reconciler:drift");
    }

    [Fact]
    public async Task Run_BoxArchivedDbOnline_TransitionsToSuspending()
    {
        // The box archived itself (TTL guardrail) or was stopped out-of-band while
        // we think it's Online. The state graph forbids Online -> Suspended in a
        // single hop, so the reconciler picks the closest legal target (Suspending)
        // and the next tick closes Suspending -> Suspended.
        var runtime = await SeedRuntimeAsync(RuntimeState.Online, "box_quiet");
        var handler = BoxListHandler(BoxListJson(("box_quiet", "archived")));

        var job = CreateJob(handler);
        await job.Run(CancellationToken.None);

        var refreshed = await _db.ProjectRuntimes.AsNoTracking().SingleAsync(r => r.Id == runtime.Id);
        refreshed.State.Should().Be(RuntimeState.Suspending,
            "Online -> Suspended is illegal in one hop; reconciler picks Suspending as the closest legal target");

        var events = await _db.RuntimeStateEvents.AsNoTracking()
            .Where(e => e.RuntimeId == runtime.Id)
            .ToListAsync();
        events.Should().HaveCount(1);
        var audit = events.Single();
        audit.FromState.Should().Be(RuntimeState.Online);
        audit.ToState.Should().Be(RuntimeState.Suspending);
        audit.Reason.Should().Be("reconciler:drift");
        audit.TriggeredBy.Should().Be("reconciler");
        audit.Metadata.Should().NotBeNull();
        audit.Metadata!.Should().Contain("\"boxState\":\"archived\"");
        audit.Metadata!.Should().Contain("\"dbState\":\"Online\"");
    }

    [Fact]
    public async Task Run_BoxArchivedDbBooting_TransitionsToCrashed()
    {
        // Mid-boot drift: the box archived while the DB still says Booting.
        // The supervisor needs Crashed so ScheduleRespawnHandler kicks in.
        var runtime = await SeedRuntimeAsync(RuntimeState.Booting, "box_boot_stuck");
        var handler = BoxListHandler(BoxListJson(("box_boot_stuck", "archived")));

        var job = CreateJob(handler);
        await job.Run(CancellationToken.None);

        var refreshed = await _db.ProjectRuntimes.AsNoTracking().SingleAsync(r => r.Id == runtime.Id);
        refreshed.State.Should().Be(RuntimeState.Crashed,
            "Booting + box:archived means the box went down mid-boot; Crashed lets ScheduleRespawnHandler recover");

        var events = await _db.RuntimeStateEvents.AsNoTracking()
            .Where(e => e.RuntimeId == runtime.Id)
            .ToListAsync();
        events.Should().HaveCount(1);
        var audit = events.Single();
        audit.FromState.Should().Be(RuntimeState.Booting);
        audit.ToState.Should().Be(RuntimeState.Crashed);
        audit.Reason.Should().Be("reconciler:drift");
        audit.Metadata!.Should().Contain("\"boxState\":\"archived\"");
        audit.Metadata!.Should().Contain("\"dbState\":\"Booting\"");
    }

    [Fact]
    public async Task Run_BoxArchivedDbBootstrapping_TransitionsToCrashed()
    {
        // Same mid-boot drift case but the runtime had advanced from Booting to
        // Bootstrapping before the box went down.
        var runtime = await SeedRuntimeAsync(RuntimeState.Bootstrapping, "box_bootstrap_stuck");
        var handler = BoxListHandler(BoxListJson(("box_bootstrap_stuck", "archived")));

        var job = CreateJob(handler);
        await job.Run(CancellationToken.None);

        var refreshed = await _db.ProjectRuntimes.AsNoTracking().SingleAsync(r => r.Id == runtime.Id);
        refreshed.State.Should().Be(RuntimeState.Crashed,
            "Bootstrapping + box:archived means daemon bootstrap died; Crashed lets ScheduleRespawnHandler recover");

        var events = await _db.RuntimeStateEvents.AsNoTracking()
            .Where(e => e.RuntimeId == runtime.Id)
            .ToListAsync();
        events.Should().HaveCount(1);
        var audit = events.Single();
        audit.FromState.Should().Be(RuntimeState.Bootstrapping);
        audit.ToState.Should().Be(RuntimeState.Crashed);
        audit.Reason.Should().Be("reconciler:drift");
    }

    [Fact]
    public async Task Run_BoxArchivedDbWaking_TransitionsToCrashed()
    {
        // Wake path mid-boot drift: we asked Box to resume but it still reports
        // archived. Mark Crashed so the supervisor can respawn.
        var runtime = await SeedRuntimeAsync(RuntimeState.Waking, "box_wake_stuck");
        var handler = BoxListHandler(BoxListJson(("box_wake_stuck", "archived")));

        var job = CreateJob(handler);
        await job.Run(CancellationToken.None);

        var refreshed = await _db.ProjectRuntimes.AsNoTracking().SingleAsync(r => r.Id == runtime.Id);
        refreshed.State.Should().Be(RuntimeState.Crashed,
            "Waking + box:archived means the resume never landed; Crashed lets ScheduleRespawnHandler recover");

        var events = await _db.RuntimeStateEvents.AsNoTracking()
            .Where(e => e.RuntimeId == runtime.Id)
            .ToListAsync();
        events.Should().HaveCount(1);
        var audit = events.Single();
        audit.FromState.Should().Be(RuntimeState.Waking);
        audit.ToState.Should().Be(RuntimeState.Crashed);
        audit.Reason.Should().Be("reconciler:drift");
        audit.Metadata!.Should().Contain("\"boxState\":\"archived\"");
        audit.Metadata!.Should().Contain("\"dbState\":\"Waking\"");
    }

    [Fact]
    public async Task Run_BoxErrorDbOnline_TransitionsToCrashed()
    {
        var runtime = await SeedRuntimeAsync(RuntimeState.Online, "box_err");
        var handler = BoxListHandler(BoxListJson(("box_err", "error")));

        var job = CreateJob(handler);
        await job.Run(CancellationToken.None);

        var refreshed = await _db.ProjectRuntimes.AsNoTracking().SingleAsync(r => r.Id == runtime.Id);
        refreshed.State.Should().Be(RuntimeState.Crashed,
            "box-side hard failure of a live runtime must surface as Crashed");

        var events = await _db.RuntimeStateEvents.AsNoTracking()
            .Where(e => e.RuntimeId == runtime.Id)
            .ToListAsync();
        events.Should().HaveCount(1);
        events.Single().Metadata!.Should().Contain("\"boxState\":\"error\"");
    }

    [Fact]
    public async Task Run_BoxErrorDbSuspending_TransitionsToSuspended()
    {
        // Suspending + error: the stop failed hard box-side. Suspended is the
        // closest truthful terminal; a later wake surfaces the error again.
        var runtime = await SeedRuntimeAsync(RuntimeState.Suspending, "box_err_susp");
        var handler = BoxListHandler(BoxListJson(("box_err_susp", "error")));

        var job = CreateJob(handler);
        await job.Run(CancellationToken.None);

        var refreshed = await _db.ProjectRuntimes.AsNoTracking().SingleAsync(r => r.Id == runtime.Id);
        refreshed.State.Should().Be(RuntimeState.Suspended);
    }

    [Fact]
    public async Task Run_StuckSuspending_BoxStillUp_RetriesStopWithoutTransition()
    {
        // DB says Suspending but the box is still up — the StopBox side-effect
        // never landed. The reconciler must re-issue the stop and must NOT
        // transition this tick (the next pass observes archived and closes
        // Suspending -> Suspended via the normal mapping).
        var runtime = await SeedRuntimeAsync(RuntimeState.Suspending, "box_stuck");
        var handler = new ScriptedHandler();
        handler.Enqueue(HttpStatusCode.OK, BoxListJson(("box_stuck", "running")));
        handler.Enqueue(HttpStatusCode.OK, "{}"); // the retried StopBox

        var job = CreateJob(handler);
        await job.Run(CancellationToken.None);

        handler.CallCount.Should().Be(2, "ListBoxes + the retried StopBox");
        var stopRequest = handler.Requests[1];
        stopRequest.Method.Should().Be(HttpMethod.Post);
        stopRequest.Url.Should().EndWith("/boxes/box_stuck/stop",
            "the reconciler must retry the missing StopBox side-effect");

        var refreshed = await _db.ProjectRuntimes.AsNoTracking().SingleAsync(r => r.Id == runtime.Id);
        refreshed.State.Should().Be(RuntimeState.Suspending,
            "no transition this tick — the next pass observes archived and closes the edge");

        (await _db.RuntimeStateEvents.CountAsync(e => e.RuntimeId == runtime.Id)).Should().Be(0);
    }

    [Fact]
    public async Task Run_MixedBatch_OnlyDriftedRowsAreMutated()
    {
        // Three runtimes — one matches Box, one drifted, one missing on Box.
        var matching = await SeedRuntimeAsync(RuntimeState.Online, "box_match");
        var drifted = await SeedRuntimeAsync(RuntimeState.Suspending, "box_drift");
        var missing = await SeedRuntimeAsync(RuntimeState.Online, "box_vanished");

        var handler = BoxListHandler(BoxListJson(
            ("box_match", "running"),
            ("box_drift", "archived")));
        // box_vanished is intentionally absent.

        var job = CreateJob(handler);
        await job.Run(CancellationToken.None);

        var matchingState = (await _db.ProjectRuntimes.AsNoTracking().SingleAsync(r => r.Id == matching.Id)).State;
        var driftedState = (await _db.ProjectRuntimes.AsNoTracking().SingleAsync(r => r.Id == drifted.Id)).State;
        var missingState = (await _db.ProjectRuntimes.AsNoTracking().SingleAsync(r => r.Id == missing.Id)).State;

        matchingState.Should().Be(RuntimeState.Online, "no drift, no change");
        driftedState.Should().Be(RuntimeState.Suspended, "Suspending + archived -> Suspended");
        missingState.Should().Be(RuntimeState.Crashed, "box missing on Box -> Crashed");

        var matchingEvents = await _db.RuntimeStateEvents.AsNoTracking()
            .Where(e => e.RuntimeId == matching.Id).ToListAsync();
        var driftedEvents = await _db.RuntimeStateEvents.AsNoTracking()
            .Where(e => e.RuntimeId == drifted.Id).ToListAsync();
        var missingEvents = await _db.RuntimeStateEvents.AsNoTracking()
            .Where(e => e.RuntimeId == missing.Id).ToListAsync();

        matchingEvents.Should().BeEmpty("no transition for the matching row");
        driftedEvents.Should().HaveCount(1);
        missingEvents.Should().HaveCount(1);
    }

    [Fact]
    public async Task Run_PendingRuntime_IsIgnored()
    {
        // Pending rows are the provisioner's job — the reconciler must not touch them
        // even when they have no box yet.
        var pending = await SeedRuntimeAsync(RuntimeState.Pending, boxId: null);
        var handler = BoxListHandler(BoxListJson());

        var job = CreateJob(handler);
        await job.Run(CancellationToken.None);

        var refreshed = await _db.ProjectRuntimes.AsNoTracking().SingleAsync(r => r.Id == pending.Id);
        refreshed.State.Should().Be(RuntimeState.Pending, "Pending rows are out of scope for the reconciler");

        (await _db.RuntimeStateEvents.CountAsync(e => e.RuntimeId == pending.Id)).Should().Be(0);
    }

    [Fact]
    public async Task Run_BoxApiFails_LogsAndReturnsClean()
    {
        // Runtime exists but Box's list call 500s — the reconciler must not touch the
        // row, must not throw, and must leave state intact for a future tick.
        var runtime = await SeedRuntimeAsync(RuntimeState.Online, "box_safe");

        var handler = new ScriptedHandler();
        handler.Enqueue(HttpStatusCode.InternalServerError,
            """{"ok":false,"type":"box.error","status":500,"code":"upstream_blip","message":"blip","error":{"code":"upstream_blip","message":"blip","status":500},"requestId":"req_500"}""");

        var job = CreateJob(handler);

        // Should NOT throw — Box being down is an expected failure mode.
        await job.Run(CancellationToken.None);

        var refreshed = await _db.ProjectRuntimes.AsNoTracking().SingleAsync(r => r.Id == runtime.Id);
        refreshed.State.Should().Be(RuntimeState.Online, "transient Box outage must not mutate state");

        (await _db.RuntimeStateEvents.CountAsync(e => e.RuntimeId == runtime.Id)).Should().Be(0);
    }

    // ------------------------------------------------------------------
    // [DisableConcurrentExecution] presence — guards against accidental removal.
    // ------------------------------------------------------------------

    [Fact]
    public void Run_HasDisableConcurrentExecutionAttribute()
    {
        var method = typeof(RuntimeReconcilerJob).GetMethod(nameof(RuntimeReconcilerJob.Run), new[] { typeof(Hangfire.IJobCancellationToken) })!;
        var attr = method.GetCustomAttributes(typeof(Hangfire.DisableConcurrentExecutionAttribute), inherit: false);
        attr.Should().NotBeEmpty(
            "two Hangfire workers must not race on the same reconcile pass — the attribute is the lock");
    }
}
