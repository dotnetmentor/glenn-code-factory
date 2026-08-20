using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
using Source.Features.BoxManagement.Models;
using Source.Features.Cloudflare.Configuration;
using Source.Features.Cloudflare.Models;
using Source.Features.Cloudflare.Services;
using Source.Features.DaemonVersions.Models;
using Source.Features.Projects.Models;
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
/// Unit tests for <see cref="RuntimeProvisionerJob"/>. We construct a real
/// <see cref="BoxClient"/> on top of a scripted <see cref="HttpMessageHandler"/>
/// and build a wired <see cref="ApplicationDbContext"/> with the
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

    private const string TemplateBoxId = "box_template_gold";

    private static BoxOptions DefaultBoxOptions() => new()
    {
        ApiKey = "box_test_key",
        ApiBaseUrl = "https://api.ascii.dev/v1",
        DefaultTtlSeconds = 21_600,
    };

    private RuntimeProvisionerJob CreateJob(HttpMessageHandler handler, BoxOptions? boxOptions = null)
    {
        boxOptions ??= DefaultBoxOptions();

        // No BaseAddress — BoxClient builds absolute URLs from the accessor.
        var http = new HttpClient(handler, disposeHandler: false);
        var box = new BoxClient(
            http,
            new StubBoxOptionsAccessor(boxOptions),
            _db,
            NullLogger<BoxClient>.Instance);
        var runtimeOptions = new StubRuntimeOptionsAccessor(new RuntimeOptions
        {
            PublicApiUrl = "https://test-api.example.com",
        });

        // The provisioner reconciles Cloudflare tunnel ingress for runtimes with
        // a non-default PreviewPort. Wire a tiny CloudflareApiClient on top of an
        // always-success handler so that code path doesn't blow up the test
        // surface — Cloudflare wire shape is covered in its own dedicated tests.
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
            box,
            new StubBoxOptionsAccessor(boxOptions),
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
    /// returns a hit during the provisioner batch.
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

    private async Task<ProjectRuntime> SeedPendingAsync(DateTime? createdAt = null, string? boxId = null)
    {
        var runtime = new ProjectRuntime
        {
            ProjectId = Guid.NewGuid(),
            Region = "de",
            VolumeSizeGb = 1,
            State = RuntimeState.Pending,
            BoxId = boxId,
            // ProjectRuntime.TenantId is required for MintAsync to succeed. Live
            // runtimes inherit this from Project.WorkspaceId; seed it here so the
            // provisioner's mint step doesn't refuse and short-circuit to Failed.
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

    /// <summary>Seed an Active golden-template row — the default fork source.</summary>
    private async Task<RuntimeTemplate> SeedActiveTemplateAsync(
        string boxId = TemplateBoxId,
        DateTime? builtAt = null)
    {
        var template = new RuntimeTemplate
        {
            Id = Guid.NewGuid(),
            BoxId = boxId,
            Label = "base-2026.08.20-test",
            GitSha = "abc1234",
            BuiltAt = builtAt ?? DateTime.UtcNow,
            Status = RuntimeTemplateStatus.Active,
        };
        _db.RuntimeTemplates.Add(template);
        await _db.SaveChangesAsync();
        return template;
    }

    /// <summary>
    /// Canned Box resource JSON (camelCase — BoxClient's serialiser settings).
    /// The default size matches BoxSizeMapper.FromSpec for the seeded runtime spec
    /// (2 cpu / 4096 MB → "small") so reboot-path tests don't trip the resize branch.
    /// </summary>
    private static string BoxJson(string id, string status = "ready", string size = "small") =>
        $$"""
        {"id":"{{id}}","name":"rt","status":"{{status}}","size":"{{size}}","region":"de","ttlSeconds":21600,"createdAt":"2026-05-08T10:00:00Z"}
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
    public async Task Run_NoActiveTemplate_TransitionsToFailed()
    {
        var runtime = await SeedPendingAsync();
        await SeedActiveDaemonVersionAsync();
        // No RuntimeTemplate rows — the pre-flight gate must fail the runtime
        // before any Box call.
        var handler = new ScriptedHandler();
        var job = CreateJob(handler);

        await job.Run(CancellationToken.None);

        handler.CallCount.Should().Be(0,
            "no Box call should fire — there is nothing to fork without a template");

        var refreshed = await _db.ProjectRuntimes.AsNoTracking().SingleAsync(r => r.Id == runtime.Id);
        refreshed.State.Should().Be(RuntimeState.Failed);
        refreshed.BoxId.Should().BeNull();

        var events = await _db.RuntimeStateEvents.AsNoTracking()
            .Where(e => e.RuntimeId == runtime.Id)
            .ToListAsync();
        events.Should().HaveCount(1);
        events.Single().Reason.Should().Contain("no_active_template",
            "the structured reason is what the operator dashboard groups on");
    }

    [Fact]
    public async Task Run_MissingBoxApiKey_TransitionsToFailedIncompleteConfig()
    {
        var runtime = await SeedPendingAsync();
        await SeedActiveTemplateAsync();
        await SeedActiveDaemonVersionAsync();

        var handler = new ScriptedHandler();
        var job = CreateJob(handler, new BoxOptions
        {
            ApiKey = "", // not configured
            ApiBaseUrl = "https://api.ascii.dev/v1",
        });

        await job.Run(CancellationToken.None);

        handler.CallCount.Should().Be(0, "misconfiguration must short-circuit before any Box call");

        var refreshed = await _db.ProjectRuntimes.AsNoTracking().SingleAsync(r => r.Id == runtime.Id);
        refreshed.State.Should().Be(RuntimeState.Failed);

        var events = await _db.RuntimeStateEvents.AsNoTracking()
            .Where(e => e.RuntimeId == runtime.Id)
            .ToListAsync();
        events.Should().HaveCount(1);
        events.Single().Reason.Should().Be("provisioner:incomplete_box_config");
    }

    [Fact]
    public async Task Run_PendingRuntime_ForksTemplateAndTransitionsToBooting()
    {
        var runtime = await SeedPendingAsync();
        var template = await SeedActiveTemplateAsync();
        await SeedActiveDaemonVersionAsync();

        var handler = new ScriptedHandler();
        handler.Enqueue(HttpStatusCode.OK, BoxJson("box_new_abc"));

        var job = CreateJob(handler);

        await job.Run(CancellationToken.None);

        // One upstream Box call: the fork.
        handler.CallCount.Should().Be(1);
        var forkRequest = handler.Requests.Single();
        forkRequest.Method.Should().Be(HttpMethod.Post);
        forkRequest.Url.Should().Be($"https://api.ascii.dev/v1/boxes/{TemplateBoxId}/fork",
            "a fresh runtime is a fork of the active golden template");

        // Wire shape: camelCase properties, VERBATIM env keys, noEnv isolation, TTL guardrail.
        forkRequest.Body.Should().Contain("\"RUNTIME_ID\"",
            "env keys pass through verbatim — the daemon reads RUNTIME_ID, not runtime_id");
        forkRequest.Body.Should().Contain("\"noEnv\":true",
            "runtime forks must never inherit the platform account's own secrets");
        forkRequest.Body.Should().Contain("\"ttlSeconds\":21600",
            "every fork is stamped with the orphan-cost TTL guardrail");

        var refreshed = await _db.ProjectRuntimes.AsNoTracking().SingleAsync(r => r.Id == runtime.Id);
        refreshed.State.Should().Be(RuntimeState.Booting);
        refreshed.BoxId.Should().Be("box_new_abc");
        refreshed.TemplateBoxId.Should().Be(template.BoxId);

        // Audit row written via PersistRuntimeStateEventHandler.
        var events = await _db.RuntimeStateEvents.AsNoTracking()
            .Where(e => e.RuntimeId == runtime.Id)
            .ToListAsync();
        events.Should().HaveCount(1);
        var audit = events.Single();
        audit.FromState.Should().Be(RuntimeState.Pending);
        audit.ToState.Should().Be(RuntimeState.Booting);
        audit.Reason.Should().Be("provisioner:forked_from_template");
        audit.TriggeredBy.Should().Be("system:provisioner");

        // The BoxOperation audit pipeline recorded the fork.
        var boxOps = await _db.BoxOperations.AsNoTracking()
            .Where(o => o.RuntimeId == runtime.Id)
            .ToListAsync();
        boxOps.Should().HaveCount(1);
        boxOps.Single().Operation.Should().Be("ForkBox");
        boxOps.Single().Status.Should().Be(BoxOperationStatus.Succeeded);
    }

    [Fact]
    public async Task Run_TransientRateLimit_LeavesPending()
    {
        var runtime = await SeedPendingAsync();
        await SeedActiveTemplateAsync();
        await SeedActiveDaemonVersionAsync();

        var handler = new ScriptedHandler();
        handler.Enqueue((HttpStatusCode)429, "{\"error\":{\"code\":\"rate_limited\"}}");

        var job = CreateJob(handler);

        await job.Run(CancellationToken.None);

        handler.CallCount.Should().Be(1);

        var refreshed = await _db.ProjectRuntimes.AsNoTracking().SingleAsync(r => r.Id == runtime.Id);
        refreshed.State.Should().Be(RuntimeState.Pending,
            "start-budget/rate-limit errors are transient — the next sweep retries when the budget frees up");
        refreshed.BoxId.Should().BeNull();

        (await _db.RuntimeStateEvents.CountAsync(e => e.RuntimeId == runtime.Id))
            .Should().Be(0, "transient errors must not move the runtime — retry on next tick");
    }

    [Fact]
    public async Task Run_HardBoxApiError_TransitionsToFailed()
    {
        var runtime = await SeedPendingAsync();
        await SeedActiveTemplateAsync();
        await SeedActiveDaemonVersionAsync();

        var handler = new ScriptedHandler();
        handler.Enqueue(HttpStatusCode.UnprocessableEntity, "{\"error\":{\"code\":\"invalid_size\"}}");

        var job = CreateJob(handler);

        await job.Run(CancellationToken.None);

        handler.CallCount.Should().Be(1);

        var refreshed = await _db.ProjectRuntimes.AsNoTracking().SingleAsync(r => r.Id == runtime.Id);
        refreshed.State.Should().Be(RuntimeState.Failed,
            "a Box 422 on fork transitions the runtime to Failed");

        var events = await _db.RuntimeStateEvents.AsNoTracking()
            .Where(e => e.RuntimeId == runtime.Id)
            .ToListAsync();
        events.Should().HaveCount(1);
        var audit = events.Single();
        audit.FromState.Should().Be(RuntimeState.Pending);
        audit.ToState.Should().Be(RuntimeState.Failed);
        audit.Reason.Should().StartWith("provisioner:box_error",
            "the reason carries the structured error code so dashboards can group on it");
        audit.Reason.Should().Contain("invalid_size");
        audit.TriggeredBy.Should().Be("system:provisioner");

        // The Box audit row should record the failed call too.
        var boxOps = await _db.BoxOperations.AsNoTracking().Where(o => o.RuntimeId == runtime.Id).ToListAsync();
        boxOps.Should().HaveCount(1);
        boxOps.Single().Status.Should().Be(BoxOperationStatus.Failed);
        boxOps.Single().HttpStatusCode.Should().Be(422);
        boxOps.Single().ErrorCode.Should().Be("invalid_size");
    }

    [Fact]
    public async Task Run_ExistingArchivedBox_ResumesAndTransitionsToBooting()
    {
        // Reboot path: the runtime already owns a box (restart, wake-after-fail, ...).
        // The provisioner must resume it in place — never re-fork — then re-arm the
        // TTL, wait for it to come up, and refresh the env via the command channel.
        var runtime = await SeedPendingAsync(boxId: "box_existing");
        await SeedActiveTemplateAsync();
        await SeedActiveDaemonVersionAsync();

        var handler = new ScriptedHandler();
        handler.Enqueue(HttpStatusCode.OK, BoxJson("box_existing", status: "archived")); // GetBox
        handler.Enqueue(HttpStatusCode.OK, "{}");                                       // POST resume
        handler.Enqueue(HttpStatusCode.OK, BoxJson("box_existing", status: "archived"));// PATCH ttl
        handler.Enqueue(HttpStatusCode.OK, BoxJson("box_existing", status: "ready"));   // GetBox (wait-up, FIRST poll is up)
        handler.Enqueue(HttpStatusCode.OK, "{}");                                       // POST commands (env refresh)

        var job = CreateJob(handler);

        await job.Run(CancellationToken.None);

        handler.CallCount.Should().Be(5);
        handler.Requests[0].Method.Should().Be(HttpMethod.Get);
        handler.Requests[0].Url.Should().EndWith("/boxes/box_existing");
        handler.Requests[1].Method.Should().Be(HttpMethod.Post);
        handler.Requests[1].Url.Should().EndWith("/boxes/box_existing/resume",
            "an archived box is resumed in place — its disk is the user's data");
        handler.Requests[2].Method.Should().Be(HttpMethod.Patch);
        handler.Requests[2].Url.Should().EndWith("/boxes/box_existing");
        handler.Requests[2].Body.Should().Contain("\"ttlSeconds\":21600");
        handler.Requests[4].Method.Should().Be(HttpMethod.Post);
        handler.Requests[4].Url.Should().EndWith("/boxes/box_existing/commands",
            "the env refresh rides the command side channel so the daemon boots with a fresh JWT");
        handler.Requests[4].Body.Should().Contain("RUNTIME_ID");

        var refreshed = await _db.ProjectRuntimes.AsNoTracking().SingleAsync(r => r.Id == runtime.Id);
        refreshed.State.Should().Be(RuntimeState.Booting);
        refreshed.BoxId.Should().Be("box_existing", "the box id is stable across reboots");

        var events = await _db.RuntimeStateEvents.AsNoTracking()
            .Where(e => e.RuntimeId == runtime.Id)
            .ToListAsync();
        events.Should().HaveCount(1);
        events.Single().Reason.Should().Be("provisioner:rebooted_existing_box");
    }

    [Fact]
    public async Task Run_ExistingBoxGone404_FallsBackToFreshFork()
    {
        var runtime = await SeedPendingAsync(boxId: "box_vanished");
        var template = await SeedActiveTemplateAsync();
        await SeedActiveDaemonVersionAsync();

        var handler = new ScriptedHandler();
        handler.Enqueue(HttpStatusCode.NotFound, "{\"error\":{\"code\":\"not_found\"}}"); // GetBox 404
        handler.Enqueue(HttpStatusCode.OK, BoxJson("box_refork"));                        // fork

        var job = CreateJob(handler);

        await job.Run(CancellationToken.None);

        handler.CallCount.Should().Be(2);
        handler.Requests[1].Url.Should().EndWith($"/boxes/{TemplateBoxId}/fork",
            "a 404'd box clears BoxId and falls back to a fresh template fork");

        var refreshed = await _db.ProjectRuntimes.AsNoTracking().SingleAsync(r => r.Id == runtime.Id);
        refreshed.State.Should().Be(RuntimeState.Booting);
        refreshed.BoxId.Should().Be("box_refork");
        refreshed.TemplateBoxId.Should().Be(template.BoxId);
    }

    [Fact]
    public async Task Run_NetworkException_LeavesPending()
    {
        var runtime = await SeedPendingAsync();
        await SeedActiveTemplateAsync();
        await SeedActiveDaemonVersionAsync();

        var handler = new ThrowingHandler(new HttpRequestException("connection reset"));

        var job = CreateJob(handler);

        await job.Run(CancellationToken.None);

        // Row must NOT have been transitioned — the next tick should retry.
        var refreshed = await _db.ProjectRuntimes.AsNoTracking().SingleAsync(r => r.Id == runtime.Id);
        refreshed.State.Should().Be(RuntimeState.Pending);
        refreshed.BoxId.Should().BeNull();

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

        await SeedActiveTemplateAsync();
        await SeedActiveDaemonVersionAsync();

        // Need 10 scripted responses — one fork per runtime.
        var handler = new ScriptedHandler();
        for (var i = 0; i < 10; i++)
        {
            handler.Enqueue(HttpStatusCode.OK, BoxJson($"box_{i}"));
        }

        var job = CreateJob(handler);

        await job.Run(CancellationToken.None);

        // 10 Box calls (one fork each) — proves we processed exactly 10.
        handler.CallCount.Should().Be(10);

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
        stillPending.Select(r => r.Id).Should().BeEquivalentTo(new[] { runtimes[10].Id, runtimes[11].Id });
    }

    // ------------------------------------------------------------------
    // RuntimeToken minting
    // ------------------------------------------------------------------

    [Fact]
    public async Task Run_PendingRuntime_InjectsRuntimeTokenIntoForkEnv()
    {
        var runtime = await SeedPendingAsync();
        await SeedActiveTemplateAsync();
        await SeedActiveDaemonVersionAsync();

        var handler = new ScriptedHandler();
        handler.Enqueue(HttpStatusCode.OK, BoxJson("box_abc"));

        var job = CreateJob(handler);

        await job.Run(CancellationToken.None);

        handler.Requests.Should().HaveCount(1);
        var forkBody = handler.Requests[0].Body;

        // BoxClient does NOT camelCase dictionary keys — env var names pass
        // through verbatim, so the daemon reads RUNTIME_ID / GLENN_RUNTIME_TOKEN.
        using var doc = JsonDocument.Parse(forkBody);
        var env = doc.RootElement.GetProperty("env");
        env.TryGetProperty("RUNTIME_ID", out var runtimeIdProp).Should().BeTrue();
        runtimeIdProp.GetString().Should().Be(runtime.Id.ToString());

        env.TryGetProperty("GLENN_RUNTIME_TOKEN", out var tokenProp).Should().BeTrue(
            "the daemon needs GLENN_RUNTIME_TOKEN in its env to authenticate back to main API");
        var token = tokenProp.GetString();
        token.Should().NotBeNullOrEmpty();
        // Shape-check only: a JWT is three dot-separated base64url segments. We
        // don't validate the signature here — RuntimeTokenServiceTests covers that.
        token!.Split('.').Should().HaveCount(3, "GLENN_RUNTIME_TOKEN must be a well-formed JWT");

        env.TryGetProperty("MAIN_API_URL", out var apiUrlProp).Should().BeTrue();
        apiUrlProp.GetString().Should().Be("https://test-api.example.com");
    }

    [Fact]
    public async Task Run_PendingRuntime_PersistsExactlyOneRuntimeTokenIssueRow()
    {
        var runtime = await SeedPendingAsync();
        await SeedActiveTemplateAsync();
        await SeedActiveDaemonVersionAsync();

        var handler = new ScriptedHandler();
        handler.Enqueue(HttpStatusCode.OK, BoxJson("box_abc"));

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
        await SeedActiveTemplateAsync();
        await SeedActiveDaemonVersionAsync();

        var handler = new ScriptedHandler();
        handler.Enqueue(HttpStatusCode.OK, BoxJson("box_abc"));

        var job = CreateJob(handler);

        await job.Run(CancellationToken.None);

        // Dig the JWT back out of the captured fork body.
        // (Env var keys pass through verbatim — uppercase GLENN_RUNTIME_TOKEN.)
        using var doc = JsonDocument.Parse(handler.Requests[0].Body);
        var token = doc.RootElement
            .GetProperty("env")
            .GetProperty("GLENN_RUNTIME_TOKEN")
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
    public async Task Run_ForkFails_PersistsRuntimeTokenIssueRowAndTransitionsToFailed()
    {
        // Per the spec the audit row is written by RuntimeTokenService.MintAsync
        // via its OWN SaveChangesAsync, which runs BEFORE the fork — so the
        // issuance row is durably persisted by the time the Box throw happens,
        // and survives the failure. That's the documented "Loss of a token never
        // means loss of audit" guarantee end-to-end. The orphan token simply
        // expires after 7 days.
        var runtime = await SeedPendingAsync();
        await SeedActiveTemplateAsync();
        await SeedActiveDaemonVersionAsync();

        var handler = new ScriptedHandler();
        handler.Enqueue(HttpStatusCode.UnprocessableEntity, "{\"error\":{\"code\":\"invalid_size\"}}");

        var job = CreateJob(handler);

        await job.Run(CancellationToken.None);

        var refreshed = await _db.ProjectRuntimes.AsNoTracking().SingleAsync(r => r.Id == runtime.Id);
        refreshed.State.Should().Be(RuntimeState.Failed,
            "a Box 422 on fork still transitions the runtime to Failed");

        var issues = await _db.RuntimeTokenIssues.AsNoTracking()
            .Where(r => r.RuntimeId == runtime.Id)
            .ToListAsync();
        issues.Should().HaveCount(1,
            "the issuance row commits via MintAsync's own SaveChangesAsync BEFORE the " +
            "Box fork call, so a fork failure leaves the audit row intact");
        issues.Single().RevokedAt.Should().BeNull(
            "the orphaned token isn't pre-revoked; it expires naturally");
    }

    // ------------------------------------------------------------------
    // Cloudflare preview-tunnel env (TUNNEL_TOKEN / PREVIEW_PORT / PREVIEW_HOSTNAME)
    // ------------------------------------------------------------------

    [Fact]
    public async Task Run_PendingRuntime_WithAssignedSubdomain_StampsTunnelEnvVars()
    {
        // Seed a project with a non-default preview port + a pool row Assigned
        // to the runtime's branch. The provisioner should decrypt the tunnel
        // token and stamp TUNNEL_TOKEN / PREVIEW_PORT / PREVIEW_HOSTNAME on
        // the fork env so the daemon can start cloudflared.
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
            Region = "de",
            VolumeSizeGb = 1,
            State = RuntimeState.Pending,
            TenantId = Guid.NewGuid(),
        };
        _db.ProjectRuntimes.Add(runtime);
        await _db.SaveChangesAsync();

        await SeedActiveTemplateAsync();
        await SeedActiveDaemonVersionAsync();

        var handler = new ScriptedHandler();
        handler.Enqueue(HttpStatusCode.OK, BoxJson("box_abc"));

        var job = CreateJob(handler);

        await job.Run(CancellationToken.None);

        // Dig the env dict out of the fork body.
        handler.Requests.Should().HaveCount(1);
        using var doc = JsonDocument.Parse(handler.Requests[0].Body);
        var env = doc.RootElement.GetProperty("env");

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
        await SeedActiveTemplateAsync();
        await SeedActiveDaemonVersionAsync();

        var handler = new ScriptedHandler();
        handler.Enqueue(HttpStatusCode.OK, BoxJson("box_abc"));

        var job = CreateJob(handler);

        await job.Run(CancellationToken.None);

        handler.Requests.Should().HaveCount(1);
        using var doc = JsonDocument.Parse(handler.Requests[0].Body);
        var env = doc.RootElement.GetProperty("env");

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
