using Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Source.Features.Conversations.Models;

namespace Api.Tests.Features.Conversations;

/// <summary>
/// Smoke tests for the <see cref="Conversation"/> / <see cref="AgentSession"/> /
/// <see cref="AgentEvent"/> entity layer. We don't exercise any business
/// behaviour here — that's owned by follow-up cards. We just verify the EF
/// model is wired up correctly:
///
/// <list type="bullet">
///   <item>Round-trip persistence works for each entity, defaults are applied,
///         and audit fields are auto-stamped.</item>
///   <item>Composite key on <see cref="AgentEvent"/> is in the model.</item>
///   <item>Cascade deletes flow Conversation → Session → Event.</item>
///   <item><see cref="ConversationStatus"/> persists as a string and
///         <see cref="AgentEvent.Args"/> / <see cref="AgentEvent.Result"/> as
///         <c>jsonb</c> — verified by reading the migration file directly
///         because the in-memory provider strips relational metadata at runtime.</item>
///   <item>Archived conversations are hidden by the global query filter.</item>
/// </list>
///
/// Mirrors <c>ProjectRuntimeEntityTests</c> — same provider, same approach.
/// </summary>
public class ConversationEntityTests : HandlerTestBase
{
    [Fact]
    public async Task Can_save_and_retrieve_Conversation()
    {
        var conversation = new Conversation
        {
            ProjectId = Guid.NewGuid(),
            Title = "Build me a login page",
        };

        Context.Conversations.Add(conversation);
        await Context.SaveChangesAsync();

        Context.ChangeTracker.Clear();

        var reloaded = await Context.Conversations.SingleAsync(c => c.Id == conversation.Id);

        reloaded.ProjectId.Should().Be(conversation.ProjectId);
        reloaded.Title.Should().Be("Build me a login page");
        reloaded.BranchId.Should().Be(conversation.BranchId, "branch FK round-trips");
        reloaded.Status.Should().Be(ConversationStatus.Active, "default starting status");
        reloaded.EventCount.Should().Be(0);
        reloaded.CreatedAt.Should().NotBe(default, "auto-set by SaveChangesAsync");
        reloaded.UpdatedAt.Should().NotBe(default, "auto-set by SaveChangesAsync");
    }

    [Fact]
    public async Task Can_save_and_retrieve_AgentSession()
    {
        var conversation = new Conversation
        {
            ProjectId = Guid.NewGuid(),
            Title = "test",
        };
        Context.Conversations.Add(conversation);
        await Context.SaveChangesAsync();

        var session = new AgentSession
        {
            ConversationId = conversation.Id,
            Prompt = "Add a button",
        };
        Context.AgentSessions.Add(session);
        await Context.SaveChangesAsync();

        Context.ChangeTracker.Clear();

        var reloaded = await Context.AgentSessions.SingleAsync(s => s.Id == session.Id);

        reloaded.ConversationId.Should().Be(conversation.Id);
        reloaded.Prompt.Should().Be("Add a button");
        reloaded.Status.Should().Be(AgentSessionStatus.Pending, "default starting status");
        reloaded.AgentId.Should().BeNull();
        reloaded.StartedAt.Should().BeNull();
        reloaded.CompletedAt.Should().BeNull();
        reloaded.FailureReason.Should().BeNull();
        reloaded.CreatedAt.Should().NotBe(default, "auto-set by SaveChangesAsync");
        reloaded.UpdatedAt.Should().NotBe(default, "auto-set by SaveChangesAsync");
    }

    [Fact]
    public async Task Can_save_and_retrieve_AgentEvent()
    {
        var conversation = new Conversation { ProjectId = Guid.NewGuid(), Title = "test" };
        Context.Conversations.Add(conversation);
        await Context.SaveChangesAsync();

        var session = new AgentSession { ConversationId = conversation.Id, Prompt = "go" };
        Context.AgentSessions.Add(session);
        await Context.SaveChangesAsync();

        var evt = new AgentEvent
        {
            SessionId = session.Id,
            Sequence = 1,
            Kind = AgentEventKind.PromptReceived,
            Text = "go",
            CreatedAt = DateTime.UtcNow,
        };
        Context.AgentEvents.Add(evt);
        await Context.SaveChangesAsync();

        Context.ChangeTracker.Clear();

        var reloaded = await Context.AgentEvents
            .SingleAsync(e => e.SessionId == session.Id && e.Sequence == 1);

        reloaded.Kind.Should().Be(AgentEventKind.PromptReceived);
        reloaded.Text.Should().Be("go");
        reloaded.CreatedAt.Should().NotBe(default);
    }

