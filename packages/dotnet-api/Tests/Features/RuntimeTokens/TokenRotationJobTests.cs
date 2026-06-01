using Api.Tests.Infrastructure;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using MediatR;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Source.Features.RuntimeLifecycle.Models;
using Source.Features.RuntimeTokens.Jobs;
using Source.Features.RuntimeTokens.Models;
using Source.Features.RuntimeTokens.Services;
using Source.Features.SignalR.Contracts;
using Source.Features.SignalR.Hubs;
using Source.Features.SystemSettings.Services;
using Source.Infrastructure;
using Source.Infrastructure.Interceptors;

namespace Api.Tests.Features.RuntimeTokens;

/// <summary>
/// Unit tests for <see cref="TokenRotationJob"/>. We wire the real
/// <see cref="IRuntimeTokenService"/> + signing-key stack so a successful
/// rotation actually writes a fresh <see cref="RuntimeTokenIssue"/> audit
/// row, and we mock the SignalR <see cref="IHubContext{THub, T}"/> + Hangfire
/// <see cref="IBackgroundJobClient"/> so tests can assert on what the job
/// pushed and what it scheduled.
///
/// <para>We do <i>not</i> drive the public <see cref="TokenRotationJob.Run"/>
/// from a real Hangfire server — Hangfire's extension method
/// <c>Schedule&lt;T&gt;(expr, delay)</c> ultimately calls
/// <see cref="IBackgroundJobClient.Create(Job, IState)"/> with a
/// <see cref="ScheduledState"/>; mirroring
/// <c>ScheduleRespawnHandlerTests</c>, we verify against that surface.</para>
/// </summary>
public class TokenRotationJobTests : IDisposable
{
    private readonly string _dbName;
    private readonly ServiceProvider _provider;
    private readonly ApplicationDbContext _db;
    private readonly IRuntimeTokenService _runtimeTokenService;
    private readonly IMediator _mediator;
    private readonly RevocationCache _revocationCache;

    // SignalR mocks — chain shape: hub.Clients.Group("...").UpdateConfig(payload).
    private readonly Mock<IHubClients<IRuntimeClient>> _hubClients = new();
    private readonly Mock<IHubContext<RuntimeHub, IRuntimeClient>> _runtimeHub = new();
    private readonly Mock<IRuntimeClient> _runtimeGroupClient = new();

    private readonly Mock<IBackgroundJobClient> _backgroundJobs = new();

    public TokenRotationJobTests()
    {
        _dbName = Guid.NewGuid().ToString();
        var cipherKeyB64 = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpContextAccessor();

        // SignalR satisfies the auto-discovered BroadcastRuntimeStateChangedHandler
        // (depends on IHubContext<AgentHub, IAgentClient>); this hub never fires
        // in these tests but DI must be able to construct the handler.
        services.AddSignalR();

        services.AddMediatR(cfg =>
            cfg.RegisterServicesFromAssembly(typeof(RuntimeTokenService).Assembly));

        services.AddSingleton<IBackgroundJobClient>(_backgroundJobs.Object);

        services.AddScoped<DomainEventInterceptor>();

        services.AddDbContext<ApplicationDbContext>((sp, options) =>
        {
            options.UseInMemoryDatabase(_dbName);
            options.AddInterceptors(sp.GetRequiredService<DomainEventInterceptor>());
        });

        // RuntimeToken stack — real implementations so the job mints a real JWT
        // and writes a real RuntimeTokenIssue audit row.
        services.AddSingleton(Options.Create(new SystemSettingsCipherOptions { EncryptionKey = cipherKeyB64 }));
        services.AddSingleton<ISystemSettingsCipher, SystemSettingsCipher>();
        services.AddSingleton<SystemSettingsCache>();
        services.AddScoped<ISystemSettingsService, SystemSettingsService>();
        services.AddSingleton<IRuntimeTokenSigningKeyService, RuntimeTokenSigningKeyService>();
        services.AddMemoryCache();
        // Real revocation cache so RevokeRotated → RevokeTokenCommand → cache prime
        // flows end-to-end.
        services.AddSingleton<IRevocationCache, RevocationCache>();
        services.AddScoped<IRuntimeTokenService, RuntimeTokenService>();

        _provider = services.BuildServiceProvider();
        _db = _provider.GetRequiredService<ApplicationDbContext>();
        _db.Database.EnsureCreated();
        _runtimeTokenService = _provider.GetRequiredService<IRuntimeTokenService>();
        _mediator = _provider.GetRequiredService<IMediator>();
        _revocationCache = (RevocationCache)_provider.GetRequiredService<IRevocationCache>();

        // Hub chain — default: success.
        _runtimeHub.SetupGet(h => h.Clients).Returns(_hubClients.Object);
        _hubClients.Setup(c => c.Group(It.IsAny<string>())).Returns(_runtimeGroupClient.Object);
        _runtimeGroupClient
            .Setup(c => c.UpdateConfig(It.IsAny<ConfigUpdatePayload>()))
            .Returns(Task.CompletedTask);
    }

