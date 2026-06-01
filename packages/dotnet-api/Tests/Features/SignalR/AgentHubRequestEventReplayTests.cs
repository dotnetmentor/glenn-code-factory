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
using Source.Features.SignalR.Contracts;
using Source.Features.SignalR.Hubs;
using Source.Infrastructure;
using Source.Infrastructure.Interceptors;

namespace Api.Tests.Features.SignalR;

/// <summary>
/// Unit tests for <see cref="AgentHub.RequestEventReplay"/> — the React-facing
/// "give me events I missed since seq N" entry point used after a network blip.
///
/// <para>Bootstrap mirrors <see cref="AgentHubCancelTurnTests"/>: real
/// <see cref="ApplicationDbContext"/> on InMemory, MediatR + the
/// <see cref="DomainEventInterceptor"/>, and a mocked cross-hub
/// <see cref="IHubContext{THub, T}"/> (unused here but the hub's ctor wants it).</para>
///
/// <para>Key invariants under test:
/// <list type="bullet">
///   <item>Strictly-greater-than cursor (since=4 returns 5..N).</item>
///   <item>Hard cap at <c>1000</c> rows.</item>
///   <item>Best-effort: unknown <see cref="EventReplayRequest.SessionId"/> →
///         empty list, never throw.</item>
///   <item>Reuses <see cref="AgentEventNotification"/> — same record the
///         live-broadcast handler emits, so the JS client converges on one
///         shape for live + replayed events.</item>
///   <item><see cref="AgentEvent.Kind"/> serialises as the enum NAME
///         ("AssistantText"), not the ordinal — wire-stability across enum
///         re-orderings.</item>
/// </list>
/// </para>
/// </summary>
public class AgentHubRequestEventReplayTests : IDisposable
{
    private const string TestUserId = "user-123";

    private readonly ServiceProvider _provider;
    private readonly ApplicationDbContext _db;

    private readonly Mock<IHubClients<IRuntimeClient>> _runtimeClients = new();
    private readonly Mock<IHubContext<RuntimeHub, IRuntimeClient>> _runtimeHub = new();

    public AgentHubRequestEventReplayTests()
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
    // Cursor semantics (exclusive — return Sequence > SinceSequence)
    // ------------------------------------------------------------------

    [Fact]
    public async Task RequestEventReplay_ReturnsEventsWithSequenceGreaterThanCursor()
    {
        var (_, session) = await SeedSessionWithEvents(eventCount: 10); // seq 0..9

        var harness = BuildHarness();
        var result = await harness.Hub.RequestEventReplay(
            new EventReplayRequest(session.Id, SinceSequence: 4));

        result.Should().HaveCount(5);
        // Sequence lives on the embedded polymorphic DTO now — same data, one
        // hop down through the union.
        result.Select(e => e.Event.Sequence).Should().Equal(5, 6, 7, 8, 9);
    }

    [Fact]
    public async Task RequestEventReplay_SinceMinus1_ReturnsAllEvents()
    {
        var (_, session) = await SeedSessionWithEvents(eventCount: 10);

        var harness = BuildHarness();
        var result = await harness.Hub.RequestEventReplay(
            new EventReplayRequest(session.Id, SinceSequence: -1));

        result.Should().HaveCount(10);
        result.Select(e => e.Event.Sequence).Should().Equal(Enumerable.Range(0, 10).Select(i => (long)i));
    }

    [Fact]
    public async Task RequestEventReplay_NoNewerEvents_ReturnsEmptyList()
    {
        var (_, session) = await SeedSessionWithEvents(eventCount: 10);

        var harness = BuildHarness();
        var result = await harness.Hub.RequestEventReplay(
            new EventReplayRequest(session.Id, SinceSequence: 999));

        result.Should().BeEmpty();
    }

    // ------------------------------------------------------------------
    // Hard cap at 1000 — runaway-replay safety net
    // ------------------------------------------------------------------

    [Fact]
    public async Task RequestEventReplay_LimitCappedAt1000()
    {
        var (_, session) = await SeedSessionWithEvents(eventCount: 1500);

        var harness = BuildHarness();
        var result = await harness.Hub.RequestEventReplay(
            new EventReplayRequest(session.Id, SinceSequence: -1));

        result.Should().HaveCount(1000);
        // Lowest 1000 — sequences 0..999 in ascending order.
        result.First().Event.Sequence.Should().Be(0);
        result.Last().Event.Sequence.Should().Be(999);
    }

    // ------------------------------------------------------------------
    // Best-effort — unknown session id → empty list, never throw
    // ------------------------------------------------------------------

    [Fact]
    public async Task RequestEventReplay_UnknownSessionId_ReturnsEmptyList()
    {
        var harness = BuildHarness();
        var result = await harness.Hub.RequestEventReplay(
            new EventReplayRequest(Guid.NewGuid(), SinceSequence: -1));

        result.Should().BeEmpty();
    }

    // ------------------------------------------------------------------
    // Cross-cuts — ConversationId join, ordering, scoping, enum-as-string
    // ------------------------------------------------------------------

    [Fact]
    public async Task RequestEventReplay_PopulatesConversationId()
    {
        var (conversation, session) = await SeedSessionWithEvents(eventCount: 3);

        var harness = BuildHarness();
        var result = await harness.Hub.RequestEventReplay(
            new EventReplayRequest(session.Id, SinceSequence: -1));

        result.Should().HaveCount(3);
        result.Should().OnlyContain(e => e.ConversationId == conversation.Id,
            "the join must surface the parent conversation id on every replayed event");
    }