    [Fact]
    public void AgentEvent_composite_PK_is_on_SessionId_and_Sequence()
    {
        // The in-memory provider does not always enforce composite PK uniqueness
        // the way Postgres would. What we actually care about is that the model
        // metadata declares the PK as (SessionId, Sequence) — the relational
        // migration then translates that to a real PK constraint on Postgres.
        var entityType = Context.Model.FindEntityType(typeof(AgentEvent));
        entityType.Should().NotBeNull();

        var primaryKey = entityType!.FindPrimaryKey();
        primaryKey.Should().NotBeNull("AgentEvent must declare a primary key");
        primaryKey!.Properties.Select(p => p.Name).Should()
            .BeEquivalentTo(new[] { nameof(AgentEvent.SessionId), nameof(AgentEvent.Sequence) },
                "the PK must be the composite (SessionId, Sequence)");
    }

    [Fact]
    public async Task Cascade_delete_session_removes_events()
    {
        var conversation = new Conversation { ProjectId = Guid.NewGuid(), Title = "test" };
        Context.Conversations.Add(conversation);
        await Context.SaveChangesAsync();

        var session = new AgentSession { ConversationId = conversation.Id, Prompt = "go" };
        Context.AgentSessions.Add(session);
        await Context.SaveChangesAsync();

        for (var i = 1; i <= 3; i++)
        {
            Context.AgentEvents.Add(new AgentEvent
            {
                SessionId = session.Id,
                Sequence = i,
                Kind = AgentEventKind.AssistantText,
                Text = "hi",
                CreatedAt = DateTime.UtcNow,
            });
        }
        await Context.SaveChangesAsync();

        (await Context.AgentEvents.CountAsync(e => e.SessionId == session.Id)).Should().Be(3);

        Context.AgentSessions.Remove(session);
        await Context.SaveChangesAsync();

        Context.ChangeTracker.Clear();

        (await Context.AgentEvents.CountAsync(e => e.SessionId == session.Id)).Should().Be(0,
            "deleting a session must cascade-delete its events");
    }

    [Fact]
    public async Task Cascade_delete_conversation_removes_sessions_and_events()
    {
        var conversation = new Conversation { ProjectId = Guid.NewGuid(), Title = "test" };
        Context.Conversations.Add(conversation);
        await Context.SaveChangesAsync();

        var session = new AgentSession { ConversationId = conversation.Id, Prompt = "go" };
        Context.AgentSessions.Add(session);
        await Context.SaveChangesAsync();

        Context.AgentEvents.Add(new AgentEvent
        {
            SessionId = session.Id,
            Sequence = 1,
            Kind = AgentEventKind.AssistantText,
            Text = "hi",
            CreatedAt = DateTime.UtcNow,
        });
        await Context.SaveChangesAsync();

        var conversationId = conversation.Id;
        var sessionId = session.Id;

        Context.Conversations.Remove(conversation);
        await Context.SaveChangesAsync();

        Context.ChangeTracker.Clear();

        (await Context.AgentSessions.IgnoreQueryFilters().CountAsync(s => s.ConversationId == conversationId))
            .Should().Be(0, "deleting a conversation must cascade-delete its sessions");
        (await Context.AgentEvents.CountAsync(e => e.SessionId == sessionId))
            .Should().Be(0, "and transitively their events");
    }

