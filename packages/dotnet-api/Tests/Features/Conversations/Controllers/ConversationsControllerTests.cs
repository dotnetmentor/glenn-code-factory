using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Source.Features.Conversations.Models;
using Source.Features.Users.Models;
using Source.Infrastructure;

namespace Api.Tests.Features.Conversations.Controllers;

/// <summary>
/// End-to-end HTTP tests for the conversations / sessions / events read surface.
/// Mirrors <see cref="Api.Tests.Features.RuntimeBootstrap.BootstrapRunsControllerTests"/>:
/// real auth via <c>/api/auth/register</c>, fresh in-memory DB per factory,
/// rows seeded straight into the test DB.
///
/// <para>The controller is the boring kind — four reads with projections — so
/// the test surface is correspondingly broad: every filter / cursor / cap gets
/// a row, plus the auth gate, plus the 404 paths.</para>
/// </summary>
[Collection(HangfireTestCollection.Name)]
public class ConversationsControllerTests : IntegrationTestBase
{
    private const string Password = "Password123!";

    // The user registered for the current test. Seed helpers create an owned
    // Project for each project id they touch so the controller's ownership gate
    // (CallerOwnsProjectAsync) passes — without a Project row owned by the caller
    // every read returns 404.
    private string? _callerUserId;

    /// <summary>
    /// Match the API's controller JSON config (<c>AddJsonOptions</c>) so we deserialise
    /// the enum responses (<see cref="ConversationStatus"/>, <see cref="AgentSessionStatus"/>,
    /// <see cref="AgentEventType"/>) from their string form rather than the default
    /// integer form.
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    // ----------------------------------------------------------------------
    // ListConversations
    // ----------------------------------------------------------------------

    [Fact]
    public async Task ListConversations_ReturnsConversationsForProject()
    {
        var (client, _) = await RegisterUserAsync();
        var projectA = Guid.NewGuid();
        var projectB = Guid.NewGuid();

        await SeedConversationAsync(projectA, "A1");
        await SeedConversationAsync(projectA, "A2");
        await SeedConversationAsync(projectB, "B1");

        var response = await client.GetAsync($"/api/projects/{projectA}/conversations");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var items = await response.Content.ReadFromJsonAsync<List<ConversationSummary>>(JsonOptions);
        items.Should().NotBeNull();
        items!.Should().HaveCount(2);
        items.Should().OnlyContain(c => c.ProjectId == projectA);
        items.Select(c => c.Title).Should().BeEquivalentTo(new[] { "A1", "A2" });
    }