    [Fact]
    public async Task RequestEventReplay_OrderedBySequenceAsc()
    {
        var conversation = await SeedConversation();
        var session = await SeedSessionRow(conversation.Id);

        // Insert events deliberately out of order — proves the ORDER BY clause,
        // not insertion order, drives the result.
        await SeedEvent(session.Id, sequence: 5);
        await SeedEvent(session.Id, sequence: 2);
        await SeedEvent(session.Id, sequence: 7);

        var harness = BuildHarness();
        var result = await harness.Hub.RequestEventReplay(
            new EventReplayRequest(session.Id, SinceSequence: -1));

        result.Select(e => e.Event.Sequence).Should().Equal(2, 5, 7);
    }

    [Fact]
    public async Task RequestEventReplay_DifferentSession_NotIncluded()
    {
        var (_, sessionA) = await SeedSessionWithEvents(eventCount: 5);
        var (_, sessionB) = await SeedSessionWithEvents(eventCount: 5);

        var harness = BuildHarness();
        var result = await harness.Hub.RequestEventReplay(
            new EventReplayRequest(sessionA.Id, SinceSequence: -1));

        result.Should().HaveCount(5);
        result.Should().OnlyContain(e => e.Event.SessionId == sessionA.Id,
            "events from session B must not leak into session A's replay");
        result.Should().NotContain(e => e.Event.SessionId == sessionB.Id);
    }

    [Fact]
    public async Task RequestEventReplay_EventTypeAsString()
    {
        var conversation = await SeedConversation();
        var session = await SeedSessionRow(conversation.Id);
        await SeedEvent(session.Id, sequence: 0, kind: AgentEventKind.AssistantText);

        var harness = BuildHarness();
        var result = await harness.Hub.RequestEventReplay(
            new EventReplayRequest(session.Id, SinceSequence: -1));

        result.Should().ContainSingle();
        // Polymorphic discriminator surfaces the concrete subtype — the React
        // client switches on event.eventKind ("assistantText") to narrow the
        // union. The DTO instance type proves the right branch lit up.
        result[0].Event.Should().BeOfType<AssistantTextEventDto>(
            "wire stability — JsonPolymorphic emits eventKind=\"assistantText\" and the React side switches on it");
    }

    // ------------------------------------------------------------------
    // Auth — anonymous caller throws (defense in depth; [Authorize] also enforces)
    // ------------------------------------------------------------------

    [Fact]
    public async Task RequestEventReplay_AnonymousUser_Throws()
    {
        var (_, session) = await SeedSessionWithEvents(eventCount: 1);

        var harness = BuildAnonymousHarness();
        var act = async () => await harness.Hub.RequestEventReplay(
            new EventReplayRequest(session.Id, SinceSequence: -1));

        await act.Should().ThrowAsync<HubException>()
            .WithMessage("*Unauthenticated*");
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private async Task<Conversation> SeedConversation()
    {
        var conversation = new Conversation
        {
            ProjectId = Guid.NewGuid(),
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

    private async Task<AgentSession> SeedSessionRow(Guid conversationId)
    {
        var session = new AgentSession
        {
            ConversationId = conversationId,
            Prompt = "test prompt",
            Status = AgentSessionStatus.Running,
        };
        _db.AgentSessions.Add(session);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        return session;
    }

    private async Task SeedEvent(
        Guid sessionId,
        int sequence,
        AgentEventKind kind = AgentEventKind.AssistantText)
    {
        // CreatedAt is NOT auto-set on AgentEvent (intentionally not IAuditable),
        // so we set it explicitly.
        _db.AgentEvents.Add(new AgentEvent
        {
            SessionId = sessionId,
            Sequence = sequence,
            Kind = kind,
            Text = kind == AgentEventKind.AssistantText
                || kind == AgentEventKind.Thinking
                || kind == AgentEventKind.PromptReceived
                ? "seeded text"
                : null,
            CreatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
    }

    private async Task<(Conversation Conversation, AgentSession Session)> SeedSessionWithEvents(int eventCount)
    {
        var conversation = await SeedConversation();
        var session = await SeedSessionRow(conversation.Id);

        // Bulk-insert in one save to avoid N round-trips. Sequences 0..N-1.
        var now = DateTime.UtcNow;
        for (var i = 0; i < eventCount; i++)
        {
            _db.AgentEvents.Add(new AgentEvent
            {
                SessionId = session.Id,
                Sequence = i,
                Kind = AgentEventKind.AssistantText,
                Text = "seeded text " + i,
                CreatedAt = now.AddMilliseconds(i),
            });
        }
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        return (conversation, session);
    }

    private record HubHarness(AgentHub Hub, FakeHubCallerContext Context, string ConnectionId);

    private HubHarness BuildHarness() => BuildHarnessCore(BuildUserPrincipal(TestUserId));

    private HubHarness BuildAnonymousHarness() => BuildHarnessCore(user: null);

    private HubHarness BuildHarnessCore(ClaimsPrincipal? user)
    {
        const string connectionId = "conn-test";

        var http = new DefaultHttpContext();
        var context = new FakeHubCallerContext(connectionId, http, user);

        var groups = new Mock<IGroupManager>();
        var clients = new Mock<IHubCallerClients<IAgentClient>>();

        var hub = new AgentHub(_db, _runtimeHub.Object, Mock.Of<Source.Features.Conversations.Services.ITurnDispatcher>(), Mock.Of<IMediator>(), Mock.Of<Source.Features.SignalR.Services.IAgentSecretsResolver>(), NullLogger<AgentHub>.Instance)
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
    /// <see cref="AgentHubSubmitPromptTests"/> / <see cref="AgentHubCancelTurnTests"/>.
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
