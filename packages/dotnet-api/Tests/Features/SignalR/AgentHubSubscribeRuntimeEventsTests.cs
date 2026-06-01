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
using Source.Features.RuntimeLifecycle.Models;
using Source.Features.SignalR.Hubs;
using Source.Infrastructure;
using Source.Infrastructure.Interceptors;

namespace Api.Tests.Features.SignalR;

/// <summary>
/// Unit tests for <see cref="AgentHub.SubscribeToRuntimeEvents"/> and
/// <see cref="AgentHub.UnsubscribeFromRuntimeEvents"/> — the React-facing
/// drawer subscription pair. The drawer calls Subscribe on mount and
/// Unsubscribe on unmount; the hub adds/removes the connection to/from
/// the <c>runtime-events:{runtimeId}</c> group so the daemon's
/// <see cref="RuntimeHub.RecordRuntimeEvent"/> broadcasts land on subscribed
/// tabs.
///
/// <para>The contract under test:</para>
/// <list type="bullet">
///   <item>Subscribe joins the correctly-named group only after the runtime
///         existence check passes.</item>
///   <item>Subscribe rejects <c>Guid.Empty</c>, missing user, and unknown
///         runtimes with <see cref="HubException"/> — these are real client
///         errors worth surfacing on the JS console, not silent no-ops.</item>
///   <item>Unsubscribe is idempotent and skips existence checks (matches
///         <c>LeaveWorkspace</c>): leaving a group you may already have
///         left is harmless and a DB round-trip on every drawer close is
///         wasteful.</item>
///   <item>Unsubscribe still rejects <c>Guid.Empty</c> — that's a real bug
///         on the calling side, not a tolerable edge case.</item>
/// </list>
/// </summary>
public class AgentHubSubscribeRuntimeEventsTests : IDisposable
{
    private const string TestUserId = "user-123";

    private readonly ServiceProvider _provider;
    private readonly ApplicationDbContext _db;

    private readonly Mock<IHubClients<IRuntimeClient>> _runtimeClients = new();
    private readonly Mock<IHubContext<RuntimeHub, IRuntimeClient>> _runtimeHub = new();

    public AgentHubSubscribeRuntimeEventsTests()
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

        services.AddDbContext<ApplicationDbContext>((sp, options) =>
        {
            options.UseInMemoryDatabase(dbName);
            options.AddInterceptors(sp.GetRequiredService<DomainEventInterceptor>());
        });

        _provider = services.BuildServiceProvider();
        _db = _provider.GetRequiredService<ApplicationDbContext>();
        _db.Database.EnsureCreated();

