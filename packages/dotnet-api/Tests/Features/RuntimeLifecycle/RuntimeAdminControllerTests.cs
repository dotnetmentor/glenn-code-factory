using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Source.Features.RuntimeLifecycle.Models;
using Source.Features.Users.Models;
using Source.Infrastructure;
using Source.Infrastructure.AuthorizationModels;

namespace Api.Tests.Features.RuntimeLifecycle;

/// <summary>
/// End-to-end HTTP tests for the operator runtime-admin surface at
/// <c>/api/admin/runtimes/*</c>. Mirrors <c>BootstrapRunsControllerTests</c> exactly:
/// real auth via <c>/api/auth/register</c> + <c>UserManager.AddToRoleAsync</c>, fresh
/// in-memory DB per factory, runtime + transition rows seeded straight into the test DB.
/// </summary>
[Collection(HangfireTestCollection.Name)]
public class RuntimeAdminControllerTests : IntegrationTestBase
{
    private const string Password = "Password123!";

    /// <summary>
    /// Match the API's controller JSON config (<c>AddJsonOptions</c>) so we deserialise
    /// <see cref="RuntimeState"/> from its string form ("Online") rather than the
    /// default integer form.
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    // ----------------------------------------------------------------------
    // Auth gating
    // ----------------------------------------------------------------------

