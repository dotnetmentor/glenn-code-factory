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
using Source.Features.RuntimeLifecycle.Models;
using Source.Features.SignalR.Contracts;
using Source.Features.SignalR.Hubs;
using Source.Infrastructure;
using Source.Infrastructure.Interceptors;

namespace Api.Tests.Features.SignalR;

/// <summary>
/// Unit tests for <see cref="AgentHub.CancelTurn"/> — the user-facing entry
/// point for "stop the in-flight turn." Bootstrap mirrors
/// <see cref="AgentHubSubmitPromptTests"/>: real <see cref="ApplicationDbContext"/>
/// on InMemory + the <see cref="DomainEventInterceptor"/>, with the cross-hub
/// <see cref="IHubContext{THub, T}"/> mocked end-to-end so we can assert the
/// dispatched <see cref="CancelTurnPayload"/> shape and target group.
///
/// <para>The key invariant under test: <see cref="AgentHub.CancelTurn"/> does
/// NOT mutate <see cref="AgentSession.Status"/>. The daemon's TurnCanceled
/// event drives the transition through <c>RuntimeHub.EmitEvent</c>; this hub
/// just dispatches the cancel signal.</para>
/// </summary>
public class AgentHubCancelTurnTests : IDisposable
{
    private const string TestUserId = "user-123";

    private readonly ServiceProvider _provider;
    private readonly ApplicationDbContext _db;

    private readonly Mock<IHubClients<IRuntimeClient>> _runtimeClients = new();
    private readonly Mock<IHubContext<RuntimeHub, IRuntimeClient>> _runtimeHub = new();
    private readonly Mock<IRuntimeClient> _runtimeGroupClient = new();

    public AgentHubCancelTurnTests()
    {
        var dbName = Guid.NewGuid().ToString();
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddHttpContextAccessor();
        services.AddSignalR();

        services.AddMediatR(cfg =>
            cfg.RegisterServicesFromAssembly(typeof(AgentEventEmitted).Assembly));

        services.AddSingleton<IBackgroundJobClient>(new Mock<IBackgroundJobClient>().Object);
        services.AddScoped<DomainEventInterceptor>();

        // CancelTurn now delegates to the CancelSessionCommand handler via MediatR,
        // and that handler dispatches the cancel to the runtime group through this
        // cross-hub. Register the mock so the real handler (resolved by the real
        // mediator below) fans out through _runtimeGroupClient, keeping the
        // end-to-end dispatch assertions valid.
        services.AddSingleton<IHubContext<RuntimeHub, IRuntimeClient>>(_runtimeHub.Object);

        services.AddDbContext<ApplicationDbContext>((sp, options) =>
        {
            options.UseInMemoryDatabase(dbName);
            options.AddInterceptors(sp.GetRequiredService<DomainEventInterceptor>());
        });

        _provider = services.BuildServiceProvider();
        _db = _provider.GetRequiredService<ApplicationDbContext>();
        _db.Database.EnsureCreated();

        _runtimeHub.SetupGet(h => h.Clients).Returns(_runtimeClients.Object);
        _runtimeClients.Setup(c => c.Group(It.IsAny<string>())).Returns(_runtimeGroupClient.Object);
        _runtimeGroupClient
            .Setup(c => c.CancelTurn(It.IsAny<CancelTurnPayload>()))
            .Returns(Task.CompletedTask);
    }

    public void Dispose()
    {
        _provider.Dispose();
        GC.SuppressFinalize(this);
    }

    // ------------------------------------------------------------------
    // Happy path
    // ------------------------------------------------------------------

