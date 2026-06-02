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
using Source.Features.Conversations.Events;
using Source.Features.Conversations.Models;
using Source.Features.Conversations.Services;
using Source.Features.Health;
using Source.Features.Health.Services;
using Source.Features.RuntimeLifecycle.Events;
using Source.Features.RuntimeLifecycle.Models;
using Source.Features.SignalR.Hubs;
using Source.Infrastructure;
using Source.Infrastructure.Interceptors;

namespace Api.Tests.Features.SignalR;

/// <summary>
/// Unit tests for <see cref="RuntimeHub.TurnRefused"/> — the daemon's
/// single-turn-invariant refusal channel. Bootstrap mirrors
/// <see cref="RuntimeHubEmitEventTests"/>: real
/// <see cref="ApplicationDbContext"/> on InMemory + the
/// <see cref="DomainEventInterceptor"/> + MediatR (so the Saved → Publish path
/// runs end-to-end), with the SignalR primitives stamped via a small
/// <see cref="FakeHubCallerContext"/>.
///
/// <para>The cross-hub fan-out to <see cref="AgentHub"/> is mocked end-to-end
/// so tests can assert (a) which payload was broadcast, and (b) that no
/// broadcast happens on the idempotent already-terminal path or the
/// claim-mismatch reject path.</para>
/// </summary>
public class RuntimeHubTurnRefusedTests : IDisposable
{
    private readonly ServiceProvider _provider;
    private readonly ApplicationDbContext _db;
    private readonly Mock<IMediator> _mediator;

    private readonly Mock<IHubClients<IAgentClient>> _agentClients = new();
    private readonly Mock<IHubContext<AgentHub, IAgentClient>> _agentHub = new();
    private readonly Mock<IAgentClient> _agentGroupClient = new();

    public RuntimeHubTurnRefusedTests()
    {
        var dbName = Guid.NewGuid().ToString();
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddHttpContextAccessor();

        // SignalR registration is required because the auto-discovered
        // BroadcastAgentEventHandler depends on IHubContext<AgentHub, IAgentClient>;
        // when the interceptor publishes domain events that handler is
        // resolved from this provider and would fail without the hub services.
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

        _agentHub.SetupGet(h => h.Clients).Returns(_agentClients.Object);
        _agentClients.Setup(c => c.Group(It.IsAny<string>())).Returns(_agentGroupClient.Object);
        _agentGroupClient
            .Setup(c => c.TurnRefused(It.IsAny<TurnRefusedPayload>()))
            .Returns(Task.CompletedTask);
    }

    public void Dispose()
    {
        _provider.Dispose();
        GC.SuppressFinalize(this);
    }

    // ------------------------------------------------------------------
    // Happy paths — Pending and Running both flip to Failed and fan out
    // ------------------------------------------------------------------

    [Fact]
    public async Task TurnRefused_PendingSession_FlipsToFailedAndFansOut()
    {
        var (runtime, _, session) = await SeedSession(AgentSessionStatus.Pending);

        var harness = BuildHarness();
        SetRuntimeContext(harness, runtime.Id);

        var payload = new TurnRefusedPayload(session.Id, "turn_already_running", CurrentSessionId: Guid.NewGuid());

        var before = DateTime.UtcNow;
        await harness.Hub.TurnRefused(payload);
        var after = DateTime.UtcNow;

        _db.ChangeTracker.Clear();
        var reloaded = await _db.AgentSessions.SingleAsync(s => s.Id == session.Id);
        reloaded.Status.Should().Be(AgentSessionStatus.Failed);
        reloaded.CancelReason.Should().Be("daemon_refused_concurrent");
        reloaded.CompletedAt.Should().NotBeNull();
        reloaded.CompletedAt!.Value.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);

