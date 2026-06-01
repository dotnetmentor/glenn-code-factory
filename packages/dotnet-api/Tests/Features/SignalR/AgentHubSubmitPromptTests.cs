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
using Source.Features.Projects.Models;
using Source.Features.RuntimeLifecycle.Models;
using Source.Features.SignalR.Contracts;
using Source.Features.SignalR.Hubs;
using Source.Infrastructure;
using Source.Infrastructure.Interceptors;

namespace Api.Tests.Features.SignalR;

/// <summary>
/// Unit tests for <see cref="AgentHub.SubmitPrompt"/> — the user-facing entry
/// point for "I typed a prompt; run a turn." Bootstrap mirrors
/// <see cref="RuntimeHubEmitEventTests"/>: real <see cref="ApplicationDbContext"/>
/// on InMemory + the <see cref="DomainEventInterceptor"/> + MediatR (so the
/// Saved → Publish chain runs end-to-end), with the SignalR primitives stamped
/// in via a <see cref="FakeHubCallerContext"/>.
///
/// <para>The cross-hub <see cref="IHubContext{THub, T}"/> for the runtime side
/// is mocked end-to-end so we can assert the dispatched
/// <see cref="StartTurnPayload"/> is correct without spinning up a real
/// <see cref="RuntimeHub"/>.</para>
/// </summary>
public class AgentHubSubmitPromptTests : IDisposable
{
    private const string TestUserId = "user-123";

    private readonly ServiceProvider _provider;
    private readonly ApplicationDbContext _db;
    private readonly TestEventSink _eventSink;

    // Cross-hub dispatch — mocked end-to-end. Tests that care assert against
    // _runtimeGroupClient (the receiver-typed proxy).
    private readonly Mock<IHubClients<IRuntimeClient>> _runtimeClients = new();
    private readonly Mock<IHubContext<RuntimeHub, IRuntimeClient>> _runtimeHub = new();
    private readonly Mock<IRuntimeClient> _runtimeGroupClient = new();

    public AgentHubSubmitPromptTests()
    {
        var dbName = Guid.NewGuid().ToString();
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddHttpContextAccessor();

        // SignalR is needed because the auto-discovered
        // BroadcastAgentEventHandler depends on IHubContext<AgentHub, IAgentClient>
        // — when the interceptor publishes AgentEventEmitted that handler is
        // resolved from this provider and would fail without the hub services.
        services.AddSignalR();

        // MediatR for handler discovery + the Saved → Publish path. The hub
        // takes its own IHubContext we pass directly; the broadcast handler
        // resolves the AgentHub IHubContext from this provider (no-op in tests
        // because no clients have joined the hub).
        services.AddMediatR(cfg =>
            cfg.RegisterServicesFromAssembly(typeof(AgentEventEmitted).Assembly));

        services.AddSingleton<IBackgroundJobClient>(new Mock<IBackgroundJobClient>().Object);

        _eventSink = new TestEventSink();
        services.AddSingleton(_eventSink);
        services.AddSingleton<INotificationHandler<AgentEventEmitted>>(_eventSink);

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
        _runtimeClients.Setup(c => c.Group(It.IsAny<string>())).Returns(_runtimeGroupClient.Object);
        _runtimeGroupClient
            .Setup(c => c.StartTurn(It.IsAny<StartTurnPayload>()))
            .Returns(Task.CompletedTask);
    }

    public void Dispose()
    {
        _provider.Dispose();
        GC.SuppressFinalize(this);
    }

    // ------------------------------------------------------------------
    // Conversation handling
    // ------------------------------------------------------------------

    [Fact]
    public async Task SubmitPrompt_NewConversation_CreatesConversationAndSession()
    {
        var projectId = Guid.NewGuid();
        var runtime = await SeedOnlineRuntime(projectId);

        var harness = BuildHarness();

        var branchId = Guid.NewGuid();
        var before = DateTime.UtcNow;
        var response = await harness.Hub.SubmitPrompt(new SubmitPromptPayload(
            ProjectId: projectId,
            ConversationId: null,
            BranchId: branchId,
            Text: "hello world"));
        var after = DateTime.UtcNow;

        // Conversation row created.
        var conversations = await _db.Conversations.ToListAsync();
        conversations.Should().HaveCount(1);
        var conversation = conversations.Single();
        conversation.ProjectId.Should().Be(projectId);
        conversation.BranchId.Should().Be(branchId);
        conversation.Title.Should().Be("hello world", "title is the prompt when ≤ 80 chars");
        conversation.Status.Should().Be(ConversationStatus.Active);
        conversation.LastActivityAt.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
        conversation.EventCount.Should().Be(1, "PromptReceived increments the counter");

        // Session row created. With Card 2's queueing, an idle runtime →
        // Dispatch() flips the session to Running before SaveChanges, so we
        // observe the post-dispatch state here. QueuePosition stays null
        // because nothing else was running.
        var sessions = await _db.AgentSessions.ToListAsync();
        sessions.Should().HaveCount(1);
        var session = sessions.Single();
        session.ConversationId.Should().Be(conversation.Id);
        session.RuntimeId.Should().Be(runtime.Id);
        session.Prompt.Should().Be("hello world");
        session.Status.Should().Be(AgentSessionStatus.Running);
        session.QueuePosition.Should().BeNull();

        // PromptReceived event row.
        var events = await _db.AgentEvents.ToListAsync();
        events.Should().HaveCount(1);
        var promptEvent = events.Single();
        promptEvent.SessionId.Should().Be(session.Id);
        promptEvent.Sequence.Should().Be(0);
        promptEvent.Kind.Should().Be(AgentEventKind.PromptReceived);
        promptEvent.Text.Should().Be("hello world", "Cursor-native schema: prompt body on first-class Text column");

        // Response carries the new ids.
        response.ConversationId.Should().Be(conversation.Id);
        response.SessionId.Should().Be(session.Id);

        // Sanity: runtime not mutated.
        var reloadedRuntime = await _db.ProjectRuntimes.SingleAsync(r => r.Id == runtime.Id);
        reloadedRuntime.State.Should().Be(RuntimeState.Online);
    }

