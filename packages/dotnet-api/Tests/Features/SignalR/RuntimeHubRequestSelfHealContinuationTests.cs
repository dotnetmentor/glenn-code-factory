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
using Source.Features.Hooks.Models;
using Source.Features.RuntimeLifecycle.Events;
using Source.Features.RuntimeLifecycle.Models;
using Source.Features.SignalR.Contracts;
using Source.Features.SignalR.Hubs;
using Source.Infrastructure;
using Source.Infrastructure.Interceptors;

namespace Api.Tests.Features.SignalR;

/// <summary>
/// Unit tests for <see cref="RuntimeHub.RequestSelfHealContinuation"/> — the
/// daemon-driven retry path after an <c>afterPrompt</c> hook fails. Bootstrap
/// mirrors <see cref="RuntimeHubEmitEventTests"/>: real
/// <see cref="ApplicationDbContext"/> on InMemory + <see cref="DomainEventInterceptor"/>
/// + MediatR, with the SignalR primitives stamped via a small
/// <see cref="FakeHubCallerContext"/>.
///
/// <para>The shared <see cref="ITurnDispatcher"/> is mocked so we can assert
/// the exact args the hub forwards on approval, without exercising the full
/// session-create + audit-event chain (covered separately in
/// <c>AgentHubSubmitPromptTests</c> and <c>RuntimeHubEmitEventTests</c>).</para>
///
/// <para>The <c>HookSelfHealStarted</c> / <c>HookSelfHealMaxedOut</c> fan-outs
/// to <see cref="AgentHub"/> are mocked end-to-end so we can assert (a) which
/// payload the hub broadcast on which result, and (b) that no broadcast
/// happens on the rejection paths that don't warrant one.</para>
/// </summary>
public class RuntimeHubRequestSelfHealContinuationTests : IDisposable
{
    private readonly ServiceProvider _provider;
    private readonly ApplicationDbContext _db;
    private readonly Mock<IMediator> _mediator;

    // Cross-hub fan-out — mocked end-to-end so tests can assert against
    // _agentGroupClient (the receiver-typed proxy on the project group).
    private readonly Mock<IHubClients<IAgentClient>> _agentClients = new();
    private readonly Mock<IHubContext<AgentHub, IAgentClient>> _agentHub = new();
    private readonly Mock<IAgentClient> _agentGroupClient = new();

    // Shared dispatcher — mocked. The dispatch path itself is tested in
    // AgentHubSubmitPromptTests; here we only assert that the hub forwarded
    // the right args on approval.
    private readonly Mock<ITurnDispatcher> _turnDispatcher = new();

    public RuntimeHubRequestSelfHealContinuationTests()
    {
        var dbName = Guid.NewGuid().ToString();
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddHttpContextAccessor();

        // SignalR registration is required because the auto-discovered
        // BroadcastAgentEventHandler depends on IHubContext<AgentHub, IAgentClient>;
        // the interceptor publishes AgentEventEmitted via this provider on
        // every save and the handler must resolve.
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

        // Wire the cross-hub mock so Clients.Group(...).HookSelfHealStarted(...)
        // (and HookSelfHealMaxedOut) route to _agentGroupClient.
        _agentHub.SetupGet(h => h.Clients).Returns(_agentClients.Object);
        _agentClients.Setup(c => c.Group(It.IsAny<string>())).Returns(_agentGroupClient.Object);
        _agentGroupClient
            .Setup(c => c.HookSelfHealStarted(It.IsAny<HookSelfHealStartedPayload>()))
            .Returns(Task.CompletedTask);
        _agentGroupClient
            .Setup(c => c.HookSelfHealMaxedOut(It.IsAny<HookSelfHealMaxedOutPayload>()))
            .Returns(Task.CompletedTask);
    }

    public void Dispose()
    {
        _provider.Dispose();
        GC.SuppressFinalize(this);
    }

    // ------------------------------------------------------------------
    // Budget cap blocks at 3
    // ------------------------------------------------------------------

