using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Source.Features.Conversations.Events;
using Source.Features.Conversations.Jobs;
using Source.Features.Conversations.Models;
using Source.Features.RuntimeLifecycle.Events;
using Source.Features.RuntimeLifecycle.Models;
using Source.Infrastructure;
using Source.Infrastructure.Interceptors;

namespace Api.Tests.Features.Conversations.Jobs;

/// <summary>
/// Unit tests for <see cref="OrphanSessionJanitorJob"/>. The bootstrap mirrors
/// <see cref="Api.Tests.Features.RuntimeLifecycle.HeartbeatWatcherJobTests"/>:
/// a wired <see cref="ApplicationDbContext"/> with the
/// <see cref="DomainEventInterceptor"/> + MediatR + SignalR + a stub
/// <see cref="IBackgroundJobClient"/> registered, so any auto-discovered
/// notification handlers can be activated by DI when the
/// <see cref="AgentSessionTerminated"/> event fires from
/// <see cref="AgentSession.Fail(string?)"/>.
///
/// <para>Tests target <see cref="OrphanSessionJanitorJob.Run"/> directly with
/// <see cref="CancellationToken.None"/> — there is no inner sleep loop, so the
/// public Hangfire entry point is the same code path tests should exercise.</para>
/// </summary>
public class OrphanSessionJanitorJobTests : IDisposable
{
    private readonly ServiceProvider _provider;
    private readonly ApplicationDbContext _db;

