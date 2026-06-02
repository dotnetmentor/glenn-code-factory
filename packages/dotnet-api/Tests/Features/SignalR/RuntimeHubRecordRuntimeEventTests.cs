using System.Security.Claims;
using Hangfire;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Source.Features.Conversations.Services;
using Source.Features.Health;
using Source.Features.Health.Services;
using Source.Features.RuntimeEvents.Commands;
using Source.Features.RuntimeEvents.Models;
using Source.Features.RuntimeLifecycle.Events;
using Source.Features.SignalR.Contracts;
using Source.Features.SignalR.Hubs;
using Source.Infrastructure;
using Source.Infrastructure.Interceptors;
using Source.Shared.Results;

namespace Api.Tests.Features.SignalR;

/// <summary>
/// Unit tests for <see cref="RuntimeHub.RecordRuntimeEvent"/> — the daemon's
/// inbound channel for structured runtime events (install / setup / service /
/// spec-delta lifecycle, per the V2 spec's event taxonomy). Bootstrap mirrors
/// <see cref="RuntimeHubTurnRefusedTests"/>: real
/// <see cref="ApplicationDbContext"/> on InMemory + <see cref="DomainEventInterceptor"/> +
/// MediatR (the mediator itself is mocked so we can assert on the dispatched
/// <see cref="RecordRuntimeEventCommand"/> directly), with the cross-hub
/// fan-out to <see cref="AgentHub"/> wired through Moq.
///
/// <para>The contract under test:</para>
/// <list type="bullet">
///   <item>Resolves <c>RuntimeId</c> from <c>Context.Items</c> — the daemon
///         does not (and cannot) supply it on the wire.</item>
///   <item>Dispatches <see cref="RecordRuntimeEventCommand"/> with the
///         resolved RuntimeId and a typed <see cref="RuntimeEventSeverity"/>
///         parsed from the wire string.</item>
///   <item>On a successful persistence result, broadcasts
///         <see cref="IAgentClient.RuntimeEventReceived"/> to
///         <c>runtime-events:{runtimeId}</c> with a notification carrying
///         the same fields.</item>
///   <item>Unrecognised severities default to <see cref="RuntimeEventSeverity.Info"/>
///         + a warn log — never drops the event.</item>
///   <item>Missing <c>RuntimeId</c> in context is a silent no-op (matches
///         every other daemon-inbound method).</item>
///   <item>A persistence failure logs and skips broadcast — the source of
///         truth is the DB, the broadcast is the live-tail UX.</item>
/// </list>
/// </summary>
public class RuntimeHubRecordRuntimeEventTests : IDisposable
{
    private readonly ServiceProvider _provider;
    private readonly ApplicationDbContext _db;
    private readonly Mock<IMediator> _mediator;

    private readonly Mock<IHubClients<IAgentClient>> _agentClients = new();
    private readonly Mock<IHubContext<AgentHub, IAgentClient>> _agentHub = new();
    private readonly Mock<IAgentClient> _agentGroupClient = new();