        // Fan-out fired exactly once on the project group with the same payload.
        _agentGroupClient.Verify(
            c => c.TurnRefused(It.Is<TurnRefusedPayload>(p =>
                p.SessionId == payload.SessionId &&
                p.Reason == payload.Reason &&
                p.CurrentSessionId == payload.CurrentSessionId)),
            Times.Once);
    }

    [Fact]
    public async Task TurnRefused_RunningSession_FlipsToFailedAndFansOut()
    {
        var (runtime, _, session) = await SeedSession(AgentSessionStatus.Running);

        var harness = BuildHarness();
        SetRuntimeContext(harness, runtime.Id);

        var payload = new TurnRefusedPayload(session.Id, "turn_already_running", CurrentSessionId: null);
        await harness.Hub.TurnRefused(payload);

        _db.ChangeTracker.Clear();
        var reloaded = await _db.AgentSessions.SingleAsync(s => s.Id == session.Id);
        reloaded.Status.Should().Be(AgentSessionStatus.Failed);
        reloaded.CancelReason.Should().Be("daemon_refused_concurrent");
        reloaded.CompletedAt.Should().NotBeNull();

        _agentGroupClient.Verify(
            c => c.TurnRefused(It.Is<TurnRefusedPayload>(p => p.SessionId == session.Id)),
            Times.Once);
    }

    [Fact]
    public async Task TurnRefused_CancelingSession_FlipsToFailedAndFansOut()
    {
        // Canceling is a non-terminal state — the daemon could still race a
        // refusal against a user cancel that hasn't reached terminal yet. The
        // entity method allows the transition; verify it.
        var (runtime, _, session) = await SeedSession(AgentSessionStatus.Canceling);

        var harness = BuildHarness();
        SetRuntimeContext(harness, runtime.Id);

        var payload = new TurnRefusedPayload(session.Id, "turn_already_running", CurrentSessionId: null);
        await harness.Hub.TurnRefused(payload);

        _db.ChangeTracker.Clear();
        var reloaded = await _db.AgentSessions.SingleAsync(s => s.Id == session.Id);
        reloaded.Status.Should().Be(AgentSessionStatus.Failed);
        reloaded.CancelReason.Should().Be("daemon_refused_concurrent");
        reloaded.CompletedAt.Should().NotBeNull();

        _agentGroupClient.Verify(
            c => c.TurnRefused(It.IsAny<TurnRefusedPayload>()),
            Times.Once);
    }

    // ------------------------------------------------------------------
    // Idempotency — already-terminal sessions don't double-flip and don't
    // re-broadcast
    // ------------------------------------------------------------------

    [Fact]
    public async Task TurnRefused_AlreadyFailedSession_IsNoOpAndNoFanOut()
    {
        var (runtime, _, session) = await SeedSession(AgentSessionStatus.Failed,
            cancelReason: "previous_reason",
            completedAt: DateTime.UtcNow.AddMinutes(-1));

        var harness = BuildHarness();
        SetRuntimeContext(harness, runtime.Id);

        var payload = new TurnRefusedPayload(session.Id, "turn_already_running", CurrentSessionId: null);
        await harness.Hub.TurnRefused(payload);

        _db.ChangeTracker.Clear();
        var reloaded = await _db.AgentSessions.SingleAsync(s => s.Id == session.Id);
        reloaded.Status.Should().Be(AgentSessionStatus.Failed);
        // Existing fields are preserved — idempotent no-op.
        reloaded.CancelReason.Should().Be("previous_reason",
            "the entity method must not overwrite fields on an already-terminal session");

        _agentGroupClient.Verify(
            c => c.TurnRefused(It.IsAny<TurnRefusedPayload>()),
            Times.Never);
    }

    [Fact]
    public async Task TurnRefused_AlreadySucceededSession_IsNoOpAndNoFanOut()
    {
        var (runtime, _, session) = await SeedSession(AgentSessionStatus.Succeeded,
            completedAt: DateTime.UtcNow.AddMinutes(-1));

        var harness = BuildHarness();
        SetRuntimeContext(harness, runtime.Id);

        var payload = new TurnRefusedPayload(session.Id, "turn_already_running", CurrentSessionId: null);
        await harness.Hub.TurnRefused(payload);

        _db.ChangeTracker.Clear();
        var reloaded = await _db.AgentSessions.SingleAsync(s => s.Id == session.Id);
        reloaded.Status.Should().Be(AgentSessionStatus.Succeeded,
            "succeeded sessions must not be flipped to Failed by a stale refusal");

        _agentGroupClient.Verify(
            c => c.TurnRefused(It.IsAny<TurnRefusedPayload>()),
            Times.Never);
    }

    [Fact]
    public async Task TurnRefused_AlreadyCanceledSession_IsNoOpAndNoFanOut()
    {
        var (runtime, _, session) = await SeedSession(AgentSessionStatus.Canceled,
            cancelReason: "user_requested",
            completedAt: DateTime.UtcNow.AddMinutes(-1));

        var harness = BuildHarness();
        SetRuntimeContext(harness, runtime.Id);

        var payload = new TurnRefusedPayload(session.Id, "turn_already_running", CurrentSessionId: null);
        await harness.Hub.TurnRefused(payload);

        _db.ChangeTracker.Clear();
        var reloaded = await _db.AgentSessions.SingleAsync(s => s.Id == session.Id);
        reloaded.Status.Should().Be(AgentSessionStatus.Canceled);
        reloaded.CancelReason.Should().Be("user_requested");

        _agentGroupClient.Verify(
            c => c.TurnRefused(It.IsAny<TurnRefusedPayload>()),
            Times.Never);
    }

    // ------------------------------------------------------------------
    // Auth / claim guards
    // ------------------------------------------------------------------

    [Fact]
    public async Task TurnRefused_RuntimeClaimMismatch_ThrowsHubException()
    {
        // Seed session whose runtime owns its project — then call the hub with
        // a DIFFERENT runtime in Context.Items. The hub must hard-fail.
        var (_, _, session) = await SeedSession(AgentSessionStatus.Pending);

        var harness = BuildHarness();
        SetRuntimeContext(harness, Guid.NewGuid()); // different runtime

        var payload = new TurnRefusedPayload(session.Id, "turn_already_running", CurrentSessionId: null);

        var act = async () => await harness.Hub.TurnRefused(payload);
        await act.Should().ThrowAsync<HubException>().WithMessage("runtime claim mismatch");

        // Critical: no DB writes — session must still be Pending.
        _db.ChangeTracker.Clear();
        var reloaded = await _db.AgentSessions.SingleAsync(s => s.Id == session.Id);
        reloaded.Status.Should().Be(AgentSessionStatus.Pending);
        reloaded.CancelReason.Should().BeNull();
        reloaded.CompletedAt.Should().BeNull();

        _agentGroupClient.Verify(
            c => c.TurnRefused(It.IsAny<TurnRefusedPayload>()),
            Times.Never);
    }

    [Fact]
    public async Task TurnRefused_MissingRuntimeIdInContext_IsSilentNoOp()
    {
        // Connection somehow bypassed the handshake gate — Items["RuntimeId"]
        // missing entirely. ResolveRuntimeIdFromContext returns null and the
        // hub drops silently, never throwing on a hot path.
        var (_, _, session) = await SeedSession(AgentSessionStatus.Pending);

        var harness = BuildHarness();
        // Deliberately do NOT set Items["RuntimeId"].

        var payload = new TurnRefusedPayload(session.Id, "turn_already_running", CurrentSessionId: null);
        await harness.Hub.TurnRefused(payload);

        _db.ChangeTracker.Clear();
        var reloaded = await _db.AgentSessions.SingleAsync(s => s.Id == session.Id);
        reloaded.Status.Should().Be(AgentSessionStatus.Pending,
            "missing RuntimeId in Context.Items must be a silent no-op");

        _agentGroupClient.Verify(
            c => c.TurnRefused(It.IsAny<TurnRefusedPayload>()),
            Times.Never);
    }

    [Fact]
    public async Task TurnRefused_UnknownSessionId_IsSilentNoOp()
    {
        var runtime = await SeedRuntime();

        var harness = BuildHarness();
        SetRuntimeContext(harness, runtime.Id);

        var payload = new TurnRefusedPayload(Guid.NewGuid(), "turn_already_running", CurrentSessionId: null);
        await harness.Hub.TurnRefused(payload);

        _agentGroupClient.Verify(
            c => c.TurnRefused(It.IsAny<TurnRefusedPayload>()),
            Times.Never);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private async Task<ProjectRuntime> SeedRuntime()
    {
        var runtime = new ProjectRuntime
        {
            ProjectId = Guid.NewGuid(),
            Region = "arn",
            VolumeSizeGb = 1,
            State = RuntimeState.Online,
        };
        _db.ProjectRuntimes.Add(runtime);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        return runtime;
    }

    private async Task<(ProjectRuntime Runtime, Conversation Conversation, AgentSession Session)>
        SeedSession(
            AgentSessionStatus status,
            string? cancelReason = null,
            DateTime? completedAt = null)
    {
        var runtime = await SeedRuntime();

        var conversation = new Conversation
        {
            ProjectId = runtime.ProjectId,
            BranchId = Guid.NewGuid(),
            Title = "test",
            Status = ConversationStatus.Active,
            LastActivityAt = DateTime.UtcNow,
        };
        _db.Conversations.Add(conversation);

        var session = new AgentSession
        {
            ConversationId = conversation.Id,
            RuntimeId = runtime.Id,
            Prompt = "p",
            Status = status,
            CancelReason = cancelReason,
            CompletedAt = completedAt,
        };
        _db.AgentSessions.Add(session);

        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        return (runtime, conversation, session);
    }

    private void SetRuntimeContext(HubHarness harness, Guid runtimeId)
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
    /// Minimal HubCallerContext stand-in. Mirrors the fakes in
    /// <see cref="RuntimeHubEmitEventTests"/> and
    /// <see cref="RuntimeHubRequestSelfHealContinuationTests"/> — only
    /// ConnectionId + Items are needed on this code path.
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