    [Fact]
    public async Task SubmitPrompt_ExistingConversation_ReusesIt()
    {
        var projectId = Guid.NewGuid();
        await SeedOnlineRuntime(projectId);
        var existing = await SeedConversation(projectId, eventCount: 4);

        var harness = BuildHarness();

        var response = await harness.Hub.SubmitPrompt(new SubmitPromptPayload(
            ProjectId: projectId,
            ConversationId: existing.Id,
            BranchId: Guid.NewGuid(),
            Text: "follow-up"));

        // No new conversation row.
        (await _db.Conversations.CountAsync()).Should().Be(1);

        // Session attached to the existing conversation.
        var sessions = await _db.AgentSessions.ToListAsync();
        sessions.Should().HaveCount(1);
        sessions.Single().ConversationId.Should().Be(existing.Id);

        // PromptReceived event inserted.
        (await _db.AgentEvents.CountAsync()).Should().Be(1);

        // Counter incremented from the seeded value.
        var reloaded = await _db.Conversations.SingleAsync(c => c.Id == existing.Id);
        reloaded.EventCount.Should().Be(5, "EventCount increments on each prompt");

        response.ConversationId.Should().Be(existing.Id);
        response.SessionId.Should().Be(sessions.Single().Id);
    }

    // ------------------------------------------------------------------
    // Title derivation
    // ------------------------------------------------------------------

    [Fact]
    public async Task SubmitPrompt_LongPrompt_TitleTruncated()
    {
        var projectId = Guid.NewGuid();
        await SeedOnlineRuntime(projectId);

        var prompt = new string('a', 200);
        var harness = BuildHarness();

        await harness.Hub.SubmitPrompt(new SubmitPromptPayload(
            ProjectId: projectId,
            ConversationId: null,
            BranchId: Guid.NewGuid(),
            Text: prompt));

        var conversation = await _db.Conversations.SingleAsync();
        conversation.Title.Should().Be(new string('a', 77) + "...");
        conversation.Title.Length.Should().Be(80);
    }

    [Fact]
    public async Task SubmitPrompt_PromptUnder80Chars_TitleIsFullPrompt()
    {
        var projectId = Guid.NewGuid();
        await SeedOnlineRuntime(projectId);

        var prompt = new string('x', 30);
        var harness = BuildHarness();

        await harness.Hub.SubmitPrompt(new SubmitPromptPayload(
            ProjectId: projectId,
            ConversationId: null,
            BranchId: Guid.NewGuid(),
            Text: prompt));

        var conversation = await _db.Conversations.SingleAsync();
        conversation.Title.Should().Be(prompt);
        conversation.Title.Length.Should().Be(30);
    }

    // ------------------------------------------------------------------
    // AgentId resume
    // ------------------------------------------------------------------

    [Fact]
    public async Task SubmitPrompt_ResumesAgentId_FromMostRecentSucceededSession()
    {
        var projectId = Guid.NewGuid();
        await SeedOnlineRuntime(projectId);
        var conversation = await SeedConversation(projectId);

        // Older Succeeded with claude-A.
        var oldSucceeded = new AgentSession
        {
            ConversationId = conversation.Id,
            Prompt = "first",
            Status = AgentSessionStatus.Succeeded,
            AgentId = "claude-A",
            CreatedAt = DateTime.UtcNow.AddMinutes(-10),
        };
        _db.AgentSessions.Add(oldSucceeded);

        // Newer Failed with claude-B — must NOT be picked.
        var failed = new AgentSession
        {
            ConversationId = conversation.Id,
            Prompt = "second",
            Status = AgentSessionStatus.Failed,
            AgentId = "claude-B",
            CreatedAt = DateTime.UtcNow.AddMinutes(-1),
        };
        _db.AgentSessions.Add(failed);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        StartTurnPayload? dispatched = null;
        _runtimeGroupClient
            .Setup(c => c.StartTurn(It.IsAny<StartTurnPayload>()))
            .Callback<StartTurnPayload>(p => dispatched = p)
            .Returns(Task.CompletedTask);

        var harness = BuildHarness();
        var response = await harness.Hub.SubmitPrompt(new SubmitPromptPayload(
            ProjectId: projectId,
            ConversationId: conversation.Id,
            BranchId: Guid.NewGuid(),
            Text: "next"));

        var newSession = await _db.AgentSessions.SingleAsync(s => s.Id == response.SessionId);
        newSession.AgentId.Should().Be("claude-A",
            "resume hint comes from the most recent Succeeded session, not the most recent overall");

        dispatched.Should().NotBeNull();
        dispatched!.AgentId.Should().Be("claude-A");
    }

