using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Source.Features.RuntimeEvents.Models;
using Source.Features.RuntimeEvents.Queries;
using Source.Features.RuntimeLifecycle.Models;
using Source.Features.Users.Models;
using Source.Infrastructure;

namespace Api.Tests.Features.RuntimeEvents;

/// <summary>
/// End-to-end HTTP tests for the user-facing runtime-events list endpoint at
/// <c>GET /api/runtime-events?runtimeId=&amp;limit=&amp;before=&amp;type=&amp;severity=</c>.
/// Mirrors the <see cref="Api.Tests.Features.RuntimeLifecycle.RuntimeStatusControllerTests"/>
/// shape: real auth via <c>/api/auth/register</c>, fresh in-memory DB per
/// factory, runtime-event rows seeded straight into the test DB.
///
/// <para>Covers the contract documented in the controller and query:</para>
/// <list type="bullet">
///   <item>Auth is required — unauthenticated callers see 401.</item>
///   <item>Happy path returns reverse-chronological events with HasMore=false
///         when the page fits.</item>
///   <item><c>before</c> cursor pagination respects strict less-than on
///         Timestamp and never bleeds in events from the previous page.</item>
///   <item><c>type</c> filter returns only the matching rows.</item>
///   <item><c>severity</c> filter returns only the matching rows.</item>
///   <item>Limit is clamped to <see cref="GetRuntimeEventsQueryHandler.MAX_LIMIT"/> = 500
///         and <see cref="GetRuntimeEventsQueryHandler.MIN_LIMIT"/> = 1 — a
///         hand-crafted curl can't blow up the page size.</item>
///   <item>An empty <c>runtimeId</c> short-circuits with 400.</item>
///   <item>Events from a different runtime never appear in another runtime's
///         page (the query is strictly RuntimeId-scoped).</item>
/// </list>
/// </summary>
[Collection(HangfireTestCollection.Name)]
public class RuntimeEventsControllerTests : IntegrationTestBase
{
    private const string Password = "Password123!";

    /// <summary>
    /// Match the API's controller JSON config (<c>AddJsonOptions</c>) — the
    /// query handler stringifies <see cref="RuntimeEventSeverity"/> for the
    /// wire shape so we don't need a custom converter on the read side, but
    /// we do still want Web defaults (camelCase, etc.) for everything else.
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // ------------------------------------------------------------------
    // Auth + input validation
    // ------------------------------------------------------------------