    public void Dispose()
    {
        _provider.Dispose();
        GC.SuppressFinalize(this);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private TokenRotationJob CreateJob() =>
        new(
            _db,
            _runtimeTokenService,
            _runtimeHub.Object,
            _backgroundJobs.Object,
            _mediator,
            NullLogger<TokenRotationJob>.Instance);

    /// <summary>
    /// Seed a runtime row directly. <see cref="RuntimeState"/> defaults to
    /// <see cref="RuntimeState.Online"/> — the case the rotation job most
    /// expects to see in production.
    /// </summary>
    private async Task<ProjectRuntime> SeedRuntimeAsync(
        RuntimeState state = RuntimeState.Online,
        bool isDeleted = false)
    {
        var runtime = new ProjectRuntime
        {
            ProjectId = Guid.NewGuid(),
            Region = "arn",
            VolumeSizeGb = 1,
            State = state,
            FlyMachineId = "mach_" + Guid.NewGuid().ToString("N")[..8],
            // Card 4 of e2e-smoketest: every runtime mints with a non-null
            // TenantId. Stamp one here so the rotation path's MintAsync passes
            // the new tenancy-chain guard. Live runtimes inherit this from
            // Project.WorkspaceId at create time (Card 3).
            TenantId = Guid.NewGuid(),
            IsDeleted = isDeleted,
            DeletedAt = isDeleted ? DateTime.UtcNow : null,
        };
        _db.ProjectRuntimes.Add(runtime);
        await _db.SaveChangesAsync();
        return runtime;
    }

    /// <summary>
    /// Insert a <see cref="RuntimeTokenIssue"/> row directly — bypasses the JWT
    /// minting so we can control <c>ExpiresAt</c>/<c>RevokedAt</c> precisely.
    /// </summary>
    private async Task<RuntimeTokenIssue> SeedIssueAsync(
        Guid runtimeId,
        DateTime expiresAt,
        DateTime? revokedAt = null,
        DateTime? issuedAt = null)
    {
        var iat = issuedAt ?? DateTime.UtcNow.AddDays(-6);
        var issue = new RuntimeTokenIssue
        {
            Id = Guid.NewGuid(),
            RuntimeId = runtimeId,
            ProjectId = Guid.NewGuid(),
            TenantId = null,
            BranchId = null,
            Scope = "runtime",
            TokenHash = new string('a', 64),
            IssuedAt = iat,
            ExpiresAt = expiresAt,
            RevokedAt = revokedAt,
        };
        _db.RuntimeTokenIssues.Add(issue);
        await _db.SaveChangesAsync();
        return issue;
    }

    /// <summary>
    /// Captures every <see cref="IBackgroundJobClient.Create"/> invocation that
    /// carries a <see cref="ScheduledState"/>. Returns the list in the order
    /// the calls happened.
    /// </summary>
    private List<(Job Job, ScheduledState State)> CapturedSchedules() =>
        _backgroundJobs.Invocations
            .Where(i => i.Method.Name == nameof(IBackgroundJobClient.Create))
            .Select(i => (i.Arguments[0] as Job, i.Arguments[1] as IState))
            .Where(x => x.Item1 is not null && x.Item2 is ScheduledState)
            .Select(x => (x.Item1!, (ScheduledState)x.Item2!))
            .ToList();

    // ------------------------------------------------------------------
    // Tests
    // ------------------------------------------------------------------

    [Fact]
    public async Task Run_NoCandidates_NoWork()
    {
        // Empty DB. Job must be a clean no-op: no hub calls, no schedules.
        await CreateJob().Run(CancellationToken.None);

        _runtimeGroupClient.Verify(
            c => c.UpdateConfig(It.IsAny<ConfigUpdatePayload>()),
            Times.Never,
            "no candidates means no daemon push");

        _backgroundJobs.Verify(
            x => x.Create(It.IsAny<Job>(), It.IsAny<IState>()),
            Times.Never,
            "no candidates means no scheduled revoke");

        (await _db.RuntimeTokenIssues.CountAsync()).Should().Be(0,
            "no rotation must have minted any new audit row");
    }

    [Fact]
    public async Task Run_TokenExpiringWithin24h_MintsPushesAndSchedulesRevoke()
    {
        var runtime = await SeedRuntimeAsync(RuntimeState.Online);
        var oldIssue = await SeedIssueAsync(
            runtimeId: runtime.Id,
            expiresAt: DateTime.UtcNow.AddHours(12));

        var before = DateTime.UtcNow;
        await CreateJob().Run(CancellationToken.None);
        var after = DateTime.UtcNow;

        // A fresh issue row landed for this runtime.
        var issuesForRuntime = await _db.RuntimeTokenIssues.AsNoTracking()
            .Where(i => i.RuntimeId == runtime.Id)
            .ToListAsync();
        issuesForRuntime.Should().HaveCount(2,
            "a rotation must add a new issue row alongside the (still-alive) old one");

        var newIssue = issuesForRuntime.Single(i => i.Id != oldIssue.Id);
        newIssue.RevokedAt.Should().BeNull("the freshly minted token must not be revoked");
        newIssue.ExpiresAt.Should().BeAfter(DateTime.UtcNow.AddDays(6),
            "default 7-day lifetime — well beyond the 1-day rotation lookahead");

        // Hub push: exactly one UpdateConfig call to the runtime's group, carrying the new token.
        _hubClients.Verify(c => c.Group($"runtime-{runtime.Id}"), Times.AtLeastOnce);
        _runtimeGroupClient.Verify(
            c => c.UpdateConfig(It.Is<ConfigUpdatePayload>(p =>
                p.RuntimeId == runtime.Id
                && p.RuntimeToken != null
                && p.RuntimeToken.Length > 0)),
            Times.Once,
            "rotation must push the new token to the daemon group");

        // Old token scheduled for revoke ~1h out.
        var schedules = CapturedSchedules();
        schedules.Should().HaveCount(1, "exactly the old token gets a scheduled revoke");

        var (job, state) = schedules.Single();
        job.Type.Should().Be<TokenRotationJob>("the scheduled job must be TokenRotationJob.RevokeRotated");
        job.Method.Name.Should().Be(nameof(TokenRotationJob.RevokeRotated));
        job.Args[0].Should().Be(oldIssue.Id, "the old token's jti must be the first argument");
        job.Args[1].Should().Be("rotation");

        var delay = state.EnqueueAt - before;
        delay.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMinutes(59));
        delay.Should().BeLessThanOrEqualTo(after - before + TimeSpan.FromMinutes(61));
    }

