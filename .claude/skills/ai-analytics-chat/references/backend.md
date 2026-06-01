# Backend Implementation

## Entity Models

### AnalyticsConversation

```csharp
public class AnalyticsConversation
{
    public Guid Id { get; set; }
    public Guid OrganisationId { get; set; }
    public string Title { get; set; } = "Ny konversation";
    public string CreatedById { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public Organisation Organisation { get; set; } = null!;
    public User CreatedBy { get; set; } = null!;
    public ICollection<AnalyticsConversationMessage> Messages { get; set; } = new List<AnalyticsConversationMessage>();
}
```

### AnalyticsConversationMessage

Two-field design: `Content` for LLM context, `BlocksJson` for frontend rendering.

```csharp
public class AnalyticsConversationMessage
{
    public Guid Id { get; set; }
    public Guid ConversationId { get; set; }
    public string Role { get; set; } = string.Empty;  // "user" | "assistant"
    public string Content { get; set; } = string.Empty;
    public string? BlocksJson { get; set; }  // Full ContentBlock[] JSON
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public AnalyticsConversation Conversation { get; set; } = null!;
}
```

### SavedQuery

Stores validated SQL for frontend to execute and render as a table.

```csharp
public class SavedQuery
{
    public Guid Id { get; set; }
    public Guid OrganisationId { get; set; }
    public string SqlQuery { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string CreatedById { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Organisation Organisation { get; set; } = null!;
    public User CreatedBy { get; set; } = null!;
}
```

### DTOs

```csharp
public record AnalyticsChatRequest(
    string Message,
    string? Model = null,
    List<ChatMessageDto>? Messages = null,
    string? ConversationId = null
);

public record ConversationResponse(Guid Id, string Title, DateTime CreatedAt, DateTime UpdatedAt);
public record CreateConversationRequest(string? Title);
public record SaveConversationMessagesRequest(List<SaveMessageDto> Messages);
public record SaveMessageDto(string Role, string Content, string? BlocksJson);
public record ConversationMessageResponse(Guid Id, string Role, string Content, string? BlocksJson, DateTime CreatedAt);
```

### DbContext Configuration

```csharp
// Add DbSets
public DbSet<AnalyticsConversation> AnalyticsConversations { get; set; }
public DbSet<AnalyticsConversationMessage> AnalyticsConversationMessages { get; set; }
public DbSet<SavedQuery> SavedQueries { get; set; }

// In OnModelCreating:
builder.Entity<AnalyticsConversation>(entity =>
{
    entity.HasKey(e => e.Id);
    entity.HasIndex(e => e.OrganisationId);
    entity.Property(e => e.CreatedAt).HasColumnType("timestamp with time zone");
    entity.Property(e => e.UpdatedAt).HasColumnType("timestamp with time zone");
    entity.HasOne(e => e.Organisation).WithMany()
        .HasForeignKey(e => e.OrganisationId).OnDelete(DeleteBehavior.Cascade);
    entity.HasOne(e => e.CreatedBy).WithMany()
        .HasForeignKey(e => e.CreatedById).OnDelete(DeleteBehavior.Restrict);
});

builder.Entity<AnalyticsConversationMessage>(entity =>
{
    entity.HasKey(e => e.Id);
    entity.HasIndex(e => e.ConversationId);
    entity.Property(e => e.CreatedAt).HasColumnType("timestamp with time zone");
    entity.Property(e => e.BlocksJson).HasColumnType("jsonb");
    entity.HasOne(e => e.Conversation).WithMany(c => c.Messages)
        .HasForeignKey(e => e.ConversationId).OnDelete(DeleteBehavior.Cascade);
});
```

## Tool Context (AsyncLocal)

Tools are instantiated by OpenRouter.NET, not by DI. Use `AsyncLocal` to pass scoped context:

```csharp
public static class AnalyticsToolContext
{
    public static readonly AsyncLocal<IServiceProvider?> Services = new();
    public static readonly AsyncLocal<Guid> OrganisationId = new();
    public static readonly AsyncLocal<string?> UserId = new();
}
```

Set before streaming:
```csharp
AnalyticsToolContext.Services.Value = _serviceProvider;
AnalyticsToolContext.OrganisationId.Value = org.Id;
AnalyticsToolContext.UserId.Value = userId;
```

## Tool Implementations

### GetBusinessOverviewTool

