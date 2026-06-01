using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Source.Features.Conversations.Commands;
using Source.Features.Conversations.Models;
using Source.Features.SignalR.Contracts;
using Source.Features.SignalR.Hubs;
using Source.Features.Users.Models;
using Source.Infrastructure;

namespace Api.Tests.Features.Conversations.Controllers;

/// <summary>
/// End-to-end HTTP tests for <c>POST /api/sessions/{id}/cancel</c>.
///
/// <para>The handler is exhaustively tested in
/// <see cref="Api.Tests.Features.Conversations.Commands.CancelSessionCommandHandlerTests"/>;
/// this file pins the controller wiring — route, body deserialisation,
/// 200 vs 404, and the SignalR mock chain swap that's needed because the
/// in-memory test host has no real daemon connections.</para>
///
/// <para>SignalR mock pattern mirrors
/// <see cref="Api.Tests.Features.GitOps.GitDestructiveOpsControllerTests"/> —
/// swap <see cref="IHubContext{THub, T}"/> for a Moq stand-in via
/// <see cref="IntegrationTestBase.WithServiceFactory"/> and assert on the
/// emitted <see cref="IRuntimeClient.CancelTurn"/> call.</para>
/// </summary>
[Collection(HangfireTestCollection.Name)]
public class SessionsControllerTests : IntegrationTestBase
{
    private const string Password = "Password123!";

    // Captured during RegisterUserAsync; seed helpers create a Project owned by
    // this user so the controller/handler ownership gate short-circuits to pass.
    private string? _callerUserId;

    /// <summary>
    /// Match the API's controller JSON config (<c>AddJsonOptions</c>) so we
    /// deserialise the enum response (<see cref="AgentSessionStatus"/>) from
    /// its string form rather than the default integer form.
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    // SignalR mock chain — hub.Clients.Group("runtime-{id}").CancelTurn(payload).
    private readonly Mock<IHubContext<RuntimeHub, IRuntimeClient>> _runtimeHub = new();
    private readonly Mock<IHubClients<IRuntimeClient>> _hubClients = new();
    private readonly Mock<IRuntimeClient> _groupClient = new();

    public SessionsControllerTests()
    {
        _runtimeHub.SetupGet(h => h.Clients).Returns(_hubClients.Object);
        _hubClients.Setup(c => c.Group(It.IsAny<string>())).Returns(_groupClient.Object);
        _groupClient
            .Setup(c => c.CancelTurn(It.IsAny<CancelTurnPayload>()))
            .Returns(Task.CompletedTask);

        WithServiceFactory(services =>
        {
            services.RemoveAll<IHubContext<RuntimeHub, IRuntimeClient>>();
            services.AddSingleton(_runtimeHub.Object);
        });
    }

    [Fact]
    public async Task Cancel_RunningSession_Returns200_AndPushesCancelTurn()
    {
        var (client, _) = await RegisterUserAsync();
        var (sessionId, runtimeId) = await SeedRunningSessionAsync();

        var response = await client.PostAsJsonAsync(
            $"/api/sessions/{sessionId}/cancel",
            new { reason = "user_requested" });

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            await response.Content.ReadAsStringAsync());

        var payload = await response.Content.ReadFromJsonAsync<CancelSessionResponse>(JsonOptions);
        payload!.SessionId.Should().Be(sessionId);
        payload.FinalStatus.Should().Be(AgentSessionStatus.Canceling);

