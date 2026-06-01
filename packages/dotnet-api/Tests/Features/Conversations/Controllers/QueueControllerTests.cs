using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Source.Features.Conversations.Commands;
using Source.Features.Conversations.Models;
using Source.Features.RuntimeLifecycle.Models;
using Source.Features.Users.Models;
using Source.Infrastructure;

namespace Api.Tests.Features.Conversations.Controllers;

/// <summary>
/// End-to-end HTTP tests for <c>PUT /api/runtimes/{runtimeId}/queue/reorder</c>.
///
/// <para>The handler logic (set comparison, renumber, mismatch sentinel) is
/// covered exhaustively in
/// <see cref="Api.Tests.Features.Conversations.Commands.ReorderQueueCommandHandlerTests"/>.
/// This file pins the controller wiring: route shape, body deserialisation,
/// 200 vs 400, and the auth requirement. Mirrors
/// <see cref="SessionsControllerTests"/> for the registration / cookie auth
/// flow.</para>
/// </summary>
[Collection(HangfireTestCollection.Name)]
public class QueueControllerTests : IntegrationTestBase
{
    private const string Password = "Password123!";

    /// <summary>
    /// Match the API's controller JSON config so optional camelCase still
    /// round-trips through the response DTO regardless of platform defaults.
    /// The string-enum converter is needed for <see cref="QueueResponse"/>
    /// where <see cref="AgentSessionStatus"/> serialises to a string.
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    [Fact]
    public async Task Reorder_HappyPath_Returns200_AndRenumbers()
    {
        var (client, _) = await RegisterUserAsync();
        var runtimeId = Guid.NewGuid();
        var (a, b, c) = await SeedThreeQueuedAsync(runtimeId);

        // Drag C to the head: [C, A, B]
        var response = await client.PutAsJsonAsync(
            $"/api/runtimes/{runtimeId}/queue/reorder",
            new { sessionIds = new[] { c, a, b } });

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            await response.Content.ReadAsStringAsync());

        var payload = await response.Content.ReadFromJsonAsync<ReorderQueueResponse>(JsonOptions);
        payload!.NewOrder.Should().Equal(new[] { c, a, b });