    [Fact]
    public async Task RequestSelfHealContinuation_AtBudgetCap_RejectsAndFlipsSessionToFailed()
    {
        var (runtime, _, session) = await SeedRunningSessionWithRuntime(selfHealAttempts: 3);

        var harness = BuildHarness();
        SetRuntimeContext(harness, runtime.Id);

        var response = await harness.Hub.RequestSelfHealContinuation(BuildPayload(runtime.Id, session));

        // Structured rejection — daemon switches on RejectionReason for UX.
        response.Accepted.Should().BeFalse();
        response.NewTurnId.Should().BeNull();
        response.RejectionReason.Should().Be("maxedOut");

        // Session should be flipped to Failed so the UI stops pretending the
        // turn is still in flight. CompletedAt anchors the terminal state.
        _db.ChangeTracker.Clear();
        var reloaded = await _db.AgentSessions.SingleAsync(s => s.Id == session.Id);
        reloaded.Status.Should().Be(AgentSessionStatus.Failed);
        reloaded.CompletedAt.Should().NotBeNull();
        reloaded.SelfHealAttempts.Should().Be(3, "the cap is read-only — we don't bump on rejection");

        // Maxed-out fan-out fired exactly once with the right payload.
        _agentGroupClient.Verify(
            c => c.HookSelfHealMaxedOut(It.Is<HookSelfHealMaxedOutPayload>(p =>
                p.RuntimeId == runtime.Id &&
                p.ConversationId == session.ConversationId &&
                p.TurnId == session.Id &&
                p.Iteration == 4)),
            Times.Once);

        // Started fan-out must NOT fire on the rejection path.
        _agentGroupClient.Verify(
            c => c.HookSelfHealStarted(It.IsAny<HookSelfHealStartedPayload>()),
            Times.Never);

        // Dispatcher must NOT be invoked when budget is exhausted.
        _turnDispatcher.Verify(
            d => d.DispatchTurnAsync(It.IsAny<DispatchTurnArgs>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ------------------------------------------------------------------
    // Successful continuation
    // ------------------------------------------------------------------

    [Fact]
    public async Task RequestSelfHealContinuation_BelowBudget_ApprovesBumpsAndDispatches()
    {
        var (runtime, conversation, session) = await SeedRunningSessionWithRuntime(selfHealAttempts: 2);

        var newTurnId = Guid.NewGuid();
        DispatchTurnArgs? capturedArgs = null;
        _turnDispatcher
            .Setup(d => d.DispatchTurnAsync(It.IsAny<DispatchTurnArgs>(), It.IsAny<CancellationToken>()))
            .Callback<DispatchTurnArgs, CancellationToken>((args, _) => capturedArgs = args)
            .ReturnsAsync(new DispatchTurnResult(newTurnId, Queued: false, QueuePosition: null));

        var harness = BuildHarness();
        SetRuntimeContext(harness, runtime.Id);

        var payload = new RequestSelfHealContinuationPayload(
            RuntimeId: runtime.Id,
            ConversationId: conversation.Id,
            TurnId: session.Id,
            AgentId: "sess_abc",
            HookName: "afterPrompt:lint",
            FeedbackPrompt: "lint failed: please fix the unused import",
            Iteration: 3);

        var response = await harness.Hub.RequestSelfHealContinuation(payload);

        // Approval response carries the new turn id.
        response.Accepted.Should().BeTrue();
        response.NewTurnId.Should().Be(newTurnId);
        response.RejectionReason.Should().BeNull();

        // Counter bumped, session still Running (the new turn is a separate
        // session — the original is still in flight from the daemon's POV
        // until it sends a terminal event).
        _db.ChangeTracker.Clear();
        var reloaded = await _db.AgentSessions.SingleAsync(s => s.Id == session.Id);
        reloaded.SelfHealAttempts.Should().Be(3, "the per-turn budget counter must advance on approval");
        reloaded.Status.Should().Be(AgentSessionStatus.Running);

        // Dispatcher invoked exactly once with the right shape.
        _turnDispatcher.Verify(
            d => d.DispatchTurnAsync(It.IsAny<DispatchTurnArgs>(), It.IsAny<CancellationToken>()),
            Times.Once);
        capturedArgs.Should().NotBeNull();
        capturedArgs!.ConversationId.Should().Be(conversation.Id);
        capturedArgs.ProjectId.Should().Be(conversation.ProjectId);
        capturedArgs.Prompt.Should().Be("lint failed: please fix the unused import");
        capturedArgs.AgentId.Should().Be("sess_abc",
            "the SDK session token must be propagated so Claude has full context of the failing turn");
        capturedArgs.EventOriginUserId.Should().BeNull(
            "self-heal is daemon-driven, not user-driven — the audit row should omit userId");

        // Started fan-out fired exactly once carrying the new turn id.
        _agentGroupClient.Verify(
            c => c.HookSelfHealStarted(It.Is<HookSelfHealStartedPayload>(p =>
                p.RuntimeId == runtime.Id &&
                p.ConversationId == conversation.Id &&
                p.PreviousTurnId == session.Id &&
                p.NewTurnId == newTurnId &&
                p.Iteration == 3)),
            Times.Once);

        // Maxed-out must not fire on the approval path.
        _agentGroupClient.Verify(
            c => c.HookSelfHealMaxedOut(It.IsAny<HookSelfHealMaxedOutPayload>()),
            Times.Never);
    }

    // ------------------------------------------------------------------
    // Turn not running (terminal / missing)
    // ------------------------------------------------------------------

    [Fact]
    public async Task RequestSelfHealContinuation_TurnAlreadyCompleted_RejectsTurnNotRunning()
    {
        // Seed a session that's already in Succeeded — the daemon's view of
        // "still running" can drift from ours if a TurnCompleted event landed
        // between the hook firing and this request. Re-attach via a tracked
        // load (the seed helper clears the change tracker) so the status
        // mutation actually persists.
        var (runtime, conversation, session) = await SeedRunningSessionWithRuntime();
        var tracked = await _db.AgentSessions.SingleAsync(s => s.Id == session.Id);
        tracked.Status = AgentSessionStatus.Succeeded;
        tracked.CompletedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var harness = BuildHarness();
        SetRuntimeContext(harness, runtime.Id);

        var response = await harness.Hub.RequestSelfHealContinuation(
            BuildPayload(runtime.Id, session, conversationId: conversation.Id));

        response.Accepted.Should().BeFalse();
        response.NewTurnId.Should().BeNull();
        response.RejectionReason.Should().Be("turnNotRunning");

        // No DB writes — counter unchanged, no terminal flip on the original
        // (it's already Succeeded).
        var reloaded = await _db.AgentSessions.SingleAsync(s => s.Id == session.Id);
        reloaded.SelfHealAttempts.Should().Be(0);
        reloaded.Status.Should().Be(AgentSessionStatus.Succeeded);

        // No dispatch, no broadcast.
        _turnDispatcher.Verify(
            d => d.DispatchTurnAsync(It.IsAny<DispatchTurnArgs>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _agentGroupClient.Verify(
            c => c.HookSelfHealStarted(It.IsAny<HookSelfHealStartedPayload>()),
            Times.Never);
        _agentGroupClient.Verify(
            c => c.HookSelfHealMaxedOut(It.IsAny<HookSelfHealMaxedOutPayload>()),
            Times.Never);
    }

    [Fact]
    public async Task RequestSelfHealContinuation_UnknownTurnId_RejectsTurnNotRunning()
    {
        // No session at all — daemon is referencing a phantom turn id.
        var runtime = await SeedRuntime();

        var harness = BuildHarness();
        SetRuntimeContext(harness, runtime.Id);

        var phantomTurnId = Guid.NewGuid();
        var response = await harness.Hub.RequestSelfHealContinuation(new RequestSelfHealContinuationPayload(
            RuntimeId: runtime.Id,
            ConversationId: Guid.NewGuid(),
            TurnId: phantomTurnId,
            AgentId: "sess_zzz",
            HookName: "afterPrompt:test",
            FeedbackPrompt: "fix it",
            Iteration: 1));

        response.Accepted.Should().BeFalse();
        response.RejectionReason.Should().Be("turnNotRunning");

        _turnDispatcher.Verify(
            d => d.DispatchTurnAsync(It.IsAny<DispatchTurnArgs>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ------------------------------------------------------------------
    // Runtime mismatch
    // ------------------------------------------------------------------

    [Fact]
    public async Task RequestSelfHealContinuation_RuntimeClaimMismatch_RejectsRuntimeMismatch()
    {
        var (_, _, session) = await SeedRunningSessionWithRuntime(selfHealAttempts: 0);

        // Claim a DIFFERENT runtime id in the payload than what's stamped on
        // the connection — defense-in-depth against a daemon claiming a peer's
        // runtime.
        var connectionRuntimeId = Guid.NewGuid();
        var claimedRuntimeId = Guid.NewGuid();

        var harness = BuildHarness();
        SetRuntimeContext(harness, connectionRuntimeId);

        var response = await harness.Hub.RequestSelfHealContinuation(new RequestSelfHealContinuationPayload(
            RuntimeId: claimedRuntimeId, // mismatch
            ConversationId: session.ConversationId,
            TurnId: session.Id,
            AgentId: "sess_x",
            HookName: "afterPrompt:noop",
            FeedbackPrompt: "fix it",
            Iteration: 1));

        response.Accepted.Should().BeFalse();
        response.NewTurnId.Should().BeNull();
        response.RejectionReason.Should().Be("runtimeMismatch");

        // Critical: must NOT have written anything. The session must still
        // be Running (not flipped to Failed) and the counter unchanged.
        _db.ChangeTracker.Clear();
        var reloaded = await _db.AgentSessions.SingleAsync(s => s.Id == session.Id);
        reloaded.SelfHealAttempts.Should().Be(0);
        reloaded.Status.Should().Be(AgentSessionStatus.Running);

        // No dispatch, no fan-out — runtime mismatch is a hard early exit.
        _turnDispatcher.Verify(
            d => d.DispatchTurnAsync(It.IsAny<DispatchTurnArgs>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _agentGroupClient.Verify(
            c => c.HookSelfHealStarted(It.IsAny<HookSelfHealStartedPayload>()),
            Times.Never);
        _agentGroupClient.Verify(
            c => c.HookSelfHealMaxedOut(It.IsAny<HookSelfHealMaxedOutPayload>()),
            Times.Never);
    }

    [Fact]
    public async Task RequestSelfHealContinuation_MissingRuntimeIdInContext_RejectsRuntimeMismatch()
    {
        // Connection somehow bypassed the handshake gate — Items["RuntimeId"]
        // missing entirely. The hub treats this as runtimeMismatch (the
        // method name in the helper is the identity for the warning log).
        var (_, _, session) = await SeedRunningSessionWithRuntime();

        var harness = BuildHarness();
        // Deliberately do NOT set Items["RuntimeId"].

        var response = await harness.Hub.RequestSelfHealContinuation(
            BuildPayload(Guid.NewGuid(), session));

        response.Accepted.Should().BeFalse();
        response.RejectionReason.Should().Be("runtimeMismatch");

        _turnDispatcher.Verify(
            d => d.DispatchTurnAsync(It.IsAny<DispatchTurnArgs>(), It.IsAny<CancellationToken>()),
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
        SeedRunningSessionWithRuntime(int selfHealAttempts = 0)
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
            Prompt = "original prompt",
            Status = AgentSessionStatus.Running,
            StartedAt = DateTime.UtcNow,
            AgentId = "sess_abc",
            SelfHealAttempts = selfHealAttempts,
        };
        _db.AgentSessions.Add(session);

        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        return (runtime, conversation, session);
    }

    private static RequestSelfHealContinuationPayload BuildPayload(
        Guid runtimeId,
        AgentSession session,
        Guid? conversationId = null,
        int iteration = 4)
    {
        return new RequestSelfHealContinuationPayload(
            RuntimeId: runtimeId,
            ConversationId: conversationId ?? session.ConversationId,
            TurnId: session.Id,
            AgentId: session.AgentId ?? "sess_default",
            HookName: "afterPrompt:lint",
            FeedbackPrompt: "lint failed: please fix",
            Iteration: iteration);
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

        // GetSecrets / GetRepoAccessToken aren't reached in these tests;
        // null! for the unused SecretEncryptionService and
        // IGithubAppTokenService keeps the harness lean.
        var hub = new RuntimeHub(_db, _mediator.Object, _agentHub.Object, _turnDispatcher.Object, new HealthSnapshotBuffer(), new ServiceDownDetector(), null!, null!, null!, null!, new Api.Tests.Infrastructure.FakeClock(), NullLogger<RuntimeHub>.Instance)
        {
            Context = context,
            Groups = groups.Object,
            Clients = clients.Object,
        };

        return new HubHarness(hub, context, groups, connectionId);
    }

    /// <summary>
    /// Minimal HubCallerContext stand-in. Mirrors the fake in
    /// <see cref="RuntimeHubEmitEventTests"/> — only ConnectionId + Items
    /// are needed on this code path.
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
