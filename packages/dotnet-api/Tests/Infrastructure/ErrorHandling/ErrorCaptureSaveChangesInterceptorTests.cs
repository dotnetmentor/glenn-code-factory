using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Source.Features.Users.Models;
using Source.Infrastructure;
using Source.Infrastructure.ErrorHandling;

namespace Api.Tests.Infrastructure.ErrorHandling;

/// <summary>
/// Tests for <see cref="ErrorCaptureSaveChangesInterceptor"/>.
///
/// <para>The interceptor has three hard guarantees:</para>
/// <list type="bullet">
///   <item><b>PII-safe ContextData.</b> Only entity TYPE names, never property values.</item>
///   <item><b>Never swallows the original exception.</b> The <c>DbUpdateException</c> still
///       reaches the caller after capture.</item>
///   <item><b>Never throws a secondary exception.</b> Enqueue failures are logged and swallowed.</item>
/// </list>
///
/// <para>We test the interceptor mostly by calling its public <c>SaveChangesFailed</c> /
/// <c>SaveChangesFailedAsync</c> overrides with a hand-constructed
/// <see cref="DbContextErrorEventData"/>. That's the cleanest way to exercise the capture
/// path without needing a real relational provider that throws (InMemory does not naturally
/// raise <c>DbUpdateException</c>). For the "successful save" case we do wire the interceptor
/// into an in-memory DbContext and verify the queue stays empty.</para>
/// </summary>
public class ErrorCaptureSaveChangesInterceptorTests
{
    // --- Helpers -----------------------------------------------------------

    private static ErrorQueue NewQueue() => new(new PiiRedactor());

    private static ErrorCaptureSaveChangesInterceptor NewInterceptor(ErrorQueue queue) =>
        new(queue, NullLogger<ErrorCaptureSaveChangesInterceptor>.Instance);