    [Fact]
    public async Task SubmitPrompt_NoPriorSucceededSession_AgentIdIsNull()
    {
        var projectId = Guid.NewGuid();
        await SeedOnlineRuntime(projectId);
        var conversation = await SeedConversation(projectId);

        var failed = new AgentSession
        {
            ConversationId = conversation.Id,
            Prompt = "first",
            Status = AgentSessionStatus.Failed,
            AgentId = "claude-X",
        };
        _db.AgentSessions.Add(failed);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var harness = BuildHarness();
        var response = await harness.Hub.SubmitPrompt(new SubmitPromptPayload(
            ProjectId: projectId,
            ConversationId: conversation.Id,
            BranchId: Guid.NewGuid(),
            Text: "next"));

        var newSession = await _db.AgentSessions.SingleAsync(s => s.Id == response.SessionId);
        newSession.AgentId.Should().BeNull(
            "no Succeeded prior session — resume hint must be null");
    }

    [Fact]
    public async Task SubmitPrompt_FirstPromptOnNewConversation_NoAgentId()
    {
        var projectId = Guid.NewGuid();
        await SeedOnlineRuntime(projectId);

        var harness = BuildHarness();
        var response = await harness.Hub.SubmitPrompt(new SubmitPromptPayload(
            ProjectId: projectId,
            ConversationId: null,
            BranchId: Guid.NewGuid(),
            Text: "brand new"));

        var session = await _db.AgentSessions.SingleAsync(s => s.Id == response.SessionId);
        session.AgentId.Should().BeNull("a brand-new conversation has no resume hint");
    }

    // ------------------------------------------------------------------
    // Runtime gating
    // ------------------------------------------------------------------

    [Fact]
    public async Task SubmitPrompt_NoRuntimeForProject_ThrowsHubException()
    {
        var projectId = Guid.NewGuid();
        // Deliberately no runtime seeded.

        var harness = BuildHarness();

        var act = async () => await harness.Hub.SubmitPrompt(new SubmitPromptPayload(
            ProjectId: projectId,
            ConversationId: null,
            BranchId: Guid.NewGuid(),
            Text: "hi"));

        var ex = await act.Should().ThrowAsync<HubException>();
        ex.Which.Message.Should().Contain("No runtime");

        // Sanity: nothing persisted.
        (await _db.Conversations.AnyAsync()).Should().BeFalse();
        (await _db.AgentSessions.AnyAsync()).Should().BeFalse();
        (await _db.AgentEvents.AnyAsync()).Should().BeFalse();
    }

    [Theory]
    [InlineData(RuntimeState.Pending)]
    [InlineData(RuntimeState.Booting)]
    [InlineData(RuntimeState.Bootstrapping)]
    [InlineData(RuntimeState.Suspending)]
    [InlineData(RuntimeState.Suspended)]
    [InlineData(RuntimeState.Waking)]
    public async Task SubmitPrompt_QueueableNotOnlineState_QueuesInsteadOfThrowing(RuntimeState state)
    {
        // P1.6: not-Online runtimes that have a future Online transition coming
        // (Pending / Booting / Bootstrapping / Suspending / Suspended / Waking)
        // must persist the prompt as a Pending+queued AgentSession instead of
        // throwing. The DispatchQueuedSessionsOnRuntimeOnlineHandler drains the
        // queue once the runtime reaches Online.
        var projectId = Guid.NewGuid();
        await SeedRuntime(projectId, state);

        var harness = BuildHarness();

        var response = await harness.Hub.SubmitPrompt(new SubmitPromptPayload(
            ProjectId: projectId,
            ConversationId: null,
            BranchId: Guid.NewGuid(),
            Text: "hi"));

        response.Queued.Should().BeTrue("the runtime isn't Online yet so the prompt is soft-queued");
        response.QueuePosition.Should().Be(1, "first queued session on an empty queue is position 1");

        var session = await _db.AgentSessions.SingleAsync();
        session.Status.Should().Be(AgentSessionStatus.Pending);
        session.QueuePosition.Should().Be(1);
        session.StartedAt.Should().BeNull("queued sessions haven't started yet");

        // Conversation + PromptReceived audit row still persist — the user's
        // prompt isn't lost while we wait for Online.
        (await _db.Conversations.CountAsync()).Should().Be(1);
        (await _db.AgentEvents.CountAsync()).Should().Be(1);

        // StartTurn was NOT dispatched to the runtime group — the daemon isn't
        // reachable yet and the queue-drain handler will fan out the StartTurn
        // when Online lands.
        _runtimeGroupClient.Verify(c => c.StartTurn(It.IsAny<StartTurnPayload>()), Times.Never);
    }

