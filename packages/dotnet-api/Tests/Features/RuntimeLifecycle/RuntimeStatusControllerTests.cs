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

namespace Api.Tests.Features.RuntimeLifecycle;

/// <summary>
/// End-to-end HTTP tests for the user-facing runtime-status endpoint at
/// <c>GET /api/projects/{projectId}/branches/{branchId}/runtime/status</c>. Mirrors
/// the <c>BootstrapRunsControllerTests</c> shape: real auth via <c>/api/auth/register</c>,
/// fresh in-memory DB per factory, runtime + transition rows seeded straight into the
/// test DB.
/// </summary>
[Collection(HangfireTestCollection.Name)]
public class RuntimeStatusControllerTests : IntegrationTestBase
{
    private const string Password = "Password123!";

    // Owner user id of the registered caller, used to seed an owned Project for access checks.
    private string? _callerUserId;

    /// <summary>
    /// Match the API's controller JSON config (<c>AddJsonOptions</c>) so we deserialise
    /// <see cref="RuntimeState"/> from its string form ("Online") rather than the
    /// default integer form.
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    [Fact]
    public async Task GetStatus_Unauthenticated_Returns401()
    {
        var response = await Client.GetAsync($"/api/projects/{Guid.NewGuid()}/branches/{Guid.NewGuid()}/runtime/status");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetStatus_NoRuntime_Returns404()
    {
        var (client, _) = await RegisterUserAsync();
        var response = await client.GetAsync($"/api/projects/{Guid.NewGuid()}/branches/{Guid.NewGuid()}/runtime/status");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetStatus_WithRuntime_ReturnsStatusAndRecentTransitions()
    {
        var (client, _) = await RegisterUserAsync();
        var projectId = Guid.NewGuid();
        var branchId = Guid.NewGuid();

        var runtime = await SeedRuntimeAsync(RuntimeState.Online, projectId, branchId);
        // Six transitions, each one a step further into the past — assert we get back
        // exactly five and that the very newest one is first in the list.
        await SeedTransitionsAsync(runtime.Id,
            (RuntimeState.Pending,       RuntimeState.Booting,        "t1"),
            (RuntimeState.Booting,       RuntimeState.Bootstrapping,  "t2"),
            (RuntimeState.Bootstrapping, RuntimeState.Online,         "t3"),
            (RuntimeState.Online,        RuntimeState.Suspending,     "t4"),
            (RuntimeState.Suspending,    RuntimeState.Suspended,      "t5"),
            (RuntimeState.Suspended,     RuntimeState.Waking,         "t6"));

        var response = await client.GetAsync($"/api/projects/{projectId}/branches/{branchId}/runtime/status");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var status = await response.Content.ReadFromJsonAsync<RuntimeStatusResponse>(JsonOptions);
        status.Should().NotBeNull();
        status!.State.Should().Be(RuntimeState.Online);
        status.Region.Should().Be("arn");
        status.RecentTransitions.Should().HaveCount(5, "the endpoint hard-caps recent transitions at 5");

        // OrderByDescending CreatedAt — the newest seeded transition (t6) should land first.
        status.RecentTransitions[0].Reason.Should().Be("t6");
        status.RecentTransitions.Select(t => t.Reason)
            .Should().ContainInOrder("t6", "t5", "t4", "t3", "t2");
    }

    [Fact]
    public async Task GetStatus_DeletedRuntime_Returns404()
    {
        var (client, _) = await RegisterUserAsync();
        var projectId = Guid.NewGuid();
        var branchId = Guid.NewGuid();

        var runtime = await SeedRuntimeAsync(RuntimeState.Online, projectId, branchId);

        // Soft-delete the runtime — the global query filter on ProjectRuntime should
        // hide it from the status endpoint, matching the user-visible UX (deleted
        // runtimes look like "no runtime").
        using (var scope = CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var row = await db.ProjectRuntimes.FirstAsync(r => r.Id == runtime.Id);
            row.IsDeleted = true;
            row.DeletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        var response = await client.GetAsync($"/api/projects/{projectId}/branches/{branchId}/runtime/status");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetStatus_OnlyMostRecentRuntimePerProject()
    {
        var (client, _) = await RegisterUserAsync();
        var projectId = Guid.NewGuid();
        var branchId = Guid.NewGuid();

        // Two runtimes for the same project+branch. The endpoint orders by CreatedAt
        // desc, so the second (newer) one must be the row whose state is returned.
        // Backdate the first row after seeding so the ordering is unambiguous.
        var older = await SeedRuntimeAsync(RuntimeState.Failed, projectId, branchId);
        using (var scope = CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var row = await db.ProjectRuntimes.FirstAsync(r => r.Id == older.Id);
            row.CreatedAt = DateTime.UtcNow.AddHours(-1);
            await db.SaveChangesAsync();
        }
        var newer = await SeedRuntimeAsync(RuntimeState.Online, projectId, branchId);

        var response = await client.GetAsync($"/api/projects/{projectId}/branches/{branchId}/runtime/status");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var status = await response.Content.ReadFromJsonAsync<RuntimeStatusResponse>(JsonOptions);
        status!.State.Should().Be(RuntimeState.Online,
            "the newer runtime (Online) is the one returned, not the older Failed one");

        // Sanity: the two seeds really are distinct rows.
        older.Id.Should().NotBe(newer.Id);
    }

    // ----------------------------------------------------------------------
    // helpers
    // ----------------------------------------------------------------------

    /// <summary>
    /// Seed a <see cref="Source.Features.Projects.Models.Project"/> owned by the caller so the
    /// controller's project-access gate (owner short-circuit) lets the request through. Idempotent.
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

    /// <summary>
    /// Insert a <see cref="ProjectRuntime"/> row in the requested state. Region defaults
    /// to "arn" so the response shape's Region assertion has something stable to match.
    /// </summary>
    private async Task<ProjectRuntime> SeedRuntimeAsync(RuntimeState state, Guid projectId, Guid branchId)
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var runtime = new ProjectRuntime
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            BranchId = branchId,
            Region = "arn",
            State = state,
        };
        db.ProjectRuntimes.Add(runtime);
        await EnsureOwnedProjectAsync(db, projectId);
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
        _callerUserId = user!.Id;
        return (client, user.Id);
    }
}
