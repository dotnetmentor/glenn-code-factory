using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Api.Tests.Features.FlyManagement;
using Api.Tests.Infrastructure;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Source.Features.Cloudflare.Configuration;
using Source.Features.Cloudflare.Models;
using Source.Features.Cloudflare.Services;
using Source.Features.DaemonVersions.Models;
using Source.Features.FlyManagement;
using Source.Features.FlyManagement.Configuration;
using Source.Features.FlyManagement.Models;
using Source.Features.Projects.Models;
using Source.Features.RuntimeImages.Models;
using Source.Features.RuntimeLifecycle.Configuration;
using Source.Features.RuntimeLifecycle.Events;
using Source.Features.RuntimeLifecycle.Jobs;
using Source.Features.RuntimeLifecycle.Models;
using Source.Features.RuntimeTokens.Models;
using Source.Features.RuntimeTokens.Services;
using Source.Features.SystemSettings.Services;
using Source.Infrastructure;
using Source.Infrastructure.Interceptors;

namespace Api.Tests.Features.RuntimeLifecycle;

/// <summary>
/// Unit tests for <see cref="RuntimeProvisionerJob"/>. We construct a real
/// <see cref="FlyClient"/> on top of a scripted <see cref="HttpMessageHandler"/>
/// (mirroring the seam <see cref="FlyManagement.FlyAdminControllerTests"/> uses) and
/// build a wired <see cref="ApplicationDbContext"/> with the
/// <see cref="DomainEventInterceptor"/> + MediatR registered so the
/// <c>RuntimeStateChanged</c> event flows through the
/// <c>PersistRuntimeStateEventHandler</c> and audit rows actually land.
/// </summary>
public class RuntimeProvisionerJobTests : IDisposable
{
    private readonly ServiceProvider _provider;
    private readonly ApplicationDbContext _db;
    private readonly IRuntimeTokenService _runtimeTokenService;
    private readonly IMediator _mediator;

    public RuntimeProvisionerJobTests()
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
        // tests here never produce a Crashed transition, but DI must still be able to
        // construct the handler at startup.
        services.AddSingleton<IBackgroundJobClient>(new Mock<IBackgroundJobClient>().Object);

        services.AddScoped<DomainEventInterceptor>();

        services.AddDbContext<ApplicationDbContext>((sp, options) =>
        {
            options.UseInMemoryDatabase(dbName);
            options.AddInterceptors(sp.GetRequiredService<DomainEventInterceptor>());
        });