    [Fact]
    public void Status_enum_is_persisted_as_string()
    {
        // The in-memory provider strips the value converter from the model
        // metadata at runtime, so checking via Context.Model is unreliable.
        // What we actually care about is that the relational migration writes
        // a string column for the Status property — verify by reading the
        // generated migration file directly. Mirrors
        // ProjectRuntimeEntityTests.State_enum_is_persisted_as_string.
        var content = ReadMigrationFile();

        content.Should().Contain(
            "Status = table.Column<string>(type: \"character varying(32)\", maxLength: 32, nullable: false)",
            "Conversation.Status and AgentSession.Status must be persisted as varchar(32) — i.e. HasConversion<string>().HasMaxLength(32)");
    }

    [Fact]
    public void Args_and_Result_are_persisted_as_jsonb()
    {
        // Cursor-native schema: per-tool args + per-tool result are opaque JSON
        // (shape varies per tool). Cheap migration-string sniff because the
        // in-memory provider strips relational metadata at runtime.
        //
        // Args + Result landed in the CursorNativeChatSchema migration (the
        // Cursor-native rewrite of AgentEvent first-class columns) — not the
        // original AddConversationsAndSessions one — so we sniff the right
        // migration file directly.
        var content = ReadCursorNativeMigrationFile();

        content.Should().Contain(
            "Args = table.Column<string>(type: \"jsonb\", nullable: true)",
            "AgentEvent.Args must be a nullable jsonb column on Postgres");
        content.Should().Contain(
            "Result = table.Column<string>(type: \"jsonb\", nullable: true)",
            "AgentEvent.Result must be a nullable jsonb column on Postgres");
    }

    [Fact]
    public async Task Archived_conversation_hidden_by_default_filter()
    {
        var projectId = Guid.NewGuid();

        var active = new Conversation { ProjectId = projectId, Title = "active" };
        var archived = new Conversation
        {
            ProjectId = projectId,
            Title = "archived",
            Status = ConversationStatus.Archived,
        };
        Context.Conversations.AddRange(active, archived);
        await Context.SaveChangesAsync();

        Context.ChangeTracker.Clear();

        var defaultResults = await Context.Conversations
            .Where(c => c.ProjectId == projectId)
            .ToListAsync();
        defaultResults.Should().HaveCount(1, "global filter hides Archived rows");
        defaultResults.Single().Title.Should().Be("active");

        var allResults = await Context.Conversations
            .IgnoreQueryFilters()
            .Where(c => c.ProjectId == projectId)
            .ToListAsync();
        allResults.Should().HaveCount(2, "IgnoreQueryFilters() exposes archived rows for admin queries");
    }

    [Fact]
    public void Migration_file_exists()
    {
        // Mirrors ErrorSignatureEntityTests.Migration_FileExists — verifies the
        // migration was generated under the expected name.
        var migrationsPath = LocateMigrationsPath();
        var migrations = Directory.GetFiles(migrationsPath, "*_AddConversationsAndSessions.cs");

        migrations.Should().NotBeEmpty(
            "a migration file ending in '_AddConversationsAndSessions.cs' must exist");
    }

    // ------------------------------------------------------------------
    // helpers

    private static string LocateMigrationsPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Migrations")))
        {
            dir = dir.Parent;
        }
        dir.Should().NotBeNull("could not locate the Migrations directory from the test binary");
        return Path.Combine(dir!.FullName, "Migrations");
    }

    private static string ReadMigrationFile()
    {
        var migrationsPath = LocateMigrationsPath();
        var files = Directory.GetFiles(migrationsPath, "*_AddConversationsAndSessions.cs");
        files.Should().NotBeEmpty("AddConversationsAndSessions migration must exist");
        return File.ReadAllText(files.Single());
    }

    /// <summary>
    /// Reads the migration that introduced the Cursor-native first-class
    /// columns on AgentEvent (CallId, Args, Result, ToolName, etc.).
    /// </summary>
    private static string ReadCursorNativeMigrationFile()
    {
        var migrationsPath = LocateMigrationsPath();
        var files = Directory.GetFiles(migrationsPath, "*_CursorNativeChatSchema.cs");
        files.Should().NotBeEmpty("CursorNativeChatSchema migration must exist");
        return File.ReadAllText(files.Single());
    }
}