    [Fact]
    public async Task Run_TokenExpiringIn5Days_Skipped()
    {
        var runtime = await SeedRuntimeAsync(RuntimeState.Online);
        await SeedIssueAsync(runtime.Id, expiresAt: DateTime.UtcNow.AddDays(5));

        await CreateJob().Run(CancellationToken.None);

        _runtimeGroupClient.Verify(
            c => c.UpdateConfig(It.IsAny<ConfigUpdatePayload>()),
            Times.Never,
            "5-day-out tokens are nowhere near the 1-day rotation threshold");

        _backgroundJobs.Verify(
            x => x.Create(It.IsAny<Job>(), It.IsAny<IState>()),
            Times.Never);

        (await _db.RuntimeTokenIssues.CountAsync(i => i.RuntimeId == runtime.Id))
            .Should().Be(1, "no new mint must have happened");
    }

    [Fact]
    public async Task Run_AlreadyExpiredToken_Skipped()
    {
        var runtime = await SeedRuntimeAsync(RuntimeState.Online);
        await SeedIssueAsync(runtime.Id, expiresAt: DateTime.UtcNow.AddHours(-1));

        await CreateJob().Run(CancellationToken.None);

        _runtimeGroupClient.Verify(
            c => c.UpdateConfig(It.IsAny<ConfigUpdatePayload>()),
            Times.Never,
            "already-expired tokens cannot be rotated — JWT lifetime check rejects regardless");

        _backgroundJobs.Verify(
            x => x.Create(It.IsAny<Job>(), It.IsAny<IState>()),
            Times.Never);

        (await _db.RuntimeTokenIssues.CountAsync(i => i.RuntimeId == runtime.Id))
            .Should().Be(1);
    }