        // DB reflects the new 1-based positions.
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var positions = await db.AgentSessions
            .Where(s => s.RuntimeId == runtimeId)
            .ToDictionaryAsync(s => s.Id, s => s.QueuePosition);
        positions[c].Should().Be(1);
        positions[a].Should().Be(2);
        positions[b].Should().Be(3);
    }

    [Fact]
    public async Task Reorder_Mismatch_Returns400_AndDbUnchanged()
    {
        var (client, _) = await RegisterUserAsync();
        var runtimeId = Guid.NewGuid();
        var (a, b, c) = await SeedThreeQueuedAsync(runtimeId);

        // Omit `b` — set mismatch.
        var response = await client.PutAsJsonAsync(
            $"/api/runtimes/{runtimeId}/queue/reorder",
            new { sessionIds = new[] { c, a } });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            await response.Content.ReadAsStringAsync());

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("queue mismatch");

        // Original positions intact — controller returned 400 before any
        // partial mutation could land.
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var positions = await db.AgentSessions
            .Where(s => s.RuntimeId == runtimeId)
            .ToDictionaryAsync(s => s.Id, s => s.QueuePosition);
        positions[a].Should().Be(1);
        positions[b].Should().Be(2);
        positions[c].Should().Be(3);
    }

    [Fact]
    public async Task Reorder_Unauthenticated_Returns401()
    {
        var response = await Client.PutAsJsonAsync(
            $"/api/runtimes/{Guid.NewGuid()}/queue/reorder",
            new { sessionIds = Array.Empty<Guid>() });
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ======================================================================
    // GET /api/runtimes/{runtimeId}/queue
    // ======================================================================

    [Fact]
    public async Task GetQueue_EmptyQueue_Returns200_WithEmptyEntriesAndNullActives()
    {
        var (client, _) = await RegisterUserAsync();
        var runtimeId = await SeedOnlineRuntimeAsync();

        var response = await client.GetAsync($"/api/runtimes/{runtimeId}/queue");

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            await response.Content.ReadAsStringAsync());

        var payload = await response.Content.ReadFromJsonAsync<QueueResponse>(JsonOptions);
        payload!.Entries.Should().BeEmpty();
        payload.RunningSessionId.Should().BeNull();
        payload.CancelingSessionId.Should().BeNull();
    }

    [Fact]
    public async Task GetQueue_OneRunningPlusTwoQueued_ReturnsRunningIdAndOrderedEntries()
    {
        var (client, _) = await RegisterUserAsync();
        var runtimeId = await SeedOnlineRuntimeAsync();
        var runningId = await SeedRunningSessionAsync(runtimeId);
        var (a, b) = await SeedTwoQueuedAsync(runtimeId);

        var response = await client.GetAsync($"/api/runtimes/{runtimeId}/queue");

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            await response.Content.ReadAsStringAsync());

        var payload = await response.Content.ReadFromJsonAsync<QueueResponse>(JsonOptions);
        payload!.RunningSessionId.Should().Be(runningId);
        payload.CancelingSessionId.Should().BeNull();
        payload.Entries.Should().HaveCount(2);
        payload.Entries.Select(e => e.SessionId).Should().Equal(new[] { a, b });
        payload.Entries.Select(e => e.QueuePosition).Should().Equal(new[] { 1, 2 });
        payload.Entries.Should().AllSatisfy(e =>
            e.Status.Should().Be(AgentSessionStatus.Pending));
    }

    [Fact]
    public async Task GetQueue_CancelingSession_ReportedSeparatelyFromRunning()
    {
        var (client, _) = await RegisterUserAsync();
        var runtimeId = await SeedOnlineRuntimeAsync();
        var cancelingId = await SeedCancelingSessionAsync(runtimeId);

        var response = await client.GetAsync($"/api/runtimes/{runtimeId}/queue");

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            await response.Content.ReadAsStringAsync());

        var payload = await response.Content.ReadFromJsonAsync<QueueResponse>(JsonOptions);
        payload!.RunningSessionId.Should().BeNull();
        payload.CancelingSessionId.Should().Be(cancelingId);
        payload.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task GetQueue_LongPrompt_TruncatedTo120CharsWithEllipsis()
    {
        var (client, _) = await RegisterUserAsync();
        var runtimeId = await SeedOnlineRuntimeAsync();

        var longPrompt = new string('x', 200);
        await SeedQueuedSessionWithPromptAsync(runtimeId, longPrompt, position: 1);

        var response = await client.GetAsync($"/api/runtimes/{runtimeId}/queue");
        var payload = await response.Content.ReadFromJsonAsync<QueueResponse>(JsonOptions);

        payload!.Entries.Should().HaveCount(1);
        payload.Entries[0].PromptPreview.Should().HaveLength(123); // 120 + "..."
        payload.Entries[0].PromptPreview.Should().EndWith("...");
    }

    [Fact]
    public async Task GetQueue_CrossRuntimeIsolation_OtherRuntimeSessionsHidden()
    {
        var (client, _) = await RegisterUserAsync();
        var runtimeA = await SeedOnlineRuntimeAsync();
        var runtimeB = await SeedOnlineRuntimeAsync();

        // Pollute runtime B with a Running + queued sessions
        await SeedRunningSessionAsync(runtimeB);
        await SeedTwoQueuedAsync(runtimeB);

        // A is empty
        var response = await client.GetAsync($"/api/runtimes/{runtimeA}/queue");
        var payload = await response.Content.ReadFromJsonAsync<QueueResponse>(JsonOptions);

        payload!.Entries.Should().BeEmpty();
        payload.RunningSessionId.Should().BeNull();
        payload.CancelingSessionId.Should().BeNull();
    }

    [Fact]
    public async Task GetQueue_MissingRuntime_Returns404()
    {
        var (client, _) = await RegisterUserAsync();

        var response = await client.GetAsync($"/api/runtimes/{Guid.NewGuid()}/queue");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetQueue_Unauthenticated_Returns401()
    {
        var response = await Client.GetAsync($"/api/runtimes/{Guid.NewGuid()}/queue");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ----------------------------------------------------------------------
    // helpers
    // ----------------------------------------------------------------------

    private async Task<Guid> SeedOnlineRuntimeAsync()
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var runtime = new ProjectRuntime
        {
            ProjectId = Guid.NewGuid(),
            State = RuntimeState.Online,
            Region = "arn",
        };
        db.ProjectRuntimes.Add(runtime);
        await db.SaveChangesAsync();
        return runtime.Id;
    }

    private async Task<Guid> SeedRunningSessionAsync(Guid runtimeId)
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var conversation = new Conversation
        {
            Id = Guid.NewGuid(),
            ProjectId = Guid.NewGuid(),
            Title = "running-test",
            BranchId = Guid.NewGuid(),
            Status = ConversationStatus.Active,
            LastActivityAt = DateTime.UtcNow,
        };
        db.Conversations.Add(conversation);

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
        await db.SaveChangesAsync();
        return session.Id;
    }

    private async Task<Guid> SeedCancelingSessionAsync(Guid runtimeId)
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var conversation = new Conversation
        {
            Id = Guid.NewGuid(),
            ProjectId = Guid.NewGuid(),
            Title = "canceling-test",
            BranchId = Guid.NewGuid(),
            Status = ConversationStatus.Active,
            LastActivityAt = DateTime.UtcNow,
        };
        db.Conversations.Add(conversation);

        var session = new AgentSession
        {
            Id = Guid.NewGuid(),
            ConversationId = conversation.Id,
            RuntimeId = runtimeId,
            Prompt = "canceling",
            Status = AgentSessionStatus.Canceling,
            StartedAt = DateTime.UtcNow.AddSeconds(-10),
            CancelReason = "user",
        };
        db.AgentSessions.Add(session);
        await db.SaveChangesAsync();
        return session.Id;
    }

    private async Task<(Guid A, Guid B)> SeedTwoQueuedAsync(Guid runtimeId)
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var conversation = new Conversation
        {
            Id = Guid.NewGuid(),
            ProjectId = Guid.NewGuid(),
            Title = "queued-test",
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

        var a = Make(1);
        var b = Make(2);
        db.AgentSessions.AddRange(a, b);
        await db.SaveChangesAsync();
        return (a.Id, b.Id);
    }

    private async Task SeedQueuedSessionWithPromptAsync(Guid runtimeId, string prompt, int position)
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var conversation = new Conversation
        {
            Id = Guid.NewGuid(),
            ProjectId = Guid.NewGuid(),
            Title = "long-prompt-test",
            BranchId = Guid.NewGuid(),
            Status = ConversationStatus.Active,
            LastActivityAt = DateTime.UtcNow,
        };
        db.Conversations.Add(conversation);

        var session = new AgentSession
        {
            Id = Guid.NewGuid(),
            ConversationId = conversation.Id,
            RuntimeId = runtimeId,
            Prompt = prompt,
            Status = AgentSessionStatus.Pending,
            QueuePosition = position,
        };
        db.AgentSessions.Add(session);
        await db.SaveChangesAsync();
    }

    private async Task<(Guid A, Guid B, Guid C)> SeedThreeQueuedAsync(Guid runtimeId)
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var conversation = new Conversation
        {
            Id = Guid.NewGuid(),
            ProjectId = Guid.NewGuid(),
            Title = "test",
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

        var a = Make(1);
        var b = Make(2);
        var c = Make(3);
        db.AgentSessions.AddRange(a, b, c);
        await db.SaveChangesAsync();
        return (a.Id, b.Id, c.Id);
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
        return (client, user!.Id);
    }
}