    [Fact]
    public async Task ListConversations_OrderedByLastActivityAtDesc()
    {
        var (client, _) = await RegisterUserAsync();
        var projectId = Guid.NewGuid();

        var older   = await SeedConversationAsync(projectId, "older");
        var middle  = await SeedConversationAsync(projectId, "middle");
        var newest  = await SeedConversationAsync(projectId, "newest");

        // Stamp LastActivityAt explicitly. Auto-stamped audit fields won't drive
        // the order — we want a deterministic spread.
        using (var scope = CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (await db.Conversations.FirstAsync(c => c.Id == older.Id)).LastActivityAt = DateTime.UtcNow.AddHours(-2);
            (await db.Conversations.FirstAsync(c => c.Id == middle.Id)).LastActivityAt = DateTime.UtcNow.AddHours(-1);
            (await db.Conversations.FirstAsync(c => c.Id == newest.Id)).LastActivityAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        var response = await client.GetAsync($"/api/projects/{projectId}/conversations");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var items = await response.Content.ReadFromJsonAsync<List<ConversationSummary>>(JsonOptions);
        items!.Select(c => c.Title).Should().ContainInOrder("newest", "middle", "older");
    }

    [Fact]
    public async Task ListConversations_ExcludesArchivedByDefault()
    {
        var (client, _) = await RegisterUserAsync();
        var projectId = Guid.NewGuid();

        await SeedConversationAsync(projectId, "active1");
        await SeedConversationAsync(projectId, "active2");
        await SeedConversationAsync(projectId, "archived", status: ConversationStatus.Archived);

        var response = await client.GetAsync($"/api/projects/{projectId}/conversations");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var items = await response.Content.ReadFromJsonAsync<List<ConversationSummary>>(JsonOptions);
        items!.Should().HaveCount(2, "global query filter hides archived rows");
        items.Should().OnlyContain(c => c.Status == ConversationStatus.Active);
    }

    [Fact]
    public async Task ListConversations_IncludesArchivedWhenRequested()
    {
        var (client, _) = await RegisterUserAsync();
        var projectId = Guid.NewGuid();

        await SeedConversationAsync(projectId, "active1");
        await SeedConversationAsync(projectId, "active2");
        await SeedConversationAsync(projectId, "archived", status: ConversationStatus.Archived);

        var response = await client.GetAsync($"/api/projects/{projectId}/conversations?includeArchived=true");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var items = await response.Content.ReadFromJsonAsync<List<ConversationSummary>>(JsonOptions);
        items!.Should().HaveCount(3);
        items.Select(c => c.Status).Should().Contain(ConversationStatus.Archived);
    }

    [Fact]
    public async Task ListConversations_PaginationRespected()
    {
        var (client, _) = await RegisterUserAsync();
        var projectId = Guid.NewGuid();

        // Seed 5 conversations with explicit LastActivityAt so we know the order
        // skip/take is slicing through.
        var ids = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            var c = await SeedConversationAsync(projectId, $"c{i}");
            ids.Add(c.Id);
        }
        using (var scope = CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            for (var i = 0; i < ids.Count; i++)
            {
                var row = await db.Conversations.FirstAsync(c => c.Id == ids[i]);
                row.LastActivityAt = DateTime.UtcNow.AddMinutes(-(ids.Count - 1 - i));
            }
            await db.SaveChangesAsync();
        }

        // skip=2 take=2 over [c4, c3, c2, c1, c0] (newest first) = [c2, c1].
        var response = await client.GetAsync($"/api/projects/{projectId}/conversations?skip=2&take=2");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var items = await response.Content.ReadFromJsonAsync<List<ConversationSummary>>(JsonOptions);
        items!.Should().HaveCount(2);
        items.Select(c => c.Title).Should().ContainInOrder("c2", "c1");
    }

    [Fact]
    public async Task ListConversations_TakeCappedAt200()
    {
        var (client, _) = await RegisterUserAsync();
        var projectId = Guid.NewGuid();

        // Seed 250 rows; if take=500 wasn't capped the response would carry 250.
        using (var scope = CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await EnsureOwnedProjectAsync(db, projectId);
            for (var i = 0; i < 250; i++)
            {
                db.Conversations.Add(new Conversation
                {
                    Id = Guid.NewGuid(),
                    ProjectId = projectId,
                    Title = $"c{i}",
                });
            }
            await db.SaveChangesAsync();
        }

        var response = await client.GetAsync($"/api/projects/{projectId}/conversations?take=500");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var items = await response.Content.ReadFromJsonAsync<List<ConversationSummary>>(JsonOptions);
        items!.Should().HaveCount(200, "take is hard-capped at 200");
    }