    private static ApplicationDbContext NewInMemoryContext(string? name = null) =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(name ?? Guid.NewGuid().ToString())
            .Options);

    private static ApplicationDbContext NewInMemoryContextWithInterceptor(
        ErrorCaptureSaveChangesInterceptor interceptor,
        string? name = null) =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(name ?? Guid.NewGuid().ToString())
            .AddInterceptors(interceptor)
            .Options);

    private static async Task<List<ErrorEntry>> DrainAsync(ErrorQueue queue)
    {
        queue.CompleteWriting();
        var collected = new List<ErrorEntry>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var entry in queue.ReadAllAsync(cts.Token))
        {
            collected.Add(entry);
        }
        return collected;
    }

    // --- Tests -------------------------------------------------------------

    [Fact]
    public async Task SaveChangesFailed_EnqueuesEntryWithSourceDatabase()
    {
        var queue = NewQueue();
        var interceptor = NewInterceptor(queue);
        using var ctx = NewInMemoryContext();

        ctx.Users.Add(new User { Id = Guid.NewGuid().ToString(), UserName = "a", Email = "a@x.com" });

        var ex = new DbUpdateException("simulated db failure", new Exception("inner boom"));
        var eventData = new DbContextErrorEventData(
            eventDefinition: null!,
            messageGenerator: null!,
            context: ctx,
            exception: ex);

        interceptor.SaveChangesFailed(eventData);

        var entries = await DrainAsync(queue);
        entries.Should().HaveCount(1);
        entries[0].Source.Should().Be("Database");
        entries[0].Severity.Should().Be("Error");
        entries[0].Message.Should().Contain("simulated db failure");
    }

    [Fact]
    public async Task SaveChangesFailedAsync_EnqueuesEntryWithSourceDatabase()
    {
        var queue = NewQueue();
        var interceptor = NewInterceptor(queue);
        using var ctx = NewInMemoryContext();

        ctx.Users.Add(new User { Id = Guid.NewGuid().ToString(), UserName = "a", Email = "a@x.com" });

        var ex = new DbUpdateException("async failure", new Exception("inner"));
        var eventData = new DbContextErrorEventData(
            eventDefinition: null!,
            messageGenerator: null!,
            context: ctx,
            exception: ex);

        await interceptor.SaveChangesFailedAsync(eventData);

        var entries = await DrainAsync(queue);
        entries.Should().HaveCount(1);
        entries[0].Source.Should().Be("Database");
    }

    [Fact]
    public async Task ContextData_ContainsAffectedEntityTypeName()
    {
        var queue = NewQueue();
        var interceptor = NewInterceptor(queue);
        using var ctx = NewInMemoryContext();

        ctx.Users.Add(new User { Id = Guid.NewGuid().ToString(), UserName = "bob", Email = "bob@x.com" });

        var eventData = new DbContextErrorEventData(
            null!, null!, ctx, new DbUpdateException("boom", new Exception("inner")));

        interceptor.Capture(eventData);

        var entries = await DrainAsync(queue);
        entries.Should().HaveCount(1);
        entries[0].ContextData.Should().NotBeNull();
        entries[0].ContextData.Should().Contain("\"User\"");
    }

    /// <summary>
    /// The core PII-safety test. If this ever turns red, the interceptor is leaking data.
    /// </summary>
    [Fact]
    public async Task ContextData_DoesNotContainPropertyValues_PiiLeakageTest()
    {
        var queue = NewQueue();
        var interceptor = NewInterceptor(queue);
        using var ctx = NewInMemoryContext();

        const string secretEmail = "secret@example.com";
        const string sensitiveHash = "sensitive-hash-value";

        ctx.Users.Add(new User
        {
            Id = Guid.NewGuid().ToString(),
            UserName = secretEmail,
            Email = secretEmail,
            PasswordHash = sensitiveHash,
            FirstName = "topsecret-first",
            LastName = "topsecret-last",
            OtpCode = "999999",
            CursorApiKey = "sk-ultra-sensitive-key"
        });

        var eventData = new DbContextErrorEventData(
            null!, null!, ctx, new DbUpdateException("boom", new Exception("inner")));

        interceptor.Capture(eventData);

        var entries = await DrainAsync(queue);
        entries.Should().HaveCount(1);
        var ctxData = entries[0].ContextData!;

        // Type name is present (the one thing we WANT)
        ctxData.Should().Contain("\"User\"");

        // None of the property values may leak
        ctxData.Should().NotContain("secret");
        ctxData.Should().NotContain("sensitive");
        ctxData.Should().NotContain("topsecret");
        ctxData.Should().NotContain("999999");
        ctxData.Should().NotContain("sk-ultra");
        ctxData.Should().NotContain("@example.com");
    }

    [Fact]
    public async Task MultipleAffectedTypes_AllListedInContextData_DistinctAndSorted()
    {
        var queue = NewQueue();
        var interceptor = NewInterceptor(queue);
        using var ctx = NewInMemoryContext();

        // Two tracked User entities (state = Added) — should collapse to a single "User" entry.
        ctx.Users.Add(new User { Id = Guid.NewGuid().ToString(), UserName = "a", Email = "a@x.com" });
        ctx.Users.Add(new User { Id = Guid.NewGuid().ToString(), UserName = "b", Email = "b@x.com" });

        // A different type — Microsoft.AspNetCore.Identity.IdentityRole is registered by the
        // IdentityDbContext base, so adding one is a valid way to put a second entity TYPE into
        // the change tracker without needing a new Features/ entity.
        ctx.Add(new Microsoft.AspNetCore.Identity.IdentityRole("test-role") { Id = Guid.NewGuid().ToString() });

        var eventData = new DbContextErrorEventData(
            null!, null!, ctx, new DbUpdateException("multi-type failure", new Exception("inner")));

        interceptor.Capture(eventData);

        var entries = await DrainAsync(queue);
        entries.Should().HaveCount(1);
        var ctxData = entries[0].ContextData!;

        ctxData.Should().Contain("\"User\"");
        ctxData.Should().Contain("\"IdentityRole\"");

        // Must be a JSON array with distinct entries (no duplicate "User")
        using var doc = System.Text.Json.JsonDocument.Parse(ctxData);
        var array = doc.RootElement.EnumerateArray().Select(e => e.GetString()!).ToArray();
        array.Should().OnlyHaveUniqueItems();
        array.Should().BeInAscendingOrder();
        array.Should().Contain("User");
        array.Should().Contain("IdentityRole");
    }

    [Fact]
    public async Task SuccessfulSave_DoesNotEnqueue()
    {
        var queue = NewQueue();
        var interceptor = NewInterceptor(queue);
        using var ctx = NewInMemoryContextWithInterceptor(interceptor);

        ctx.Users.Add(new User { Id = Guid.NewGuid().ToString(), UserName = "ok", Email = "ok@x.com" });
        await ctx.SaveChangesAsync();

        var entries = await DrainAsync(queue);
        entries.Should().BeEmpty("successful SaveChanges must never write to the error queue");
    }

    [Fact]
    public async Task Capture_WithNullDbContext_EnqueuesEntryWithEmptyTypeArray()
    {
        // Defensive — EF always provides a context in practice, but the interceptor must
        // survive a null context without throwing.
        var queue = NewQueue();
        var interceptor = NewInterceptor(queue);

        var eventData = new DbContextErrorEventData(
            null!, null!, context: null!, new DbUpdateException("no-context", new Exception("inner")));

        interceptor.Capture(eventData);

        var entries = await DrainAsync(queue);
        entries.Should().HaveCount(1);
        entries[0].ContextData.Should().Be("[]");
    }

    [Fact]
    public async Task EnqueueFails_CaptureDoesNotThrow()
    {
        // Complete the queue so TryWrite fails — proves that even when the pipeline is dead,
        // the interceptor swallows and logs rather than propagating a secondary exception on
        // top of the original DbUpdateException.
        var queue = NewQueue();
        queue.CompleteWriting();

        var interceptor = NewInterceptor(queue);
        using var ctx = NewInMemoryContext();
        ctx.Users.Add(new User { Id = Guid.NewGuid().ToString(), UserName = "a", Email = "a@x.com" });

        var eventData = new DbContextErrorEventData(
            null!, null!, ctx, new DbUpdateException("boom", new Exception("inner")));

        var act = () => interceptor.Capture(eventData);
        act.Should().NotThrow("the interceptor must never add a secondary exception");

        // The queue counts it as a failure (channel writer completed) — good observability,
        // and proves we really attempted the write.
        queue.FailureCount.Should().BeGreaterThan(0);
        await Task.CompletedTask;
    }

    [Fact]
    public void Capture_DoesNotAccessPropertyValues_StructuralPiiTest()
    {
        // Structural proof that the interceptor only reads Entity.GetType().Name and never
        // a property getter. We track an entity whose every property throws when read;
        // if the interceptor accessed any property, it would surface here.
        var queue = NewQueue();
        var interceptor = NewInterceptor(queue);
        using var ctx = new ThrowingEntityDbContext(new DbContextOptionsBuilder<ThrowingEntityDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

        ctx.Attach(new EntityWhoseGetterExplodes()).State = EntityState.Added;

        var eventData = new DbContextErrorEventData(
            null!, null!, ctx, new DbUpdateException("boom", new Exception("inner")));

        var act = () => interceptor.Capture(eventData);
        act.Should().NotThrow(
            "Capture must read only .Entity.GetType().Name — never property getters");
    }

    [Fact]
    public async Task SaveChangesFailed_CallsBase_ReturnsCompletedTask()
    {
        // The "exception still propagates" guarantee is the fact that our overrides always call
        // base.SaveChangesFailed[Async], which never swallows. We verify the async override
        // returns the completed task that base does, and neither override itself throws.
        var queue = NewQueue();
        var interceptor = NewInterceptor(queue);
        using var ctx = NewInMemoryContext();

        var eventData = new DbContextErrorEventData(
            null!, null!, ctx, new DbUpdateException("propagation check", new Exception("inner")));

        var syncAct = () => interceptor.SaveChangesFailed(eventData);
        syncAct.Should().NotThrow();

        var task = interceptor.SaveChangesFailedAsync(eventData);
        await task; // should complete without throwing — base does not rethrow
        task.IsCompletedSuccessfully.Should().BeTrue();
    }

    // --- Helper types for the structural-PII test -------------------------

    private class ThrowingEntityDbContext : DbContext
    {
        public ThrowingEntityDbContext(DbContextOptions<ThrowingEntityDbContext> options) : base(options) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<EntityWhoseGetterExplodes>(e =>
            {
                e.HasKey(x => x.Id);
                // Treat the dangerous property as ignored at the model level so EF itself
                // doesn't try to read it — we want ONLY our interceptor under test to be
                // the thing that might or might not touch the getter.
                e.Ignore(x => x.Danger);
            });
        }

        public DbSet<EntityWhoseGetterExplodes> Exploders => Set<EntityWhoseGetterExplodes>();
    }

    private class EntityWhoseGetterExplodes
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Danger => throw new InvalidOperationException(
            "if you see this in a stack trace, the interceptor is reading property values");
    }
}