    public OrphanSessionJanitorJobTests()
    {
        var dbName = Guid.NewGuid().ToString();
        var services = new ServiceCollection();

        services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));
        services.AddHttpContextAccessor();

        // SignalR services satisfy any auto-discovered broadcast handlers that
        // depend on IHubContext<...>. The hub never fires in tests (no connected
        // clients) but DI must be able to construct the handler.
        services.AddSignalR();

        services.AddMediatR(cfg =>
        {
            // Pull both event-source assemblies so the AgentSessionTerminated
            // notification fans out to whichever handlers are registered for it
            // and the cross-feature RuntimeStateChanged graph stays consistent.
            cfg.RegisterServicesFromAssembly(typeof(AgentSessionTerminated).Assembly);
            cfg.RegisterServicesFromAssembly(typeof(RuntimeStateChanged).Assembly);
        });

        // Some auto-discovered handlers (e.g. ScheduleRespawnHandler) depend on
        // IBackgroundJobClient. The janitor itself never schedules anything,
        // but DI must be able to construct co-resident handlers.
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
    }

    public void Dispose()
    {
        _provider.Dispose();
        GC.SuppressFinalize(this);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private OrphanSessionJanitorJob CreateJob() =>
        new(_db, NullLogger<OrphanSessionJanitorJob>.Instance);

    /// <summary>
    /// Seed a runtime in the requested state. Provisions only the columns the
    /// janitor actually queries; everything else stays at its property
    /// initializer default.
    /// </summary>
    private async Task<ProjectRuntime> SeedRuntimeAsync(RuntimeState state)
    {
        var runtime = new ProjectRuntime
        {
            ProjectId = Guid.NewGuid(),
            Region = "arn",
            VolumeSizeGb = 1,
            State = state,
            FlyMachineId = "mach_" + Guid.NewGuid().ToString("N")[..8],
        };
        _db.ProjectRuntimes.Add(runtime);
        await _db.SaveChangesAsync();
        return runtime;
    }

    /// <summary>
    /// Seed a conversation tied to the same project as the runtime. The
    /// AgentSession FK to Conversation is required.
    /// </summary>
    private async Task<Conversation> SeedConversationAsync(Guid projectId)
    {
        var conversation = new Conversation
        {
            ProjectId = projectId,
            Title = "test",
            BranchId = Guid.NewGuid(),
            Status = ConversationStatus.Active,
            LastActivityAt = DateTime.UtcNow,
        };
        _db.Conversations.Add(conversation);
        await _db.SaveChangesAsync();
        return conversation;
    }

    private async Task<AgentSession> SeedSessionAsync(
        Guid conversationId,
        Guid runtimeId,
        AgentSessionStatus status,
        string prompt = "seeded")
    {
        var session = new AgentSession
        {
            ConversationId = conversationId,
            RuntimeId = runtimeId,
            Prompt = prompt,
            Status = status,
            CompletedAt = status is AgentSessionStatus.Succeeded
                or AgentSessionStatus.Failed
                or AgentSessionStatus.Canceled
                ? DateTime.UtcNow
                : null,
        };
        _db.AgentSessions.Add(session);
        await _db.SaveChangesAsync();
        return session;
    }

    // ------------------------------------------------------------------
    // Tests
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(RuntimeState.Crashed)]
    [InlineData(RuntimeState.Failed)]
    [InlineData(RuntimeState.Suspended)]
    [InlineData(RuntimeState.Suspending)]
    [InlineData(RuntimeState.Deleting)]
    [InlineData(RuntimeState.Deleted)]
    public async Task Run_RunningSessionOnUnavailableRuntime_TransitionsToFailed(RuntimeState unavailableState)
    {
        // For each state where the daemon will never close the loop, a Running
        // session must be reaped to Failed with reason="runtime_unavailable".
        var runtime = await SeedRuntimeAsync(unavailableState);
        var conversation = await SeedConversationAsync(runtime.ProjectId);
        var session = await SeedSessionAsync(conversation.Id, runtime.Id, AgentSessionStatus.Running);

        await CreateJob().Run(CancellationToken.None);

        var refreshed = await _db.AgentSessions.AsNoTracking().SingleAsync(s => s.Id == session.Id);
        refreshed.Status.Should().Be(AgentSessionStatus.Failed,
            $"Running sessions on a {unavailableState} runtime are orphans and must be reaped");
        refreshed.FailureReason.Should().Be("runtime_unavailable");
        refreshed.CompletedAt.Should().NotBeNull(
            "Fail() must stamp CompletedAt so the chat UI can render terminal state");
        refreshed.QueuePosition.Should().BeNull();
    }

    [Fact]
    public async Task Run_CancelingSessionOnCrashedRuntime_TransitionsToFailed()
    {
        // Canceling is also in-flight as far as the runtime is concerned —
        // the daemon was draining the turn but it's now dead, so the session
        // will never see turn_canceled. Reap it.
        var runtime = await SeedRuntimeAsync(RuntimeState.Crashed);
        var conversation = await SeedConversationAsync(runtime.ProjectId);
        var session = await SeedSessionAsync(conversation.Id, runtime.Id, AgentSessionStatus.Canceling);

        await CreateJob().Run(CancellationToken.None);

        var refreshed = await _db.AgentSessions.AsNoTracking().SingleAsync(s => s.Id == session.Id);
        refreshed.Status.Should().Be(AgentSessionStatus.Failed,
            "a Canceling session on a Crashed runtime is just as orphaned as a Running one");
        refreshed.FailureReason.Should().Be("runtime_unavailable");
        refreshed.CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Run_RunningSessionOnOnlineRuntime_NotTouched()
    {
        // The healthy path: the daemon will close the loop normally. Janitor
        // must never interfere with sessions on Online runtimes.
        var runtime = await SeedRuntimeAsync(RuntimeState.Online);
        var conversation = await SeedConversationAsync(runtime.ProjectId);
        var session = await SeedSessionAsync(conversation.Id, runtime.Id, AgentSessionStatus.Running);

        await CreateJob().Run(CancellationToken.None);

        var refreshed = await _db.AgentSessions.AsNoTracking().SingleAsync(s => s.Id == session.Id);
        refreshed.Status.Should().Be(AgentSessionStatus.Running,
            "an Online runtime can still drive its session to a real terminal — hands off");
        refreshed.FailureReason.Should().BeNull();
        refreshed.CompletedAt.Should().BeNull();
    }

    [Theory]
    [InlineData(RuntimeState.Booting)]
    [InlineData(RuntimeState.Bootstrapping)]
    [InlineData(RuntimeState.Waking)]
    public async Task Run_RunningSessionOnRecoverableRuntime_NotTouched(RuntimeState recoverableState)
    {
        // Booting / Bootstrapping / Waking are mid-transition states the
        // daemon will recover from. A session in Running against them is
        // genuinely waiting, not orphaned.
        var runtime = await SeedRuntimeAsync(recoverableState);
        var conversation = await SeedConversationAsync(runtime.ProjectId);
        var session = await SeedSessionAsync(conversation.Id, runtime.Id, AgentSessionStatus.Running);

        await CreateJob().Run(CancellationToken.None);

        var refreshed = await _db.AgentSessions.AsNoTracking().SingleAsync(s => s.Id == session.Id);
        refreshed.Status.Should().Be(AgentSessionStatus.Running,
            $"{recoverableState} is recoverable — sessions on it are not orphans");
    }

    [Fact]
    public async Task Run_AlreadyFailedSessionOnCrashedRuntime_NotTouched()
    {
        // Idempotency: a session that's already terminal must not be
        // re-Failed (would double-raise AgentSessionTerminated, polluting the
        // dispatch chain).
        var runtime = await SeedRuntimeAsync(RuntimeState.Crashed);
        var conversation = await SeedConversationAsync(runtime.ProjectId);
        var session = await SeedSessionAsync(conversation.Id, runtime.Id, AgentSessionStatus.Failed);

        var originalCompletedAt = session.CompletedAt;

        await CreateJob().Run(CancellationToken.None);

        var refreshed = await _db.AgentSessions.AsNoTracking().SingleAsync(s => s.Id == session.Id);
        refreshed.Status.Should().Be(AgentSessionStatus.Failed,
            "already-Failed sessions are filtered out by the Status predicate — no re-touch");
        refreshed.CompletedAt.Should().Be(originalCompletedAt,
            "an idempotent re-run must not bump the completion timestamp");
    }

    [Theory]
    [InlineData(AgentSessionStatus.Succeeded)]
    [InlineData(AgentSessionStatus.Canceled)]
    [InlineData(AgentSessionStatus.Pending)]
    public async Task Run_NonOrphanStatusOnCrashedRuntime_NotTouched(AgentSessionStatus status)
    {
        // Pending: not yet dispatched — the dispatch path will fail naturally
        //          if the runtime is dead; not the janitor's concern.
        // Succeeded / Canceled: terminal — already accounted for.
        var runtime = await SeedRuntimeAsync(RuntimeState.Crashed);
        var conversation = await SeedConversationAsync(runtime.ProjectId);
        var session = await SeedSessionAsync(conversation.Id, runtime.Id, status);

        await CreateJob().Run(CancellationToken.None);

        var refreshed = await _db.AgentSessions.AsNoTracking().SingleAsync(s => s.Id == session.Id);
        refreshed.Status.Should().Be(status,
            $"sessions in {status} are out of scope for the orphan janitor");
    }

    [Fact]
    public async Task Run_LargeBatchOfOrphans_AllProcessed()
    {
        // Orphan volume can spike during an outage. Verify batching works:
        // 100 Running sessions on one Crashed runtime, all reaped in a single
        // Run() call (multiple internal batches of 50).
        var runtime = await SeedRuntimeAsync(RuntimeState.Crashed);
        var conversation = await SeedConversationAsync(runtime.ProjectId);

        for (int i = 0; i < 100; i++)
        {
            await SeedSessionAsync(conversation.Id, runtime.Id, AgentSessionStatus.Running, prompt: $"orphan-{i}");
        }

        await CreateJob().Run(CancellationToken.None);

        var refreshed = await _db.AgentSessions.AsNoTracking()
            .Where(s => s.RuntimeId == runtime.Id)
            .ToListAsync();
        refreshed.Should().HaveCount(100);
        refreshed.Should().OnlyContain(s => s.Status == AgentSessionStatus.Failed,
            "every Running session on the Crashed runtime must be reaped");
        refreshed.Should().OnlyContain(s => s.FailureReason == "runtime_unavailable");
    }

    [Fact]
    public async Task Run_NoOrphans_NoOpAndNoMutation()
    {
        // Empty-table or all-healthy → the job must be a clean no-op.
        // Includes an Online runtime with a Running session and a Crashed
        // runtime with only Pending / Succeeded sessions (none orphans).
        var online = await SeedRuntimeAsync(RuntimeState.Online);
        var crashed = await SeedRuntimeAsync(RuntimeState.Crashed);
        var conversationOnline = await SeedConversationAsync(online.ProjectId);
        var conversationCrashed = await SeedConversationAsync(crashed.ProjectId);

        var healthyRunning = await SeedSessionAsync(conversationOnline.Id, online.Id, AgentSessionStatus.Running);
        var preDispatch = await SeedSessionAsync(conversationCrashed.Id, crashed.Id, AgentSessionStatus.Pending);
        var alreadyDone = await SeedSessionAsync(conversationCrashed.Id, crashed.Id, AgentSessionStatus.Succeeded);

        var act = async () => await CreateJob().Run(CancellationToken.None);
        await act.Should().NotThrowAsync();

        // No statuses moved.
        (await _db.AgentSessions.AsNoTracking().SingleAsync(s => s.Id == healthyRunning.Id))
            .Status.Should().Be(AgentSessionStatus.Running);
        (await _db.AgentSessions.AsNoTracking().SingleAsync(s => s.Id == preDispatch.Id))
            .Status.Should().Be(AgentSessionStatus.Pending);
        (await _db.AgentSessions.AsNoTracking().SingleAsync(s => s.Id == alreadyDone.Id))
            .Status.Should().Be(AgentSessionStatus.Succeeded);
    }

    [Fact]
    public async Task Run_MixedScenario_OnlyOrphansFlipped()
    {
        // The realistic case: one Crashed runtime with one orphan Running and
        // one already-Failed; one Online runtime with a Running session that
        // must stay Running. Janitor flips exactly one row.
        var crashed = await SeedRuntimeAsync(RuntimeState.Crashed);
        var online = await SeedRuntimeAsync(RuntimeState.Online);
        var conversationCrashed = await SeedConversationAsync(crashed.ProjectId);
        var conversationOnline = await SeedConversationAsync(online.ProjectId);

        var orphan = await SeedSessionAsync(conversationCrashed.Id, crashed.Id, AgentSessionStatus.Running);
        var alreadyTerminal = await SeedSessionAsync(conversationCrashed.Id, crashed.Id, AgentSessionStatus.Failed);
        var healthy = await SeedSessionAsync(conversationOnline.Id, online.Id, AgentSessionStatus.Running);

        await CreateJob().Run(CancellationToken.None);

        (await _db.AgentSessions.AsNoTracking().SingleAsync(s => s.Id == orphan.Id))
            .Status.Should().Be(AgentSessionStatus.Failed, "the orphan must be reaped");
        (await _db.AgentSessions.AsNoTracking().SingleAsync(s => s.Id == alreadyTerminal.Id))
            .Status.Should().Be(AgentSessionStatus.Failed, "already terminal — untouched");
        (await _db.AgentSessions.AsNoTracking().SingleAsync(s => s.Id == healthy.Id))
            .Status.Should().Be(AgentSessionStatus.Running, "different runtime is fine — untouched");
    }

    [Fact]
    public async Task Run_OrphanedSession_RaisesAgentSessionTerminatedEvent()
    {
        // Domain-event smoke test: Fail() must raise AgentSessionTerminated,
        // and the DomainEventInterceptor must persist it to the event store.
        var runtime = await SeedRuntimeAsync(RuntimeState.Crashed);
        var conversation = await SeedConversationAsync(runtime.ProjectId);
        var session = await SeedSessionAsync(conversation.Id, runtime.Id, AgentSessionStatus.Running);

        await CreateJob().Run(CancellationToken.None);

        // The interceptor writes raised IDomainEvents into StoredDomainEvents.
        var stored = await _db.StoredDomainEvents.AsNoTracking()
            .Where(e => e.EntityId == session.Id.ToString()
                     && e.EntityType == nameof(AgentSession)
                     && e.EventType == nameof(AgentSessionTerminated))
            .ToListAsync();
        stored.Should().HaveCount(1,
            "exactly one AgentSessionTerminated must be raised when the janitor reaps a session");
    }

    // ------------------------------------------------------------------
    // [DisableConcurrentExecution] presence — guards against accidental removal.
    // ------------------------------------------------------------------

    [Fact]
    public void Run_HasDisableConcurrentExecutionAttribute()
    {
        var method = typeof(OrphanSessionJanitorJob).GetMethod(nameof(OrphanSessionJanitorJob.Run), new[] { typeof(Hangfire.IJobCancellationToken) })!;
        var attr = method.GetCustomAttributes(typeof(DisableConcurrentExecutionAttribute), inherit: false);
        attr.Should().NotBeEmpty(
            "two Hangfire workers must not race on the same orphan-scan minute — the attribute is the lock");
    }
}