        _runtimeHub.SetupGet(h => h.Clients).Returns(_runtimeClients.Object);
    }

    public void Dispose()
    {
        _provider.Dispose();
        GC.SuppressFinalize(this);
    }

    // ------------------------------------------------------------------
    // SubscribeToRuntimeEvents
    // ------------------------------------------------------------------

    [Fact]
    public async Task Subscribe_HappyPath_JoinsRuntimeEventsGroup()
    {
        var runtime = await SeedRuntime();
        var harness = BuildHarness();

        await harness.Hub.SubscribeToRuntimeEvents(runtime.Id);

        // The group name is the contract — the daemon broadcasts to exactly
        // this string in RuntimeHub.RecordRuntimeEvent.
        harness.Groups.Verify(
            g => g.AddToGroupAsync(
                harness.ConnectionId,
                $"runtime-events:{runtime.Id}",
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Subscribe_EmptyRuntimeId_ThrowsHubException()
    {
        // Empty Guid is a real bug on the calling side (the React drawer should
        // never call this with a missing id). Surface it loud — HubException
        // makes the JS console show the error.
        var harness = BuildHarness();

        var act = async () => await harness.Hub.SubscribeToRuntimeEvents(Guid.Empty);
        await act.Should().ThrowAsync<HubException>().WithMessage("Invalid runtimeId");

        harness.Groups.Verify(
            g => g.AddToGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Subscribe_UnknownRuntime_ThrowsHubException()
    {
        // Subscribing to a runtime that does not exist (or has been soft-
        // deleted — global query filter hides those) is a real client error,
        // not a silent no-op: the React side has stale state and needs to
        // know about it.
        var harness = BuildHarness();

        var act = async () => await harness.Hub.SubscribeToRuntimeEvents(Guid.NewGuid());
        await act.Should().ThrowAsync<HubException>().WithMessage("Runtime not found");

        harness.Groups.Verify(
            g => g.AddToGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Subscribe_Unauthenticated_ThrowsHubException()
    {
        // [Authorize] should have rejected before the hub even ran — this is
        // defense-in-depth for the case where a connection somehow has a
        // null/empty user principal. We must NOT join a group with a missing
        // user id.
        var runtime = await SeedRuntime();
        var harness = BuildAnonymousHarness();

        var act = async () => await harness.Hub.SubscribeToRuntimeEvents(runtime.Id);
        await act.Should().ThrowAsync<HubException>().WithMessage("Unauthenticated");

        harness.Groups.Verify(
            g => g.AddToGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ------------------------------------------------------------------
    // UnsubscribeFromRuntimeEvents
    // ------------------------------------------------------------------

    [Fact]
    public async Task Unsubscribe_HappyPath_LeavesRuntimeEventsGroup()
    {
        var harness = BuildHarness();
        var runtimeId = Guid.NewGuid();

        // Unsubscribe doesn't check existence — it's idempotent, no DB
        // round-trip. So we don't need to seed a runtime here.
        await harness.Hub.UnsubscribeFromRuntimeEvents(runtimeId);

        harness.Groups.Verify(
            g => g.RemoveFromGroupAsync(
                harness.ConnectionId,
                $"runtime-events:{runtimeId}",
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Unsubscribe_EmptyRuntimeId_ThrowsHubException()
    {
        // Same rationale as Subscribe — Guid.Empty is a real calling-side
        // bug, not a tolerable edge case.
        var harness = BuildHarness();

        var act = async () => await harness.Hub.UnsubscribeFromRuntimeEvents(Guid.Empty);
        await act.Should().ThrowAsync<HubException>().WithMessage("Invalid runtimeId");

        harness.Groups.Verify(
            g => g.RemoveFromGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Unsubscribe_UnknownRuntime_IsIdempotent()
    {
        // Critical: Unsubscribe must NOT throw on a non-existent runtime —
        // the user could close the drawer after the runtime was hard-deleted
        // in another tab. SignalR's RemoveFromGroupAsync is a no-op for
        // non-members, and we don't add a DB existence check on this path
        // (matches LeaveWorkspace).
        var harness = BuildHarness();

        var act = async () => await harness.Hub.UnsubscribeFromRuntimeEvents(Guid.NewGuid());
        await act.Should().NotThrowAsync(
            "Unsubscribe is idempotent — leaving a group you may not be in (or for a runtime that no longer exists) must not throw");
    }

    [Fact]
    public async Task Unsubscribe_Unauthenticated_DoesNotThrow()
    {
        // Unlike Subscribe, Unsubscribe does NOT gate on user identity — the
        // drawer close path runs even on a connection that's about to be torn
        // down, and forcing an auth round-trip there is wasteful. Mirrors
        // LeaveWorkspace's contract.
        var harness = BuildAnonymousHarness();

        var act = async () => await harness.Hub.UnsubscribeFromRuntimeEvents(Guid.NewGuid());
        await act.Should().NotThrowAsync();
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

    private record HubHarness(
        AgentHub Hub,
        FakeHubCallerContext Context,
        Mock<IGroupManager> Groups,
        string ConnectionId);

    private HubHarness BuildHarness() => BuildHarnessCore(BuildUserPrincipal(TestUserId));

    private HubHarness BuildAnonymousHarness() => BuildHarnessCore(user: null);

    private HubHarness BuildHarnessCore(ClaimsPrincipal? user)
    {
        const string connectionId = "conn-test";

        var http = new DefaultHttpContext();
        var context = new FakeHubCallerContext(connectionId, http, user);

        var groups = new Mock<IGroupManager>();
        var clients = new Mock<IHubCallerClients<IAgentClient>>();

        var hub = new AgentHub(
            _db,
            _runtimeHub.Object,
            Mock.Of<Source.Features.Conversations.Services.ITurnDispatcher>(),
            Mock.Of<IMediator>(),
            Mock.Of<Source.Features.SignalR.Services.IAgentSecretsResolver>(),
            NullLogger<AgentHub>.Instance)
        {
            Context = context,
            Groups = groups.Object,
            Clients = clients.Object,
        };

        return new HubHarness(hub, context, groups, connectionId);
    }

    private static ClaimsPrincipal BuildUserPrincipal(string userId)
    {
        var identity = new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, userId) },
            authenticationType: "Test");
        return new ClaimsPrincipal(identity);
    }

    /// <summary>
    /// Minimal HubCallerContext stand-in. Mirrors the fake used in the
    /// sibling AgentHub test files.
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