    public RuntimeHubRecordRuntimeEventTests()
    {
        var dbName = Guid.NewGuid().ToString();
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddHttpContextAccessor();

        // SignalR is registered so the auto-discovered domain-event handlers
        // (BroadcastAgentEventHandler, etc.) can resolve their IHubContext
        // dependencies — DomainEventInterceptor publishes through the real
        // IPublisher and would fail to construct downstream handlers without
        // SignalR services in the container.
        services.AddSignalR();

        services.AddMediatR(cfg =>
            cfg.RegisterServicesFromAssembly(typeof(RuntimeStateChanged).Assembly));

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

        _mediator = new Mock<IMediator>();

        // Default: persistence succeeds. Individual tests override to test the
        // failure path.
        _mediator
            .Setup(m => m.Send(It.IsAny<RecordRuntimeEventCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());

        _agentHub.SetupGet(h => h.Clients).Returns(_agentClients.Object);
        _agentClients.Setup(c => c.Group(It.IsAny<string>())).Returns(_agentGroupClient.Object);
        _agentGroupClient
            .Setup(c => c.RuntimeEventReceived(It.IsAny<RuntimeEventNotification>()))
            .Returns(Task.CompletedTask);
    }

    public void Dispose()
    {
        _provider.Dispose();
        GC.SuppressFinalize(this);
    }

    // ------------------------------------------------------------------
    // Happy path — dispatch + broadcast
    // ------------------------------------------------------------------

    [Fact]
    public async Task RecordRuntimeEvent_DispatchesCommandWithResolvedRuntimeIdAndTypedSeverity()
    {
        var runtimeId = Guid.NewGuid();
        var harness = BuildHarness();
        SetRuntimeContext(harness, runtimeId);

        var payload = new RuntimeEventPayloadDto(
            Type: RuntimeEventTypes.InstallCompleted,
            Severity: "Info",
            Timestamp: DateTime.UtcNow,
            DurationMs: 1234,
            Payload: "{\"hash\":\"abc\"}");

        await harness.Hub.RecordRuntimeEvent(payload);

        // RuntimeId is the resolved value from Context.Items — never trusted from the wire.
        // Severity is parsed into the typed enum.
        _mediator.Verify(
            m => m.Send(It.Is<RecordRuntimeEventCommand>(c =>
                c.RuntimeId == runtimeId &&
                c.Type == RuntimeEventTypes.InstallCompleted &&
                c.Severity == RuntimeEventSeverity.Info &&
                c.Timestamp == payload.Timestamp &&
                c.DurationMs == 1234L &&
                c.Payload == "{\"hash\":\"abc\"}"), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RecordRuntimeEvent_OnPersistSuccess_BroadcastsToRuntimeEventsGroup()
    {
        var runtimeId = Guid.NewGuid();
        var harness = BuildHarness();
        SetRuntimeContext(harness, runtimeId);

        var emittedAt = DateTime.UtcNow;
        var payload = new RuntimeEventPayloadDto(
            Type: RuntimeEventTypes.SpecDeltaApplied,
            Severity: "Info",
            Timestamp: emittedAt,
            DurationMs: 250,
            Payload: "{\"phaseTimings\":{\"installMs\":100}}");

        await harness.Hub.RecordRuntimeEvent(payload);

        // The group name MUST be runtime-events:{runtimeId} — runtime-scoped,
        // not project-scoped. A project with two runtimes opens the drawer on
        // one of them; events from the other one must not bleed in.
        _agentClients.Verify(c => c.Group($"runtime-events:{runtimeId}"), Times.AtLeastOnce);

        _agentGroupClient.Verify(
            c => c.RuntimeEventReceived(It.Is<RuntimeEventNotification>(n =>
                n.RuntimeId == runtimeId &&
                n.Type == RuntimeEventTypes.SpecDeltaApplied &&
                n.Severity == "Info" &&
                n.Timestamp == emittedAt &&
                n.DurationMs == 250L &&
                n.Payload == "{\"phaseTimings\":{\"installMs\":100}}" &&
                n.Id != Guid.Empty)),
            Times.Once);
    }

    [Fact]
    public async Task RecordRuntimeEvent_BroadcastsToCorrectRuntimeGroup_NotOtherRuntimes()
    {
        // Belt-and-braces against a regression where the group name accidentally
        // shifts (e.g. project-{id} or runtime-{id} instead of the documented
        // runtime-events:{id}). The drawer subscribes specifically to
        // runtime-events:{id}; any other group string is a silent fan-out leak.
        var runtimeId = Guid.NewGuid();
        var otherRuntimeId = Guid.NewGuid();

        var harness = BuildHarness();
        SetRuntimeContext(harness, runtimeId);

        await harness.Hub.RecordRuntimeEvent(new RuntimeEventPayloadDto(
            Type: RuntimeEventTypes.ServiceRunning,
            Severity: "Info",
            Timestamp: DateTime.UtcNow,
            DurationMs: null,
            Payload: "{}"));

        _agentClients.Verify(c => c.Group($"runtime-events:{runtimeId}"), Times.AtLeastOnce);
        _agentClients.Verify(c => c.Group($"runtime-events:{otherRuntimeId}"), Times.Never);
        _agentClients.Verify(c => c.Group($"runtime-{runtimeId}"), Times.Never,
            "broadcast must use the runtime-events: prefix, not the legacy runtime- prefix");
        _agentClients.Verify(c => c.Group(It.Is<string>(g => g.StartsWith("project-", StringComparison.Ordinal))), Times.Never,
            "broadcast must NOT fan out to the project group — runtime-event groups are per-runtime by design");
    }

    // ------------------------------------------------------------------
    // Severity parsing
    // ------------------------------------------------------------------

    [Fact]
    public async Task RecordRuntimeEvent_ParsesWarnSeverity()
    {
        var runtimeId = Guid.NewGuid();
        var harness = BuildHarness();
        SetRuntimeContext(harness, runtimeId);

        await harness.Hub.RecordRuntimeEvent(new RuntimeEventPayloadDto(
            Type: RuntimeEventTypes.InstallSkipped,
            Severity: "Warn",
            Timestamp: DateTime.UtcNow,
            DurationMs: null,
            Payload: "{}"));

        _mediator.Verify(
            m => m.Send(It.Is<RecordRuntimeEventCommand>(c => c.Severity == RuntimeEventSeverity.Warn),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RecordRuntimeEvent_ParsesErrorSeverity()
    {
        var runtimeId = Guid.NewGuid();
        var harness = BuildHarness();
        SetRuntimeContext(harness, runtimeId);

        await harness.Hub.RecordRuntimeEvent(new RuntimeEventPayloadDto(
            Type: RuntimeEventTypes.InstallFailed,
            Severity: "Error",
            Timestamp: DateTime.UtcNow,
            DurationMs: 50,
            Payload: "{\"exitCode\":1}"));

        _mediator.Verify(
            m => m.Send(It.Is<RecordRuntimeEventCommand>(c => c.Severity == RuntimeEventSeverity.Error),
                It.IsAny<CancellationToken>()),
            Times.Once);

        _agentGroupClient.Verify(
            c => c.RuntimeEventReceived(It.Is<RuntimeEventNotification>(n => n.Severity == "Error")),
            Times.Once);
    }

    [Fact]
    public async Task RecordRuntimeEvent_SeverityIsCaseInsensitive()
    {
        // Daemons emitting lowercase / mixed-case severity must still land
        // typed correctly — the hub uses Enum.TryParse with ignoreCase: true
        // so any cased variant maps to the enum value.
        var runtimeId = Guid.NewGuid();
        var harness = BuildHarness();
        SetRuntimeContext(harness, runtimeId);

        await harness.Hub.RecordRuntimeEvent(new RuntimeEventPayloadDto(
            Type: RuntimeEventTypes.ServiceCrashed,
            Severity: "error", // lowercase
            Timestamp: DateTime.UtcNow,
            DurationMs: null,
            Payload: "{}"));

        _mediator.Verify(
            m => m.Send(It.Is<RecordRuntimeEventCommand>(c => c.Severity == RuntimeEventSeverity.Error),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RecordRuntimeEvent_UnknownSeverity_DefaultsToInfo_StillPersistsAndBroadcasts()
    {
        // A daemon that emits a new severity ahead of a coordinated deploy must
        // not have its events dropped on the floor. Default to Info, log it,
        // keep going. The event is still observability data worth surfacing.
        var runtimeId = Guid.NewGuid();
        var harness = BuildHarness();
        SetRuntimeContext(harness, runtimeId);

        await harness.Hub.RecordRuntimeEvent(new RuntimeEventPayloadDto(
            Type: RuntimeEventTypes.ServiceCrashed,
            Severity: "Critical", // unrecognised
            Timestamp: DateTime.UtcNow,
            DurationMs: null,
            Payload: "{}"));

        _mediator.Verify(
            m => m.Send(It.Is<RecordRuntimeEventCommand>(c => c.Severity == RuntimeEventSeverity.Info),
                It.IsAny<CancellationToken>()),
            Times.Once);

        // Broadcast still fires — observability data is too valuable to drop
        // just because the severity classification disagrees with us.
        _agentGroupClient.Verify(
            c => c.RuntimeEventReceived(It.IsAny<RuntimeEventNotification>()),
            Times.Once);
    }

    // ------------------------------------------------------------------
    // Auth / context guards
    // ------------------------------------------------------------------

    [Fact]
    public async Task RecordRuntimeEvent_MissingRuntimeIdInContext_IsSilentNoOp()
    {
        // A connection that bypassed the handshake gate (or for whatever
        // reason has no RuntimeId in Items) must NOT throw on a hot path —
        // matches the silent-no-op contract of every other daemon-inbound
        // method (see TurnRefused, EmitEvent, etc.).
        var harness = BuildHarness();
        // Deliberately do NOT call SetRuntimeContext.

        var act = async () => await harness.Hub.RecordRuntimeEvent(new RuntimeEventPayloadDto(
            Type: RuntimeEventTypes.InstallStarted,
            Severity: "Info",
            Timestamp: DateTime.UtcNow,
            DurationMs: null,
            Payload: "{}"));

        await act.Should().NotThrowAsync("missing RuntimeId in Context.Items must be a silent no-op on a hot path");

        _mediator.Verify(
            m => m.Send(It.IsAny<RecordRuntimeEventCommand>(), It.IsAny<CancellationToken>()),
            Times.Never);

        _agentGroupClient.Verify(
            c => c.RuntimeEventReceived(It.IsAny<RuntimeEventNotification>()),
            Times.Never);
    }

    // ------------------------------------------------------------------
    // Persistence failure path
    // ------------------------------------------------------------------

    [Fact]
    public async Task RecordRuntimeEvent_PersistenceFailure_SkipsBroadcast()
    {
        // The command handler is best-effort and returns Result.Failure on
        // exceptions rather than throwing. The hub must respect that and skip
        // the broadcast — fanning out a notification for an event that did
        // not persist would confuse the drawer's REST-reconcile-on-reconnect
        // story.
        var runtimeId = Guid.NewGuid();
        var harness = BuildHarness();
        SetRuntimeContext(harness, runtimeId);

        _mediator
            .Setup(m => m.Send(It.IsAny<RecordRuntimeEventCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure("simulated DB outage"));

        await harness.Hub.RecordRuntimeEvent(new RuntimeEventPayloadDto(
            Type: RuntimeEventTypes.InstallFailed,
            Severity: "Error",
            Timestamp: DateTime.UtcNow,
            DurationMs: null,
            Payload: "{}"));

        // Mediator was still called (persistence was attempted) …
        _mediator.Verify(
            m => m.Send(It.IsAny<RecordRuntimeEventCommand>(), It.IsAny<CancellationToken>()),
            Times.Once);

        // … but broadcast was suppressed because the row never landed.
        _agentGroupClient.Verify(
            c => c.RuntimeEventReceived(It.IsAny<RuntimeEventNotification>()),
            Times.Never);
    }

    [Fact]
    public async Task RecordRuntimeEvent_BroadcastFailure_DoesNotThrow()
    {
        // Even if the fan-out itself blows up (transport hiccup, serializer
        // disagreement, whatever) the hub must NOT propagate — the row is
        // persisted and the drawer will reconcile via REST on its next
        // refresh. The daemon doesn't await this and we mustn't crash the
        // pipeline behind it.
        var runtimeId = Guid.NewGuid();
        var harness = BuildHarness();
        SetRuntimeContext(harness, runtimeId);

        _agentGroupClient
            .Setup(c => c.RuntimeEventReceived(It.IsAny<RuntimeEventNotification>()))
            .ThrowsAsync(new InvalidOperationException("simulated transport blow-up"));

        var act = async () => await harness.Hub.RecordRuntimeEvent(new RuntimeEventPayloadDto(
            Type: RuntimeEventTypes.SpecDeltaApplied,
            Severity: "Info",
            Timestamp: DateTime.UtcNow,
            DurationMs: 42,
            Payload: "{}"));

        await act.Should().NotThrowAsync(
            "broadcast failure is non-fatal — the row is the source of truth, the fan-out is live-tail UX");

        // Persistence still happened.
        _mediator.Verify(
            m => m.Send(It.IsAny<RecordRuntimeEventCommand>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ------------------------------------------------------------------
    // Null DurationMs round-trip
    // ------------------------------------------------------------------

    [Fact]
    public async Task RecordRuntimeEvent_NullDurationMs_ForwardsAsNull()
    {
        // *Started events have no duration yet — null must round-trip cleanly
        // through both the command dispatch and the broadcast notification.
        var runtimeId = Guid.NewGuid();
        var harness = BuildHarness();
        SetRuntimeContext(harness, runtimeId);

        await harness.Hub.RecordRuntimeEvent(new RuntimeEventPayloadDto(
            Type: RuntimeEventTypes.InstallStarted,
            Severity: "Info",
            Timestamp: DateTime.UtcNow,
            DurationMs: null,
            Payload: "{\"hash\":\"abc\"}"));

        _mediator.Verify(
            m => m.Send(It.Is<RecordRuntimeEventCommand>(c => c.DurationMs == null),
                It.IsAny<CancellationToken>()),
            Times.Once);

        _agentGroupClient.Verify(
            c => c.RuntimeEventReceived(It.Is<RuntimeEventNotification>(n => n.DurationMs == null)),
            Times.Once);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static void SetRuntimeContext(HubHarness harness, Guid runtimeId)
    {
        harness.Context.Items["RuntimeId"] = runtimeId;
    }

    private record HubHarness(
        RuntimeHub Hub,
        FakeHubCallerContext Context,
        Mock<IGroupManager> Groups,
        string ConnectionId);

    private HubHarness BuildHarness()
    {
        const string connectionId = "conn-test";

        var http = new DefaultHttpContext();
        var context = new FakeHubCallerContext(connectionId, http);

        var groups = new Mock<IGroupManager>();
        var clients = new Mock<IHubCallerClients<IRuntimeClient>>();

        var hub = new RuntimeHub(
            _db,
            _mediator.Object,
            _agentHub.Object,
            Mock.Of<ITurnDispatcher>(),
            new HealthSnapshotBuffer(),
            new ServiceDownDetector(),
            // SecretEncryptionService + IGithubAppTokenService + IAgentPermissionsResolver
            // + ISystemSettingsService + IAgentSecretsResolver unused on this path — null! placeholders.
            null!,
            null!,
            null!,
            null!,
            null!,
            new Api.Tests.Infrastructure.FakeClock(),
            NullLogger<RuntimeHub>.Instance)
        {
            Context = context,
            Groups = groups.Object,
            Clients = clients.Object,
        };

        return new HubHarness(hub, context, groups, connectionId);
    }

    /// <summary>
    /// Minimal HubCallerContext stand-in — only ConnectionId and Items are
    /// touched on this code path. Mirrors the fakes in sibling RuntimeHub
    /// test files.
    /// </summary>
    private sealed class FakeHubCallerContext : HubCallerContext
    {
        private readonly string _connectionId;
        private readonly IFeatureCollection _features;
        private readonly Dictionary<object, object?> _items = new();

        public int AbortCount { get; private set; }

        public FakeHubCallerContext(string connectionId, HttpContext httpContext)
        {
            _connectionId = connectionId;
            _features = new FeatureCollection();
            _features.Set<IHttpContextFeature>(new HttpContextFeature { HttpContext = httpContext });
        }

        public override string ConnectionId => _connectionId;
        public override string? UserIdentifier => null;
        public override ClaimsPrincipal? User => null;
        public override IDictionary<object, object?> Items => _items;
        public override IFeatureCollection Features => _features;
        public override CancellationToken ConnectionAborted => CancellationToken.None;

        public override void Abort() => AbortCount++;

        private sealed class HttpContextFeature : IHttpContextFeature
        {
            public HttpContext? HttpContext { get; set; }
        }
    }
}