    [Theory]
    [InlineData(RuntimeState.Failed)]
    [InlineData(RuntimeState.Crashed)]
    [InlineData(RuntimeState.Deleting)]
    public async Task SubmitPrompt_NonQueueableNotOnlineState_StillThrows(RuntimeState state)
    {
        // P1.6: states with no path back to Online (Failed / Crashed /
        // Deleting / Deleted) still reject the prompt — there's no future
        // transition to drain the queue against.
        var projectId = Guid.NewGuid();
        await SeedRuntime(projectId, state);

        var harness = BuildHarness();

        var act = async () => await harness.Hub.SubmitPrompt(new SubmitPromptPayload(
            ProjectId: projectId,
            ConversationId: null,
            BranchId: Guid.NewGuid(),
            Text: "hi"));

        var ex = await act.Should().ThrowAsync<HubException>();
        ex.Which.Message.Should().Contain("must be Online");

        (await _db.AgentSessions.AnyAsync()).Should().BeFalse();
    }

    // ------------------------------------------------------------------
    // Cross-hub dispatch
    // ------------------------------------------------------------------

    [Fact]
    public async Task SubmitPrompt_DispatchesStartTurnToCorrectRuntimeGroup()
    {
        var projectId = Guid.NewGuid();
        var runtime = await SeedOnlineRuntime(projectId);

        StartTurnPayload? dispatched = null;
        _runtimeGroupClient
            .Setup(c => c.StartTurn(It.IsAny<StartTurnPayload>()))
            .Callback<StartTurnPayload>(p => dispatched = p)
            .Returns(Task.CompletedTask);

        var harness = BuildHarness();
        var response = await harness.Hub.SubmitPrompt(new SubmitPromptPayload(
            ProjectId: projectId,
            ConversationId: null,
            BranchId: Guid.NewGuid(),
            Text: "go"));

        // Dispatched once to the runtime-{runtime.Id} group.
        _runtimeClients.Verify(c => c.Group($"runtime-{runtime.Id}"), Times.Once);
        _runtimeClients.Verify(
            c => c.Group(It.Is<string>(s => s != $"runtime-{runtime.Id}")),
            Times.Never);
        _runtimeGroupClient.Verify(c => c.StartTurn(It.IsAny<StartTurnPayload>()), Times.Once);

        // Payload matches the freshly-created session.
        dispatched.Should().NotBeNull();
        dispatched!.SessionId.Should().Be(response.SessionId);
        dispatched.ConversationId.Should().Be(response.ConversationId);
        dispatched.Prompt.Should().Be("go");
        dispatched.AgentId.Should().BeNull();
    }

    // ------------------------------------------------------------------
    // Validation
    // ------------------------------------------------------------------

    [Fact]
    public async Task SubmitPrompt_EmptyText_ThrowsHubException()
    {
        var projectId = Guid.NewGuid();
        await SeedOnlineRuntime(projectId);

        var harness = BuildHarness();
        var act = async () => await harness.Hub.SubmitPrompt(new SubmitPromptPayload(
            ProjectId: projectId,
            ConversationId: null,
            BranchId: Guid.NewGuid(),
            Text: ""));

        var ex = await act.Should().ThrowAsync<HubException>();
        ex.Which.Message.Should().Contain("cannot be empty");
    }

    [Fact]
    public async Task SubmitPrompt_TextTooLong_ThrowsHubException()
    {
        var projectId = Guid.NewGuid();
        await SeedOnlineRuntime(projectId);

        var huge = new string('a', 60_000);
        var harness = BuildHarness();

        var act = async () => await harness.Hub.SubmitPrompt(new SubmitPromptPayload(
            ProjectId: projectId,
            ConversationId: null,
            BranchId: Guid.NewGuid(),
            Text: huge));

        var ex = await act.Should().ThrowAsync<HubException>();
        ex.Which.Message.Should().Contain("too long");
    }

    [Fact]
    public async Task SubmitPrompt_ConversationFromDifferentProject_ThrowsHubException()
    {
        var projectIdA = Guid.NewGuid();
        var projectIdB = Guid.NewGuid();
        await SeedOnlineRuntime(projectIdA);
        // Conversation belongs to project B, not A.
        var conversation = await SeedConversation(projectIdB);

        var harness = BuildHarness();
        var act = async () => await harness.Hub.SubmitPrompt(new SubmitPromptPayload(
            ProjectId: projectIdA,
            ConversationId: conversation.Id,
            BranchId: Guid.NewGuid(),
            Text: "cross-project attempt"));

        var ex = await act.Should().ThrowAsync<HubException>();
        ex.Which.Message.Should().Contain("does not belong");
    }

