using System.IdentityModel.Tokens.Jwt;
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
using Source.Features.Health;
using Source.Features.Health.Services;
using Source.Features.RuntimeLifecycle.Events;
using Source.Features.RuntimeLifecycle.Models;
using Source.Features.RuntimeTokens.Models;
using Source.Features.SignalR.Contracts;
using Source.Features.SignalR.Events;
using Source.Features.SignalR.Hubs;
using Source.Infrastructure;
using Source.Infrastructure.Interceptors;

namespace Api.Tests.Features.SignalR;

/// <summary>
/// Unit tests for <see cref="RuntimeHub"/>. We bootstrap a real
/// <see cref="ApplicationDbContext"/> on EF Core InMemory + the
/// <see cref="DomainEventInterceptor"/> + MediatR (mirroring
/// <c>RuntimeReconcilerJobTests</c>) so the soft-delete query filter runs and
/// the publish path is real. The SignalR primitives — <c>Context</c>,
/// <c>Groups</c>, <c>Clients</c> — are stamped in directly.
///
/// <para><see cref="HubCallerContext"/> is replaced with a thin <see cref="FakeHubCallerContext"/>
/// rather than a Moq instance because <see cref="HubCallerContext.User"/> /
/// <see cref="HubCallerContext.Items"/> are abstract and the hub also calls
/// <c>Context.GetHttpContext()</c>, an extension that reads from <c>Features</c>.
/// Moq can't intercept the extension and would need every member overridden
/// anyway — a small fake is simpler.</para>
///
/// <para>Auth lives on the <see cref="ClaimsPrincipal"/> the JWT middleware
/// produces. Tests stamp the principal directly: this is the boundary between
/// the JWT pipeline (covered separately in <c>RuntimeTokenAuthSchemeTests</c>)
/// and the hub. The hub's only contract with the principal is "read
/// <see cref="RuntimeTokenClaimNames.RuntimeId"/>"; we test that contract here.</para>
/// </summary>
public class RuntimeHubTests : IDisposable
{
    private readonly ServiceProvider _provider;
    private readonly ApplicationDbContext _db;
    private readonly Mock<IMediator> _mediator;

    public RuntimeHubTests()
    {
        var dbName = Guid.NewGuid().ToString();
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddHttpContextAccessor();

        // MediatR is registered for type wiring (handler discovery), but tests
        // verify against the mock we inject into the hub directly so we can
        // assert on Publish calls.
        services.AddMediatR(cfg =>
            cfg.RegisterServicesFromAssembly(typeof(RuntimeStateChanged).Assembly));

        // ScheduleRespawnHandler is auto-discovered and depends on IBackgroundJobClient.
        // The hub never produces a Crashed transition, but DI must still be able to
        // construct the handler at startup.
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
    }

    public void Dispose()
    {
        _provider.Dispose();
        GC.SuppressFinalize(this);
    }

    // ------------------------------------------------------------------
    // OnConnectedAsync
    // ------------------------------------------------------------------

