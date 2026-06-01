using System.Net;
using System.Text;
using Api.Tests.Features.FlyManagement;
using Hangfire;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Source.Features.Cloudflare.Configuration;
using Source.Features.Cloudflare.Services;
using Source.Features.DaemonVersions.Models;
using Source.Features.FlyManagement;
using Source.Features.FlyManagement.Configuration;
using Source.Features.RuntimeImages.Models;
using Source.Features.RuntimeLifecycle.Configuration;
using Source.Features.RuntimeLifecycle.Events;
using Source.Features.RuntimeLifecycle.Jobs;
using Source.Features.RuntimeLifecycle.Models;
using Source.Features.RuntimeTokens.Services;
using Source.Features.SystemSettings.Services;
using Source.Infrastructure;
using Source.Infrastructure.Interceptors;

namespace Api.Tests.Features.RuntimeLifecycle;

/// <summary>
/// Unit tests for <see cref="RespawnRuntimeJob"/>. Mirrors the bootstrap that
/// <c>RuntimeProvisionerJobTests</c> uses: a real <see cref="FlyClient"/>
/// driven by a scripted <see cref="HttpMessageHandler"/>, and a wired
/// <see cref="ApplicationDbContext"/> with the
/// <see cref="DomainEventInterceptor"/> + MediatR registered so the
/// <c>RuntimeStateChanged</c> event flows through the
/// <c>PersistRuntimeStateEventHandler</c> and audit rows actually land.
/// </summary>
public class RespawnRuntimeJobTests : IDisposable
{
    private readonly ServiceProvider _provider;
    private readonly ApplicationDbContext _db;
    private readonly IRuntimeTokenService _runtimeTokenService;
    private readonly IMediator _mediator;