    [Fact]
    public async Task SubmitPrompt_UnknownConversationId_ThrowsHubException()
    {
        var projectId = Guid.NewGuid();
        await SeedOnlineRuntime(projectId);

        var harness = BuildHarness();
        var act = async () => await harness.Hub.SubmitPrompt(new SubmitPromptPayload(
            ProjectId: projectId,
            ConversationId: Guid.NewGuid(),
            BranchId: Guid.NewGuid(),
            Text: "hi"));

        var ex = await act.Should().ThrowAsync<HubException>();
        ex.Which.Message.Should().Contain("Conversation not found");
    }

    // ------------------------------------------------------------------
    // Domain event publish
    // ------------------------------------------------------------------

    [Fact]
    public async Task SubmitPrompt_RaisesAgentEventEmittedDomainEvent()
    {
        var projectId = Guid.NewGuid();
        await SeedOnlineRuntime(projectId);

        _eventSink.Captured.Clear();

        var harness = BuildHarness();
        var response = await harness.Hub.SubmitPrompt(new SubmitPromptPayload(
            ProjectId: projectId,
            ConversationId: null,
            BranchId: Guid.NewGuid(),
            Text: "first"));

        _eventSink.Captured.Should().HaveCount(1,
            "every SubmitPrompt must raise an AgentEventEmitted for the PromptReceived row");

        var captured = _eventSink.Captured.Single();
        captured.SessionId.Should().Be(response.SessionId);
        captured.ConversationId.Should().Be(response.ConversationId);
        captured.ProjectId.Should().Be(projectId);
        captured.Sequence.Should().Be(0);
        captured.Kind.Should().Be(AgentEventKind.PromptReceived);
        // Card 2 (cursor-native schema): the prompt text now lives on the
        // first-class AgentEvent.Text column, not in an opaque EventData blob
        // on the domain event. Assertion below loads the row and inspects Text.
        var promptRow = await _db.AgentEvents.SingleAsync(e =>
            e.SessionId == captured.SessionId && e.Sequence == 0);
        promptRow.Text.Should().Contain("first");
    }

    // ------------------------------------------------------------------
    // Per-runtime queueing (Card 2 of agent-execution-control)
    // ------------------------------------------------------------------

    [Fact]
    public async Task SubmitPrompt_NoActiveSession_DispatchesImmediately()
    {
        // Idle runtime → first prompt dispatches without queueing.
        var projectId = Guid.NewGuid();
        var runtime = await SeedOnlineRuntime(projectId);

        var harness = BuildHarness();
        var response = await harness.Hub.SubmitPrompt(new SubmitPromptPayload(
            ProjectId: projectId,
            ConversationId: null,
            BranchId: Guid.NewGuid(),
            Text: "first"));

        response.Queued.Should().BeFalse("nothing else was running on the runtime");
        response.QueuePosition.Should().BeNull();

        // Session is Running with no QueuePosition.
        var session = await _db.AgentSessions.SingleAsync(s => s.Id == response.SessionId);
        session.Status.Should().Be(AgentSessionStatus.Running);
        session.QueuePosition.Should().BeNull();
        session.RuntimeId.Should().Be(runtime.Id);

        // StartTurn was dispatched to the runtime group exactly once.
        _runtimeGroupClient.Verify(c => c.StartTurn(It.IsAny<StartTurnPayload>()), Times.Once);
    }

    [Fact]
    public async Task SubmitPrompt_ActiveSessionRunning_QueuesNewPromptAndSkipsStartTurn()
    {
        var projectId = Guid.NewGuid();
        var runtime = await SeedOnlineRuntime(projectId);

        // First prompt → Running. Tracks the StartTurn invocation count.
        var harness = BuildHarness();
        var first = await harness.Hub.SubmitPrompt(new SubmitPromptPayload(
            ProjectId: projectId,
            ConversationId: null,
            BranchId: Guid.NewGuid(),
            Text: "first"));
        _runtimeGroupClient.Verify(c => c.StartTurn(It.IsAny<StartTurnPayload>()), Times.Once);

        // Second prompt → must queue behind it.
        var second = await harness.Hub.SubmitPrompt(new SubmitPromptPayload(
            ProjectId: projectId,
            ConversationId: first.ConversationId,
            BranchId: Guid.NewGuid(),
            Text: "second"));

        second.Queued.Should().BeTrue("first session is still Running on this runtime");
        second.QueuePosition.Should().Be(1, "first queued slot is position 1");

        // Second session row is Pending with QueuePosition=1.
        var secondSession = await _db.AgentSessions.SingleAsync(s => s.Id == second.SessionId);
        secondSession.Status.Should().Be(AgentSessionStatus.Pending);
        secondSession.QueuePosition.Should().Be(1);
        secondSession.RuntimeId.Should().Be(runtime.Id);

        // First session is still Running and has no queue position.
        var firstSession = await _db.AgentSessions.SingleAsync(s => s.Id == first.SessionId);
        firstSession.Status.Should().Be(AgentSessionStatus.Running);
        firstSession.QueuePosition.Should().BeNull();

        // Crucially: StartTurn was NOT dispatched a second time.
        _runtimeGroupClient.Verify(c => c.StartTurn(It.IsAny<StartTurnPayload>()), Times.Once);
    }