    [Fact]
    public async Task ListConversations_LatestSessionStatus_Populated()
    {
        var (client, _) = await RegisterUserAsync();
        var projectId = Guid.NewGuid();

        var conversation = await SeedConversationAsync(projectId, "with-sessions");

        // Three sessions, latest by CreatedAt is Running. Backdate the older two
        // explicitly so the audit-stamp ordering is unambiguous.
        var s1 = await SeedSessionAsync(conversation.Id, AgentSessionStatus.Pending);
        var s2 = await SeedSessionAsync(conversation.Id, AgentSessionStatus.Succeeded);
        var s3 = await SeedSessionAsync(conversation.Id, AgentSessionStatus.Running);
        using (var scope = CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (await db.AgentSessions.FirstAsync(s => s.Id == s1.Id)).CreatedAt = DateTime.UtcNow.AddMinutes(-10);
            (await db.AgentSessions.FirstAsync(s => s.Id == s2.Id)).CreatedAt = DateTime.UtcNow.AddMinutes(-5);
            (await db.AgentSessions.FirstAsync(s => s.Id == s3.Id)).CreatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        var response = await client.GetAsync($"/api/projects/{projectId}/conversations");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var items = await response.Content.ReadFromJsonAsync<List<ConversationSummary>>(JsonOptions);
        var summary = items!.Single();
        summary.LatestSessionStatus.Should().Be(AgentSessionStatus.Running);
    }

    [Fact]
    public async Task ListConversations_NoSessions_LatestSessionStatusIsNull()
    {
        var (client, _) = await RegisterUserAsync();
        var projectId = Guid.NewGuid();

        await SeedConversationAsync(projectId, "fresh");

        var response = await client.GetAsync($"/api/projects/{projectId}/conversations");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var items = await response.Content.ReadFromJsonAsync<List<ConversationSummary>>(JsonOptions);
        var summary = items!.Single();
        summary.LatestSessionStatus.Should().BeNull("a conversation with no sessions has no latest status");
    }

    // ----------------------------------------------------------------------
    // GetConversation
    // ----------------------------------------------------------------------

    [Fact]
    public async Task GetConversation_ReturnsDetailWithSessions()
    {
        var (client, _) = await RegisterUserAsync();
        var projectId = Guid.NewGuid();

        var conversation = await SeedConversationAsync(projectId, "detail");
        var sessionA = await SeedSessionAsync(conversation.Id, AgentSessionStatus.Succeeded, "first prompt");
        var sessionB = await SeedSessionAsync(conversation.Id, AgentSessionStatus.Running,   "second prompt");

        using (var scope = CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (await db.AgentSessions.FirstAsync(s => s.Id == sessionA.Id)).CreatedAt = DateTime.UtcNow.AddMinutes(-5);
            (await db.AgentSessions.FirstAsync(s => s.Id == sessionB.Id)).CreatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        var response = await client.GetAsync($"/api/conversations/{conversation.Id}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var detail = await response.Content.ReadFromJsonAsync<ConversationDetail>(JsonOptions);
        detail!.Id.Should().Be(conversation.Id);
        detail.Sessions.Should().HaveCount(2);
        detail.Sessions.Select(s => s.Prompt).Should()
            .ContainInOrder("first prompt", "second prompt");
    }

    [Fact]
    public async Task GetConversation_UnknownId_Returns404()
    {
        var (client, _) = await RegisterUserAsync();
        var response = await client.GetAsync($"/api/conversations/{Guid.NewGuid()}");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetConversation_ArchivedConversation_StillReturned()
    {
        var (client, _) = await RegisterUserAsync();
        var projectId = Guid.NewGuid();

        var archived = await SeedConversationAsync(
            projectId, "archived", status: ConversationStatus.Archived);

        var response = await client.GetAsync($"/api/conversations/{archived.Id}");
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "GetConversation always uses IgnoreQueryFilters() so admins / unarchive flows can fetch archived rows by id");

        var detail = await response.Content.ReadFromJsonAsync<ConversationDetail>(JsonOptions);
        detail!.Status.Should().Be(ConversationStatus.Archived);
    }

    // ----------------------------------------------------------------------
    // GetSession
    // ----------------------------------------------------------------------

    [Fact]
    public async Task GetSession_ReturnsDetail()
    {
        var (client, _) = await RegisterUserAsync();
        var projectId = Guid.NewGuid();

        var conversation = await SeedConversationAsync(projectId, "host");
        var session = await SeedSessionAsync(conversation.Id, AgentSessionStatus.Running, "prompt");
        await SeedEventsAsync(session.Id, count: 5);

        var response = await client.GetAsync($"/api/sessions/{session.Id}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var detail = await response.Content.ReadFromJsonAsync<SessionDetail>(JsonOptions);
        detail!.Id.Should().Be(session.Id);
        detail.ConversationId.Should().Be(conversation.Id);
        detail.Status.Should().Be(AgentSessionStatus.Running);
        detail.Prompt.Should().Be("prompt");
        detail.EventCount.Should().Be(5);
    }

    [Fact]
    public async Task GetSession_UnknownId_Returns404()
    {
        var (client, _) = await RegisterUserAsync();
        var response = await client.GetAsync($"/api/sessions/{Guid.NewGuid()}");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ----------------------------------------------------------------------
    // GetEvents
    // ----------------------------------------------------------------------

    [Fact]
    public async Task GetEvents_ReturnsEventsForSession()
    {
        var (client, _) = await RegisterUserAsync();
        var projectId = Guid.NewGuid();

        var conversation = await SeedConversationAsync(projectId, "events-host");
        var session = await SeedSessionAsync(conversation.Id, AgentSessionStatus.Running);
        await SeedEventsAsync(session.Id, count: 3);

        var response = await client.GetAsync($"/api/sessions/{session.Id}/events");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var events = await response.Content.ReadFromJsonAsync<List<AgentEventDto>>(JsonOptions);
        events!.Should().HaveCount(3);
        events.Select(e => e.Sequence).Should().ContainInOrder(1, 2, 3);
    }

    [Fact]
    public async Task GetEvents_SinceCursor_ReturnsOnlyNewer()
    {
        var (client, _) = await RegisterUserAsync();
        var projectId = Guid.NewGuid();

        var conversation = await SeedConversationAsync(projectId, "cursor-host");
        var session = await SeedSessionAsync(conversation.Id, AgentSessionStatus.Running);
        // Sequences 0..9 — note the seeder writes Sequence=i so since=4 should
        // return events with sequences 5..9.
        await SeedEventsAsync(session.Id, count: 10, startSequence: 0);

        var response = await client.GetAsync($"/api/sessions/{session.Id}/events?since=4");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var events = await response.Content.ReadFromJsonAsync<List<AgentEventDto>>(JsonOptions);
        events!.Should().HaveCount(5);
        events.Select(e => e.Sequence).Should().ContainInOrder(5, 6, 7, 8, 9);
    }

    [Fact]
    public async Task GetEvents_LimitRespected()
    {
        var (client, _) = await RegisterUserAsync();
        var projectId = Guid.NewGuid();

        var conversation = await SeedConversationAsync(projectId, "limit-host");
        var session = await SeedSessionAsync(conversation.Id, AgentSessionStatus.Running);
        await SeedEventsAsync(session.Id, count: 10);

        var response = await client.GetAsync($"/api/sessions/{session.Id}/events?limit=3");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var events = await response.Content.ReadFromJsonAsync<List<AgentEventDto>>(JsonOptions);
        events!.Should().HaveCount(3);
        events.Select(e => e.Sequence).Should().ContainInOrder(1, 2, 3);
    }

    [Fact]
    public async Task GetEvents_LimitCappedAt1000()
    {
        var (client, _) = await RegisterUserAsync();
        var projectId = Guid.NewGuid();

        var conversation = await SeedConversationAsync(projectId, "cap-host");
        var session = await SeedSessionAsync(conversation.Id, AgentSessionStatus.Running);
        // Seed 1500 to prove limit=5000 doesn't return everything.
        await SeedEventsAsync(session.Id, count: 1500);

        var response = await client.GetAsync($"/api/sessions/{session.Id}/events?limit=5000");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var events = await response.Content.ReadFromJsonAsync<List<AgentEventDto>>(JsonOptions);
        events!.Should().HaveCount(1000, "limit is hard-capped at 1000");
    }

    [Fact]
    public async Task GetEvents_UnknownSessionId_Returns404()
    {
        var (client, _) = await RegisterUserAsync();
        var response = await client.GetAsync($"/api/sessions/{Guid.NewGuid()}/events");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "even with zero events the client needs 404 to know the session id is wrong");
    }

    [Fact]
    public async Task GetEvents_ExistingSessionNoEventsAfterCursor_ReturnsEmptyArray()
    {
        var (client, _) = await RegisterUserAsync();
        var projectId = Guid.NewGuid();

        var conversation = await SeedConversationAsync(projectId, "no-new-events");
        var session = await SeedSessionAsync(conversation.Id, AgentSessionStatus.Running);
        await SeedEventsAsync(session.Id, count: 5);

        var response = await client.GetAsync($"/api/sessions/{session.Id}/events?since=999");
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "existing session + no matching events must be 200 with empty list, not 404");

        var events = await response.Content.ReadFromJsonAsync<List<AgentEventDto>>(JsonOptions);
        events.Should().NotBeNull();
        events!.Should().BeEmpty();
    }

    // ----------------------------------------------------------------------
    // RenameConversation
    // ----------------------------------------------------------------------

    [Fact]
    public async Task Rename_ValidTitle_UpdatesAndReturnsDetail()
    {
        var (client, _) = await RegisterUserAsync();
        var projectId = Guid.NewGuid();
        var conversation = await SeedConversationAsync(projectId, "old title");

        var response = await client.PostAsJsonAsync(
            $"/api/conversations/{conversation.Id}/rename",
            new { title = "new title" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var detail = await response.Content.ReadFromJsonAsync<ConversationDetail>(JsonOptions);
        detail!.Id.Should().Be(conversation.Id);
        detail.Title.Should().Be("new title");

        // Persisted.
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var stored = await db.Conversations.FirstAsync(c => c.Id == conversation.Id);
        stored.Title.Should().Be("new title");
    }

    [Fact]
    public async Task Rename_EmptyTitle_Returns400()
    {
        var (client, _) = await RegisterUserAsync();
        var projectId = Guid.NewGuid();
        var conversation = await SeedConversationAsync(projectId, "before");

        var response = await client.PostAsJsonAsync(
            $"/api/conversations/{conversation.Id}/rename",
            new { title = "" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // Title not mutated.
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var stored = await db.Conversations.FirstAsync(c => c.Id == conversation.Id);
        stored.Title.Should().Be("before");
    }

    [Fact]
    public async Task Rename_WhitespaceTitle_Returns400()
    {
        var (client, _) = await RegisterUserAsync();
        var projectId = Guid.NewGuid();
        var conversation = await SeedConversationAsync(projectId, "before");

        var response = await client.PostAsJsonAsync(
            $"/api/conversations/{conversation.Id}/rename",
            new { title = "   " });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "whitespace-only titles count as empty");
    }

    [Fact]
    public async Task Rename_TitleTooLong_Returns400()
    {
        var (client, _) = await RegisterUserAsync();
        var projectId = Guid.NewGuid();
        var conversation = await SeedConversationAsync(projectId, "before");

        var response = await client.PostAsJsonAsync(
            $"/api/conversations/{conversation.Id}/rename",
            new { title = new string('x', 250) });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Rename_UnknownId_Returns404()
    {
        var (client, _) = await RegisterUserAsync();

        var response = await client.PostAsJsonAsync(
            $"/api/conversations/{Guid.NewGuid()}/rename",
            new { title = "anything" });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Rename_ArchivedConversation_StillWorks()
    {
        var (client, _) = await RegisterUserAsync();
        var projectId = Guid.NewGuid();
        var conversation = await SeedConversationAsync(
            projectId, "old", status: ConversationStatus.Archived);

        var response = await client.PostAsJsonAsync(
            $"/api/conversations/{conversation.Id}/rename",
            new { title = "renamed" });

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "rename uses IgnoreQueryFilters() — archived conversations are renameable");

        var detail = await response.Content.ReadFromJsonAsync<ConversationDetail>(JsonOptions);
        detail!.Title.Should().Be("renamed");
        detail.Status.Should().Be(ConversationStatus.Archived,
            "rename does not affect status");
    }

    [Fact]
    public async Task Rename_TitleTrimmed()
    {
        var (client, _) = await RegisterUserAsync();
        var projectId = Guid.NewGuid();
        var conversation = await SeedConversationAsync(projectId, "before");

        var response = await client.PostAsJsonAsync(
            $"/api/conversations/{conversation.Id}/rename",
            new { title = "   spaced out   " });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var stored = await db.Conversations.FirstAsync(c => c.Id == conversation.Id);
        stored.Title.Should().Be("spaced out", "title is trimmed before save");
    }

    // ----------------------------------------------------------------------
    // ArchiveConversation
    // ----------------------------------------------------------------------

    [Fact]
    public async Task Archive_ActiveConversation_TransitionsToArchived()
    {
        var (client, _) = await RegisterUserAsync();
        var projectId = Guid.NewGuid();
        var conversation = await SeedConversationAsync(projectId, "to-archive");

        var response = await client.PostAsync(
            $"/api/conversations/{conversation.Id}/archive", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Persisted as Archived.
        using (var scope = CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var stored = await db.Conversations
                .IgnoreQueryFilters()
                .FirstAsync(c => c.Id == conversation.Id);
            stored.Status.Should().Be(ConversationStatus.Archived);
        }

        // Default list now excludes it.
        var listResponse = await client.GetAsync($"/api/projects/{projectId}/conversations");
        listResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var items = await listResponse.Content.ReadFromJsonAsync<List<ConversationSummary>>(JsonOptions);
        items!.Should().BeEmpty("global query filter hides newly-archived rows");
    }

    [Fact]
    public async Task Archive_AlreadyArchived_NoOpReturns204()
    {
        var (client, _) = await RegisterUserAsync();
        var projectId = Guid.NewGuid();
        var conversation = await SeedConversationAsync(
            projectId, "already-archived", status: ConversationStatus.Archived);

        var response = await client.PostAsync(
            $"/api/conversations/{conversation.Id}/archive", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent,
            "archive is idempotent — already-archived returns 204");
    }

    [Fact]
    public async Task Archive_UnknownId_Returns404()
    {
        var (client, _) = await RegisterUserAsync();

        var response = await client.PostAsync(
            $"/api/conversations/{Guid.NewGuid()}/archive", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ----------------------------------------------------------------------
    // UnarchiveConversation
    // ----------------------------------------------------------------------

    [Fact]
    public async Task Unarchive_ArchivedConversation_TransitionsToActive()
    {
        var (client, _) = await RegisterUserAsync();
        var projectId = Guid.NewGuid();
        var conversation = await SeedConversationAsync(
            projectId, "to-restore", status: ConversationStatus.Archived);

        var response = await client.PostAsync(
            $"/api/conversations/{conversation.Id}/unarchive", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Persisted as Active.
        using (var scope = CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var stored = await db.Conversations.FirstAsync(c => c.Id == conversation.Id);
            stored.Status.Should().Be(ConversationStatus.Active);
        }

        // Default list now includes it again.
        var listResponse = await client.GetAsync($"/api/projects/{projectId}/conversations");
        listResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var items = await listResponse.Content.ReadFromJsonAsync<List<ConversationSummary>>(JsonOptions);
        items!.Should().HaveCount(1);
        items.Single().Title.Should().Be("to-restore");
    }

    [Fact]
    public async Task Unarchive_AlreadyActive_NoOpReturns204()
    {
        var (client, _) = await RegisterUserAsync();
        var projectId = Guid.NewGuid();
        var conversation = await SeedConversationAsync(projectId, "already-active");

        var response = await client.PostAsync(
            $"/api/conversations/{conversation.Id}/unarchive", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent,
            "unarchive is idempotent — already-active returns 204");
    }

    [Fact]
    public async Task Unarchive_UnknownId_Returns404()
    {
        var (client, _) = await RegisterUserAsync();

        var response = await client.PostAsync(
            $"/api/conversations/{Guid.NewGuid()}/unarchive", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ----------------------------------------------------------------------
    // Auth
    // ----------------------------------------------------------------------

    [Fact]
    public async Task ListConversations_Anonymous_Returns401()
    {
        // Confirm [Authorize] is wired on the controller. The other endpoints
        // share the attribute via the controller-class declaration so this single
        // probe covers the gate.
        var response = await Client.GetAsync($"/api/projects/{Guid.NewGuid()}/conversations");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ----------------------------------------------------------------------
    // helpers
    // ----------------------------------------------------------------------

    private async Task<Conversation> SeedConversationAsync(
        Guid projectId,
        string title,
        ConversationStatus status = ConversationStatus.Active)
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var conversation = new Conversation
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Title = title,
            Status = status,
            LastActivityAt = DateTime.UtcNow,
        };
        await EnsureOwnedProjectAsync(db, projectId);
        db.Conversations.Add(conversation);
        await db.SaveChangesAsync();
        return conversation;
    }

    private async Task<AgentSession> SeedSessionAsync(
        Guid conversationId,
        AgentSessionStatus status,
        string prompt = "test prompt")
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var session = new AgentSession
        {
            Id = Guid.NewGuid(),
            ConversationId = conversationId,
            Prompt = prompt,
            Status = status,
        };
        db.AgentSessions.Add(session);
        await db.SaveChangesAsync();
        return session;
    }

    /// <summary>
    /// Insert <paramref name="count"/> <see cref="AgentEvent"/> rows under
    /// <paramref name="sessionId"/> with sequences <paramref name="startSequence"/>..
    /// startSequence + count - 1. <c>CreatedAt</c> is set explicitly because
    /// <see cref="AgentEvent"/> deliberately does not implement <c>IAuditable</c>
    /// (see the entity comment).
    /// </summary>
    private async Task SeedEventsAsync(Guid sessionId, int count, int startSequence = 1)
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        for (var i = 0; i < count; i++)
        {
            db.AgentEvents.Add(new AgentEvent
            {
                SessionId = sessionId,
                Sequence = startSequence + i,
                Kind = AgentEventKind.AssistantText,
                Text = "seeded text",
                CreatedAt = DateTime.UtcNow,
            });
        }
        await db.SaveChangesAsync();
    }

    private async Task<(HttpClient Client, string UserId)> RegisterUserAsync()
    {
        await SeedRolesAsync();

        var email = $"user-{Guid.NewGuid():N}@example.com";
        var registerClient = Factory.CreateClient();
        var response = await registerClient.PostAsJsonAsync("/api/auth/register", new { email, password = Password });
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

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
    /// Idempotent. The controller's CallerOwnsProjectAsync gate 404s without it.
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