    [Fact]
    public async Task List_Unauthenticated_Returns401()
    {
        var response = await Client.GetAsync($"/api/runtime-events?runtimeId={Guid.NewGuid()}");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task List_EmptyRuntimeId_Returns400()
    {
        var (client, _) = await RegisterUserAsync();

        // Empty Guid sentinel — the controller short-circuits before the
        // handler runs so we don't issue a wide-open DB scan for missing input.
        var response = await client.GetAsync($"/api/runtime-events?runtimeId={Guid.Empty}");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task List_MissingRuntimeId_Returns400()
    {
        var (client, _) = await RegisterUserAsync();

        // No query string at all — model binding leaves runtimeId at default
        // (Guid.Empty) and the controller's BadRequest path fires.
        var response = await client.GetAsync("/api/runtime-events");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ------------------------------------------------------------------
    // Happy path
    // ------------------------------------------------------------------

    [Fact]
    public async Task List_HappyPath_ReturnsEventsNewestFirst()
    {
        var (client, _) = await RegisterUserAsync();
        var runtimeId = Guid.NewGuid();

        // Three events with deterministic timestamps so we can pin the order.
        await SeedEventsAsync(runtimeId,
            (RuntimeEventTypes.InstallStarted,    RuntimeEventSeverity.Info,  DateTime.UtcNow.AddMinutes(-3), null),
            (RuntimeEventTypes.InstallCompleted,  RuntimeEventSeverity.Info,  DateTime.UtcNow.AddMinutes(-2), 1234L),
            (RuntimeEventTypes.SpecDeltaApplied,  RuntimeEventSeverity.Info,  DateTime.UtcNow.AddMinutes(-1), null));

        var response = await client.GetAsync($"/api/runtime-events?runtimeId={runtimeId}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<ListRuntimeEventsResponse>(JsonOptions);
        body.Should().NotBeNull();
        body!.Events.Should().HaveCount(3);
        body.HasMore.Should().BeFalse("the page fits and there is no extra row");

        // OrderByDescending Timestamp — the newest seeded event lands first.
        body.Events.Select(e => e.Type).Should().ContainInOrder(
            RuntimeEventTypes.SpecDeltaApplied,
            RuntimeEventTypes.InstallCompleted,
            RuntimeEventTypes.InstallStarted);
    }

    [Fact]
    public async Task List_NoEventsForRuntime_ReturnsEmptyPage()
    {
        var (client, _) = await RegisterUserAsync();
        var runtimeId = Guid.NewGuid();
        // The runtime must exist + be accessible for the gate to pass; the
        // "no events" assertion is about the events payload, not the gate.
        await EnsureAccessibleRuntimeAsync(runtimeId);

        // No seeded events at all — the "no events yet" shape is an empty
        // events array with HasMore=false, NOT a 404. The drawer renders the
        // empty state from this response.
        var response = await client.GetAsync($"/api/runtime-events?runtimeId={runtimeId}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<ListRuntimeEventsResponse>(JsonOptions);
        body!.Events.Should().BeEmpty();
        body.HasMore.Should().BeFalse();
    }

    [Fact]
    public async Task List_OnlyReturnsEventsForRequestedRuntime()
    {
        var (client, _) = await RegisterUserAsync();
        var runtimeA = Guid.NewGuid();
        var runtimeB = Guid.NewGuid();

        await SeedEventsAsync(runtimeA,
            (RuntimeEventTypes.InstallStarted, RuntimeEventSeverity.Info, DateTime.UtcNow.AddMinutes(-2), null));
        await SeedEventsAsync(runtimeB,
            (RuntimeEventTypes.ServiceCrashed, RuntimeEventSeverity.Error, DateTime.UtcNow.AddMinutes(-1), null));

        var response = await client.GetAsync($"/api/runtime-events?runtimeId={runtimeA}");
        var body = await response.Content.ReadFromJsonAsync<ListRuntimeEventsResponse>(JsonOptions);

        body!.Events.Should().HaveCount(1, "the query is strictly per-runtime");
        body.Events.Single().RuntimeId.Should().Be(runtimeA);
        body.Events.Single().Type.Should().Be(RuntimeEventTypes.InstallStarted);
    }

    // ------------------------------------------------------------------
    // Pagination
    // ------------------------------------------------------------------

    [Fact]
    public async Task List_BeforeCursor_PaginatesCorrectly()
    {
        var (client, _) = await RegisterUserAsync();
        var runtimeId = Guid.NewGuid();

        // Five events 1..5 minutes ago. With limit=2 we expect two pages of
        // two events each + a final one-event page (with HasMore=false).
        var baseTime = DateTime.UtcNow;
        await SeedEventsAsync(runtimeId,
            ("e1", RuntimeEventSeverity.Info, baseTime.AddMinutes(-5), null),
            ("e2", RuntimeEventSeverity.Info, baseTime.AddMinutes(-4), null),
            ("e3", RuntimeEventSeverity.Info, baseTime.AddMinutes(-3), null),
            ("e4", RuntimeEventSeverity.Info, baseTime.AddMinutes(-2), null),
            ("e5", RuntimeEventSeverity.Info, baseTime.AddMinutes(-1), null));

        // Page 1: newest two (e5, e4). HasMore=true because the +1 probe row
        // sees e3 sitting in the tail.
        var page1Response = await client.GetAsync($"/api/runtime-events?runtimeId={runtimeId}&limit=2");
        page1Response.StatusCode.Should().Be(HttpStatusCode.OK);
        var page1 = await page1Response.Content.ReadFromJsonAsync<ListRuntimeEventsResponse>(JsonOptions);

        page1!.Events.Should().HaveCount(2);
        page1.Events.Select(e => e.Type).Should().ContainInOrder("e5", "e4");
        page1.HasMore.Should().BeTrue();

        // Page 2: pass the oldest timestamp from page 1 as `before`. Should
        // yield e3, e2 (strictly < e4.Timestamp), HasMore still true.
        var cursor = page1.Events.Last().Timestamp;
        var page2Response = await client.GetAsync(
            $"/api/runtime-events?runtimeId={runtimeId}&limit=2&before={Uri.EscapeDataString(cursor.ToString("o"))}");
        page2Response.StatusCode.Should().Be(HttpStatusCode.OK);
        var page2 = await page2Response.Content.ReadFromJsonAsync<ListRuntimeEventsResponse>(JsonOptions);

        page2!.Events.Should().HaveCount(2);
        page2.Events.Select(e => e.Type).Should().ContainInOrder("e3", "e2");
        page2.HasMore.Should().BeTrue("e1 is still waiting in the tail");

        // Page 3: only e1 left. HasMore=false because no row sits behind it.
        var cursor2 = page2.Events.Last().Timestamp;
        var page3Response = await client.GetAsync(
            $"/api/runtime-events?runtimeId={runtimeId}&limit=2&before={Uri.EscapeDataString(cursor2.ToString("o"))}");
        var page3 = await page3Response.Content.ReadFromJsonAsync<ListRuntimeEventsResponse>(JsonOptions);

        page3!.Events.Should().HaveCount(1);
        page3.Events.Single().Type.Should().Be("e1");
        page3.HasMore.Should().BeFalse("no events older than e1 — the tail is empty");
    }

    [Fact]
    public async Task List_HasMore_TrueWhenExtraRowExists()
    {
        var (client, _) = await RegisterUserAsync();
        var runtimeId = Guid.NewGuid();

        // Seed exactly limit+1 events. The handler fetches limit+1, observes
        // the extra, sets HasMore=true and drops it before serialising.
        await SeedEventsAsync(runtimeId,
            ("a", RuntimeEventSeverity.Info, DateTime.UtcNow.AddSeconds(-3), null),
            ("b", RuntimeEventSeverity.Info, DateTime.UtcNow.AddSeconds(-2), null),
            ("c", RuntimeEventSeverity.Info, DateTime.UtcNow.AddSeconds(-1), null));

        var response = await client.GetAsync($"/api/runtime-events?runtimeId={runtimeId}&limit=2");
        var body = await response.Content.ReadFromJsonAsync<ListRuntimeEventsResponse>(JsonOptions);

        body!.Events.Should().HaveCount(2, "limit is honoured — the +1 probe row must be dropped");
        body.HasMore.Should().BeTrue();
    }

    // ------------------------------------------------------------------
    // Filters
    // ------------------------------------------------------------------

    [Fact]
    public async Task List_TypeFilter_ReturnsOnlyMatching()
    {
        var (client, _) = await RegisterUserAsync();
        var runtimeId = Guid.NewGuid();

        await SeedEventsAsync(runtimeId,
            (RuntimeEventTypes.InstallStarted,   RuntimeEventSeverity.Info,  DateTime.UtcNow.AddMinutes(-3), null),
            (RuntimeEventTypes.InstallCompleted, RuntimeEventSeverity.Info,  DateTime.UtcNow.AddMinutes(-2), 100L),
            (RuntimeEventTypes.ServiceCrashed,   RuntimeEventSeverity.Error, DateTime.UtcNow.AddMinutes(-1), null));

        var response = await client.GetAsync(
            $"/api/runtime-events?runtimeId={runtimeId}&type={RuntimeEventTypes.InstallCompleted}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<ListRuntimeEventsResponse>(JsonOptions);
        body!.Events.Should().HaveCount(1);
        body.Events.Single().Type.Should().Be(RuntimeEventTypes.InstallCompleted);
    }

    [Fact]
    public async Task List_SeverityFilter_ReturnsOnlyMatching()
    {
        var (client, _) = await RegisterUserAsync();
        var runtimeId = Guid.NewGuid();

        await SeedEventsAsync(runtimeId,
            ("infoA",  RuntimeEventSeverity.Info,  DateTime.UtcNow.AddMinutes(-4), null),
            ("warnA",  RuntimeEventSeverity.Warn,  DateTime.UtcNow.AddMinutes(-3), null),
            ("errorA", RuntimeEventSeverity.Error, DateTime.UtcNow.AddMinutes(-2), null),
            ("errorB", RuntimeEventSeverity.Error, DateTime.UtcNow.AddMinutes(-1), null));

        // Severity is a string-bound enum on the wire — `Error` matches the
        // exact name the controller's [FromQuery] binder produces.
        var response = await client.GetAsync(
            $"/api/runtime-events?runtimeId={runtimeId}&severity=Error");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<ListRuntimeEventsResponse>(JsonOptions);
        body!.Events.Should().HaveCount(2);
        body.Events.Should().OnlyContain(e => e.Severity == "Error");

        // Newest-first: errorB before errorA.
        body.Events.Select(e => e.Type).Should().ContainInOrder("errorB", "errorA");
    }

    [Fact]
    public async Task List_CombinedTypeAndSeverityFilter_Intersects()
    {
        var (client, _) = await RegisterUserAsync();
        var runtimeId = Guid.NewGuid();

        await SeedEventsAsync(runtimeId,
            (RuntimeEventTypes.InstallStarted,  RuntimeEventSeverity.Info,  DateTime.UtcNow.AddMinutes(-3), null),
            (RuntimeEventTypes.InstallFailed,   RuntimeEventSeverity.Error, DateTime.UtcNow.AddMinutes(-2), null),
            (RuntimeEventTypes.ServiceCrashed,  RuntimeEventSeverity.Error, DateTime.UtcNow.AddMinutes(-1), null));

        var response = await client.GetAsync(
            $"/api/runtime-events?runtimeId={runtimeId}&type={RuntimeEventTypes.InstallFailed}&severity=Error");
        var body = await response.Content.ReadFromJsonAsync<ListRuntimeEventsResponse>(JsonOptions);

        body!.Events.Should().HaveCount(1, "intersection of both filters narrows to a single row");
        body.Events.Single().Type.Should().Be(RuntimeEventTypes.InstallFailed);
        body.Events.Single().Severity.Should().Be("Error");
    }

    // ------------------------------------------------------------------
    // Limit clamping
    // ------------------------------------------------------------------

    [Fact]
    public async Task List_LimitOver500_ClampedToMaxLimit()
    {
        var (client, _) = await RegisterUserAsync();
        var runtimeId = Guid.NewGuid();

        // Seed 510 events, ask for limit=10000. Handler must clamp the page
        // size at MAX_LIMIT = 500 — the response must not contain all 510.
        var rows = new List<(string Type, RuntimeEventSeverity Severity, DateTime Timestamp, long? DurationMs)>();
        var baseTime = DateTime.UtcNow.AddHours(-1);
        for (var i = 0; i < 510; i++)
        {
            rows.Add(($"e{i:D4}", RuntimeEventSeverity.Info, baseTime.AddSeconds(i), null));
        }
        await SeedEventsAsync(runtimeId, rows.ToArray());

        var response = await client.GetAsync($"/api/runtime-events?runtimeId={runtimeId}&limit=10000");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<ListRuntimeEventsResponse>(JsonOptions);
        body!.Events.Should().HaveCount(GetRuntimeEventsQueryHandler.MAX_LIMIT,
            "wild limit values must be clamped to MAX_LIMIT (500) to bound response size");
        body.HasMore.Should().BeTrue("10 more events sit behind the clamped page");
    }

    [Fact]
    public async Task List_LimitZero_ClampedToMinLimit()
    {
        var (client, _) = await RegisterUserAsync();
        var runtimeId = Guid.NewGuid();

        // Seed two events; asking for limit=0 must still return at least one
        // row, not silently return empty. The floor is MIN_LIMIT = 1.
        await SeedEventsAsync(runtimeId,
            ("first",  RuntimeEventSeverity.Info, DateTime.UtcNow.AddMinutes(-2), null),
            ("second", RuntimeEventSeverity.Info, DateTime.UtcNow.AddMinutes(-1), null));

        var response = await client.GetAsync($"/api/runtime-events?runtimeId={runtimeId}&limit=0");
        var body = await response.Content.ReadFromJsonAsync<ListRuntimeEventsResponse>(JsonOptions);

        body!.Events.Should().HaveCount(GetRuntimeEventsQueryHandler.MIN_LIMIT,
            "limit=0 is clamped to MIN_LIMIT (1) so 'no events' never looks like 'busy runtime'");
        body.Events.Single().Type.Should().Be("second", "newest event survives the clamp");
        body.HasMore.Should().BeTrue();
    }

    // ------------------------------------------------------------------
    // Payload + DurationMs round-trip
    // ------------------------------------------------------------------

    [Fact]
    public async Task List_RoundTripsPayloadAndDurationMs()
    {
        var (client, _) = await RegisterUserAsync();
        var runtimeId = Guid.NewGuid();

        // A realistic payload — the Timeline relies on the parsed JsonElement
        // surfacing as a structured object, not a string the React side has
        // to re-parse.
        await SeedEventsAsync(runtimeId,
            (RuntimeEventTypes.InstallCompleted, RuntimeEventSeverity.Info, DateTime.UtcNow.AddMinutes(-1), 4242L,
             "{\"hash\":\"abc123\",\"snippet\":\"apt-get install -y curl\"}"));

        var response = await client.GetAsync($"/api/runtime-events?runtimeId={runtimeId}");
        var body = await response.Content.ReadFromJsonAsync<ListRuntimeEventsResponse>(JsonOptions);

        body!.Events.Should().HaveCount(1);
        var ev = body.Events.Single();
        ev.DurationMs.Should().Be(4242, "the top-level DurationMs column drives 'slowest' queries");
        ev.Payload.GetProperty("hash").GetString().Should().Be("abc123");
        ev.Payload.GetProperty("snippet").GetString().Should().Be("apt-get install -y curl");
    }

    // ------------------------------------------------------------------
    // helpers
    // ------------------------------------------------------------------

    /// <summary>
    /// Insert one <see cref="RuntimeEvent"/> row per supplied tuple. Same
    /// "save then overwrite timestamps" trick as <c>RuntimeStateEvent</c>
    /// seeding in <see cref="Api.Tests.Features.RuntimeLifecycle.RuntimeStatusControllerTests"/>
    /// — we explicitly set <see cref="RuntimeEvent.Timestamp"/> (which is
    /// distinct from <c>CreatedAt</c>; the query orders on Timestamp).
    /// </summary>
    private async Task SeedEventsAsync(
        Guid runtimeId,
        params (string Type, RuntimeEventSeverity Severity, DateTime Timestamp, long? DurationMs)[] events)
    {
        // Delegate to the overload that takes an explicit payload string —
        // most callers don't care about payload content and just want "{}".
        var withPayload = events
            .Select(e => (e.Type, e.Severity, e.Timestamp, e.DurationMs, Payload: "{}"))
            .ToArray();
        await SeedEventsAsync(runtimeId, withPayload);
    }

    private async Task SeedEventsAsync(
        Guid runtimeId,
        params (string Type, RuntimeEventSeverity Severity, DateTime Timestamp, long? DurationMs, string Payload)[] events)
    {
        await EnsureAccessibleRuntimeAsync(runtimeId);

        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        foreach (var (type, severity, timestamp, durationMs, payload) in events)
        {
            db.RuntimeEvents.Add(new RuntimeEvent
            {
                Id = Guid.NewGuid(),
                RuntimeId = runtimeId,
                Type = type,
                Severity = severity,
                Timestamp = timestamp,
                DurationMs = durationMs,
                Payload = payload,
                // CreatedAt/UpdatedAt are stamped by the IAuditable interceptor.
            });
        }

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Register a fresh user and return an HttpClient pre-loaded with the
    /// resulting auth cookie. Same shape as the helper in
    /// <see cref="Api.Tests.Features.RuntimeLifecycle.RuntimeStatusControllerTests"/>.
    /// </summary>
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

    // The user registered for the current test. The list endpoint gates via
    // ResolveAccessibleRuntimeAsync, which requires a ProjectRuntime row whose
    // Project is accessible to the caller — without it every request 404s.
    private string? _callerUserId;

    /// <summary>
    /// Ensure a ProjectRuntime with <paramref name="runtimeId"/> exists, backed by a
    /// Project owned by the current caller. Idempotent. Required for the
    /// runtime-events access gate to pass.
    /// </summary>
    private async Task EnsureAccessibleRuntimeAsync(Guid runtimeId)
    {
        if (_callerUserId is null) return;
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        if (await db.ProjectRuntimes.AnyAsync(r => r.Id == runtimeId)) return;
        var projectId = Guid.NewGuid();
        db.Projects.Add(new Source.Features.Projects.Models.Project
        {
            Id = projectId,
            OwnerUserId = _callerUserId,
            WorkspaceId = Guid.NewGuid(),
            Name = "Test Project",
        });
        db.ProjectRuntimes.Add(new ProjectRuntime
        {
            Id = runtimeId,
            ProjectId = projectId,
            BranchId = Guid.NewGuid(),
            Region = "arn",
            State = RuntimeState.Online,
        });
        await db.SaveChangesAsync();
    }
}