    [Fact]
    public async Task SubmitPrompt_ThirdPromptWhileTwoQueued_GetsQueuePositionTwo()
    {
        var projectId = Guid.NewGuid();
        await SeedOnlineRuntime(projectId);

        var harness = BuildHarness();

        // 1st: dispatched immediately.
        var first = await harness.Hub.SubmitPrompt(new SubmitPromptPayload(
            ProjectId: projectId,
            ConversationId: null,
            BranchId: Guid.NewGuid(),
            Text: "first"));
        first.Queued.Should().BeFalse();

        // 2nd: queued at position 1.
        var second = await harness.Hub.SubmitPrompt(new SubmitPromptPayload(
            ProjectId: projectId,
            ConversationId: first.ConversationId,
            BranchId: Guid.NewGuid(),
            Text: "second"));
        second.Queued.Should().BeTrue();
        second.QueuePosition.Should().Be(1);

        // 3rd: queued at position 2 — appended to the tail.
        var third = await harness.Hub.SubmitPrompt(new SubmitPromptPayload(
            ProjectId: projectId,
            ConversationId: first.ConversationId,
            BranchId: Guid.NewGuid(),
            Text: "third"));
        third.Queued.Should().BeTrue();
        third.QueuePosition.Should().Be(2, "queue positions monotonically increment per runtime");

        var thirdSession = await _db.AgentSessions.SingleAsync(s => s.Id == third.SessionId);
        thirdSession.Status.Should().Be(AgentSessionStatus.Pending);
        thirdSession.QueuePosition.Should().Be(2);

        // StartTurn was only ever dispatched for the first session.
        _runtimeGroupClient.Verify(c => c.StartTurn(It.IsAny<StartTurnPayload>()), Times.Once);
    }

    [Fact]
    public async Task SubmitPrompt_ActiveSessionCanceling_QueuesNewPrompt()
    {
        // A session in the transient Canceling state still occupies the
        // runtime — the daemon hasn't acked the cancel yet, and dispatching
        // another StartTurn would race the cancel-confirm event stream.
        var projectId = Guid.NewGuid();
        var runtime = await SeedOnlineRuntime(projectId);

        // Manually seed a Canceling session on this runtime.
        var conversation = await SeedConversation(projectId);
        var canceling = new AgentSession
        {
            ConversationId = conversation.Id,
            RuntimeId = runtime.Id,
            Prompt = "in flight",
            Status = AgentSessionStatus.Canceling,
        };
        _db.AgentSessions.Add(canceling);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var harness = BuildHarness();
        var response = await harness.Hub.SubmitPrompt(new SubmitPromptPayload(
            ProjectId: projectId,
            ConversationId: conversation.Id,
            BranchId: Guid.NewGuid(),
            Text: "follow-up"));

        response.Queued.Should().BeTrue("a Canceling session still occupies the runtime");
        response.QueuePosition.Should().Be(1);

        var newSession = await _db.AgentSessions.SingleAsync(s => s.Id == response.SessionId);
        newSession.Status.Should().Be(AgentSessionStatus.Pending);

        // No StartTurn while a session is mid-cancel.
        _runtimeGroupClient.Verify(c => c.StartTurn(It.IsAny<StartTurnPayload>()), Times.Never);
    }

    [Fact]
    public async Task SubmitPrompt_PriorTerminalSessions_DoNotBlockDispatch()
    {
        // Terminal sessions (Succeeded / Failed / Canceled) on the runtime
        // must NOT count as occupying — only Running / Canceling do.
        var projectId = Guid.NewGuid();
        var runtime = await SeedOnlineRuntime(projectId);
        var conversation = await SeedConversation(projectId);

        foreach (var terminal in new[]
                 {
                     AgentSessionStatus.Succeeded,
                     AgentSessionStatus.Failed,
                     AgentSessionStatus.Canceled,
                 })
        {
            _db.AgentSessions.Add(new AgentSession
            {
                ConversationId = conversation.Id,
                RuntimeId = runtime.Id,
                Prompt = $"old-{terminal}",
                Status = terminal,
            });
        }
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var harness = BuildHarness();
        var response = await harness.Hub.SubmitPrompt(new SubmitPromptPayload(
            ProjectId: projectId,
            ConversationId: conversation.Id,
            BranchId: Guid.NewGuid(),
            Text: "fresh"));

        response.Queued.Should().BeFalse("terminal sessions do not block new dispatches");
        response.QueuePosition.Should().BeNull();

        var session = await _db.AgentSessions.SingleAsync(s => s.Id == response.SessionId);
        session.Status.Should().Be(AgentSessionStatus.Running);

        _runtimeGroupClient.Verify(c => c.StartTurn(It.IsAny<StartTurnPayload>()), Times.Once);
    }

