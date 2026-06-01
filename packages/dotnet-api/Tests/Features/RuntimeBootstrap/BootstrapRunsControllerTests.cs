using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Source.Features.RuntimeBootstrap.Models;
using Source.Features.Users.Models;
using Source.Infrastructure;
using Source.Infrastructure.AuthorizationModels;

namespace Api.Tests.Features.RuntimeBootstrap;

/// <summary>
/// End-to-end HTTP tests for the admin read-only surface at
/// <c>/api/admin/bootstrap-runs/*</c>. Mirrors <c>FlyAdminControllerTests</c> exactly:
/// real auth via <c>/api/auth/register</c> + <c>UserManager.AddToRoleAsync</c>, fresh
/// in-memory DB per factory, BootstrapRun rows seeded straight into the test DB.
/// </summary>
[Collection(HangfireTestCollection.Name)]
public class BootstrapRunsControllerTests : IntegrationTestBase
{
    private const string Password = "Password123!";

    /// <summary>
    /// Match the API's controller JSON config (<c>AddJsonOptions</c>) so we deserialise
    /// <see cref="BootstrapStage"/> from its string form ("Ready") rather than the
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
        var response = await Client.GetAsync("/api/admin/bootstrap-runs");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Unauthenticated_Returns401_OnGetById()
    {
        var response = await Client.GetAsync($"/api/admin/bootstrap-runs/{Guid.NewGuid()}");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task NonSuperAdmin_Returns403_OnList()
    {
        var (client, _) = await RegisterUserAsync();
        var response = await client.GetAsync("/api/admin/bootstrap-runs");
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ----------------------------------------------------------------------
    // Filters
    // ----------------------------------------------------------------------

    [Fact]
    public async Task List_FiltersByRuntimeId()
    {
        var (client, _) = await RegisterSuperAdminAsync();
        var targetRuntime = Guid.NewGuid();
        var otherRuntime = Guid.NewGuid();

        await SeedBootstrapRunsAsync(
            new SeedRow(RuntimeId: targetRuntime, Success: true,  FinalStage: BootstrapStage.Ready),
            new SeedRow(RuntimeId: targetRuntime, Success: false, FinalStage: BootstrapStage.CloningRepo),
            new SeedRow(RuntimeId: otherRuntime,  Success: true,  FinalStage: BootstrapStage.Ready));

        var response = await client.GetAsync($"/api/admin/bootstrap-runs?runtimeId={targetRuntime}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var page = await response.Content.ReadFromJsonAsync<BootstrapRunsResponse>(JsonOptions);
        page!.Total.Should().Be(2);
        page.Items.Should().OnlyContain(r => r.RuntimeId == targetRuntime);
    }

    [Fact]
    public async Task List_FiltersBySuccess()
    {
        var (client, _) = await RegisterSuperAdminAsync();

        await SeedBootstrapRunsAsync(
            new SeedRow(Success: true,  FinalStage: BootstrapStage.Ready),
            new SeedRow(Success: false, FinalStage: BootstrapStage.CloningRepo),
            new SeedRow(Success: false, FinalStage: BootstrapStage.RunningSetup),
            new SeedRow(Success: true,  FinalStage: BootstrapStage.Ready));

        var response = await client.GetAsync("/api/admin/bootstrap-runs?success=false");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var page = await response.Content.ReadFromJsonAsync<BootstrapRunsResponse>(JsonOptions);
        page!.Total.Should().Be(2, "two failed rows were seeded");
        page.Items.Should().OnlyContain(r => r.Success == false);
    }

    [Fact]
    public async Task List_FiltersByStage()
    {
        var (client, _) = await RegisterSuperAdminAsync();

        await SeedBootstrapRunsAsync(
            new SeedRow(Success: false, FinalStage: BootstrapStage.CloningRepo),
            new SeedRow(Success: false, FinalStage: BootstrapStage.CloningRepo),
            new SeedRow(Success: false, FinalStage: BootstrapStage.RunningSetup),
            new SeedRow(Success: true,  FinalStage: BootstrapStage.Ready));

        var response = await client.GetAsync("/api/admin/bootstrap-runs?stage=CloningRepo");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var page = await response.Content.ReadFromJsonAsync<BootstrapRunsResponse>(JsonOptions);
        page!.Total.Should().Be(2);
        page.Items.Should().OnlyContain(r => r.FinalStage == BootstrapStage.CloningRepo);
    }

    [Fact]
    public async Task List_FiltersBySince()
    {
        var (client, _) = await RegisterSuperAdminAsync();

        // Seed 3 rows; backdate one of them well past the since cutoff after insert
        // so the filter has something concrete to exclude.
        await SeedBootstrapRunsAsync(
            new SeedRow(Success: true, FinalStage: BootstrapStage.Ready),
            new SeedRow(Success: true, FinalStage: BootstrapStage.Ready),
            new SeedRow(Success: true, FinalStage: BootstrapStage.Ready));

        using (var scope = CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var oldest = await db.BootstrapRuns.OrderBy(r => r.StartedAt).FirstAsync();
            oldest.StartedAt = DateTime.UtcNow.AddDays(-2);
            await db.SaveChangesAsync();
        }

        var sinceIso = Uri.EscapeDataString(DateTime.UtcNow.AddDays(-1).ToString("o"));
        var response = await client.GetAsync($"/api/admin/bootstrap-runs?since={sinceIso}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var page = await response.Content.ReadFromJsonAsync<BootstrapRunsResponse>(JsonOptions);
        page!.Total.Should().Be(2, "the day-old row should be filtered out by since");
        page.Items.Should().HaveCount(2);
    }

    // ----------------------------------------------------------------------
    // Paging coercion
    // ----------------------------------------------------------------------

    [Fact]
    public async Task List_PaginationCapsAt200()
    {
        var (client, _) = await RegisterSuperAdminAsync();

        var seeds = Enumerable.Range(0, 250)
            .Select(_ => new SeedRow(Success: true, FinalStage: BootstrapStage.Ready))
            .ToArray();
        await SeedBootstrapRunsAsync(seeds);

        var response = await client.GetAsync("/api/admin/bootstrap-runs?pageSize=500");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var page = await response.Content.ReadFromJsonAsync<BootstrapRunsResponse>(JsonOptions);
        page!.Total.Should().Be(250);
        page.PageSize.Should().Be(200, "pageSize is hard-capped at 200");
        page.Items.Should().HaveCount(200);
    }

    [Fact]
    public async Task List_DefaultsAndCoerces()
    {
        var (client, _) = await RegisterSuperAdminAsync();
        await SeedBootstrapRunsAsync(
            new SeedRow(Success: true, FinalStage: BootstrapStage.Ready));

        // page=0 should coerce to 1 and pageSize=0 to 50.
        var response = await client.GetAsync("/api/admin/bootstrap-runs?page=0&pageSize=0");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var page = await response.Content.ReadFromJsonAsync<BootstrapRunsResponse>(JsonOptions);
        page!.Page.Should().Be(1);
        page.PageSize.Should().Be(50);
    }

    [Fact]
    public async Task List_OrderedDescByStartedAt()
    {
        var (client, _) = await RegisterSuperAdminAsync();

        await SeedBootstrapRunsAsync(
            new SeedRow(Success: true, FinalStage: BootstrapStage.Ready, ErrorReason: "First"),
            new SeedRow(Success: true, FinalStage: BootstrapStage.Ready, ErrorReason: "Second"),
            new SeedRow(Success: true, FinalStage: BootstrapStage.Ready, ErrorReason: "Third"));

        // Backdate so "Third" is the newest, "First" the oldest. ErrorReason is
        // doubling as a label — this table allows null/free-text and the order is
        // what we're asserting.
        using (var scope = CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var rows = await db.BootstrapRuns.ToListAsync();
            var first  = rows.Single(r => r.ErrorReason == "First");
            var second = rows.Single(r => r.ErrorReason == "Second");
            var third  = rows.Single(r => r.ErrorReason == "Third");
            first.StartedAt  = DateTime.UtcNow.AddMinutes(-10);
            second.StartedAt = DateTime.UtcNow.AddMinutes(-5);
            third.StartedAt  = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        var response = await client.GetAsync("/api/admin/bootstrap-runs");
        var page = await response.Content.ReadFromJsonAsync<BootstrapRunsResponse>(JsonOptions);
        page!.Items.Select(r => r.ErrorReason).Should().ContainInOrder("Third", "Second", "First");
    }

    // ----------------------------------------------------------------------
    // GET by id
    // ----------------------------------------------------------------------

    [Fact]
    public async Task GetById_Found_Returns200()
    {
        var (client, _) = await RegisterSuperAdminAsync();
        await SeedBootstrapRunsAsync(
            new SeedRow(Success: true, FinalStage: BootstrapStage.Ready, ErrorReason: "needle"));

        Guid id;
        using (var scope = CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            id = (await db.BootstrapRuns.SingleAsync()).Id;
        }

        var response = await client.GetAsync($"/api/admin/bootstrap-runs/{id}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var run = await response.Content.ReadFromJsonAsync<BootstrapRun>(JsonOptions);
        run!.Id.Should().Be(id);
        run.ErrorReason.Should().Be("needle");
        run.FinalStage.Should().Be(BootstrapStage.Ready);
    }

    [Fact]
    public async Task GetById_NotFound_Returns404()
    {
        var (client, _) = await RegisterSuperAdminAsync();
        var response = await client.GetAsync($"/api/admin/bootstrap-runs/{Guid.NewGuid()}");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ----------------------------------------------------------------------
    // helpers
    // ----------------------------------------------------------------------

    /// <summary>
    /// Compact value-tuple-ish row spec for the seeder. Default <see cref="StartedAt"/>
    /// to UTC-now per row (assigned in the seeder so each row picks a fresh "now").
    /// </summary>
    private sealed record SeedRow(
        bool Success,
        BootstrapStage FinalStage,
        Guid? RuntimeId = null,
        string? ErrorReason = null);

    private async Task SeedBootstrapRunsAsync(params SeedRow[] rows)
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        foreach (var row in rows)
        {
            // ErrorReason is technically expected to be null on Success in real prod
            // data, but the column is nullable free-text — using it as a label in
            // tests (e.g. for the ordering assertion) keeps the seed compact.
            db.BootstrapRuns.Add(new BootstrapRun
            {
                Id = Guid.NewGuid(),
                RuntimeId = row.RuntimeId ?? Guid.NewGuid(),
                StartedAt = DateTime.UtcNow,
                EndedAt = DateTime.UtcNow,
                FinalStage = row.FinalStage,
                Success = row.Success,
                ErrorReason = row.ErrorReason ?? (row.Success ? null : "test failure"),
                DaemonVersion = "test-daemon",
                ImageDigest = "sha256:test",
                BootstrapVersion = "v1",
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