    public RespawnRuntimeJobTests()
    {
        var dbName = Guid.NewGuid().ToString();
        var services = new ServiceCollection();

        services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));
        services.AddHttpContextAccessor();

        // SignalR services satisfy the auto-discovered BroadcastRuntimeStateChangedHandler.
        services.AddSignalR();

        services.AddMediatR(cfg =>
            cfg.RegisterServicesFromAssembly(typeof(RuntimeStateChanged).Assembly));

        // ScheduleRespawnHandler depends on IBackgroundJobClient. The respawn job
        // itself never publishes a Crashed transition (it transitions to Booting)
        // so the handler never reaches its scheduling path here, but DI must
        // still be able to construct it.
        services.AddSingleton<IBackgroundJobClient>(new Mock<IBackgroundJobClient>().Object);

        services.AddScoped<DomainEventInterceptor>();

        services.AddDbContext<ApplicationDbContext>((sp, options) =>
        {
            options.UseInMemoryDatabase(dbName);
            options.AddInterceptors(sp.GetRequiredService<DomainEventInterceptor>());
        });

        // RuntimeToken stack — real implementations so the respawn job mints a real
        // JWT and writes a real RuntimeTokenIssue audit row through the same
        // ApplicationDbContext as the rest of the respawn state.
        var cipherKeyB64 = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());
        services.AddSingleton(Options.Create(new SystemSettingsCipherOptions { EncryptionKey = cipherKeyB64 }));
        services.AddSingleton<ISystemSettingsCipher, SystemSettingsCipher>();
        services.AddSingleton<SystemSettingsCache>();
        services.AddScoped<ISystemSettingsService, SystemSettingsService>();
        services.AddSingleton<IRuntimeTokenSigningKeyService, RuntimeTokenSigningKeyService>();
        services.AddMemoryCache();
        // No-op revocation cache — we never revoke during a respawn test.
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
    /// Tiny in-memory <c>IFileStorageService</c> for the respawn tests. Only
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

    private RespawnRuntimeJob CreateJob(HttpMessageHandler handler)
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

        // The respawn job now reconciles Cloudflare tunnel ingress on every
        // respawn of a runtime with a non-default PreviewPort — matching the
        // provisioner's belt-and-braces logic. Stub the API client with an
        // always-success handler so existing test coverage holds; CloudflareApiClient's
        // own wire-shape tests live elsewhere.
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

        return new RespawnRuntimeJob(
            _db,
            fly,
            runtimeOptions,
            _runtimeTokenService,
            _mediator,
            _provider.GetRequiredService<ISystemSettingsCipher>(),
            cloudflare,
            NullLogger<RespawnRuntimeJob>.Instance);
    }

    /// <summary>Always-success Cloudflare API stub. See provisioner test for the parallel.</summary>
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

    private async Task<ProjectRuntime> SeedRuntimeAsync(
        RuntimeState state = RuntimeState.Crashed,
        string? flyMachineId = "mach_old",
        string? flyVolumeId = "vol_persist",
        string? imageDigest = null,
        int respawnRetries = 0)
    {
        var runtime = new ProjectRuntime
        {
            ProjectId = Guid.NewGuid(),
            Region = "arn",
            VolumeSizeGb = 1,
            State = state,
            RespawnRetries = respawnRetries,
            FlyMachineId = flyMachineId,
            FlyVolumeId = flyVolumeId,
            ImageDigest = imageDigest ?? ("sha256:" + new string('a', 64)),
            // Required for IRuntimeTokenService.MintAsync to succeed — live
            // runtimes inherit this from Project.WorkspaceId; seed it here so
            // the respawn job's mint step doesn't refuse and short-circuit.
            TenantId = Guid.NewGuid(),
        };
        _db.ProjectRuntimes.Add(runtime);
        await _db.SaveChangesAsync();
        return runtime;
    }

    /// <summary>
    /// Seed an active daemon-bundle row so <c>ResolveDaemonVersionQuery</c>
    /// returns a hit during the respawn flow. Tests that exercise the
    /// happy path call this.
    /// </summary>
    private async Task<DaemonVersion> SeedActiveDaemonVersionAsync(
        string version = "2026.05.10.000000",
        string channel = "stable")
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
        return v;
    }

    /// <summary>

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

    /// <summary>Canned JSON for <c>FlyMachine</c> response bodies.</summary>
    private static string MachineJson(string id) =>
        $$"""
        {"id":"{{id}}","name":"rt","state":"created","region":"arn","instance_id":null,"private_ip":null,"created_at":"2026-05-08T10:00:00Z"}
        """;

    // ------------------------------------------------------------------
    // Tests
    // ------------------------------------------------------------------

    [Fact]
    public async Task Run_RuntimeNotFound_NoOp()
    {
        var handler = new ScriptedHandler();
        var job = CreateJob(handler);

        // No runtime exists with this id — job must no-op without hitting Fly.
        await job.Run(Guid.NewGuid(), CancellationToken.None);

        handler.CallCount.Should().Be(0,
            "a missing runtime row must short-circuit before any Fly call");
        (await _db.RuntimeStateEvents.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Run_RuntimeNotInCrashedState_NoOp()
    {
        // Someone else moved this runtime out of Crashed between schedule and run.
        var runtime = await SeedRuntimeAsync(state: RuntimeState.Online);

        var handler = new ScriptedHandler();
        var job = CreateJob(handler);

        await job.Run(runtime.Id, CancellationToken.None);

        handler.CallCount.Should().Be(0,
            "a runtime that is no longer Crashed must not be touched");

        var refreshed = await _db.ProjectRuntimes.AsNoTracking().SingleAsync(r => r.Id == runtime.Id);
        refreshed.State.Should().Be(RuntimeState.Online, "state stays as found");
        refreshed.RespawnRetries.Should().Be(0, "no retry bump on no-op");
    }

    [Fact]
    public async Task Run_HappyPath_DestroysAndCreates()
    {
        await SeedActiveImageAsync();
        await SeedActiveDaemonVersionAsync();
        var runtime = await SeedRuntimeAsync(
            state: RuntimeState.Crashed,
            flyMachineId: "mach_old_abc",
            flyVolumeId: "vol_persist_xyz",
            respawnRetries: 0);

        var handler = new ScriptedHandler();
        // Destroy returns the {"ok":true} envelope (any 2xx body works).
        handler.Enqueue(HttpStatusCode.OK, "{\"ok\":true}");
        // Create returns a fresh machine.
        handler.Enqueue(HttpStatusCode.OK, MachineJson("mach_new_def"));

        var job = CreateJob(handler);

        await job.Run(runtime.Id, CancellationToken.None);

        // Two upstream calls: destroy old + create new.
        handler.CallCount.Should().Be(2);

        var refreshed = await _db.ProjectRuntimes.AsNoTracking().SingleAsync(r => r.Id == runtime.Id);
        refreshed.FlyMachineId.Should().Be("mach_new_def",
            "the new machine id must replace the old one");
        refreshed.FlyVolumeId.Should().Be("vol_persist_xyz",
            "the volume is reused — that's the whole point of respawn-on-volume");
        refreshed.RespawnRetries.Should().Be(1,
            "the respawn job is the canonical bump site for the retry counter");
        refreshed.State.Should().Be(RuntimeState.Booting,
            "Crashed -> Booting closes the respawn loop");

        // Audit row written by PersistRuntimeStateEventHandler.
        var events = await _db.RuntimeStateEvents.AsNoTracking()
            .Where(e => e.RuntimeId == runtime.Id)
            .ToListAsync();
        events.Should().HaveCount(1);
        var audit = events.Single();
        audit.FromState.Should().Be(RuntimeState.Crashed);
        audit.ToState.Should().Be(RuntimeState.Booting);
        audit.Reason.Should().Be("respawn:created");
        audit.TriggeredBy.Should().Be("respawn-job");
        audit.Metadata.Should().NotBeNullOrWhiteSpace();
        audit.Metadata!.Should().Contain("mach_old_abc",
            "metadata must record the old machine id for traceability");
        audit.Metadata!.Should().Contain("mach_new_def",
            "metadata must record the new machine id for traceability");
    }

    [Fact]
    public async Task Run_DestroyReturns404_StillCreates()
    {
        await SeedActiveImageAsync();
        await SeedActiveDaemonVersionAsync();
        var runtime = await SeedRuntimeAsync(
            state: RuntimeState.Crashed,
            flyMachineId: "mach_already_gone");

        var handler = new ScriptedHandler();
        // Destroy 404: machine already gone (Fly cleaned it up, or a redeliver).
        handler.Enqueue(HttpStatusCode.NotFound, "{\"error\":\"not_found\"}");
        // Create still proceeds.
        handler.Enqueue(HttpStatusCode.OK, MachineJson("mach_new_404path"));

        var job = CreateJob(handler);

        await job.Run(runtime.Id, CancellationToken.None);

        handler.CallCount.Should().Be(2,
            "404 on destroy must not abort the create");

        var refreshed = await _db.ProjectRuntimes.AsNoTracking().SingleAsync(r => r.Id == runtime.Id);
        refreshed.State.Should().Be(RuntimeState.Booting);
        refreshed.FlyMachineId.Should().Be("mach_new_404path");
        refreshed.RespawnRetries.Should().Be(1);
    }

    [Fact]
    public async Task Run_NoFlyMachineId_SkipsDestroy()
    {
        await SeedActiveImageAsync();
        await SeedActiveDaemonVersionAsync();
        // Edge case: a Crashed runtime with no FlyMachineId. There's nothing
        // to destroy, but the create path should still run.
        var runtime = await SeedRuntimeAsync(
            state: RuntimeState.Crashed,
            flyMachineId: null,
            flyVolumeId: "vol_persist_zzz");

        var handler = new ScriptedHandler();
        // Only the create call should fire.
        handler.Enqueue(HttpStatusCode.OK, MachineJson("mach_first"));

        var job = CreateJob(handler);

        await job.Run(runtime.Id, CancellationToken.None);

        handler.CallCount.Should().Be(1,
            "no machine id means no destroy — only the create call runs");

        var refreshed = await _db.ProjectRuntimes.AsNoTracking().SingleAsync(r => r.Id == runtime.Id);
        refreshed.State.Should().Be(RuntimeState.Booting);
        refreshed.FlyMachineId.Should().Be("mach_first");
    }

    [Fact]
    public async Task Run_NoActiveImage_TransitionsToFailed()
    {
        // The production code no longer reads runtime.ImageDigest for the
        // registry/tag — it re-resolves the currently active RuntimeImage so
        // newly-respawned VMs always land on the current platform image.
        // When there is no active image, the runtime is transitioned to
        // Failed with reason "respawn:no_active_image" so an operator can fix
        // the platform configuration. This replaces the obsolete
        // Run_NoImageDigest_Throws test.
        var runtime = await SeedRuntimeAsync(
            state: RuntimeState.Crashed,
            flyMachineId: "mach_old_no_image");

        // No RuntimeImage rows seeded — the resolve must miss.
        var handler = new ScriptedHandler();
        // The destroy call still fires before the image check; the create
        // never does because the image check transitions to Failed and returns.
        handler.Enqueue(HttpStatusCode.OK, "{\"ok\":true}");

        var job = CreateJob(handler);

        await job.Run(runtime.Id, CancellationToken.None);

        handler.CallCount.Should().Be(1,
            "destroy runs first, but the create must be skipped once we discover no active image");

        var refreshed = await _db.ProjectRuntimes.AsNoTracking().SingleAsync(r => r.Id == runtime.Id);
        refreshed.State.Should().Be(RuntimeState.Failed,
            "no active image must surface as an operator-actionable Failed state");
        refreshed.RespawnRetries.Should().Be(0,
            "retries are bumped only on a successful create");

        var events = await _db.RuntimeStateEvents.AsNoTracking()
            .Where(e => e.RuntimeId == runtime.Id)
            .ToListAsync();
        events.Should().HaveCount(1);
        events.Single().Reason.Should().Be("respawn:no_active_image",
            "the structured reason is what the operator dashboard surfaces");
    }

    [Fact]
    public async Task Run_CreateFails_NoTransition()
    {
        await SeedActiveImageAsync();
        await SeedActiveDaemonVersionAsync();
        var runtime = await SeedRuntimeAsync(
            state: RuntimeState.Crashed,
            flyMachineId: "mach_old_fail",
            respawnRetries: 0);

        var handler = new ScriptedHandler();
        // Destroy succeeds.
        handler.Enqueue(HttpStatusCode.OK, "{\"ok\":true}");
        // Create fails with a 500 — Hangfire should retry the whole job.
        handler.Enqueue(HttpStatusCode.InternalServerError, "{\"error\":\"upstream\"}");

        var job = CreateJob(handler);

        var act = async () => await job.Run(runtime.Id, CancellationToken.None);

        await act.Should().ThrowAsync<FlyApiException>(
            "Fly 500 on create must propagate so Hangfire retries");

        var refreshed = await _db.ProjectRuntimes.AsNoTracking().SingleAsync(r => r.Id == runtime.Id);
        refreshed.State.Should().Be(RuntimeState.Crashed,
            "no transition when the create call fails");
        refreshed.RespawnRetries.Should().Be(0,
            "retries are bumped only on a successful create");

        // No audit row for the (non-existent) transition.
        (await _db.RuntimeStateEvents.CountAsync(e => e.RuntimeId == runtime.Id))
            .Should().Be(0);
    }

    // ------------------------------------------------------------------
    // [DisableConcurrentExecution] presence — guards against accidental removal.
    // ------------------------------------------------------------------

    [Fact]
    public void Run_HasDisableConcurrentExecutionAttribute()
    {
        var method = typeof(RespawnRuntimeJob).GetMethod(nameof(RespawnRuntimeJob.Run))!;
        var attr = method.GetCustomAttributes(typeof(Hangfire.DisableConcurrentExecutionAttribute), inherit: false);
        attr.Should().NotBeEmpty(
            "two Hangfire workers must not race on the same respawn — the attribute is the lock");
    }

    // ------------------------------------------------------------------
    // Test doubles — copied from RuntimeProvisionerJobTests for parity.
    // ------------------------------------------------------------------

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