    [Fact]
    public async Task SubmitPrompt_DispatchedSession_RaisesSessionDispatchedEvent()
    {
        var projectId = Guid.NewGuid();
        var runtime = await SeedOnlineRuntime(projectId);

        var dispatchedSink = new TestNotificationSink<SessionDispatched>();
        var enqueuedSink = new TestNotificationSink<SessionEnqueued>();
        // Re-build the provider to register the secondary sinks. Easier: pull
        // the existing mediator, register late. We resolve the mediator and
        // add the handlers via the provider's IServiceCollection... but the
        // provider is already built. Instead, we listen via a side-channel:
        // hook the mediator's INotificationHandler through DI on a fresh test.
        // Pragmatic path: assert the SessionDispatched event is observable
        // through the session entity's domain event collection BEFORE the
        // interceptor clears it. Since the interceptor runs on SaveChanges,
        // we can't observe the entity post-save. Instead, assert on the
        // StoredDomainEvents row written by the interceptor.

        var harness = BuildHarness();
        var response = await harness.Hub.SubmitPrompt(new SubmitPromptPayload(
            ProjectId: projectId,
            ConversationId: null,
            BranchId: Guid.NewGuid(),
            Text: "fresh"));

        var stored = await _db.StoredDomainEvents
            .Where(e => e.EntityId == response.SessionId.ToString())
            .ToListAsync();

        stored.Should().Contain(e => e.EventType == nameof(SessionDispatched),
            "an idle-runtime dispatch must raise SessionDispatched on the session");
        stored.Should().NotContain(e => e.EventType == nameof(SessionEnqueued),
            "no other session was running, so the new session was not queued");

        _ = dispatchedSink; _ = enqueuedSink; _ = runtime; // avoid unused warnings
    }

    [Fact]
    public async Task SubmitPrompt_QueuedSession_RaisesSessionEnqueuedEvent()
    {
        var projectId = Guid.NewGuid();
        await SeedOnlineRuntime(projectId);

        var harness = BuildHarness();

        // Burn the runtime with a first prompt.
        var first = await harness.Hub.SubmitPrompt(new SubmitPromptPayload(
            ProjectId: projectId,
            ConversationId: null,
            BranchId: Guid.NewGuid(),
            Text: "first"));

        // Second prompt — must be enqueued.
        var second = await harness.Hub.SubmitPrompt(new SubmitPromptPayload(
            ProjectId: projectId,
            ConversationId: first.ConversationId,
            BranchId: Guid.NewGuid(),
            Text: "second"));

        var stored = await _db.StoredDomainEvents
            .Where(e => e.EntityId == second.SessionId.ToString())
            .ToListAsync();

        stored.Should().Contain(e => e.EventType == nameof(SessionEnqueued),
            "queued sessions must raise SessionEnqueued for traceability");
        stored.Should().NotContain(e => e.EventType == nameof(SessionDispatched),
            "a queued session has not been dispatched yet");
    }

    /// <summary>
    /// Tiny notification handler used by ad-hoc tests to capture published events
    /// in-order. Not registered in the default provider — tests that want it
    /// rebuild the SP. Kept here so the AgentHub test bootstrap stays compact.
    /// </summary>
    private sealed class TestNotificationSink<T> : INotificationHandler<T> where T : INotification
    {
        public List<T> Captured { get; } = new();
        public Task Handle(T notification, CancellationToken cancellationToken)
        {
            Captured.Add(notification);
            return Task.CompletedTask;
        }
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private async Task<ProjectRuntime> SeedOnlineRuntime(Guid projectId)
    {
        await SeedProject(projectId);
        var branchId = Guid.NewGuid();
        await SeedBranch(projectId, branchId);
        return await SeedOnlineRuntimeInternal(projectId, branchId);
    }

    /// <summary>
    /// Seed an Online runtime explicitly bound to a caller-supplied branch.
    /// Hub's <c>SubmitPrompt</c> resolves the runtime by <c>BranchId</c>, so
    /// new tests that need their dispatch to land coordinate the BranchId
    /// across the seed + the SubmitPromptPayload via this overload.
    /// </summary>
    private async Task<ProjectRuntime> SeedOnlineRuntime(Guid projectId, Guid branchId)
    {
        await SeedProject(projectId);
        await SeedBranch(projectId, branchId);
        return await SeedOnlineRuntimeInternal(projectId, branchId);
    }

    private async Task<ProjectRuntime> SeedOnlineRuntimeInternal(Guid projectId, Guid branchId)
    {
        var runtime = new ProjectRuntime
        {
            ProjectId = projectId,
            BranchId = branchId,
            State = RuntimeState.Online,
            Region = "arn",
        };
        _db.ProjectRuntimes.Add(runtime);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        return runtime;
    }

    private async Task SeedBranch(Guid projectId, Guid branchId)
    {
        if (await _db.ProjectBranches.AnyAsync(b => b.Id == branchId))
        {
            return;
        }

        _db.ProjectBranches.Add(new ProjectBranch
        {
            Id = branchId,
            ProjectId = projectId,
            Name = "main",
            IsDefault = true,
        });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
    }