    [Fact]
    public async Task Run_AlreadyRevokedToken_Skipped()
    {
        var runtime = await SeedRuntimeAsync(RuntimeState.Online);
        await SeedIssueAsync(
            runtime.Id,
            expiresAt: DateTime.UtcNow.AddHours(12),
            revokedAt: DateTime.UtcNow.AddMinutes(-5));

        await CreateJob().Run(CancellationToken.None);

        _runtimeGroupClient.Verify(
            c => c.UpdateConfig(It.IsAny<ConfigUpdatePayload>()),
            Times.Never,
            "an operator-revoked token must NOT be rotated — they revoked it on purpose");

        _backgroundJobs.Verify(
            x => x.Create(It.IsAny<Job>(), It.IsAny<IState>()),
            Times.Never);

        (await _db.RuntimeTokenIssues.CountAsync(i => i.RuntimeId == runtime.Id))
            .Should().Be(1);
    }

    [Theory]
    [InlineData(RuntimeState.Failed)]
    [InlineData(RuntimeState.Deleted)]
    [InlineData(RuntimeState.Deleting)]
    public async Task Run_RuntimeInDeadOrDyingState_Skipped(RuntimeState state)
    {
        var runtime = await SeedRuntimeAsync(state);
        await SeedIssueAsync(runtime.Id, expiresAt: DateTime.UtcNow.AddHours(12));

        await CreateJob().Run(CancellationToken.None);

        _runtimeGroupClient.Verify(
            c => c.UpdateConfig(It.IsAny<ConfigUpdatePayload>()),
            Times.Never,
            $"a runtime in {state} has no live daemon to push to");

        _backgroundJobs.Verify(
            x => x.Create(It.IsAny<Job>(), It.IsAny<IState>()),
            Times.Never);

        (await _db.RuntimeTokenIssues.CountAsync(i => i.RuntimeId == runtime.Id))
            .Should().Be(1);
    }

    [Fact]
    public async Task Run_SoftDeletedRuntime_Skipped()
    {
        // IsDeleted = true; State irrelevant. The IgnoreQueryFilters on the join
        // pulls the row back, but the !IsDeleted filter rejects it.
        var runtime = await SeedRuntimeAsync(RuntimeState.Online, isDeleted: true);
        await SeedIssueAsync(runtime.Id, expiresAt: DateTime.UtcNow.AddHours(12));

        await CreateJob().Run(CancellationToken.None);

        _runtimeGroupClient.Verify(
            c => c.UpdateConfig(It.IsAny<ConfigUpdatePayload>()),
            Times.Never,
            "soft-deleted runtimes are out of scope even if state isn't terminal");

        _backgroundJobs.Verify(
            x => x.Create(It.IsAny<Job>(), It.IsAny<IState>()),
            Times.Never);
    }

    [Fact]
    public async Task Run_MultipleAliveTokenRowsForSameRuntime_OneMintAllRevoked()
    {
        // Drift case: previous rotations went weird and left two non-revoked,
        // non-expired rows alive for the same runtime, both within the 1-day
        // window. The job should mint exactly ONE new token, push it once, and
        // schedule revoke for BOTH old rows.
        var runtime = await SeedRuntimeAsync(RuntimeState.Online);
        var older = await SeedIssueAsync(
            runtime.Id,
            expiresAt: DateTime.UtcNow.AddHours(6),
            issuedAt: DateTime.UtcNow.AddDays(-7));
        var newer = await SeedIssueAsync(
            runtime.Id,
            expiresAt: DateTime.UtcNow.AddHours(20),
            issuedAt: DateTime.UtcNow.AddDays(-5));

        await CreateJob().Run(CancellationToken.None);

        // Exactly one new mint.
        var allForRuntime = await _db.RuntimeTokenIssues.AsNoTracking()
            .Where(i => i.RuntimeId == runtime.Id)
            .ToListAsync();
        allForRuntime.Should().HaveCount(3,
            "two pre-existing alive rows + one fresh mint");

        // Exactly one push.
        _runtimeGroupClient.Verify(
            c => c.UpdateConfig(It.IsAny<ConfigUpdatePayload>()),
            Times.Once,
            "one runtime → one push regardless of how many old token rows it has");

        // Two scheduled revokes — one per old row.
        var schedules = CapturedSchedules();
        schedules.Should().HaveCount(2,
            "every alive old token row must be scheduled for revoke; the cleanup is part of rotation");

        var scheduledJtis = schedules.Select(s => (Guid)s.Job.Args[0]!).ToHashSet();
        scheduledJtis.Should().BeEquivalentTo(new[] { older.Id, newer.Id },
            "both old rows must be scheduled, not just the most recent");
    }

