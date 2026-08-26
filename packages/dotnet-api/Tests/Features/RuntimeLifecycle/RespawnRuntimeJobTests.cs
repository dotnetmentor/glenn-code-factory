using System.Net;
using System.Text;
using Api.Tests.Features.BoxManagement;
using Hangfire;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Source.Features.BoxManagement;
using Source.Features.BoxManagement.Configuration;
using Source.Features.Cloudflare.Configuration;
using Source.Features.Cloudflare.Services;
using Source.Features.DaemonVersions.Models;
using Source.Features.RuntimeLifecycle.Configuration;
using Source.Features.RuntimeLifecycle.Events;
using Source.Features.RuntimeLifecycle.Jobs;
using Source.Features.RuntimeLifecycle.Models;
using Source.Features.RuntimeTemplates.Models;
using Source.Features.RuntimeTokens.Services;
using Source.Features.SystemSettings.Services;
using Source.Infrastructure;
using Source.Infrastructure.Interceptors;

namespace Api.Tests.Features.RuntimeLifecycle;

/// <summary>
/// Unit tests for <see cref="RespawnRuntimeJob"/>. Mirrors the bootstrap that
/// <c>RuntimeProvisionerJobTests</c> uses: a real <see cref="BoxClient"/>
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

        services.AddSingleton<IRuntimeOptionsAccessor>(
            new StubRuntimeOptionsAccessor(new RuntimeOptions
            {
                PublicApiUrl = "https://test-api.example.com",
            }));

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

    private const string TemplateBoxId = "box_template_gold";

    private static BoxOptions DefaultBoxOptions() => new()
    {
        ApiKey = "box_test_key",
        ApiBaseUrl = "https://ascii.dev/api/box/v1",
        DefaultTtlSeconds = 21_600,
    };

    private RespawnRuntimeJob CreateJob(HttpMessageHandler handler, string? publicApiUrl = "https://test-api.example.com")
    {
        // No BaseAddress — BoxClient builds absolute URLs from the accessor.
        var http = new HttpClient(handler, disposeHandler: false);
        var box = new BoxClient(
            http,
            new StubBoxOptionsAccessor(DefaultBoxOptions()),
            _db,
            NullLogger<BoxClient>.Instance);
        var runtimeOptions = new StubRuntimeOptionsAccessor(new RuntimeOptions
        {
            PublicApiUrl = publicApiUrl ?? string.Empty,
        });

        // Always-success Cloudflare stub — the respawn job reconciles tunnel
        // ingress best-effort; wire shape is covered by CloudflareApiClient's
        // own dedicated tests.
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
            box,
            new StubBoxOptionsAccessor(DefaultBoxOptions()),
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
        string? boxId = "box_old",
        int respawnRetries = 0)
    {
        var runtime = new ProjectRuntime
        {
            ProjectId = Guid.NewGuid(),
            Region = "de",
            VolumeSizeGb = 1,
            State = state,
            RespawnRetries = respawnRetries,
            BoxId = boxId,
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
    /// returns a hit during the respawn flow.
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

    /// <summary>Seed an Active golden-template row — the fork source when the box is gone.</summary>
    private async Task<RuntimeTemplate> SeedActiveTemplateAsync(string boxId = TemplateBoxId)
    {
        var template = new RuntimeTemplate
        {
            Id = Guid.NewGuid(),
            BoxId = boxId,
            Label = "base-2026.08.20-test",
            GitSha = "abc1234",
            BuiltAt = DateTime.UtcNow,
            Status = RuntimeTemplateStatus.Active,
        };
        _db.RuntimeTemplates.Add(template);
        await _db.SaveChangesAsync();
        return template;
    }

    /// <summary>
    /// Canned bare Box resource JSON. Type "small" matches BoxTypeMapper.FromSpec
    /// for the default seeded spec (2 cpu / 4096 MB); the lifecycle field on the
    /// wire is `state`.
    /// </summary>
    private static string BareBoxJson(string id, string state = "ready") =>
        $$"""
        {"id":"{{id}}","name":"rt","state":"{{state}}","type":"small","region":"de","ttlSeconds":21600,"createdAt":"2026-05-08T10:00:00Z"}
        """;

    /// <summary>GET /boxes/{id} + PATCH /boxes/{id} envelope per the contract.</summary>
    private static string BoxInfoJson(string id, string state = "ready") =>
        $$"""{"ok":true,"type":"box.info","box":{{BareBoxJson(id, state)}}}""";

    /// <summary>POST /boxes/{id}/fork response envelope per the contract.</summary>
    private static string BoxCreatedJson(string id, string state = "provisioning") =>
        $$"""{"type":"box.created","box":{{BareBoxJson(id, state)}},"status":"provisioning","ttlSeconds":21600}""";

    /// <summary>GET /boxes list envelope per the contract (empty — adopt-by-name misses).</summary>
    private static string BoxListJson() =>
        """{"ok":true,"type":"box.list","boxes":[],"pageInfo":{"hasNextPage":false}}""";

    // ------------------------------------------------------------------
    // Tests
    // ------------------------------------------------------------------

    [Fact]
    public async Task Run_RuntimeNotFound_NoOp()
    {
        var handler = new ScriptedHandler();
        var job = CreateJob(handler);

        // No runtime exists with this id — job must no-op without hitting Box.
        await job.Run(Guid.NewGuid(), CancellationToken.None);

        handler.CallCount.Should().Be(0,
            "a missing runtime row must short-circuit before any Box call");
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
    public async Task Run_CrashedBoxStillUp_StopsResumesAndTransitionsToBooting()
    {
        // A crashed runtime whose box is up-but-wedged: the Box-native respawn is
        // a clean VM reboot of the SAME box — stop (fresh snapshot), resume,
        // re-arm TTL, wait for it to come up, refresh the env with the fresh JWT.
        await SeedActiveDaemonVersionAsync();
        var runtime = await SeedRuntimeAsync(
            state: RuntimeState.Crashed,
            boxId: "box_wedged",
            respawnRetries: 0);

        var handler = new ScriptedHandler();
        handler.Enqueue(HttpStatusCode.OK, BoxInfoJson("box_wedged", state: "running")); // GetBox
        handler.Enqueue(HttpStatusCode.OK, "{}");                                        // POST stop
        handler.Enqueue(HttpStatusCode.OK, "{}");                                        // POST resume (env+ttl in body)
        handler.Enqueue(HttpStatusCode.OK, BoxInfoJson("box_wedged", state: "running")); // PATCH ttl
        handler.Enqueue(HttpStatusCode.OK, BoxInfoJson("box_wedged", state: "ready"));   // GetBox (wait-up, FIRST poll is up)
        handler.Enqueue(HttpStatusCode.OK, "{}");                                        // POST command (env refresh)

        var job = CreateJob(handler);

        await job.Run(runtime.Id, CancellationToken.None);

        handler.CallCount.Should().Be(6);
        handler.Requests[1].Method.Should().Be(HttpMethod.Post);
        handler.Requests[1].Url.Should().EndWith("/boxes/box_wedged/stop",
            "an up-but-wedged box is stopped first so the resume boots from a fresh snapshot");
        handler.Requests[2].Method.Should().Be(HttpMethod.Post);
        handler.Requests[2].Url.Should().EndWith("/boxes/box_wedged/resume");
        handler.Requests[2].Body.Should().Contain("GLENN_RUNTIME_TOKEN",
            "the resume body carries the fresh env directly (the contract supports it)");
        handler.Requests[3].Method.Should().Be(HttpMethod.Patch);
        handler.Requests[3].Url.Should().EndWith("/boxes/box_wedged");
        handler.Requests[5].Method.Should().Be(HttpMethod.Post);
        handler.Requests[5].Url.Should().EndWith("/boxes/box_wedged/commands",
            "the env refresh delivers the freshly-minted JWT and bounces the daemon (plural /commands per the contract)");
        handler.Requests[5].Body.Should().Contain("GLENN_RUNTIME_TOKEN");

        var refreshed = await _db.ProjectRuntimes.AsNoTracking().SingleAsync(r => r.Id == runtime.Id);
        refreshed.State.Should().Be(RuntimeState.Booting,
            "Crashed -> Booting closes the respawn loop");
        refreshed.BoxId.Should().Be("box_wedged", "reboot keeps the same box — its disk is the persistence");
        refreshed.RespawnRetries.Should().Be(1,
            "the respawn job is the canonical bump site for the retry counter");

        // Audit row written by PersistRuntimeStateEventHandler.
        var events = await _db.RuntimeStateEvents.AsNoTracking()
            .Where(e => e.RuntimeId == runtime.Id)
            .ToListAsync();
        events.Should().HaveCount(1);
        var audit = events.Single();
        audit.FromState.Should().Be(RuntimeState.Crashed);
        audit.ToState.Should().Be(RuntimeState.Booting);
        audit.Reason.Should().Be("respawn:rebooted");
        audit.TriggeredBy.Should().Be("respawn-job");
        audit.Metadata.Should().NotBeNullOrWhiteSpace();
        audit.Metadata!.Should().Contain("box_wedged",
            "metadata must record the box id for traceability");
        audit.Metadata!.Should().Contain("\"rebooted\":true");
    }

    [Fact]
    public async Task Run_CrashedBoxGone404_ForksFreshFromTemplate()
    {
        await SeedActiveDaemonVersionAsync();
        var template = await SeedActiveTemplateAsync();
        var runtime = await SeedRuntimeAsync(
            state: RuntimeState.Crashed,
            boxId: "box_vanished");

        var handler = new ScriptedHandler();
        handler.Enqueue(HttpStatusCode.NotFound,
            """{"ok":false,"type":"box.error","status":404,"code":"not_found","message":"not found","error":{"code":"not_found","message":"not found","status":404},"requestId":"req_404"}"""); // GetBox 404
        handler.Enqueue(HttpStatusCode.OK, BoxListJson());                     // adopt-by-name miss
        handler.Enqueue(HttpStatusCode.OK, BoxCreatedJson("box_fresh_fork"));  // POST fork
        handler.Enqueue(HttpStatusCode.OK, BoxInfoJson("box_fresh_fork"));     // PATCH name

        var job = CreateJob(handler);

        await job.Run(runtime.Id, CancellationToken.None);

        handler.CallCount.Should().Be(4,
            "404 on GetBox means box gone — adopt-check list, fork fresh from the template, PATCH the name");
        handler.Requests[2].Method.Should().Be(HttpMethod.Post);
        handler.Requests[2].Url.Should().EndWith($"/boxes/{TemplateBoxId}/fork",
            "the fresh fork must come from the active golden template");

        var refreshed = await _db.ProjectRuntimes.AsNoTracking().SingleAsync(r => r.Id == runtime.Id);
        refreshed.State.Should().Be(RuntimeState.Booting);
        refreshed.BoxId.Should().Be("box_fresh_fork",
            "the new box id must replace the vanished one");
        refreshed.TemplateBoxId.Should().Be(template.BoxId);
        refreshed.RespawnRetries.Should().Be(1);

        var events = await _db.RuntimeStateEvents.AsNoTracking()
            .Where(e => e.RuntimeId == runtime.Id)
            .ToListAsync();
        events.Should().HaveCount(1);
        var audit = events.Single();
        audit.Reason.Should().Be("respawn:rebooted");
        audit.Metadata!.Should().Contain("box_vanished",
            "metadata must record the old box id for traceability");
        audit.Metadata!.Should().Contain("box_fresh_fork",
            "metadata must record the new box id for traceability");
        audit.Metadata!.Should().Contain("\"rebooted\":false");
    }

    [Fact]
    public async Task Run_CrashedBoxGone404_NoActiveTemplate_TransitionsToFailed()
    {
        await SeedActiveDaemonVersionAsync();
        // No RuntimeTemplate rows seeded — the re-fork must fail the runtime.
        var runtime = await SeedRuntimeAsync(
            state: RuntimeState.Crashed,
            boxId: "box_vanished_no_tpl");

        var handler = new ScriptedHandler();
        handler.Enqueue(HttpStatusCode.NotFound,
            """{"ok":false,"type":"box.error","status":404,"code":"not_found","message":"not found","error":{"code":"not_found","message":"not found","status":404},"requestId":"req_404b"}"""); // GetBox 404

        var job = CreateJob(handler);

        await job.Run(runtime.Id, CancellationToken.None);

        handler.CallCount.Should().Be(1,
            "no fork can be issued without an active template");

        var refreshed = await _db.ProjectRuntimes.AsNoTracking().SingleAsync(r => r.Id == runtime.Id);
        refreshed.State.Should().Be(RuntimeState.Failed,
            "no active template must surface as an operator-actionable Failed state");
        refreshed.RespawnRetries.Should().Be(0,
            "retries are bumped only on a successful reboot/fork");

        var events = await _db.RuntimeStateEvents.AsNoTracking()
            .Where(e => e.RuntimeId == runtime.Id)
            .ToListAsync();
        events.Should().HaveCount(1);
        events.Single().Reason.Should().Be("respawn:no_active_template",
            "the structured reason is what the operator dashboard surfaces");
    }

    [Fact]
    public async Task Run_MissingPublicApiUrl_TransitionsToFailed()
    {
        var runtime = await SeedRuntimeAsync(state: RuntimeState.Crashed);

        var handler = new ScriptedHandler();
        var job = CreateJob(handler, publicApiUrl: "");

        await job.Run(runtime.Id, CancellationToken.None);

        handler.CallCount.Should().Be(0,
            "misconfiguration must short-circuit before any Box call");

        var refreshed = await _db.ProjectRuntimes.AsNoTracking().SingleAsync(r => r.Id == runtime.Id);
        refreshed.State.Should().Be(RuntimeState.Failed,
            "a daemon without MAIN_API_URL can never dial back — refuse to respawn");

        var events = await _db.RuntimeStateEvents.AsNoTracking()
            .Where(e => e.RuntimeId == runtime.Id)
            .ToListAsync();
        events.Should().HaveCount(1);
        events.Single().Reason.Should().Be("provisioner:no_public_api_url");
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
}