    private async Task<ProjectRuntime> SeedRuntime(Guid projectId, RuntimeState state)
    {
        await SeedProject(projectId);
        var branchId = Guid.NewGuid();
        await SeedBranch(projectId, branchId);
        var runtime = new ProjectRuntime
        {
            ProjectId = projectId,
            BranchId = branchId,
            State = state,
            Region = "arn",
        };
        _db.ProjectRuntimes.Add(runtime);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        return runtime;
    }

    private async Task<Conversation> SeedConversation(Guid projectId, int eventCount = 0)
    {
        return await SeedConversation(projectId, Guid.NewGuid(), eventCount);
    }

    /// <summary>
    /// Seed a conversation explicitly bound to a caller-supplied branch.
    /// The hub dispatches via <see cref="DispatchTurnArgs.BranchId"/> taken
    /// from the loaded conversation row (not the payload), so continuation
    /// tests that need the runtime lookup to land must coordinate the
    /// conversation's BranchId with the seeded runtime's BranchId.
    /// </summary>
    private async Task<Conversation> SeedConversation(Guid projectId, Guid branchId, int eventCount = 0)
    {
        var conversation = new Conversation
        {
            ProjectId = projectId,
            BranchId = branchId,
            Title = "seeded",
            Status = ConversationStatus.Active,
            LastActivityAt = DateTime.UtcNow,
            EventCount = eventCount,
        };
        _db.Conversations.Add(conversation);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        return conversation;
    }

    private async Task<Project> SeedProject(Guid projectId)
    {
        var project = new Project
        {
            Id = projectId,
            WorkspaceId = Guid.NewGuid(),
            OwnerUserId = TestUserId,
            Name = "seeded-project",
            GithubRepoOwner = "acme",
            GithubRepoName = "demo",
            GithubInstallationId = Guid.NewGuid(),
        };
        _db.Projects.Add(project);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        return project;
    }

    private record HubHarness(AgentHub Hub, FakeHubCallerContext Context, string ConnectionId);

    private HubHarness BuildHarness(
        Source.Features.SignalR.Services.IAgentSecretsResolver? secretsResolver = null)
    {
        const string connectionId = "conn-test";

        var http = new DefaultHttpContext();
        var context = new FakeHubCallerContext(connectionId, http, BuildUserPrincipal(TestUserId));

        var groups = new Mock<IGroupManager>();
        var clients = new Mock<IHubCallerClients<IAgentClient>>();

        // Real TurnDispatcher — SubmitPrompt now delegates the session-create
        // + audit-event + StartTurn dispatch to it, so we need the actual
        // service wired against the same db + cross-hub mock to preserve all
        // the existing test expectations (event sequencing, counter bumps,
        // domain-event publish, dispatched StartTurnPayload shape).
        var dispatcher = new Source.Features.Conversations.Services.TurnDispatcher(
            _db, _runtimeHub.Object, NullLogger<Source.Features.Conversations.Services.TurnDispatcher>.Instance);

        var hub = new AgentHub(
            _db,
            _runtimeHub.Object,
            dispatcher,
            Mock.Of<IMediator>(),
            secretsResolver ?? BuildSecretsResolver(),
            NullLogger<AgentHub>.Instance)
        {
            Context = context,
            Groups = groups.Object,
            Clients = clients.Object,
        };

        return new HubHarness(hub, context, connectionId);
    }

    /// <summary>
    /// Stubbed BYOK pre-flight that always reports a key configured. The Card
    private static Source.Features.SignalR.Services.IAgentSecretsResolver BuildSecretsResolver(
        string? cursorApiKey = "test-cursor-key")
    {
        var mock = new Mock<Source.Features.SignalR.Services.IAgentSecretsResolver>();
        mock.Setup(r => r.ResolveCursorApiKeyAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(cursorApiKey);
        return mock.Object;
    }

    private static ClaimsPrincipal BuildUserPrincipal(string userId)
    {
        var identity = new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, userId) },
            authenticationType: "Test");
        return new ClaimsPrincipal(identity);
    }

    /// <summary>
    /// Captures every <see cref="AgentEventEmitted"/> the interceptor publishes
    /// after <c>SaveChangesAsync</c>. Registered into DI as a singleton so
    /// tests can reset + inspect the captured list.
    /// </summary>
    private sealed class TestEventSink : INotificationHandler<AgentEventEmitted>
    {
        public List<AgentEventEmitted> Captured { get; } = new();

        public Task Handle(AgentEventEmitted notification, CancellationToken cancellationToken)
        {
            Captured.Add(notification);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Minimal HubCallerContext stand-in. Mirrors <see cref="RuntimeHubEmitEventTests"/>'s
    /// fake but with a settable <see cref="ClaimsPrincipal"/> so SubmitPrompt's
    /// auth check sees a real NameIdentifier claim.
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