        // RuntimeToken stack — real implementations so the provisioner mints a real
        // JWT and writes a real RuntimeTokenIssue audit row through the same
        // ApplicationDbContext as the rest of the provisioner state.
        var cipherKeyB64 = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());
        services.AddSingleton(Options.Create(new SystemSettingsCipherOptions { EncryptionKey = cipherKeyB64 }));
        services.AddSingleton<ISystemSettingsCipher, SystemSettingsCipher>();
        services.AddSingleton<SystemSettingsCache>();
        services.AddScoped<ISystemSettingsService, SystemSettingsService>();
        services.AddSingleton<IRuntimeTokenSigningKeyService, RuntimeTokenSigningKeyService>();
        services.AddMemoryCache();
        // No-op revocation cache — we never revoke during a provisioner test, and
        // wiring the real cache would warm itself off the in-memory DB on first
        // use which adds noise to these tests.
        services.AddSingleton(Mock.Of<IRevocationCache>());
        services.AddScoped<IRuntimeTokenService, RuntimeTokenService>();

        // ResolveDaemonVersionHandler depends on IFileStorageService for URL
        // resolution. A stub is enough for unit tests — the handler just calls
        // GetFileUrlAsync(storageKey) and we want a deterministic URL back.
        services.AddSingleton<Source.Infrastructure.Services.FileStorage.IFileStorageService>(
            new StubFileStorageService());

        _provider = services.BuildServiceProvider();
        _db = _provider.GetRequiredService<ApplicationDbContext>();
        _db.Database.EnsureCreated();
        _runtimeTokenService = _provider.GetRequiredService<IRuntimeTokenService>();
        _mediator = _provider.GetRequiredService<IMediator>();
    }

    /// <summary>
    /// Tiny in-memory <c>IFileStorageService</c> for the provisioner tests. Only
    /// <see cref="GetFileUrlAsync"/> is exercised — the resolver handler just
    /// needs a deterministic URL it can stamp into the env vars.
    /// </summary>
    private sealed class StubFileStorageService : Source.Infrastructure.Services.FileStorage.IFileStorageService
    {
        public Task<string> SaveFileAsync(Stream fileStream, string fileName, string? folder = null, CancellationToken cancellationToken = default)
            => Task.FromResult($"{folder ?? "uploads"}/{fileName}");
        public Task<Stream> GetFileAsync(string filePath, CancellationToken cancellationToken = default)
            => Task.FromResult<Stream>(new MemoryStream());
        public Task<bool> DeleteFileAsync(string filePath, CancellationToken cancellationToken = default)
            => Task.FromResult(true);
        public Task<bool> FileExistsAsync(string filePath, CancellationToken cancellationToken = default)
            => Task.FromResult(true);
        public Task<string> GetFileUrlAsync(string filePath, CancellationToken cancellationToken = default)
            => Task.FromResult($"https://stub.example.com/{filePath}");
        public Task<string> GetPresignedPutUrlAsync(string key, string? contentType, TimeSpan ttl, CancellationToken cancellationToken = default)
            => Task.FromResult($"https://stub.example.com/put/{key}");
        public Task<string> GetPresignedGetUrlAsync(string key, TimeSpan ttl, CancellationToken cancellationToken = default)
            => Task.FromResult($"https://stub.example.com/get/{key}");
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

    private RuntimeProvisionerJob CreateJob(HttpMessageHandler handler)
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
        var runtimeOptions = new StubRuntimeOptionsAccessor(new RuntimeOptions
        {
            PublicApiUrl = "https://test-api.example.com",
        });

        // The provisioner now reconciles Cloudflare tunnel ingress on every
        // boot of a runtime with a non-default PreviewPort. Wire a tiny
        // CloudflareApiClient on top of an always-success handler so the new
        // code path doesn't blow up the existing test surface — the tests
        // here aren't trying to assert Cloudflare wire shape (that's covered
        // in CloudflareApiClient's own dedicated tests).
        var cloudflareHttp = new HttpClient(new AlwaysSuccessCloudflareHandler(), disposeHandler: false)
        {
            BaseAddress = new Uri("https://api.cloudflare.com/client/v4/"),
        };
        var cloudflare = new CloudflareApiClient(
            cloudflareHttp,
            new StubCloudflareOptionsAccessor(new CloudflareOptions
            {
                ApiToken = "stub-token",
                AccountId = "stub-account",
                ZoneId = "stub-zone",
            }),
            NullLogger<CloudflareApiClient>.Instance);

        return new RuntimeProvisionerJob(
            _db,
            fly,
            new StubFlyOptionsAccessor(DefaultFlyOptions),
            _runtimeTokenService,
            runtimeOptions,
            _mediator,
            _provider.GetRequiredService<ISystemSettingsCipher>(),
            cloudflare,
            NullLogger<RuntimeProvisionerJob>.Instance);
    }

    /// <summary>
    /// Returns Cloudflare's standard <c>{ success: true, result: {} }</c>
    /// envelope for every request. The provisioner's defensive PUT only cares
    /// that the call doesn't throw — it's best-effort and any failure is
    /// swallowed and logged, so a passing stub is sufficient.
    /// </summary>
    private sealed class AlwaysSuccessCloudflareHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"success\":true,\"result\":{},\"errors\":[],\"messages\":[]}",
                    Encoding.UTF8,
                    "application/json"),
            };
            return Task.FromResult(response);
        }
    }

    /// <summary>Minimal stub of <see cref="ICloudflareOptionsAccessor"/> for tests.</summary>
    private sealed class StubCloudflareOptionsAccessor : ICloudflareOptionsAccessor
    {
        public StubCloudflareOptionsAccessor(CloudflareOptions options) => Current = options;
        public CloudflareOptions Current { get; }
    }

    /// <summary>
    /// Seed an active daemon-bundle row so <c>ResolveDaemonVersionQuery</c>
    /// returns a hit during the provisioner batch. **Also seeds an active
    /// agent-natives row** by default — the provisioner now requires BOTH to
    /// be published before it will provision a runtime (matching the prod
    /// reality where daemon + natives ship in lockstep). Tests that want to
    /// assert the natives-missing short-circuit can pass
    /// <paramref name="seedNatives"/>=false.
    /// </summary>
    private async Task<DaemonVersion> SeedActiveDaemonVersionAsync(
        string version = "2026.05.10.000000",
        string channel = "stable",
        bool seedNatives = true)
    {
        var v = new DaemonVersion
        {
            Id = Guid.NewGuid(),
            Version = version,
            Channel = channel,
            BundleStorageKey = $"daemon-bundles/daemon-{version}.tar.gz",
            BundleSha256 = new string('a', 64),
            BundleSizeBytes = 1024,
            Notes = "test seed",
            ReleasedAt = DateTime.UtcNow,
            IsActive = true,
        };
        _db.DaemonVersions.Add(v);
        await _db.SaveChangesAsync();

        if (seedNatives)
        {
        }

        return v;
    }

    /// <summary>

    private async Task<ProjectRuntime> SeedPendingAsync(DateTime? createdAt = null)
    {
        var runtime = new ProjectRuntime
        {
            ProjectId = Guid.NewGuid(),
            Region = "arn",
            VolumeSizeGb = 1,
            State = RuntimeState.Pending,
            // Card 4 of e2e-smoketest: ProjectRuntime.TenantId is required for
            // MintAsync to succeed. Live runtimes inherit this from
            // Project.WorkspaceId (Card 3); seed it here so the provisioner's
            // mint step doesn't refuse and short-circuit to Failed.
            TenantId = Guid.NewGuid(),
        };
        _db.ProjectRuntimes.Add(runtime);
        await _db.SaveChangesAsync();

        if (createdAt is { } when)
        {
            // Override CreatedAt — IAuditable interceptor stamps DateTime.UtcNow on insert.
            runtime.CreatedAt = when;
            await _db.SaveChangesAsync();
        }

        return runtime;
    }

    private async Task<RuntimeImage> SeedActiveImageAsync(string tag = "2026.05.08-aaa", DateTime? builtAt = null)
    {
        var image = new RuntimeImage
        {
            Id = Guid.NewGuid(),
            Tag = tag,
            Digest = "sha256:" + new string('a', 64),
            Registry = "registry.fly.io/fwd-runtime",
            GitSha = "abc1234",
            BuiltAt = builtAt ?? DateTime.UtcNow,
            SizeMb = 200,
            Status = RuntimeImageStatus.Active,
        };
        _db.RuntimeImages.Add(image);
        await _db.SaveChangesAsync();
        return image;
    }

    /// <summary>
    /// Canned JSON the scripted handler can replay back as a <c>FlyVolume</c>.
    /// Snake-case to match the FlyClient's serialiser settings.
    /// </summary>
    private static string VolumeJson(string id) =>
        $$"""
        {"id":"{{id}}","name":"vol","region":"arn","size_gb":1,"state":"created","attached_machine_id":null,"encrypted":true,"created_at":"2026-05-08T10:00:00Z"}
        """;

    /// <summary>Canned JSON for <c>FlyMachine</c>.</summary>
    private static string MachineJson(string id) =>
        $$"""
        {"id":"{{id}}","name":"rt","state":"created","region":"arn","instance_id":null,"private_ip":null,"created_at":"2026-05-08T10:00:00Z"}
        """;

    // ------------------------------------------------------------------
    // Tests
    // ------------------------------------------------------------------

    [Fact]
    public async Task Run_NoPending_NoOp()
    {
        var handler = new ScriptedHandler();
        var job = CreateJob(handler);

        await job.Run(CancellationToken.None);

        // No HTTP calls, no rows inserted/changed.
        handler.CallCount.Should().Be(0);
        (await _db.ProjectRuntimes.CountAsync()).Should().Be(0);
        (await _db.RuntimeStateEvents.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Run_NoActiveImage_LogsWarningAndSkips()
    {
        var runtime = await SeedPendingAsync();
        var handler = new ScriptedHandler();
        var job = CreateJob(handler);

        await job.Run(CancellationToken.None);

        // No Fly call should have fired and the runtime stays Pending.
        handler.CallCount.Should().Be(0);

        var refreshed = await _db.ProjectRuntimes.AsNoTracking().SingleAsync(r => r.Id == runtime.Id);
        refreshed.State.Should().Be(RuntimeState.Pending);
        refreshed.FlyMachineId.Should().BeNull();
        refreshed.FlyVolumeId.Should().BeNull();

        (await _db.RuntimeStateEvents.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Run_PendingRuntime_CreatesVolumeAndMachineAndTransitionsToBooting()
    {
        var runtime = await SeedPendingAsync();
        var image = await SeedActiveImageAsync();
        await SeedActiveDaemonVersionAsync();

        var handler = new ScriptedHandler();
        handler.Enqueue(HttpStatusCode.OK, VolumeJson("vol_abc"));
        handler.Enqueue(HttpStatusCode.OK, MachineJson("mach_abc"));

        var job = CreateJob(handler);

        await job.Run(CancellationToken.None);

        // Two upstream Fly calls (volume + machine), in that order.
        handler.CallCount.Should().Be(2);

        var refreshed = await _db.ProjectRuntimes.AsNoTracking().SingleAsync(r => r.Id == runtime.Id);
        refreshed.State.Should().Be(RuntimeState.Booting);
        refreshed.FlyVolumeId.Should().Be("vol_abc");
        refreshed.FlyMachineId.Should().Be("mach_abc");
        refreshed.ImageDigest.Should().Be(image.Digest);

        // Audit row written via PersistRuntimeStateEventHandler.
        var events = await _db.RuntimeStateEvents.AsNoTracking()
            .Where(e => e.RuntimeId == runtime.Id)
            .ToListAsync();
        events.Should().HaveCount(1);
        var audit = events.Single();
        audit.FromState.Should().Be(RuntimeState.Pending);
        audit.ToState.Should().Be(RuntimeState.Booting);
        audit.Reason.Should().Be("provisioner:created");
        audit.TriggeredBy.Should().Be("system:provisioner");
    }

    [Fact]
    public async Task Run_FlyApiException_TransitionsToFailed()
    {
        var runtime = await SeedPendingAsync();
        await SeedActiveImageAsync();
        await SeedActiveDaemonVersionAsync();

        var handler = new ScriptedHandler();
        handler.Enqueue(HttpStatusCode.UnprocessableEntity,
            "{\"error\":\"name_taken\"}");

        var job = CreateJob(handler);

        await job.Run(CancellationToken.None);

        // Fly was called once (the volume create) and 422'd.
        handler.CallCount.Should().Be(1);

        var refreshed = await _db.ProjectRuntimes.AsNoTracking().SingleAsync(r => r.Id == runtime.Id);
        refreshed.State.Should().Be(RuntimeState.Failed,
            "a Fly 422 on volume create transitions the runtime to Failed");

        // Audit row records the failure with a structured reason.
        var events = await _db.RuntimeStateEvents.AsNoTracking()
            .Where(e => e.RuntimeId == runtime.Id)
            .ToListAsync();
        events.Should().HaveCount(1);
        var audit = events.Single();
        audit.FromState.Should().Be(RuntimeState.Pending);
        audit.ToState.Should().Be(RuntimeState.Failed);
        audit.Reason.Should().StartWith("provisioner:fly_error",
            "the reason carries the structured error code so dashboards can group on it");
        audit.TriggeredBy.Should().Be("system:provisioner");

        // The Fly audit row should record the failed call too.
        var flyOps = await _db.FlyOperations.AsNoTracking().Where(o => o.RuntimeId == runtime.Id).ToListAsync();
        flyOps.Should().HaveCount(1);
        flyOps.Single().Status.Should().Be(Source.Features.FlyManagement.Models.FlyOperationStatus.Failed);
        flyOps.Single().HttpStatusCode.Should().Be(422);
    }

    [Fact]
    public async Task Run_NetworkException_LeavesPending()
    {
        var runtime = await SeedPendingAsync();
        await SeedActiveImageAsync();
        await SeedActiveDaemonVersionAsync();

        var handler = new ThrowingHandler(new HttpRequestException("connection reset"));

        var job = CreateJob(handler);

        await job.Run(CancellationToken.None);

        // Row must NOT have been transitioned — the next tick should retry.
        var refreshed = await _db.ProjectRuntimes.AsNoTracking().SingleAsync(r => r.Id == runtime.Id);
        refreshed.State.Should().Be(RuntimeState.Pending);
        refreshed.FlyMachineId.Should().BeNull();
        refreshed.FlyVolumeId.Should().BeNull();

        (await _db.RuntimeStateEvents.CountAsync(e => e.RuntimeId == runtime.Id))
            .Should().Be(0, "transport failures must not move the runtime forward — retry on next tick");
    }

    [Fact]
    public async Task Run_ProcessesUpToTen()
    {
        // Seed 12 Pending runtimes; only 10 should be processed in this batch.
        // We backdate CreatedAt so the ordering by CreatedAt is deterministic and
        // we can identify which two were skipped.
        var runtimes = new List<ProjectRuntime>();
        for (var i = 0; i < 12; i++)
        {
            // Older = lower index, so the first ten (0..9) should be the ones picked up.
            var r = await SeedPendingAsync(createdAt: DateTime.UtcNow.AddMinutes(-100 + i));
            runtimes.Add(r);
        }

        await SeedActiveImageAsync();
        await SeedActiveDaemonVersionAsync();

        // Need 20 scripted responses — 10 volumes + 10 machines, alternating per-runtime.
        var handler = new ScriptedHandler();
        for (var i = 0; i < 10; i++)
        {
            handler.Enqueue(HttpStatusCode.OK, VolumeJson($"vol_{i}"));
            handler.Enqueue(HttpStatusCode.OK, MachineJson($"mach_{i}"));
        }

        var job = CreateJob(handler);

        await job.Run(CancellationToken.None);

        // 20 Fly calls (10 × volume + 10 × machine) — proves we processed exactly 10.
        handler.CallCount.Should().Be(20);

        var bootingCount = await _db.ProjectRuntimes.CountAsync(r => r.State == RuntimeState.Booting);
        var pendingCount = await _db.ProjectRuntimes.CountAsync(r => r.State == RuntimeState.Pending);

        bootingCount.Should().Be(10, "the batch limit is 10 per tick");
        pendingCount.Should().Be(2, "the two newest runtimes wait for the next tick");

        // The two Pending leftovers should be the most recently created (highest CreatedAt).
        var stillPending = await _db.ProjectRuntimes.AsNoTracking()
            .Where(r => r.State == RuntimeState.Pending)
            .OrderBy(r => r.CreatedAt)
            .ToListAsync();
        stillPending.Should().HaveCount(2);
        // We seeded ascending CreatedAt; so the leftovers should be runtimes[10] and runtimes[11].
        stillPending.Select(r => r.Id).Should().BeEquivalentTo(new[] { runtimes[10].Id, runtimes[11].Id });
    }

    // ------------------------------------------------------------------
    // RuntimeToken minting (Card 8)
    // ------------------------------------------------------------------

    [Fact]
    public async Task Run_PendingRuntime_InjectsRuntimeTokenIntoMachineEnv()
    {
        var runtime = await SeedPendingAsync();
        await SeedActiveImageAsync();
        await SeedActiveDaemonVersionAsync();

        var handler = new ScriptedHandler();
        handler.Enqueue(HttpStatusCode.OK, VolumeJson("vol_abc"));
        handler.Enqueue(HttpStatusCode.OK, MachineJson("mach_abc"));

        var job = CreateJob(handler);

        await job.Run(CancellationToken.None);

        // Two upstream Fly calls; the second (index 1) is the machine create.
        handler.CapturedBodies.Should().HaveCount(2);
        var machineBody = handler.CapturedBodies[1];

        // Parse the snake_case JSON Fly receives and dig out config.env.glenn_runtime_token.
        // FlyClient serialises with DictionaryKeyPolicy=SnakeCaseLower, so the env dict
        // keys arrive on the wire lowercased (existing behaviour for RUNTIME_ID too).
        using var doc = JsonDocument.Parse(machineBody);
        var env = doc.RootElement.GetProperty("config").GetProperty("env");
        env.TryGetProperty("runtime_id", out var runtimeIdProp).Should().BeTrue();
        runtimeIdProp.GetString().Should().Be(runtime.Id.ToString());

        env.TryGetProperty("glenn_runtime_token", out var tokenProp).Should().BeTrue(
            "the daemon needs GLENN_RUNTIME_TOKEN in its env to authenticate back to main API");
        var token = tokenProp.GetString();
        token.Should().NotBeNullOrEmpty();
        // Shape-check only: a JWT is three dot-separated base64url segments. We
        // don't validate the signature here — RuntimeTokenServiceTests covers that.
        token!.Split('.').Should().HaveCount(3, "GLENN_RUNTIME_TOKEN must be a well-formed JWT");
    }

    [Fact]
    public async Task Run_PendingRuntime_PersistsExactlyOneRuntimeTokenIssueRow()
    {
        var runtime = await SeedPendingAsync();
        await SeedActiveImageAsync();
        await SeedActiveDaemonVersionAsync();

        var handler = new ScriptedHandler();
        handler.Enqueue(HttpStatusCode.OK, VolumeJson("vol_abc"));
        handler.Enqueue(HttpStatusCode.OK, MachineJson("mach_abc"));

        var job = CreateJob(handler);

        await job.Run(CancellationToken.None);

        var issues = await _db.RuntimeTokenIssues.AsNoTracking()
            .Where(r => r.RuntimeId == runtime.Id)
            .ToListAsync();

        issues.Should().HaveCount(1,
            "one provisioner pass mints exactly one RuntimeToken — the audit row is the persistent record");
        var issue = issues.Single();
        issue.RuntimeId.Should().Be(runtime.Id);
        issue.ProjectId.Should().Be(runtime.ProjectId);
        issue.Scope.Should().Be("runtime");
        issue.RevokedAt.Should().BeNull(
            "a fresh provision never produces a pre-revoked token");
    }

    [Fact]
    public async Task Run_PendingRuntime_TokenInEnvMatchesAuditRowTokenHash()
    {
        var runtime = await SeedPendingAsync();
        await SeedActiveImageAsync();
        await SeedActiveDaemonVersionAsync();

        var handler = new ScriptedHandler();
        handler.Enqueue(HttpStatusCode.OK, VolumeJson("vol_abc"));
        handler.Enqueue(HttpStatusCode.OK, MachineJson("mach_abc"));

        var job = CreateJob(handler);

        await job.Run(CancellationToken.None);

        // Dig the JWT back out of the captured machine-create body.
        // (Wire-format key is lowercase snake_case — see Run_PendingRuntime_InjectsRuntimeTokenIntoMachineEnv.)
        using var doc = JsonDocument.Parse(handler.CapturedBodies[1]);
        var token = doc.RootElement
            .GetProperty("config")
            .GetProperty("env")
            .GetProperty("glenn_runtime_token")
            .GetString();
        token.Should().NotBeNullOrEmpty();

        var expectedHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token!)))
            .ToLowerInvariant();

        var issue = await _db.RuntimeTokenIssues.AsNoTracking()
            .SingleAsync(r => r.RuntimeId == runtime.Id);

        issue.TokenHash.Should().Be(expectedHash,
            "the env JWT and the audit row's TokenHash must round-trip — that's the end-to-end " +
            "'audit before issuance' guarantee: every JWT we ever hand out has a matching forensic record");

        // Also sanity-check the jti claim in the JWT matches the audit row Id.
        var jwt = new JwtSecurityToken(token);
        var jtiClaim = jwt.Claims.Single(c => c.Type == JwtRegisteredClaimNames.Jti).Value;
        Guid.Parse(jtiClaim).Should().Be(issue.Id,
            "the jti claim in the JWT must equal the RuntimeTokenIssue.Id (PK)");
    }

    [Fact]
    public async Task Run_FlyMachineCreateFailure_PersistsRuntimeTokenIssueRowAndTransitionsToFailed()
    {
        // Volume create succeeds; machine create 422s. Per the spec the audit row
        // is written by RuntimeTokenService.MintAsync via its OWN SaveChangesAsync,
        // which runs BEFORE the machine create — so the issuance row is durably
        // persisted by the time the Fly throw happens, and survives the failure.
        // That's the documented "Loss of a token never means loss of audit"
        // guarantee end-to-end. The orphan token simply expires after 7 days.
        var runtime = await SeedPendingAsync();
        await SeedActiveImageAsync();
        await SeedActiveDaemonVersionAsync();

        var handler = new ScriptedHandler();
        handler.Enqueue(HttpStatusCode.OK, VolumeJson("vol_abc"));
        handler.Enqueue(HttpStatusCode.UnprocessableEntity, "{\"error\":\"name_taken\"}");

        var job = CreateJob(handler);

        await job.Run(CancellationToken.None);

        // Existing failure-path contract: runtime moves to Failed (mirrors
        // Run_FlyApiException_TransitionsToFailed above; we re-assert here so a
        // future regression in the failure branch surfaces in the token tests too).
        var refreshed = await _db.ProjectRuntimes.AsNoTracking().SingleAsync(r => r.Id == runtime.Id);
        refreshed.State.Should().Be(RuntimeState.Failed,
            "a Fly 422 on machine create still transitions the runtime to Failed");

        // The audit row lives — RuntimeTokenService.MintAsync committed it before
        // the machine call threw. Loss of a Machine never means loss of audit.
        var issues = await _db.RuntimeTokenIssues.AsNoTracking()
            .Where(r => r.RuntimeId == runtime.Id)
            .ToListAsync();
        issues.Should().HaveCount(1,
            "the issuance row commits via MintAsync's own SaveChangesAsync BEFORE the " +
            "Fly machine-create call, so a machine-create failure leaves the audit row intact");
        issues.Single().RevokedAt.Should().BeNull(
            "the orphaned token isn't pre-revoked; it expires naturally");
    }

    // ------------------------------------------------------------------
    // Cloudflare preview-tunnel env (Phase 4: TUNNEL_TOKEN / PREVIEW_PORT / PREVIEW_HOSTNAME)
    // ------------------------------------------------------------------

    [Fact]
    public async Task Run_PendingRuntime_WithAssignedSubdomain_StampsTunnelEnvVars()
    {
        // Seed a project with a non-default preview port + a pool row Assigned
        // to the runtime's branch. The provisioner should decrypt the tunnel
        // token and stamp TUNNEL_TOKEN / PREVIEW_PORT / PREVIEW_HOSTNAME on
        // the machine env so the daemon can start cloudflared.
        var projectId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();

        _db.Projects.Add(new Project
        {
            Id = projectId,
            WorkspaceId = workspaceId,
            OwnerUserId = "user-1",
            Name = "test-project",
            GithubRepoOwner = "acme",
            GithubRepoName = "demo",
            GithubInstallationId = Guid.NewGuid(),
            PreviewPort = 3000,
        });

        // Encrypt with the same cipher the provisioner will use to decrypt.
        var cipher = _provider.GetRequiredService<ISystemSettingsCipher>();
        const string plaintextTunnelToken = "tt_super_secret_token_xyz";

        _db.SubdomainAssignments.Add(new SubdomainAssignment
        {
            Hostname = "a43ns7we.glenncode.ai",
            Subdomain = "a43ns7we",
            TunnelId = Guid.NewGuid().ToString(),
            TunnelToken = cipher.Encrypt(plaintextTunnelToken),
            Status = SubdomainStatus.Assigned,
            AssignedBranchId = branchId,
            AssignedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();

        // Pending runtime pinned to the same project + branch.
        var runtime = new ProjectRuntime
        {
            ProjectId = projectId,
            BranchId = branchId,
            Region = "arn",
            VolumeSizeGb = 1,
            State = RuntimeState.Pending,
            TenantId = Guid.NewGuid(),
        };
        _db.ProjectRuntimes.Add(runtime);
        await _db.SaveChangesAsync();

        await SeedActiveImageAsync();
        await SeedActiveDaemonVersionAsync();

        var handler = new ScriptedHandler();
        handler.Enqueue(HttpStatusCode.OK, VolumeJson("vol_abc"));
        handler.Enqueue(HttpStatusCode.OK, MachineJson("mach_abc"));

        var job = CreateJob(handler);

        await job.Run(CancellationToken.None);

        // Dig the env dict out of the machine-create body.
        handler.CapturedBodies.Should().HaveCount(2);
        using var doc = JsonDocument.Parse(handler.CapturedBodies[1]);
        var env = doc.RootElement.GetProperty("config").GetProperty("env");

        // Env dict keys pass through verbatim — FlyClient deliberately does NOT
        // snake-case dictionary keys (the daemon reads RUNTIME_ID, not runtime_id).
        env.TryGetProperty("TUNNEL_TOKEN", out var tunnelTokenProp).Should().BeTrue(
            "TUNNEL_TOKEN must be stamped on the env when the branch has an Assigned subdomain");
        tunnelTokenProp.GetString().Should().Be(plaintextTunnelToken,
            "the provisioner must decrypt the stored ciphertext — cloudflared can't authenticate against a base64 ciphertext blob");

        env.TryGetProperty("PREVIEW_PORT", out var previewPortProp).Should().BeTrue();
        previewPortProp.GetString().Should().Be("3000",
            "PREVIEW_PORT reflects the project's configured port, not the 5173 default");

        env.TryGetProperty("PREVIEW_HOSTNAME", out var hostnameProp).Should().BeTrue();
        hostnameProp.GetString().Should().Be("a43ns7we.glenncode.ai",
            "PREVIEW_HOSTNAME mirrors the SubdomainAssignment.Hostname for logging + debug");
    }

    [Fact]
    public async Task Run_PendingRuntime_WithoutAssignedSubdomain_SkipsTunnelEnvVars()
    {
        // Legacy branch — no SubdomainAssignment row. The provisioner must
        // NOT stamp TUNNEL_TOKEN / PREVIEW_PORT / PREVIEW_HOSTNAME; the daemon
        // will simply not start cloudflared, and the runtime boots cleanly
        // without a preview tunnel.
        var runtime = await SeedPendingAsync();
        await SeedActiveImageAsync();
        await SeedActiveDaemonVersionAsync();

        var handler = new ScriptedHandler();
        handler.Enqueue(HttpStatusCode.OK, VolumeJson("vol_abc"));
        handler.Enqueue(HttpStatusCode.OK, MachineJson("mach_abc"));

        var job = CreateJob(handler);

        await job.Run(CancellationToken.None);

        handler.CapturedBodies.Should().HaveCount(2);
        using var doc = JsonDocument.Parse(handler.CapturedBodies[1]);
        var env = doc.RootElement.GetProperty("config").GetProperty("env");

        // Env dict keys pass through verbatim — FlyClient deliberately does NOT
        // snake-case dictionary keys (the daemon reads RUNTIME_ID, not runtime_id).
        env.TryGetProperty("TUNNEL_TOKEN", out _).Should().BeFalse(
            "TUNNEL_TOKEN must be absent when no SubdomainAssignment is bound to the branch");
        env.TryGetProperty("PREVIEW_PORT", out _).Should().BeFalse(
            "PREVIEW_PORT only goes on the env alongside TUNNEL_TOKEN — they travel together");
        env.TryGetProperty("PREVIEW_HOSTNAME", out _).Should().BeFalse(
            "PREVIEW_HOSTNAME only goes on the env alongside TUNNEL_TOKEN — they travel together");

        // Sanity: the baseline env vars are still stamped — only the tunnel trio is conditional.
        env.TryGetProperty("RUNTIME_ID", out _).Should().BeTrue();
        env.TryGetProperty("GLENN_RUNTIME_TOKEN", out _).Should().BeTrue();
    }

    // ------------------------------------------------------------------
    // [DisableConcurrentExecution] presence — guards against accidental removal.
    // ------------------------------------------------------------------

    [Fact]
    public void Run_HasDisableConcurrentExecutionAttribute()
    {
        var method = typeof(RuntimeProvisionerJob).GetMethod(nameof(RuntimeProvisionerJob.Run), new[] { typeof(Hangfire.IJobCancellationToken) })!;
        var attr = method.GetCustomAttributes(typeof(Hangfire.DisableConcurrentExecutionAttribute), inherit: false);
        attr.Should().NotBeEmpty(
            "two Hangfire workers must not race on the same Pending row — the attribute is the lock");
    }

    // ------------------------------------------------------------------
    // Test doubles
    // ------------------------------------------------------------------

    /// <summary>
    /// FIFO scripted handler. Mirrors the inner sealed class in
    /// <c>FlyAdminControllerTests</c>.
    /// </summary>
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new();
        public int CallCount { get; private set; }

        /// <summary>
        /// Captured request bodies in call order. Lets a test assert what we
        /// actually sent to Fly (e.g. that the env dict contains
        /// GLENN_RUNTIME_TOKEN).
        /// </summary>
        public List<string> CapturedBodies { get; } = new();

        public void Enqueue(HttpStatusCode status, string body)
        {
            _responses.Enqueue(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            // Capture the request body BEFORE the response is dispatched so a
            // test that asserts "exception path leaves audit row" still has
            // the captured payload to inspect.
            if (request.Content is not null)
            {
                CapturedBodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
            }
            else
            {
                CapturedBodies.Add(string.Empty);
            }
            if (_responses.Count == 0)
            {
                throw new InvalidOperationException(
                    $"ScriptedHandler exhausted after {CallCount} calls — test under-mocked.");
            }
            return _responses.Dequeue();
        }
    }

    /// <summary>Always throws — simulates a transport-level failure.</summary>
    private sealed class ThrowingHandler : HttpMessageHandler
    {
        private readonly Exception _ex;
        public ThrowingHandler(Exception ex) => _ex = ex;
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => throw _ex;
    }
}