        // Daemon group push happened with the supplied reason.
        _hubClients.Verify(c => c.Group($"runtime-{runtimeId}"), Times.AtLeastOnce);
        _groupClient.Verify(
            c => c.CancelTurn(It.Is<CancelTurnPayload>(p =>
                p.SessionId == sessionId && p.Reason == "user_requested")),
            Times.Once);
    }

    [Fact]
    public async Task Cancel_MissingSession_Returns404()
    {
        var (client, _) = await RegisterUserAsync();

        var response = await client.PostAsJsonAsync(
            $"/api/sessions/{Guid.NewGuid()}/cancel",
            new { reason = "user_requested" });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        _groupClient.Verify(
            c => c.CancelTurn(It.IsAny<CancelTurnPayload>()),
            Times.Never);
    }

    [Fact]
    public async Task Cancel_EmptyBody_DefaultsReasonToUser()
    {
        // No body / null / empty reason all collapse to "user" at the
        // controller — the daemon and audit log get a non-empty reason
        // regardless of how the caller built the request.
        var (client, _) = await RegisterUserAsync();
        var (sessionId, _) = await SeedRunningSessionAsync();

        var response = await client.PostAsync(
            $"/api/sessions/{sessionId}/cancel",
            content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            await response.Content.ReadAsStringAsync());

        _groupClient.Verify(
            c => c.CancelTurn(It.Is<CancelTurnPayload>(p =>
                p.SessionId == sessionId && p.Reason == "user")),
            Times.Once);
    }

    [Fact]
    public async Task Cancel_Unauthenticated_Returns401()
    {
        var response = await Client.PostAsJsonAsync(
            $"/api/sessions/{Guid.NewGuid()}/cancel",
            new { reason = "user_requested" });
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ======================================================================
    // GET /api/sessions/{sessionId}/position
    // ======================================================================

    [Fact]
    public async Task GetPosition_PendingSession_ReturnsPositionAndQueueLength()
    {
        var (client, _) = await RegisterUserAsync();
        var runtimeId = Guid.NewGuid();
        var (pendingId, _, _) = await SeedQueuedTrioAsync(runtimeId);

        var response = await client.GetAsync($"/api/sessions/{pendingId}/position");

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            await response.Content.ReadAsStringAsync());

        var payload = await response.Content.ReadFromJsonAsync<SessionPositionResponse>(JsonOptions);
        payload!.SessionId.Should().Be(pendingId);
        payload.Status.Should().Be(AgentSessionStatus.Pending);
        payload.QueuePosition.Should().Be(1);
        payload.RuntimeQueueLength.Should().Be(3);
    }

    [Fact]
    public async Task GetPosition_RunningSession_ReturnsNullPosition_AndCountOfPendingPeers()
    {
        var (client, _) = await RegisterUserAsync();
        var (sessionId, runtimeId) = await SeedRunningSessionAsync();
        // Park 2 Pending peers on the same runtime so the count is non-zero
        await SeedQueuedTrioAsync(runtimeId, count: 2);

        var response = await client.GetAsync($"/api/sessions/{sessionId}/position");

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            await response.Content.ReadAsStringAsync());

        var payload = await response.Content.ReadFromJsonAsync<SessionPositionResponse>(JsonOptions);
        payload!.SessionId.Should().Be(sessionId);
        payload.Status.Should().Be(AgentSessionStatus.Running);
        payload.QueuePosition.Should().BeNull();
        payload.RuntimeQueueLength.Should().Be(2);
    }

    [Fact]
    public async Task GetPosition_MissingSession_Returns404()
    {
        var (client, _) = await RegisterUserAsync();

        var response = await client.GetAsync($"/api/sessions/{Guid.NewGuid()}/position");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetPosition_Unauthenticated_Returns401()
    {
        var response = await Client.GetAsync($"/api/sessions/{Guid.NewGuid()}/position");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ----------------------------------------------------------------------
    // helpers
    // ----------------------------------------------------------------------

    private async Task<(Guid First, Guid Second, Guid Third)> SeedQueuedTrioAsync(
        Guid runtimeId,
        int count = 3)
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var projectId = Guid.NewGuid();
        var conversation = new Conversation
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Title = "queued-trio",
            BranchId = Guid.NewGuid(),
            Status = ConversationStatus.Active,
            LastActivityAt = DateTime.UtcNow,
        };
        db.Conversations.Add(conversation);

        AgentSession Make(int pos) => new()
        {
            Id = Guid.NewGuid(),
            ConversationId = conversation.Id,
            RuntimeId = runtimeId,
            Prompt = $"queued-{pos}",
            Status = AgentSessionStatus.Pending,
            QueuePosition = pos,
        };

        var sessions = Enumerable.Range(1, count).Select(Make).ToArray();
        db.AgentSessions.AddRange(sessions);
        await EnsureOwnedProjectAsync(db, projectId);
        await db.SaveChangesAsync();

        // Pad to a 3-tuple so the deconstruction in the happy-path test stays
        // ergonomic; callers that ask for fewer can ignore the trailing ids.
        var ids = sessions.Select(s => s.Id).ToArray();
        var first = ids.Length > 0 ? ids[0] : Guid.Empty;
        var second = ids.Length > 1 ? ids[1] : Guid.Empty;
        var third = ids.Length > 2 ? ids[2] : Guid.Empty;
        return (first, second, third);
    }

    private async Task<(Guid SessionId, Guid RuntimeId)> SeedRunningSessionAsync()
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var projectId = Guid.NewGuid();
        var conversation = new Conversation
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Title = "test",
            BranchId = Guid.NewGuid(),
            Status = ConversationStatus.Active,
            LastActivityAt = DateTime.UtcNow,
        };
        db.Conversations.Add(conversation);

        var runtimeId = Guid.NewGuid();
        var session = new AgentSession
        {
            Id = Guid.NewGuid(),
            ConversationId = conversation.Id,
            RuntimeId = runtimeId,
            Prompt = "running",
            Status = AgentSessionStatus.Running,
            StartedAt = DateTime.UtcNow.AddSeconds(-5),
        };
        db.AgentSessions.Add(session);

        await EnsureOwnedProjectAsync(db, projectId);
        await db.SaveChangesAsync();
        return (session.Id, runtimeId);
    }

    private async Task<(HttpClient Client, string UserId)> RegisterUserAsync()
    {
        await SeedRolesAsync();

        var email = $"user-{Guid.NewGuid():N}@example.com";
        var registerClient = Factory.CreateClient();
        var response = await registerClient.PostAsJsonAsync(
            "/api/auth/register", new { email, password = Password });
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            await response.Content.ReadAsStringAsync());

        var cookies = response.Headers.GetValues("Set-Cookie");
        var authCookie = cookies.First(c => c.StartsWith("auth-token=", StringComparison.Ordinal));
        var cookieValue = authCookie.Split(';')[0];

        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", cookieValue);

        using var scope = CreateScope();
        var um = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var user = await um.FindByEmailAsync(email);
        _callerUserId = user!.Id;
        return (client, user.Id);
    }

    /// <summary>
    /// Ensure a Project row owned by the current caller exists for <paramref name="projectId"/>.
    /// Idempotent. The ownership gate 404s without it.
    /// </summary>
    private async Task EnsureOwnedProjectAsync(ApplicationDbContext db, Guid projectId)
    {
        if (_callerUserId is null) return;
        if (await db.Projects.AnyAsync(p => p.Id == projectId)) return;
        db.Projects.Add(new Source.Features.Projects.Models.Project
        {
            Id = projectId,
            OwnerUserId = _callerUserId,
            WorkspaceId = Guid.NewGuid(),
            Name = "Test Project",
        });
    }
}