    [Fact]
    public async Task CancelTurn_RunningSession_DispatchesCancelToRuntimeGroup()
    {
        var projectId = Guid.NewGuid();
        var runtime = await SeedOnlineRuntime(projectId);
        var conversation = await SeedConversation(projectId);
        var session = await SeedSession(conversation.Id, AgentSessionStatus.Running, runtime.Id);

        CancelTurnPayload? dispatched = null;
        _runtimeGroupClient
            .Setup(c => c.CancelTurn(It.IsAny<CancelTurnPayload>()))
            .Callback<CancelTurnPayload>(p => dispatched = p)
            .Returns(Task.CompletedTask);

        var harness = BuildHarness();
        await harness.Hub.CancelTurn(new CancelTurnRequest(session.Id));

        // Dispatched once to the runtime-{runtime.Id} group.
        _runtimeClients.Verify(c => c.Group($"runtime-{runtime.Id}"), Times.Once);
        _runtimeClients.Verify(
            c => c.Group(It.Is<string>(s => s != $"runtime-{runtime.Id}")),
            Times.Never);
        _runtimeGroupClient.Verify(c => c.CancelTurn(It.IsAny<CancelTurnPayload>()), Times.Once);

        dispatched.Should().NotBeNull();
        dispatched!.SessionId.Should().Be(session.Id);

        // The CancelSessionCommand handler marks a Running session Canceling
        // (optimistic local transition); the daemon's later TurnCanceled event
        // drives the final terminal transition.
        var reloaded = await _db.AgentSessions.SingleAsync(s => s.Id == session.Id);
        reloaded.Status.Should().Be(AgentSessionStatus.Canceling,
            "cancelling a Running session moves it to the intermediate Canceling state");
    }

    // ------------------------------------------------------------------
    // Status gating — non-Running statuses no-op
    // ------------------------------------------------------------------

    [Fact]
    public async Task CancelTurn_PendingSession_NoDispatch()
    {
        var projectId = Guid.NewGuid();
        await SeedOnlineRuntime(projectId);
        var conversation = await SeedConversation(projectId);
        var session = await SeedSession(conversation.Id, AgentSessionStatus.Pending);

        var harness = BuildHarness();
        var act = async () => await harness.Hub.CancelTurn(new CancelTurnRequest(session.Id));

        await act.Should().NotThrowAsync();
        // A Pending (queued, never-dispatched) session has no in-flight turn, so
        // nothing is pushed to the runtime — but the handler cancels it outright.
        _runtimeGroupClient.Verify(c => c.CancelTurn(It.IsAny<CancelTurnPayload>()), Times.Never);

        var reloaded = await _db.AgentSessions.SingleAsync(s => s.Id == session.Id);
        reloaded.Status.Should().Be(AgentSessionStatus.Canceled,
            "a Pending session is canceled immediately — there is no running turn to drain");
    }

    [Fact]
    public async Task CancelTurn_SucceededSession_NoDispatch()
    {
        var projectId = Guid.NewGuid();
        await SeedOnlineRuntime(projectId);
        var conversation = await SeedConversation(projectId);
        var session = await SeedSession(conversation.Id, AgentSessionStatus.Succeeded);

        var harness = BuildHarness();
        var act = async () => await harness.Hub.CancelTurn(new CancelTurnRequest(session.Id));

        await act.Should().NotThrowAsync();
        _runtimeGroupClient.Verify(c => c.CancelTurn(It.IsAny<CancelTurnPayload>()), Times.Never);
    }

    [Fact]
    public async Task CancelTurn_FailedSession_NoDispatch()
    {
        var projectId = Guid.NewGuid();
        await SeedOnlineRuntime(projectId);
        var conversation = await SeedConversation(projectId);
        var session = await SeedSession(conversation.Id, AgentSessionStatus.Failed);

        var harness = BuildHarness();
        await harness.Hub.CancelTurn(new CancelTurnRequest(session.Id));

        _runtimeGroupClient.Verify(c => c.CancelTurn(It.IsAny<CancelTurnPayload>()), Times.Never);
    }

    [Fact]
    public async Task CancelTurn_AlreadyCanceledSession_NoDispatch()
    {
        var projectId = Guid.NewGuid();
        await SeedOnlineRuntime(projectId);
        var conversation = await SeedConversation(projectId);
        var session = await SeedSession(conversation.Id, AgentSessionStatus.Canceled);

        var harness = BuildHarness();
        await harness.Hub.CancelTurn(new CancelTurnRequest(session.Id));

        _runtimeGroupClient.Verify(c => c.CancelTurn(It.IsAny<CancelTurnPayload>()), Times.Never);
    }

    // ------------------------------------------------------------------
    // Unknown session — silent no-op
    // ------------------------------------------------------------------

    [Fact]
    public async Task CancelTurn_UnknownSession_NoOp()
    {
        var projectId = Guid.NewGuid();
        await SeedOnlineRuntime(projectId);

        var harness = BuildHarness();
        var act = async () => await harness.Hub.CancelTurn(new CancelTurnRequest(Guid.NewGuid()));

        await act.Should().NotThrowAsync();
        _runtimeGroupClient.Verify(c => c.CancelTurn(It.IsAny<CancelTurnPayload>()), Times.Never);
    }