    [Fact]
    public async Task Run_HubPushFailureForFirstRuntime_DoesNotBlockSecond()
    {
        // First runtime: hub push throws. Second runtime: hub push succeeds.
        // The mint already happened by the time the push fails, so the audit
        // row for runtime 1 is still there. The job must process runtime 2
        // fully — push + schedule.
        var runtime1 = await SeedRuntimeAsync(RuntimeState.Online);
        var runtime2 = await SeedRuntimeAsync(RuntimeState.Online);
        var old1 = await SeedIssueAsync(runtime1.Id, expiresAt: DateTime.UtcNow.AddHours(8));
        var old2 = await SeedIssueAsync(runtime2.Id, expiresAt: DateTime.UtcNow.AddHours(8));

        // Make the hub-push throw for runtime1's group only.
        var failingClient = new Mock<IRuntimeClient>();
        failingClient
            .Setup(c => c.UpdateConfig(It.IsAny<ConfigUpdatePayload>()))
            .ThrowsAsync(new InvalidOperationException("simulated hub blow-up"));

        _hubClients
            .Setup(c => c.Group($"runtime-{runtime1.Id}"))
            .Returns(failingClient.Object);
        _hubClients
            .Setup(c => c.Group($"runtime-{runtime2.Id}"))
            .Returns(_runtimeGroupClient.Object);

        await CreateJob().Run(CancellationToken.None);

        // Both runtimes got a fresh mint.
        (await _db.RuntimeTokenIssues.AsNoTracking()
            .CountAsync(i => i.RuntimeId == runtime1.Id))
            .Should().Be(2, "runtime1's mint runs before the failing push");
        (await _db.RuntimeTokenIssues.AsNoTracking()
            .CountAsync(i => i.RuntimeId == runtime2.Id))
            .Should().Be(2, "runtime2 must be processed despite runtime1's push failure");

        // Both old tokens scheduled for revoke (mint+schedule are independent of push outcome).
        var schedules = CapturedSchedules();
        var scheduledJtis = schedules.Select(s => (Guid)s.Job.Args[0]!).ToHashSet();
        scheduledJtis.Should().Contain(old1.Id,
            "runtime1's old token must still be scheduled for revoke even though its push failed");
        scheduledJtis.Should().Contain(old2.Id);

        // Failing client got exactly one call, succeeding client got exactly one call.
        failingClient.Verify(
            c => c.UpdateConfig(It.IsAny<ConfigUpdatePayload>()),
            Times.Once);
        _runtimeGroupClient.Verify(
            c => c.UpdateConfig(It.IsAny<ConfigUpdatePayload>()),
            Times.Once);
    }

    [Fact]
    public async Task RevokeRotated_StampsRevokedAtAndReason()
    {
        // Direct-invoke RevokeRotated. Asserts the IMediator path: the
        // RuntimeTokenIssue row's RevokedAt + RevocationReason are stamped, and
        // the in-memory revocation cache is primed (proving the command's full
        // policy ran end-to-end, not just a raw DB write).
        var runtime = await SeedRuntimeAsync(RuntimeState.Online);
        var issue = await SeedIssueAsync(runtime.Id, expiresAt: DateTime.UtcNow.AddHours(2));

        var before = DateTime.UtcNow;
        await CreateJob().RevokeRotated(issue.Id, "rotation");
        var after = DateTime.UtcNow;

        var refreshed = await _db.RuntimeTokenIssues.AsNoTracking()
            .SingleAsync(r => r.Id == issue.Id);
        refreshed.RevokedAt.Should().NotBeNull();
        refreshed.RevokedAt!.Value.Should().BeOnOrAfter(before.AddSeconds(-1))
            .And.BeOnOrBefore(after.AddSeconds(1));
        refreshed.RevocationReason.Should().Be("rotation");

        _revocationCache.IsRevoked(issue.Id).Should().BeTrue(
            "RevokeRotated routes through RevokeTokenCommand which primes the cache");
    }

    // ------------------------------------------------------------------
    // [DisableConcurrentExecution] presence — guards against accidental removal.
    // ------------------------------------------------------------------

    [Fact]
    public void Run_HasDisableConcurrentExecutionAttribute()
    {
        var method = typeof(TokenRotationJob).GetMethod(nameof(TokenRotationJob.Run))!;
        var attr = method.GetCustomAttributes(typeof(DisableConcurrentExecutionAttribute), inherit: false);
        attr.Should().NotBeEmpty(
            "two Hangfire workers must not race on the same daily rotation pass — the attribute is the lock");
    }
}