    [Fact]
    public async Task Unauthenticated_Returns401_OnList()
    {
        var response = await Client.GetAsync("/api/admin/runtimes");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task NonSuperAdmin_Returns403_OnList()
    {
        var (client, _) = await RegisterUserAsync();
        var response = await client.GetAsync("/api/admin/runtimes");
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ----------------------------------------------------------------------
    // List filters
    // ----------------------------------------------------------------------

    [Fact]
    public async Task List_FiltersByState()
    {
        var (client, _) = await RegisterSuperAdminAsync();

        await SeedRuntimeAsync(RuntimeState.Online, Guid.NewGuid());
        await SeedRuntimeAsync(RuntimeState.Online, Guid.NewGuid());
        await SeedRuntimeAsync(RuntimeState.Failed, Guid.NewGuid());

        var response = await client.GetAsync("/api/admin/runtimes?state=Online");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var page = await response.Content.ReadFromJsonAsync<RuntimesListResponse>(JsonOptions);
        page!.Total.Should().Be(2);
        page.Items.Should().OnlyContain(r => r.State == RuntimeState.Online);
    }

    [Fact]
    public async Task List_FiltersByProjectId()
    {
        var (client, _) = await RegisterSuperAdminAsync();
        var targetProject = Guid.NewGuid();
        var otherProject  = Guid.NewGuid();

        await SeedRuntimeAsync(RuntimeState.Online, targetProject);
        await SeedRuntimeAsync(RuntimeState.Failed, targetProject);
        await SeedRuntimeAsync(RuntimeState.Online, otherProject);

        var response = await client.GetAsync($"/api/admin/runtimes?projectId={targetProject}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var page = await response.Content.ReadFromJsonAsync<RuntimesListResponse>(JsonOptions);
        page!.Total.Should().Be(2);
        page.Items.Should().OnlyContain(r => r.ProjectId == targetProject);
    }

    [Fact]
    public async Task List_PaginationCapsAt200()
    {
        var (client, _) = await RegisterSuperAdminAsync();

        // 250 runtimes — past the 200 cap so we can prove the cap actually clamps.
        for (var i = 0; i < 250; i++)
        {
            await SeedRuntimeAsync(RuntimeState.Online, Guid.NewGuid());
        }

        var response = await client.GetAsync("/api/admin/runtimes?pageSize=500");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var page = await response.Content.ReadFromJsonAsync<RuntimesListResponse>(JsonOptions);
        page!.Total.Should().Be(250);
        page.PageSize.Should().Be(200, "pageSize is hard-capped at 200");
        page.Items.Should().HaveCount(200);
    }

    // ----------------------------------------------------------------------
    // GetById
    // ----------------------------------------------------------------------

    [Fact]
    public async Task GetById_Found_Returns200WithRecentTransitions()
    {
        var (client, _) = await RegisterSuperAdminAsync();
        var runtime = await SeedRuntimeAsync(RuntimeState.Online, Guid.NewGuid());

        // 60 transitions — past the 50 cap. Each one carries a distinct Reason so the
        // assertion can observe newest-first ordering and the cap together.
        var stages = new (RuntimeState? From, RuntimeState To, string Reason)[60];
        for (var i = 0; i < 60; i++)
        {
            stages[i] = (RuntimeState.Online, RuntimeState.Online, $"r{i:D2}");
        }
        await SeedTransitionsAsync(runtime.Id, stages);

        var response = await client.GetAsync($"/api/admin/runtimes/{runtime.Id}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var detail = await response.Content.ReadFromJsonAsync<RuntimeDetailResponse>(JsonOptions);
        detail!.Runtime.Id.Should().Be(runtime.Id);
        detail.RecentTransitions.Should().HaveCount(50, "the endpoint hard-caps recent transitions at 50");

        // Newest first — last-seeded "r59" should be at the top.
        detail.RecentTransitions[0].Reason.Should().Be("r59");
        detail.RecentTransitions[^1].Reason.Should().Be("r10",
            "the 50 newest after r59..r10 inclusive");
    }

    [Fact]
    public async Task GetById_NotFound_Returns404()
    {
        var (client, _) = await RegisterSuperAdminAsync();
        var response = await client.GetAsync($"/api/admin/runtimes/{Guid.NewGuid()}");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ----------------------------------------------------------------------
    // Reset
    // ----------------------------------------------------------------------

    [Fact]
    public async Task Reset_FailedRuntime_TransitionsToPending()
    {
        var (client, _) = await RegisterSuperAdminAsync();
        var runtime = await SeedRuntimeAsync(RuntimeState.Failed, Guid.NewGuid());

        var response = await client.PostAsync($"/api/admin/runtimes/{runtime.Id}/reset", content: null);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        var body = await response.Content.ReadFromJsonAsync<ProjectRuntime>(JsonOptions);
        body!.State.Should().Be(RuntimeState.Pending);

        // And the audit row landed via the DomainEventInterceptor pipeline.
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var events = await db.RuntimeStateEvents.Where(e => e.RuntimeId == runtime.Id).ToListAsync();
        events.Should().ContainSingle()
            .Which.ToState.Should().Be(RuntimeState.Pending);
    }

    [Fact]
    public async Task Reset_OnlineRuntime_Returns409()
    {
        // Online -> Pending is illegal in the state graph (only Failed -> Pending is).
        var (client, _) = await RegisterSuperAdminAsync();
        var runtime = await SeedRuntimeAsync(RuntimeState.Online, Guid.NewGuid());

        var response = await client.PostAsync($"/api/admin/runtimes/{runtime.Id}/reset", content: null);
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);

        // State must be untouched — TransitionTo bails before mutating on rejection.
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var row = await db.ProjectRuntimes.FirstAsync(r => r.Id == runtime.Id);
        row.State.Should().Be(RuntimeState.Online);
    }

    // ----------------------------------------------------------------------
    // ForceSuspend
    // ----------------------------------------------------------------------

    [Fact]
    public async Task ForceSuspend_OnlineRuntime_TransitionsToSuspending()
    {
        var (client, _) = await RegisterSuperAdminAsync();
        var runtime = await SeedRuntimeAsync(RuntimeState.Online, Guid.NewGuid());

        var response = await client.PostAsync($"/api/admin/runtimes/{runtime.Id}/force-suspend", content: null);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        var body = await response.Content.ReadFromJsonAsync<ProjectRuntime>(JsonOptions);
        body!.State.Should().Be(RuntimeState.Suspending);
    }

    [Fact]
    public async Task ForceSuspend_PendingRuntime_Returns409()
    {
        // Pending -> Suspending is not a legal edge.
        var (client, _) = await RegisterSuperAdminAsync();
        var runtime = await SeedRuntimeAsync(RuntimeState.Pending, Guid.NewGuid());

        var response = await client.PostAsync($"/api/admin/runtimes/{runtime.Id}/force-suspend", content: null);
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    // ----------------------------------------------------------------------
    // ForceDelete
    // ----------------------------------------------------------------------

    [Fact]
    public async Task ForceDelete_AnyState_TransitionsToDeleting()
    {
        // Online -> Deleting is legal. Pick the busiest happy-path state to exercise.
        var (client, _) = await RegisterSuperAdminAsync();
        var runtime = await SeedRuntimeAsync(RuntimeState.Online, Guid.NewGuid());

        var response = await client.PostAsync($"/api/admin/runtimes/{runtime.Id}/force-delete", content: null);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        var body = await response.Content.ReadFromJsonAsync<ProjectRuntime>(JsonOptions);
        body!.State.Should().Be(RuntimeState.Deleting);
    }

    [Fact]
    public async Task ForceDelete_AlreadyDeleted_Returns409()
    {
        // Deleted is terminal — no outgoing edges, including Deleting.
        var (client, _) = await RegisterSuperAdminAsync();
        var runtime = await SeedRuntimeAsync(RuntimeState.Deleted, Guid.NewGuid());

        var response = await client.PostAsync($"/api/admin/runtimes/{runtime.Id}/force-delete", content: null);
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    // ----------------------------------------------------------------------
    // ForceRespawn
    // ----------------------------------------------------------------------

    [Fact]
    public async Task ForceRespawn_OnlineRuntime_TransitionsToCrashed_Returns202()
    {
        // The happy path: an Online runtime gets punched into Crashed and
        // ScheduleRespawnHandler is left to queue the destroy/recreate job.
        var (client, _) = await RegisterSuperAdminAsync();
        var runtime = await SeedRuntimeAsync(RuntimeState.Online, Guid.NewGuid());

        var response = await client.PostAsync($"/api/admin/runtimes/{runtime.Id}/force-respawn", content: null);
        response.StatusCode.Should().Be(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());

        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var row = await db.ProjectRuntimes.FirstAsync(r => r.Id == runtime.Id);
        row.State.Should().Be(RuntimeState.Crashed);

        // Audit row must exist with the operator-tagged reason so post-mortems
        // can tell a force-respawn from an organic Fly-reported crash.
        var audit = await db.RuntimeStateEvents
            .Where(e => e.RuntimeId == runtime.Id)
            .ToListAsync();
        audit.Should().ContainSingle()
            .Which.Reason.Should().Be("operator:force-respawn");
        audit[0].ToState.Should().Be(RuntimeState.Crashed);
        audit[0].FromState.Should().Be(RuntimeState.Online);
        audit[0].TriggeredBy.Should().StartWith("operator:");
    }

    [Fact]
    public async Task ForceRespawn_BootstrappingRuntime_TransitionsToCrashed()
    {
        // Bootstrapping is a legal source state — exercises one of the rarer
        // entries in _forceRespawnableStates so we know the set isn't just "Online".
        var (client, _) = await RegisterSuperAdminAsync();
        var runtime = await SeedRuntimeAsync(RuntimeState.Bootstrapping, Guid.NewGuid());

        var response = await client.PostAsync($"/api/admin/runtimes/{runtime.Id}/force-respawn", content: null);
        response.StatusCode.Should().Be(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());

        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var row = await db.ProjectRuntimes.FirstAsync(r => r.Id == runtime.Id);
        row.State.Should().Be(RuntimeState.Crashed);
    }

    [Fact]
    public async Task ForceRespawn_FailedRuntime_Returns409()
    {
        // Failed has no -> Crashed edge in the graph, and respawning a Failed
        // runtime is the wrong tool anyway: the operator wants reset-from-failed.
        // We assert the 409 hint mentions that so the response is actionable.
        var (client, _) = await RegisterSuperAdminAsync();
        var runtime = await SeedRuntimeAsync(RuntimeState.Failed, Guid.NewGuid());

        var response = await client.PostAsync($"/api/admin/runtimes/{runtime.Id}/force-respawn", content: null);
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("reset-from-failed",
            "the 409 should hint at the right tool for a Failed runtime");

        // State must be untouched.
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var row = await db.ProjectRuntimes.FirstAsync(r => r.Id == runtime.Id);
        row.State.Should().Be(RuntimeState.Failed);
    }

    [Fact]
    public async Task ForceRespawn_PendingRuntime_Returns409()
    {
        // Pending has no Fly machine to crash yet, so respawn doesn't apply.
        var (client, _) = await RegisterSuperAdminAsync();
        var runtime = await SeedRuntimeAsync(RuntimeState.Pending, Guid.NewGuid());

        var response = await client.PostAsync($"/api/admin/runtimes/{runtime.Id}/force-respawn", content: null);
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);

        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var row = await db.ProjectRuntimes.FirstAsync(r => r.Id == runtime.Id);
        row.State.Should().Be(RuntimeState.Pending);
    }

    [Fact]
    public async Task ForceRespawn_SuspendedRuntime_Returns409()
    {
        // Suspended -> Crashed has no edge in the graph (a stopped machine can't
        // crash). The 409 hint should point the operator at "wake first".
        var (client, _) = await RegisterSuperAdminAsync();
        var runtime = await SeedRuntimeAsync(RuntimeState.Suspended, Guid.NewGuid());

        var response = await client.PostAsync($"/api/admin/runtimes/{runtime.Id}/force-respawn", content: null);
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Wake",
            "the 409 should hint at waking the runtime first for a Suspended source state");
    }

    [Fact]
    public async Task ForceRespawn_DeletedRuntime_Returns409()
    {
        // Deleted is terminal. Other admin endpoints (force-delete) treat this as
        // 409 (the row is still found via the global filter but has no outgoing
        // edge), so we mirror that behaviour rather than 404.
        var (client, _) = await RegisterSuperAdminAsync();
        var runtime = await SeedRuntimeAsync(RuntimeState.Deleted, Guid.NewGuid());

        var response = await client.PostAsync($"/api/admin/runtimes/{runtime.Id}/force-respawn", content: null);
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task ForceRespawn_NonExistentId_Returns404()
    {
        var (client, _) = await RegisterSuperAdminAsync();

        var response = await client.PostAsync($"/api/admin/runtimes/{Guid.NewGuid()}/force-respawn", content: null);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ForceRespawn_AnonymousUser_Returns401()
    {
        // No auth cookie attached — the [Authorize] gate must reject before we even
        // load the row. Use a real seeded runtime id so we know the 401 is from auth,
        // not from a missing row.
        var runtime = await SeedRuntimeAsync(RuntimeState.Online, Guid.NewGuid());

        var response = await Client.PostAsync($"/api/admin/runtimes/{runtime.Id}/force-respawn", content: null);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ForceRespawn_NonSuperAdmin_Returns403()
    {
        // Authenticated but lacks the SuperAdmin role — must be 403, not 401.
        var (client, _) = await RegisterUserAsync();
        var runtime = await SeedRuntimeAsync(RuntimeState.Online, Guid.NewGuid());

        var response = await client.PostAsync($"/api/admin/runtimes/{runtime.Id}/force-respawn", content: null);
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ----------------------------------------------------------------------
    // helpers
    // ----------------------------------------------------------------------

    /// <summary>
    /// Insert a <see cref="ProjectRuntime"/> row in the requested state. Region defaults
    /// to "arn"; ProjectId is supplied per call so callers can scope filter tests.
    /// </summary>
    private async Task<ProjectRuntime> SeedRuntimeAsync(RuntimeState state, Guid projectId)
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var runtime = new ProjectRuntime
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Region = "arn",
            State = state,
        };
        db.ProjectRuntimes.Add(runtime);
        await db.SaveChangesAsync();
        return runtime;
    }

    /// <summary>
    /// Insert one <see cref="RuntimeStateEvent"/> row per supplied stage. <see cref="ApplicationDbContext.SaveChangesAsync"/>
    /// stamps <c>CreatedAt</c> on every Added IAuditable, so the timestamps are overwritten
    /// by the first insert. We do an INSERT pass and then a second pass to backdate each row
    /// — earlier-indexed rows further into the past, last-indexed = newest. This is the same
    /// "save then update timestamps" pattern <see cref="Api.Tests.Features.RuntimeBootstrap.BootstrapRunsControllerTests"/>
    /// uses for its `since` filter test.
    /// </summary>
    private async Task SeedTransitionsAsync(
        Guid runtimeId,
        params (RuntimeState? From, RuntimeState To, string Reason)[] stages)
    {
        var ids = new List<(Guid Id, int Index)>();
        using (var scope = CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            for (var i = 0; i < stages.Length; i++)
            {
                var (from, to, reason) = stages[i];
                var id = Guid.NewGuid();
                db.RuntimeStateEvents.Add(new RuntimeStateEvent
                {
                    Id = id,
                    RuntimeId = runtimeId,
                    FromState = from,
                    ToState = to,
                    Reason = reason,
                    TriggeredBy = "test",
                });
                ids.Add((id, i));
            }
            await db.SaveChangesAsync();
        }

        using (var scope = CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var now = DateTime.UtcNow;
            foreach (var (id, idx) in ids)
            {
                var row = await db.RuntimeStateEvents.FirstAsync(e => e.Id == id);
                // Earlier-indexed rows = older. Last-indexed = `now`.
                row.CreatedAt = now.AddSeconds(-(stages.Length - 1 - idx));
            }
            await db.SaveChangesAsync();
        }
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
        return (client, user!.Id);
    }

    private async Task<(HttpClient Client, string UserId)> RegisterSuperAdminAsync()
    {
        await SeedRolesAsync();

        var email = $"admin-{Guid.NewGuid():N}@example.com";
        var registerClient = Factory.CreateClient();
        var response = await registerClient.PostAsJsonAsync("/api/auth/register", new { email, password = Password });
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        string userId;
        using (var scope = CreateScope())
        {
            var um = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            var user = await um.FindByEmailAsync(email);
            (await um.AddToRoleAsync(user!, RoleConstants.SuperAdmin)).Succeeded.Should().BeTrue();
            userId = user!.Id;
        }

        // Re-login so the JWT carries the SuperAdmin role claim.
        var loginClient = Factory.CreateClient();
        var loginResponse = await loginClient.PostAsJsonAsync("/api/auth/login", new { email, password = Password });
        loginResponse.StatusCode.Should().Be(HttpStatusCode.OK, await loginResponse.Content.ReadAsStringAsync());

        var cookies = loginResponse.Headers.GetValues("Set-Cookie");
        var authCookie = cookies.First(c => c.StartsWith("auth-token=", StringComparison.Ordinal));
        var cookieValue = authCookie.Split(';')[0];

        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", cookieValue);
        return (client, userId);
    }
}