    // ------------------------------------------------------------------
    // No runtime for project — silent no-op (logs warning)
    // ------------------------------------------------------------------

    [Fact]
    public async Task CancelTurn_NoRuntimeForProject_NoDispatch()
    {
        var projectId = Guid.NewGuid();
        // Deliberately no runtime seeded.
        var conversation = await SeedConversation(projectId);
        var session = await SeedSession(conversation.Id, AgentSessionStatus.Running);

        var harness = BuildHarness();
        var act = async () => await harness.Hub.CancelTurn(new CancelTurnRequest(session.Id));

        await act.Should().NotThrowAsync();
        _runtimeGroupClient.Verify(c => c.CancelTurn(It.IsAny<CancelTurnPayload>()), Times.Never);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private async Task<ProjectRuntime> SeedOnlineRuntime(Guid projectId)
    {
        // CancelTurn gates on CallerCanAccessProjectAsync — the conversation's
        // project must be owned by the caller or the cancel is denied (no dispatch).
        if (!await _db.Projects.AnyAsync(p => p.Id == projectId))
        {
            _db.Projects.Add(new Source.Features.Projects.Models.Project
            {
                Id = projectId,
                OwnerUserId = TestUserId,
                WorkspaceId = Guid.NewGuid(),
                Name = "seeded-project",
            });
        }
        var runtime = new ProjectRuntime
        {
            ProjectId = projectId,
            State = RuntimeState.Online,
            Region = "arn",
        };
        _db.ProjectRuntimes.Add(runtime);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        return runtime;
    }

    private async Task<Conversation> SeedConversation(Guid projectId)
    {
        var conversation = new Conversation
        {
            ProjectId = projectId,
            BranchId = Guid.NewGuid(),
            Title = "seeded",
            Status = ConversationStatus.Active,
            LastActivityAt = DateTime.UtcNow,
            EventCount = 0,
        };
        _db.Conversations.Add(conversation);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        return conversation;
    }

    private async Task<AgentSession> SeedSession(
        Guid conversationId, AgentSessionStatus status, Guid? runtimeId = null)
    {
        var session = new AgentSession
        {
            ConversationId = conversationId,
            RuntimeId = runtimeId ?? Guid.NewGuid(),
            Prompt = "test prompt",
            Status = status,
        };
        _db.AgentSessions.Add(session);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        return session;
    }

    private record HubHarness(AgentHub Hub, FakeHubCallerContext Context, string ConnectionId);

    private HubHarness BuildHarness()
    {
        const string connectionId = "conn-test";

        var http = new DefaultHttpContext();
        var context = new FakeHubCallerContext(connectionId, http, BuildUserPrincipal(TestUserId));

        var groups = new Mock<IGroupManager>();
        var clients = new Mock<IHubCallerClients<IAgentClient>>();

        var hub = new AgentHub(_db, _runtimeHub.Object, Mock.Of<Source.Features.Conversations.Services.ITurnDispatcher>(), _provider.GetRequiredService<IMediator>(), Mock.Of<Source.Features.SignalR.Services.IAgentSecretsResolver>(), NullLogger<AgentHub>.Instance)
        {
            Context = context,
            Groups = groups.Object,
            Clients = clients.Object,
        };

        return new HubHarness(hub, context, connectionId);
    }

    private static ClaimsPrincipal BuildUserPrincipal(string userId)
    {
        var identity = new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, userId) },
            authenticationType: "Test");
        return new ClaimsPrincipal(identity);
    }

    /// <summary>
    /// Minimal HubCallerContext stand-in. Mirrors the fake used in
    /// <see cref="AgentHubSubmitPromptTests"/>.
    /// </summary>
    private sealed class FakeHubCallerContext : HubCallerContext
    {
        private readonly string _connectionId;
        private readonly IFeatureCollection _features;
        private readonly Dictionary<object, object?> _items = new();
        private readonly ClaimsPrincipal? _user;

        public int AbortCount { get; private set; }

        public FakeHubCallerContext(string connectionId, HttpContext httpContext, ClaimsPrincipal? user)
        {
            _connectionId = connectionId;
            _features = new FeatureCollection();
            _features.Set<IHttpContextFeature>(new HttpContextFeature { HttpContext = httpContext });
            _user = user;
        }

        public override string ConnectionId => _connectionId;
        public override string? UserIdentifier => null;
        public override ClaimsPrincipal? User => _user;
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