    [Fact]
    public async Task OnConnectedAsync_missing_runtime_claim_aborts_connection()
    {
        // [Authorize(Scheme=RuntimeToken)] would normally reject before reaching
        // the hub — but if a misconfigured pipeline ever lets a principal through
        // without our custom claim, the hub itself must close the connection.
        var harness = BuildHarness(principal: new ClaimsPrincipal(new ClaimsIdentity()));

        await harness.Hub.OnConnectedAsync();

        harness.Context.AbortCount.Should().Be(1);
        harness.Groups.Verify(
            g => g.AddToGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _mediator.Verify(
            m => m.Publish(It.IsAny<RuntimeConnected>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task OnConnectedAsync_unparseable_runtime_claim_aborts()
    {
        // A claim that's syntactically present but not a Guid is the same kind
        // of "should never happen" case as missing — abort.
        var harness = BuildHarness(principal: PrincipalForRuntime("not-a-guid"));

        await harness.Hub.OnConnectedAsync();

        harness.Context.AbortCount.Should().Be(1);
        harness.Groups.Verify(
            g => g.AddToGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task OnConnectedAsync_unknown_runtime_aborts()
    {
        // Token validates and carries an rt_runtime claim, but the runtime row
        // does not exist (deleted hard, or token issued for a phantom). Abort.
        var unknown = Guid.NewGuid();
        var harness = BuildHarness(principal: PrincipalForRuntime(unknown.ToString()));

        await harness.Hub.OnConnectedAsync();

        harness.Context.AbortCount.Should().Be(1);
        _mediator.Verify(
            m => m.Publish(It.IsAny<RuntimeConnected>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task OnConnectedAsync_soft_deleted_runtime_aborts()
    {
        // Soft-deleted rows are filtered by ProjectRuntime's HasQueryFilter, so
        // a daemon whose runtime row has been janitor-marked must not be able
        // to connect even with a valid token. The query filter is the kill switch.
        var runtime = new ProjectRuntime
        {
            ProjectId = Guid.NewGuid(),
            Region = "arn",
            VolumeSizeGb = 1,
            State = RuntimeState.Online,
            IsDeleted = true,
            DeletedAt = DateTime.UtcNow,
        };
        _db.ProjectRuntimes.Add(runtime);
        await _db.SaveChangesAsync();

        var harness = BuildHarness(principal: PrincipalForRuntime(runtime.Id.ToString()));

        await harness.Hub.OnConnectedAsync();

        harness.Context.AbortCount.Should().Be(1);
        harness.Groups.Verify(
            g => g.AddToGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _mediator.Verify(
            m => m.Publish(It.IsAny<RuntimeConnected>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task OnConnectedAsync_valid_runtime_claim_joins_group_and_publishes_event()
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

        var harness = BuildHarness(principal: PrincipalForRuntime(runtime.Id.ToString()));

        await harness.Hub.OnConnectedAsync();

        harness.Context.AbortCount.Should().Be(0);
        harness.Groups.Verify(
            g => g.AddToGroupAsync(harness.ConnectionId, $"runtime-{runtime.Id}", It.IsAny<CancellationToken>()),
            Times.Once);

        _mediator.Verify(
            m => m.Publish(
                It.Is<RuntimeConnected>(e =>
                    e.RuntimeId == runtime.Id &&
                    e.ProjectId == runtime.ProjectId &&
                    e.ConnectionId == harness.ConnectionId),
                It.IsAny<CancellationToken>()),
            Times.Once);

        harness.Context.Items["RuntimeId"].Should().Be(runtime.Id);
        harness.Context.Items["ProjectId"].Should().Be(runtime.ProjectId);
    }

    // ------------------------------------------------------------------
    // OnConnectedAsync — hook config bootstrap delivery
    // ------------------------------------------------------------------

    [Fact]
    public async Task OnConnectedAsync_with_existing_hook_config_pushes_stored_json_to_caller()
    {
        // The daemon must learn about hooks the moment it connects.
        // OnConnectedAsync looks up RuntimeHookConfig.Json and fires a
        // one-shot UpdateConfig at Clients.Caller carrying the bytes.
        var runtime = new ProjectRuntime
        {
            ProjectId = Guid.NewGuid(),
            Region = "arn",
            VolumeSizeGb = 1,
            State = RuntimeState.Online,
        };
        _db.ProjectRuntimes.Add(runtime);

        const string storedJson = """{"beforePrompt":[{"name":"lint"}],"afterPrompt":[],"onFileChange":[],"beforeCommit":[]}""";
        _db.RuntimeHookConfigs.Add(new Source.Features.Hooks.Models.RuntimeHookConfig
        {
            RuntimeId = runtime.Id,
            Json = storedJson,
        });
        await _db.SaveChangesAsync();

        var harness = BuildHarness(principal: PrincipalForRuntime(runtime.Id.ToString()));

        await harness.Hub.OnConnectedAsync();

        harness.Context.AbortCount.Should().Be(0);
        harness.Caller.Verify(
            c => c.UpdateConfig(It.Is<ConfigUpdatePayload>(p =>
                p.RuntimeId == runtime.Id
                && p.RuntimeToken == null
                && p.HooksJson == storedJson)),
            Times.Once,
            "the connecting daemon must receive the persisted hook config verbatim");
    }

    [Fact]
    public async Task OnConnectedAsync_without_hook_config_pushes_null_hooks_json()
    {
        // No RuntimeHookConfig row for this runtime — bootstrap must still
        // fire so the daemon knows "we have no config; fall back to defaults",
        // but HooksJson is null on the wire. The negative signal is the
        // daemon's cue to use empty arrays without polling for a row that
        // may never be written.
        var runtime = new ProjectRuntime
        {
            ProjectId = Guid.NewGuid(),
            Region = "arn",
            VolumeSizeGb = 1,
            State = RuntimeState.Online,
        };
        _db.ProjectRuntimes.Add(runtime);
        await _db.SaveChangesAsync();

        var harness = BuildHarness(principal: PrincipalForRuntime(runtime.Id.ToString()));

        await harness.Hub.OnConnectedAsync();

        harness.Context.AbortCount.Should().Be(0);
        harness.Caller.Verify(
            c => c.UpdateConfig(It.Is<ConfigUpdatePayload>(p =>
                p.RuntimeId == runtime.Id
                && p.RuntimeToken == null
                && p.HooksJson == null)),
            Times.Once,
            "missing RuntimeHookConfig must still fire a bootstrap UpdateConfig with HooksJson=null");
    }

    // ------------------------------------------------------------------
    // OnConnectedAsync — git config bootstrap delivery
    // ------------------------------------------------------------------

    [Fact]
    public async Task OnConnectedAsync_with_existing_git_config_pushes_autoCommit_and_deployKey_to_caller()
    {
        // Same channel as hooks — one UpdateConfig call carries both. The
        // daemon must learn AutoCommit + DeployKey the moment it connects so
        // the first push happens with the right keyring and the right cadence.
        var runtime = new ProjectRuntime
        {
            ProjectId = Guid.NewGuid(),
            Region = "arn",
            VolumeSizeGb = 1,
            State = RuntimeState.Online,
        };
        _db.ProjectRuntimes.Add(runtime);

        const string storedKey =
            "-----BEGIN OPENSSH PRIVATE KEY-----\nfake\n-----END OPENSSH PRIVATE KEY-----";
        _db.RuntimeGitConfigs.Add(new Source.Features.GitOps.Models.RuntimeGitConfig
        {
            RuntimeId = runtime.Id,
            AutoCommit = false,
            DeployKey = storedKey,
            DeployKeyHostKey = "github.com ssh-ed25519 AAAA",
        });
        await _db.SaveChangesAsync();

        var harness = BuildHarness(principal: PrincipalForRuntime(runtime.Id.ToString()));

        await harness.Hub.OnConnectedAsync();

        harness.Context.AbortCount.Should().Be(0);
        harness.Caller.Verify(
            c => c.UpdateConfig(It.Is<ConfigUpdatePayload>(p =>
                p.RuntimeId == runtime.Id
                && p.AutoCommit == false
                && p.DeployKey == storedKey)),
            Times.Once,
            "the connecting daemon must receive the persisted git config verbatim on the same one-shot UpdateConfig as hooks");
    }

    [Fact]
    public async Task OnConnectedAsync_without_git_config_pushes_default_autoCommit_true_and_null_deployKey()
    {
        // No RuntimeGitConfig row — the daemon still receives a positive
        // signal: AutoCommit=true (matching the entity default and the
        // contract documented on RuntimeGitConfig.AutoCommit) and
        // DeployKey=null. The wire signal is "we know, here is the state",
        // not "we forgot to tell you".
        var runtime = new ProjectRuntime
        {
            ProjectId = Guid.NewGuid(),
            Region = "arn",
            VolumeSizeGb = 1,
            State = RuntimeState.Online,
        };
        _db.ProjectRuntimes.Add(runtime);
        await _db.SaveChangesAsync();

        var harness = BuildHarness(principal: PrincipalForRuntime(runtime.Id.ToString()));

        await harness.Hub.OnConnectedAsync();

        harness.Context.AbortCount.Should().Be(0);
        harness.Caller.Verify(
            c => c.UpdateConfig(It.Is<ConfigUpdatePayload>(p =>
                p.RuntimeId == runtime.Id
                && p.AutoCommit == true
                && p.DeployKey == null)),
            Times.Once,
            "missing RuntimeGitConfig must still fire a bootstrap UpdateConfig with AutoCommit=true (the documented default) and DeployKey=null");
    }

    // ------------------------------------------------------------------
    // OnDisconnectedAsync
    // ------------------------------------------------------------------

    [Fact]
    public async Task OnDisconnectedAsync_with_runtime_in_context_publishes_RuntimeDisconnected()
    {
        var runtimeId = Guid.NewGuid();
        var projectId = Guid.NewGuid();

        var harness = BuildHarness(principal: null);
        harness.Context.Items["RuntimeId"] = runtimeId;
        harness.Context.Items["ProjectId"] = projectId;

        await harness.Hub.OnDisconnectedAsync(exception: null);

        _mediator.Verify(
            m => m.Publish(
                It.Is<RuntimeDisconnected>(e =>
                    e.RuntimeId == runtimeId &&
                    e.ProjectId == projectId &&
                    e.ConnectionId == harness.ConnectionId &&
                    e.ExceptionMessage == null),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task OnDisconnectedAsync_without_runtime_does_not_publish()
    {
        // Connection was rejected pre-handshake (missing/bogus claim, or runtime
        // not found), so the OnConnected hook never stashed a runtime. Disconnect
        // must be a no-op event-wise — otherwise we'd publish RuntimeDisconnected
        // for a daemon we never accepted.
        var harness = BuildHarness(principal: null);

        await harness.Hub.OnDisconnectedAsync(exception: null);

        _mediator.Verify(
            m => m.Publish(It.IsAny<RuntimeDisconnected>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ------------------------------------------------------------------
    // Heartbeat
    // ------------------------------------------------------------------

    [Fact]
    public async Task Heartbeat_updates_LastHeartbeatAt_on_existing_runtime()
    {
        var runtime = new ProjectRuntime
        {
            ProjectId = Guid.NewGuid(),
            Region = "arn",
            VolumeSizeGb = 1,
            State = RuntimeState.Online,
            LastHeartbeatAt = null,
        };
        _db.ProjectRuntimes.Add(runtime);
        await _db.SaveChangesAsync();

        var harness = BuildHarness(principal: null);
        harness.Context.Items["RuntimeId"] = runtime.Id;
        harness.Context.Items["ProjectId"] = runtime.ProjectId;

        await harness.Hub.Heartbeat(new HeartbeatPayload(
            EmittedAt: DateTime.UtcNow.AddSeconds(-30), // intentionally skewed
            DaemonVersion: "1.2.3",
            CpuPercent: 12.5,
            MemoryUsedMb: 256));

        // Re-read from a fresh tracking context in case the entity was detached.
        // LastHeartbeatAt must be the server clock at the moment of receive — the
        // injected FakeClock's fixed instant — NOT the EmittedAt payload field
        // (set 30s in the past, and a different year from the FakeClock, so this
        // also proves EmittedAt was ignored).
        var stored = await _db.ProjectRuntimes.FirstAsync(r => r.Id == runtime.Id);
        stored.LastHeartbeatAt.Should().NotBeNull();
        stored.LastHeartbeatAt!.Value.Should().Be(
            new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc),
            "Heartbeat stamps the server-side clock (FakeClock default), not the daemon's EmittedAt");
    }

    [Fact]
    public async Task Heartbeat_with_missing_runtime_id_in_context_is_noop()
    {
        // Seed a runtime so we can prove we did NOT touch it via the global
        // "any runtime got modified?" assertion below.
        var runtime = new ProjectRuntime
        {
            ProjectId = Guid.NewGuid(),
            Region = "arn",
            VolumeSizeGb = 1,
            State = RuntimeState.Online,
            LastHeartbeatAt = null,
        };
        _db.ProjectRuntimes.Add(runtime);
        await _db.SaveChangesAsync();

        var harness = BuildHarness(principal: null);
        // Deliberately do NOT set Context.Items["RuntimeId"].

        // Should not throw — "swallow + warn" is the contract for an
        // unauthenticated heartbeat.
        await harness.Hub.Heartbeat(new HeartbeatPayload(
            EmittedAt: DateTime.UtcNow,
            DaemonVersion: "1.2.3",
            CpuPercent: null,
            MemoryUsedMb: null));

        var stored = await _db.ProjectRuntimes.FirstAsync(r => r.Id == runtime.Id);
        stored.LastHeartbeatAt.Should().BeNull();
    }

    [Fact]
    public async Task Heartbeat_for_unknown_runtime_id_is_noop()
    {
        // No runtime in DB at all. Context.Items has a Guid that doesn't
        // resolve — daemon should be silently dropped, hub should not throw.
        var harness = BuildHarness(principal: null);
        harness.Context.Items["RuntimeId"] = Guid.NewGuid();

        await harness.Hub.Heartbeat(new HeartbeatPayload(
            EmittedAt: DateTime.UtcNow,
            DaemonVersion: "1.2.3",
            CpuPercent: null,
            MemoryUsedMb: null));

        var anyRuntimes = await _db.ProjectRuntimes.AnyAsync();
        anyRuntimes.Should().BeFalse();
    }

    [Fact]
    public async Task Heartbeat_for_soft_deleted_runtime_is_noop()
    {
        // Soft-deleted rows are filtered by ProjectRuntime's HasQueryFilter,
        // so a heartbeat for a janitor-marked runtime must not bump
        // LastHeartbeatAt — the global filter is the kill switch.
        var runtime = new ProjectRuntime
        {
            ProjectId = Guid.NewGuid(),
            Region = "arn",
            VolumeSizeGb = 1,
            State = RuntimeState.Online,
            LastHeartbeatAt = null,
            IsDeleted = true,
            DeletedAt = DateTime.UtcNow,
        };
        _db.ProjectRuntimes.Add(runtime);
        await _db.SaveChangesAsync();

        var harness = BuildHarness(principal: null);
        harness.Context.Items["RuntimeId"] = runtime.Id;

        await harness.Hub.Heartbeat(new HeartbeatPayload(
            EmittedAt: DateTime.UtcNow,
            DaemonVersion: "1.2.3",
            CpuPercent: 50.0,
            MemoryUsedMb: 1024));

        // Re-query bypassing the filter — the row still exists physically,
        // but LastHeartbeatAt must remain null because the filtered lookup
        // inside the hub returned null and the update was skipped.
        var stored = await _db.ProjectRuntimes
            .IgnoreQueryFilters()
            .FirstAsync(r => r.Id == runtime.Id);
        stored.LastHeartbeatAt.Should().BeNull();
    }

    // ------------------------------------------------------------------
    // Harness
    // ------------------------------------------------------------------

    private record HubHarness(
        RuntimeHub Hub,
        FakeHubCallerContext Context,
        Mock<IGroupManager> Groups,
        Mock<IRuntimeClient> Caller,
        string ConnectionId);

    /// <summary>
    /// Build a fresh hub + fake context. Pass <paramref name="principal"/> to
    /// stamp the <c>Context.User</c> the hub reads its rt_runtime claim off —
    /// pass null when the test doesn't care about <see cref="RuntimeHub.OnConnectedAsync"/>
    /// (e.g. the Heartbeat tests that pre-seed <c>Context.Items["RuntimeId"]</c>
    /// directly to bypass the connect path).
    /// </summary>
    private HubHarness BuildHarness(ClaimsPrincipal? principal)
    {
        const string connectionId = "conn-test";

        var http = new DefaultHttpContext();
        var context = new FakeHubCallerContext(connectionId, http) { UserPrincipal = principal };

        var groups = new Mock<IGroupManager>();
        var clients = new Mock<IHubCallerClients<IRuntimeClient>>();

        // OnConnectedAsync now fires a bootstrap UpdateConfig at Clients.Caller
        // (per the hooks-runner spec — daemon must learn its hook config the
        // moment it connects). Set the Caller mock up by default so the
        // bootstrap push doesn't NPE on the existing happy-path test, and so
        // the new bootstrap-delivery tests have something concrete to verify.
        var caller = new Mock<IRuntimeClient>();
        caller.Setup(c => c.UpdateConfig(It.IsAny<ConfigUpdatePayload>()))
            .Returns(Task.CompletedTask);
        clients.SetupGet(c => c.Caller).Returns(caller.Object);

        // Hook fan-out path takes IHubContext<AgentHub, IAgentClient>.
        // The OnConnected / Heartbeat tests in this file don't exercise it,
        // so a bare mock is enough to satisfy the constructor.
        var agentHub = new Mock<IHubContext<AgentHub, IAgentClient>>();

        // SecretEncryptionService + IGithubAppTokenService are unused in
        // these RuntimeHub tests (no path exercises GetSecrets /
        // GetRepoAccessToken) — bare null! placeholders keep the test rig
        // minimal without bringing the SystemSettings + cipher + github-token
        // graph into scope.
        var hub = new RuntimeHub(_db, _mediator.Object, agentHub.Object, Mock.Of<Source.Features.Conversations.Services.ITurnDispatcher>(), new HealthSnapshotBuffer(), new ServiceDownDetector(), null!, null!, null!, null!, null!, new Api.Tests.Infrastructure.FakeClock(), NullLogger<RuntimeHub>.Instance)
        {
            Context = context,
            Groups = groups.Object,
            Clients = clients.Object,
        };

        return new HubHarness(hub, context, groups, caller, connectionId);
    }

    /// <summary>
    /// Build a <see cref="ClaimsPrincipal"/> that mirrors what the
    /// <c>RuntimeToken</c> JWT scheme would produce — an identity carrying
    /// <see cref="RuntimeTokenClaimNames.RuntimeId"/> and a jti. Authentication
    /// type "Test" is enough for <see cref="ClaimsIdentity.IsAuthenticated"/>
    /// to be true; the hub itself only reads the claim value.
    /// </summary>
    private static ClaimsPrincipal PrincipalForRuntime(string runtimeIdClaim)
    {
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(RuntimeTokenClaimNames.RuntimeId, runtimeIdClaim),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        }, authenticationType: "Test");
        return new ClaimsPrincipal(identity);
    }

    /// <summary>
    /// Minimal HubCallerContext stand-in. The hub uses four things off it:
    /// <c>ConnectionId</c>, <c>User</c> (for the rt_runtime claim),
    /// <c>GetHttpContext()</c> (an extension that reads <c>Features</c>), and
    /// <c>Items</c>. Implementing these directly is simpler than fighting Moq's
    /// extension-method + abstract-member limitations.
    /// </summary>
    private sealed class FakeHubCallerContext : HubCallerContext
    {
        private readonly string _connectionId;
        private readonly IFeatureCollection _features;
        private readonly Dictionary<object, object?> _items = new();

        public int AbortCount { get; private set; }

        // Settable so each test can stamp its own principal without rebuilding
        // the fake. The hub reads via Context.User?.FindFirstValue(...), so
        // null is also a valid state (means "no principal" — should abort).
        public ClaimsPrincipal? UserPrincipal { get; set; }

        public FakeHubCallerContext(string connectionId, HttpContext httpContext)
        {
            _connectionId = connectionId;
            _features = new FeatureCollection();
            _features.Set<IHttpContextFeature>(new HttpContextFeature { HttpContext = httpContext });
        }

        public override string ConnectionId => _connectionId;
        public override string? UserIdentifier => null;
        public override ClaimsPrincipal? User => UserPrincipal;
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
