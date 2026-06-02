using System.Net;
using System.Text;
using Api.Tests.Features.FlyManagement;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Source.Features.FlyManagement;
using Source.Features.FlyManagement.Configuration;
using Source.Features.RuntimeLifecycle.Events;
using Source.Features.RuntimeLifecycle.Jobs;
using Source.Features.RuntimeLifecycle.Models;
using Source.Infrastructure;
using Source.Infrastructure.Interceptors;

namespace Api.Tests.Features.RuntimeLifecycle;

/// <summary>
/// Unit tests for <see cref="RuntimeReconcilerJob"/>. We construct a real
/// <see cref="FlyClient"/> on top of a scripted <see cref="HttpMessageHandler"/>
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
        // tests that don't trigger a respawn never invoke its Schedule path, but DI must
        // still be able to construct the handler.
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

    private static readonly FlyOptions DefaultFlyOptions = new()
    {
        ApiToken = "fly_pat_secret_xyz",
        OrgSlug = "personal",
        AppName = "test-app",
        DefaultRegion = "arn",
    };

    private RuntimeReconcilerJob CreateJob(HttpMessageHandler handler)
    {
        var http = new HttpClient(handler, disposeHandler: false)
        {
            BaseAddress = new Uri("https://api.machines.dev/v1/"),
        };
        var fly = new FlyClient(
            http,
            new StubFlyOptionsAccessor(DefaultFlyOptions),
            _db,
            new Mock<ILogger<FlyClient>>().Object);
        return new RuntimeReconcilerJob(_db, fly, NullLogger<RuntimeReconcilerJob>.Instance);
    }

    private async Task<ProjectRuntime> SeedRuntimeAsync(RuntimeState state, string? machineId)
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
        return runtime;
    }

    /// <summary>
    /// Build a scripted handler that returns the supplied JSON body for a single
    /// <c>ListMachines</c> call.
    /// </summary>
    private static ScriptedHandler MachineListHandler(string body)
    {
        var handler = new ScriptedHandler();
        handler.Enqueue(HttpStatusCode.OK, body);
        return handler;
    }

    /// <summary>
    /// Format Fly's machine-list JSON. State values are stringly-typed on the wire;
    /// see <c>FlyMachine</c> for the full vocabulary Fly uses.
    /// </summary>
    private static string MachineListJson(params (string id, string state)[] machines)
    {
        var items = string.Join(",", machines.Select(m =>
            $$"""{"id":"{{m.id}}","name":"rt","state":"{{m.state}}","region":"arn","instance_id":null,"private_ip":null,"created_at":"2026-05-08T10:00:00Z"}"""));
        return $"[{items}]";
    }

    // ------------------------------------------------------------------
    // Tests
    // ------------------------------------------------------------------

    [Fact]
    public async Task Run_EmptyDbAndEmptyFlyList_NoOp()
    {
        var handler = MachineListHandler("[]");
        var job = CreateJob(handler);

        await job.Run(CancellationToken.None);

        // ListMachines was the only call.
        handler.CallCount.Should().Be(1);
        (await _db.ProjectRuntimes.CountAsync()).Should().Be(0);
        (await _db.RuntimeStateEvents.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Run_NoDrift_NoStateChangesNoEvents()
    {
        // DB matches Fly: runtime is Online and Fly says started.
        var runtime = await SeedRuntimeAsync(RuntimeState.Online, "mach_ok");
        var handler = MachineListHandler(MachineListJson(("mach_ok", "started")));

        var job = CreateJob(handler);
        await job.Run(CancellationToken.None);

        var refreshed = await _db.ProjectRuntimes.AsNoTracking().SingleAsync(r => r.Id == runtime.Id);
        refreshed.State.Should().Be(RuntimeState.Online,
            "DB matched Fly so no transition should have fired");

        (await _db.RuntimeStateEvents.CountAsync(e => e.RuntimeId == runtime.Id))
            .Should().Be(0, "no drift means no audit row");
    }

    [Fact]
    public async Task Run_FlyMachineMissing_TransitionsToCrashed()
    {
        // Runtime claims a Fly machine that Fly doesn't know about — classic drift.
        var runtime = await SeedRuntimeAsync(RuntimeState.Online, "mach_lost");

        // Fly returns an unrelated machine id, so ours is missing.
        var handler = MachineListHandler(MachineListJson(("mach_other", "started")));

        var job = CreateJob(handler);
        await job.Run(CancellationToken.None);

        var refreshed = await _db.ProjectRuntimes.AsNoTracking().SingleAsync(r => r.Id == runtime.Id);
        refreshed.State.Should().Be(RuntimeState.Crashed,
            "missing Fly machine forces a Crashed transition so the supervisor can react");

        var events = await _db.RuntimeStateEvents.AsNoTracking()
            .Where(e => e.RuntimeId == runtime.Id)
            .ToListAsync();
        events.Should().HaveCount(1, "the Crashed transition must produce a single audit row");
        var audit = events.Single();
        audit.FromState.Should().Be(RuntimeState.Online);
        audit.ToState.Should().Be(RuntimeState.Crashed);
        audit.Reason.Should().Be("reconciler:machine_missing");
        audit.TriggeredBy.Should().Be("reconciler");
    }

    [Fact]
    public async Task Run_FlyStoppedDbOnline_TransitionsToSuspending()
    {
        // Spec card asks for Suspended, but the state graph forbids Online -> Suspended
        // in a single hop. We pick the closest legal target (Suspending) and let the
        // next tick or a webhook close Suspending -> Suspended.
        var runtime = await SeedRuntimeAsync(RuntimeState.Online, "mach_quiet");
        var handler = MachineListHandler(MachineListJson(("mach_quiet", "stopped")));

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
        audit.Metadata!.Should().Contain("\"flyState\":\"stopped\"");
        audit.Metadata!.Should().Contain("\"dbState\":\"Online\"");
    }

    [Fact]
    public async Task Run_FlyStoppedDbBooting_TransitionsToCrashed()
    {
        // Mid-boot drift: Fly stopped the machine while the DB still says Booting.
        // The supervisor needs Crashed so ScheduleRespawnHandler kicks in (destroy
        // + recreate fresh machine) rather than us logging drift forever.
        var runtime = await SeedRuntimeAsync(RuntimeState.Booting, "mach_boot_stuck");
        var handler = MachineListHandler(MachineListJson(("mach_boot_stuck", "stopped")));

        var job = CreateJob(handler);
        await job.Run(CancellationToken.None);

        var refreshed = await _db.ProjectRuntimes.AsNoTracking().SingleAsync(r => r.Id == runtime.Id);
        refreshed.State.Should().Be(RuntimeState.Crashed,
            "Booting + fly:stopped means the machine died mid-boot; Crashed lets ScheduleRespawnHandler recover");

        var events = await _db.RuntimeStateEvents.AsNoTracking()
            .Where(e => e.RuntimeId == runtime.Id)
            .ToListAsync();
        events.Should().HaveCount(1);
        var audit = events.Single();
        audit.FromState.Should().Be(RuntimeState.Booting);
        audit.ToState.Should().Be(RuntimeState.Crashed);
        audit.Reason.Should().Be("reconciler:drift");
        audit.TriggeredBy.Should().Be("reconciler");
        audit.Metadata.Should().NotBeNull();
        audit.Metadata!.Should().Contain("\"flyState\":\"stopped\"");
        audit.Metadata!.Should().Contain("\"dbState\":\"Booting\"");
    }

    [Fact]
    public async Task Run_FlyStoppedDbBootstrapping_TransitionsToCrashed()
    {
        // Same mid-boot drift case but the runtime had advanced from Booting to
        // Bootstrapping before the machine went down (e.g. daemon hit the
        // "no model slug configured" non-recoverable error in bootstrap-opencode).
        var runtime = await SeedRuntimeAsync(RuntimeState.Bootstrapping, "mach_bootstrap_stuck");
        var handler = MachineListHandler(MachineListJson(("mach_bootstrap_stuck", "stopped")));

        var job = CreateJob(handler);
        await job.Run(CancellationToken.None);

        var refreshed = await _db.ProjectRuntimes.AsNoTracking().SingleAsync(r => r.Id == runtime.Id);
        refreshed.State.Should().Be(RuntimeState.Crashed,
            "Bootstrapping + fly:stopped means daemon bootstrap died; Crashed lets ScheduleRespawnHandler recover");

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
    public async Task Run_FlySuspendedDbWaking_TransitionsToCrashed()
    {
        // Wake path mid-boot drift: we asked Fly to start a suspended machine but
        // Fly still reports it as suspended. Mark Crashed so the supervisor can
        // respawn instead of leaving the runtime stuck in Waking forever.
        var runtime = await SeedRuntimeAsync(RuntimeState.Waking, "mach_wake_stuck");
        var handler = MachineListHandler(MachineListJson(("mach_wake_stuck", "suspended")));

        var job = CreateJob(handler);
        await job.Run(CancellationToken.None);

        var refreshed = await _db.ProjectRuntimes.AsNoTracking().SingleAsync(r => r.Id == runtime.Id);
        refreshed.State.Should().Be(RuntimeState.Crashed,
            "Waking + fly:suspended means the wake never landed; Crashed lets ScheduleRespawnHandler recover");

        var events = await _db.RuntimeStateEvents.AsNoTracking()
            .Where(e => e.RuntimeId == runtime.Id)
            .ToListAsync();
        events.Should().HaveCount(1);
        var audit = events.Single();
        audit.FromState.Should().Be(RuntimeState.Waking);
        audit.ToState.Should().Be(RuntimeState.Crashed);
        audit.Reason.Should().Be("reconciler:drift");
        audit.Metadata.Should().NotBeNull();
        audit.Metadata!.Should().Contain("\"flyState\":\"suspended\"");
        audit.Metadata!.Should().Contain("\"dbState\":\"Waking\"");
    }

    [Fact]
    public async Task Run_FlyStoppedDbSuspending_TransitionsToSuspended()
    {
        // Webhook missed: Suspending should advance to Suspended once Fly confirms stopped.
        var runtime = await SeedRuntimeAsync(RuntimeState.Suspending, "mach_susp");
        var handler = MachineListHandler(MachineListJson(("mach_susp", "stopped")));

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
    public async Task Run_MixedBatch_OnlyDriftedRowsAreMutated()
    {
        // Three runtimes — one matches Fly, one drifted, one missing on Fly.
        var matching = await SeedRuntimeAsync(RuntimeState.Online, "mach_match");
        var drifted = await SeedRuntimeAsync(RuntimeState.Suspending, "mach_drift");
        var missing = await SeedRuntimeAsync(RuntimeState.Online, "mach_gone");

        var handler = MachineListHandler(MachineListJson(
            ("mach_match", "started"),
            ("mach_drift", "stopped")));
        // mach_gone is intentionally absent.

        var job = CreateJob(handler);
        await job.Run(CancellationToken.None);

        var matchingState = (await _db.ProjectRuntimes.AsNoTracking().SingleAsync(r => r.Id == matching.Id)).State;
        var driftedState = (await _db.ProjectRuntimes.AsNoTracking().SingleAsync(r => r.Id == drifted.Id)).State;
        var missingState = (await _db.ProjectRuntimes.AsNoTracking().SingleAsync(r => r.Id == missing.Id)).State;

        matchingState.Should().Be(RuntimeState.Online, "no drift, no change");
        driftedState.Should().Be(RuntimeState.Suspended, "Suspending + stopped -> Suspended");
        missingState.Should().Be(RuntimeState.Crashed, "machine missing on Fly -> Crashed");

        // Exactly two audit rows: one for the drifted transition, one for the missing-machine transition.
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
        // even when they have no Fly machine yet.
        var pending = await SeedRuntimeAsync(RuntimeState.Pending, machineId: null);
        var handler = MachineListHandler("[]");

        var job = CreateJob(handler);
        await job.Run(CancellationToken.None);

        var refreshed = await _db.ProjectRuntimes.AsNoTracking().SingleAsync(r => r.Id == pending.Id);
        refreshed.State.Should().Be(RuntimeState.Pending, "Pending rows are out of scope for the reconciler");

        (await _db.RuntimeStateEvents.CountAsync(e => e.RuntimeId == pending.Id)).Should().Be(0);
    }

    [Fact]
    public async Task Run_FlyApiFails_LogsAndReturnsClean()
    {
        // Runtime exists but Fly's list call 500s — the reconciler must not touch the
        // row, must not throw, and must leave state intact for a future tick.
        var runtime = await SeedRuntimeAsync(RuntimeState.Online, "mach_safe");

        var handler = new ScriptedHandler();
        handler.Enqueue(HttpStatusCode.InternalServerError, "{\"error\":\"upstream_blip\"}");

        var job = CreateJob(handler);

        // Should NOT throw — Fly being down is an expected failure mode.
        await job.Run(CancellationToken.None);

        var refreshed = await _db.ProjectRuntimes.AsNoTracking().SingleAsync(r => r.Id == runtime.Id);
        refreshed.State.Should().Be(RuntimeState.Online, "transient Fly outage must not mutate state");

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

    // ------------------------------------------------------------------
    // Test doubles
    // ------------------------------------------------------------------

    /// <summary>
    /// FIFO scripted handler. Mirrors the inner sealed class in
    /// <see cref="RuntimeProvisionerJobTests"/>.
    /// </summary>
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new();
        public int CallCount { get; private set; }

        public void Enqueue(HttpStatusCode status, string body)
        {
            _responses.Enqueue(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            if (_responses.Count == 0)
            {
                throw new InvalidOperationException(
                    $"ScriptedHandler exhausted after {CallCount} calls — test under-mocked.");
            }
            return Task.FromResult(_responses.Dequeue());
        }
    }
}