High-level org summary — call this FIRST instead of get_schema for general questions.

```csharp
public class GetBusinessOverviewTool : Tool<GetBusinessOverviewParams, BusinessOverviewResult>
{
    public override string Name => "get_business_overview";
    public override string Description =>
        "Get a high-level overview of the organisation's current setup: " +
        "resource types, resources, active pricing schedules with price ranges, " +
        "discount rules, membership levels, and recent booking stats.";

    protected override BusinessOverviewResult Handle(GetBusinessOverviewParams p)
    {
        var services = AnalyticsToolContext.Services.Value!;
        var orgId = AnalyticsToolContext.OrganisationId.Value;
        var db = services.GetRequiredService<ApplicationDbContext>();

        // Query: resource types + resources, active pricing schedules,
        // discount rules, member counts, last 30 days booking stats, campaigns
        // Return structured summary — NO SQL needed by the LLM
    }
}
```

This prevents the LLM from writing exploratory SQL just to understand the org's setup.

### GetSchemaTool

```csharp
public class GetSchemaTool : Tool<GetSchemaParams, GetSchemaResult>
{
    public override string Name => "get_schema";
    public override string Description =>
        "Discover the database schema. Returns table names and column definitions. " +
        "Optionally filter by table name substring.";

    protected override GetSchemaResult Handle(GetSchemaParams p)
    {
        var schemaService = services.GetRequiredService<ISchemaDiscoveryService>();
        var tables = schemaService.GetTenantScopedSchemaAsync().GetAwaiter().GetResult();

        if (!string.IsNullOrEmpty(p.TableFilter))
            tables = tables.Where(t => t.TableName.Contains(p.TableFilter, ...)).ToList();

        return new GetSchemaResult { Tables = tables.Select(...).ToList() };
    }
}
```

### ExecuteQueryTool

```csharp
public class ExecuteQueryTool : Tool<ExecuteQueryParams, ExecuteQueryResult>
{
    public override string Name => "execute_query";
    public override string Description =>
        "Execute a read-only SQL query and get results back for analysis. " +
        "Max 100 rows returned.";

    protected override ExecuteQueryResult Handle(ExecuteQueryParams p)
    {
        // 1. Validate SQL (whitelist tables, block DML, check org scoping)
        var validationResult = validator.ValidateAndScope(p.Sql, organisationId);
        if (!validationResult.IsSuccess)
            return new ExecuteQueryResult { Error = validationResult.Error };

        // 2. Execute with row limit
        var queryResult = executor.ExecuteAsync(validationResult.Value, organisationId, maxRows: 100)
            .GetAwaiter().GetResult();

        if (!queryResult.IsSuccess)
            return new ExecuteQueryResult { Error = queryResult.Error };

        return new ExecuteQueryResult { Columns = ..., Rows = ..., TotalRowCount = ..., Truncated = ... };
    }
}
```

### SaveAndPresentQueryTool

Key pattern: validate first, save, return ID. Frontend renders separately.

```csharp
public class SaveAndPresentQueryTool : Tool<SaveAndPresentQueryParams, SaveAndPresentQueryResult>
{
    public override string Name => "save_and_present_query";
    public override string Description =>
        "Save a SQL query for the frontend to display as a data table. " +
        "You do NOT see the results. Include a short description.";

    protected override SaveAndPresentQueryResult Handle(SaveAndPresentQueryParams p)
    {
        // 1. Validate SQL
        var validationResult = validator.ValidateAndScope(p.Sql, organisationId);
        if (!validationResult.IsSuccess)
            return new SaveAndPresentQueryResult { Error = validationResult.Error };

        // 2. Test with LIMIT 1 (catches column typos, bad joins)
        var testResult = executor.ExecuteAsync(validationResult.Value, organisationId, maxRows: 1)
            .GetAwaiter().GetResult();
        if (!testResult.IsSuccess)
            return new SaveAndPresentQueryResult { Error = $"Query validation failed: {testResult.Error}" };

        // 3. Save to DB
        var savedQuery = new SavedQuery { ... };
        dbContext.SavedQueries.Add(savedQuery);
        dbContext.SaveChangesAsync().GetAwaiter().GetResult();

        return new SaveAndPresentQueryResult { QueryId = savedQuery.Id.ToString(), Description = p.Description };
    }
}
```

## Schema Discovery

