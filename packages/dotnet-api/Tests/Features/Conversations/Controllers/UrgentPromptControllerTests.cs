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
using Source.Features.RuntimeLifecycle.Models;
using Source.Features.SignalR.Contracts;
using Source.Features.SignalR.Hubs;
using Source.Features.Users.Models;
using Source.Infrastructure;

namespace Api.Tests.Features.Conversations.Controllers;

/// <summary>
/// End-to-end HTTP tests for
/// <c>POST /api/conversations/{conversationId}/urgent-prompt</c>.
///
/// <para>The handler is exhaustively tested in
/// <see cref="Api.Tests.Features.Conversations.Commands.SubmitUrgentPromptCommandHandlerTests"/>;
/// this file pins the controller wiring — route, body deserialisation,
/// 200 vs 400 vs 404, the SignalR mock chain swap. Mirrors
/// <see cref="SessionsControllerTests"/> for the registration / cookie auth
/// flow.</para>
/// </summary>
[Collection(HangfireTestCollection.Name)]
public class UrgentPromptControllerTests : IntegrationTestBase
{
    private const string Password = "Password123!";

    // Caller's user id, captured at registration, used to seed an owned Project.
    private string? _callerUserId;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    // SignalR mock chain — hub.Clients.Group("runtime-{id}").{StartTurn|CancelTurn}(payload).
    private readonly Mock<IHubContext<RuntimeHub, IRuntimeClient>> _runtimeHub = new();
    private readonly Mock<IHubClients<IRuntimeClient>> _hubClients = new();
    private readonly Mock<IRuntimeClient> _groupClient = new();

    public UrgentPromptControllerTests()
    {
        _runtimeHub.SetupGet(h => h.Clients).Returns(_hubClients.Object);
        _hubClients.Setup(c => c.Group(It.IsAny<string>())).Returns(_groupClient.Object);
        _groupClient
            .Setup(c => c.StartTurn(It.IsAny<StartTurnPayload>()))
            .Returns(Task.CompletedTask);
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
    public async Task Submit_HappyPath_IdleRuntime_Returns200_AndDispatches()
    {
        var (client, _) = await RegisterUserAsync();
        var (conversationId, runtimeId) = await SeedRuntimeAndConversationAsync();

        var response = await client.PostAsJsonAsync(
            $"/api/conversations/{conversationId}/urgent-prompt",
            new { prompt = "do this now" });

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            await response.Content.ReadAsStringAsync());

        var payload = await response.Content.ReadFromJsonAsync<SubmitUrgentPromptResponse>(JsonOptions);
        payload!.Queued.Should().BeFalse();
        payload.CanceledSessionId.Should().BeNull();
        payload.QueuePosition.Should().BeNull();

        // DB reflects the immediate dispatch.
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var session = await db.AgentSessions.SingleAsync(s => s.Id == payload.SessionId);
        session.Status.Should().Be(AgentSessionStatus.Running);
        session.RuntimeId.Should().Be(runtimeId);

        // StartTurn pushed; no CancelTurn (nothing to cancel).
        _groupClient.Verify(c => c.StartTurn(It.IsAny<StartTurnPayload>()), Times.Once);
        _groupClient.Verify(c => c.CancelTurn(It.IsAny<CancelTurnPayload>()), Times.Never);
    }

    [Fact]
    public async Task Submit_BusyRuntime_Returns200_PreemptsAndQueues()
    {
        var (client, _) = await RegisterUserAsync();
        var (conversationId, runtimeId) = await SeedRuntimeAndConversationAsync();
        var runningId = await SeedRunningSessionAsync(conversationId, runtimeId);

        var response = await client.PostAsJsonAsync(
            $"/api/conversations/{conversationId}/urgent-prompt",
            new { prompt = "urgent!" });

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            await response.Content.ReadAsStringAsync());

        var payload = await response.Content.ReadFromJsonAsync<SubmitUrgentPromptResponse>(JsonOptions);
        payload!.Queued.Should().BeTrue();
        payload.QueuePosition.Should().Be(1);
        payload.CanceledSessionId.Should().Be(runningId);

        // DB: current Canceling with reason urgent_preempted; urgent at pos 1.
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var current = await db.AgentSessions.SingleAsync(s => s.Id == runningId);
        current.Status.Should().Be(AgentSessionStatus.Canceling);
        current.CancelReason.Should().Be("urgent_preempted");

        var urgent = await db.AgentSessions.SingleAsync(s => s.Id == payload.SessionId);
        urgent.Status.Should().Be(AgentSessionStatus.Pending);
        urgent.QueuePosition.Should().Be(1);

        _groupClient.Verify(c => c.CancelTurn(It.Is<CancelTurnPayload>(p =>
            p.SessionId == runningId && p.Reason == "urgent_preempted")), Times.Once);
    }

    [Fact]
    public async Task Submit_MissingConversation_Returns404()
    {
        var (client, _) = await RegisterUserAsync();

        var response = await client.PostAsJsonAsync(
            $"/api/conversations/{Guid.NewGuid()}/urgent-prompt",
            new { prompt = "hello" });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Submit_EmptyPrompt_Returns400()
    {
        var (client, _) = await RegisterUserAsync();
        var (conversationId, _) = await SeedRuntimeAndConversationAsync();

        var response = await client.PostAsJsonAsync(
            $"/api/conversations/{conversationId}/urgent-prompt",
            new { prompt = "   " });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Submit_Unauthenticated_Returns401()
    {
        var response = await Client.PostAsJsonAsync(
            $"/api/conversations/{Guid.NewGuid()}/urgent-prompt",
            new { prompt = "hello" });
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ----------------------------------------------------------------------
    // helpers
    // ----------------------------------------------------------------------

    private async Task<(Guid ConversationId, Guid RuntimeId)> SeedRuntimeAndConversationAsync()
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var projectId = Guid.NewGuid();
        // The urgent-prompt handler dispatches to the runtime serving the
        // conversation's branch, so the runtime and conversation must share a
        // BranchId and the runtime must be in a dispatchable (Online) state.
        var branchId = Guid.NewGuid();
        var runtime = new ProjectRuntime
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            BranchId = branchId,
            Region = "arn",
            State = RuntimeState.Online,
        };
        db.ProjectRuntimes.Add(runtime);

        var conversation = new Conversation
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Title = "test",
            BranchId = branchId,
            Status = ConversationStatus.Active,
            LastActivityAt = DateTime.UtcNow,
        };
        db.Conversations.Add(conversation);

        await EnsureOwnedProjectAsync(db, projectId);
        await db.SaveChangesAsync();
        return (conversation.Id, runtime.Id);
    }

    private async Task<Guid> SeedRunningSessionAsync(Guid conversationId, Guid runtimeId)
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var session = new AgentSession
        {
            Id = Guid.NewGuid(),
            ConversationId = conversationId,
            RuntimeId = runtimeId,
            Prompt = "running",
            Status = AgentSessionStatus.Running,
            StartedAt = DateTime.UtcNow.AddSeconds(-5),
        };
        db.AgentSessions.Add(session);
        await db.SaveChangesAsync();
        return session.Id;
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