Expose only allowed tables with scoping notes. Block sensitive columns.

```csharp
private static readonly Dictionary<string, string> TableScopingNotes = new(StringComparer.OrdinalIgnoreCase)
{
    ["ResourceTypes"] = "Direct: has OrganisationId",
    ["Resources"] = "Indirect: JOIN via ResourceTypes.OrganisationId",
    ["Bookings"] = "Indirect: JOIN via Resources → ResourceTypes.OrganisationId",
    ["Members"] = "Direct: has OrganisationId",
    ["AspNetUsers"] = "JOIN-only: use in JOINs for user names/emails. Sensitive columns blocked.",
    // ... add all allowed tables
};

private static readonly HashSet<string> BlockedColumns = new(StringComparer.OrdinalIgnoreCase)
{
    "PasswordHash", "SecurityStamp", "ConcurrencyStamp", "Otp", "OtpExpiresAt",
    // ... security-sensitive columns
};
```

Query `information_schema.columns` filtered to allowed tables, skip blocked columns.

## SQL Validation

```csharp
public Result<string> ValidateAndScope(string sql, Guid organisationId)
{
    // 1. Must start with SELECT
    // 2. No semicolons
    // 3. Block dangerous keywords: INSERT, UPDATE, DELETE, DROP, ALTER, CREATE, TRUNCATE, etc.
    // 4. Extract table names from FROM/JOIN — all must be in allowlist
    // 5. Indirect tables must JOIN to a direct table (ensures tenant scoping)
    // 6. Block sensitive columns
    // 7. Must reference @organisationId for direct tables
    return Result.Success(sql);
}
```

## Controller Pattern

```csharp
[HttpPost("chat/stream")]
public async Task StreamChat(string slug, [FromBody] AnalyticsChatRequest request)
{
    // Auth + org lookup...

    // Set tool context
    AnalyticsToolContext.Services.Value = _serviceProvider;
    AnalyticsToolContext.OrganisationId.Value = org.Id;
    AnalyticsToolContext.UserId.Value = userId;

    var client = _openRouter.CreateClient();
    client.RegisterTool<GetSchemaTool>();
    client.RegisterTool<GetBusinessOverviewTool>();
    client.RegisterTool<ExecuteQueryTool>();
    client.RegisterTool<SaveAndPresentQueryTool>();

    var messages = new List<Message>();
    messages.Add(Message.FromSystem(BuildSystemPrompt(org.Name)));

    // Load history from DB (conversationId) OR from request (backward compat)
    if (!string.IsNullOrEmpty(request.ConversationId) && Guid.TryParse(request.ConversationId, out var convId))
    {
        var dbMessages = await _dbContext.AnalyticsConversationMessages
            .Where(m => m.ConversationId == convId)
            .OrderBy(m => m.CreatedAt)
            .Select(m => new { m.Role, m.Content })
            .ToListAsync();

        foreach (var m in dbMessages)
            messages.Add(m.Role == "assistant" ? Message.FromAssistant(m.Content) : Message.FromUser(m.Content));
    }

    messages.Add(Message.FromUser(request.Message));
    await client.StreamAsSseAsync(new ChatCompletionRequest { Model = request.Model ?? "anthropic/claude-sonnet-4.5", Messages = messages }, Response);
}
```

### Conversation CRUD

```csharp
// GET  /conversations              → List<ConversationResponse>
// POST /conversations              → ConversationResponse
// DELETE /conversations/{id}       → NoContent (cascade deletes messages)
// GET  /conversations/{id}/messages → List<ConversationMessageResponse>
// POST /conversations/{id}/messages → Ok (append after stream)
```

Auto-title in save endpoint: if title is still "Ny konversation", update to first user message (80 char truncation).

### Saved Query Execution

Separate endpoint for frontend to fetch saved query results:

```csharp
[HttpGet("saved-queries/{queryId}/execute")]
public async Task<ActionResult<SavedQueryExecutionResult>> ExecuteSavedQuery(string slug, Guid queryId)
{
    // Auth + org lookup...
    var query = await _dbContext.SavedQueries.FirstOrDefaultAsync(q => q.Id == queryId && q.OrganisationId == org.Id);
    var result = await executor.ExecuteAsync(query.SqlQuery, query.OrganisationId);
    return Ok(new SavedQueryExecutionResult { Columns = ..., Rows = ..., Description = query.Description });
}
```
