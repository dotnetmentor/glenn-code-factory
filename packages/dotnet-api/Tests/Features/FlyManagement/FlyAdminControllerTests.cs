using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Source.Features.FlyManagement;
using Source.Features.FlyManagement.Models;
using Source.Features.SystemSettings.Services;
using Source.Features.Users.Models;
using Source.Infrastructure;
using Source.Infrastructure.AuthorizationModels;

namespace Api.Tests.Features.FlyManagement;

/// <summary>
/// End-to-end HTTP tests for the Card-8 admin surface at <c>/api/admin/fly/*</c>. Auth is
/// real: every test that exercises a 200/204 path registers a SuperAdmin via the standard
/// <c>/api/auth/register</c> + <c>UserManager.AddToRoleAsync</c> flow and re-issues a login
/// so the JWT cookie carries the SuperAdmin role claim.
///
/// <para>Where the surface delegates to <see cref="FlyClient"/> we plug a scripted
/// <see cref="HttpMessageHandler"/> in below the resilience pipeline via
/// <c>ConfigurePrimaryHttpMessageHandler</c> — the same technique
/// <see cref="FlyResiliencePipelineTests"/> uses — so the real client + audit pipeline run
/// and we just substitute the wire-level Fly response. The audit-log tests skip the
/// handler entirely and write <see cref="FlyOperation"/> rows straight into the test DB.</para>
/// </summary>
[Collection(HangfireTestCollection.Name)]
public class FlyAdminControllerTests : IntegrationTestBase
{
    private const string Password = "Password123!";

    /// <summary>
    /// Mirrors the API's controller JSON config (<c>AddJsonOptions</c> in Program.cs) so the
    /// test client deserialises <see cref="FlyOperationStatus"/> from the string form the
    /// server emits ("Succeeded") instead of the default integer form.
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly Source.Features.FlyManagement.Configuration.FlyOptions DefaultFlyOptions = new()
    {
        ApiToken = "fly_pat_secret_xyz",
        OrgSlug = "personal",
        AppName = "test-app",
        DefaultRegion = "arn",
    };

    /// <summary>
    /// Seed the DB-backed <c>Fly:*</c> SystemSettings rows. The Testing environment skips
    /// the production seeder, and FlyOptionsAccessor reads through SystemSettings, so
    /// without this every Fly call would hit an empty AppName and 404 the Fly API.
    /// </summary>
    private async Task SeedFlySettingsAsync()
    {
        using var scope = CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISystemSettingsService>();
        await settings.SetAsync("Fly:ApiToken", DefaultFlyOptions.ApiToken, isSecret: true);
        await settings.SetAsync("Fly:OrgSlug", DefaultFlyOptions.OrgSlug, isSecret: false);
        await settings.SetAsync("Fly:AppName", DefaultFlyOptions.AppName, isSecret: false);
        await settings.SetAsync("Fly:DefaultRegion", DefaultFlyOptions.DefaultRegion, isSecret: false);
    }

    // ----------------------------------------------------------------------
    // Auth gating — every endpoint must require SuperAdmin
    // ----------------------------------------------------------------------

    [Fact]
    public async Task Unauthorized_WithoutAuth_Returns401_OnListMachines()
    {
        var response = await Client.GetAsync("/api/admin/fly/machines");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Unauthorized_WithoutAuth_Returns401_OnListOperations()
    {
        var response = await Client.GetAsync("/api/admin/fly/operations");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task NonSuperAdmin_Returns403_OnListMachines()
    {
        var (client, _) = await RegisterUserAsync();
        var response = await client.GetAsync("/api/admin/fly/machines");
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task NonSuperAdmin_Returns403_OnListOperations()
    {
        var (client, _) = await RegisterUserAsync();
        var response = await client.GetAsync("/api/admin/fly/operations");
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ----------------------------------------------------------------------
    // Fly passthrough endpoints — wire shape + happy path
    // ----------------------------------------------------------------------

    [Fact]
    public async Task ListMachines_Authorized_ReturnsOk()
    {
        var handler = new ScriptedHandler();
        handler.Enqueue(HttpStatusCode.OK, """
            [
                {"id":"m-1","name":"a","state":"started","region":"arn","instance_id":"i-1","private_ip":null,"created_at":"2026-05-08T10:00:00Z"},
                {"id":"m-2","name":"b","state":"stopped","region":"arn","instance_id":"i-2","private_ip":null,"created_at":"2026-05-08T10:01:00Z"}
            ]
            """);
        WithFlyHttpHandler(handler);

        var (client, _) = await RegisterSuperAdminAsync();
        await SeedFlySettingsAsync();

        var response = await client.GetAsync("/api/admin/fly/machines");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var machines = await response.Content.ReadFromJsonAsync<List<FlyMachine>>();
        machines.Should().NotBeNull();
        machines!.Should().HaveCount(2);
        machines[0].Id.Should().Be("m-1");
        machines[1].State.Should().Be("stopped");

        // Confirms the controller delegated to FlyClient — exactly one upstream call landed.
        handler.CallCount.Should().Be(1);
        handler.LastRequest!.RequestUri!.AbsoluteUri
            .Should().Be("https://api.machines.dev/v1/apps/test-app/machines");
    }

    [Fact]
    public async Task GetMachine_Authorized_ReturnsOk_AndTargetsCorrectUrl()
    {
        var handler = new ScriptedHandler();
        handler.Enqueue(HttpStatusCode.OK, """
            {"id":"m-1","name":"rt","state":"started","region":"arn","instance_id":"i-1","private_ip":"fdaa::1","created_at":"2026-05-08T10:00:00Z"}
            """);
        WithFlyHttpHandler(handler);

        var (client, _) = await RegisterSuperAdminAsync();
        await SeedFlySettingsAsync();

        var response = await client.GetAsync("/api/admin/fly/machines/m-1");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var machine = await response.Content.ReadFromJsonAsync<FlyMachine>();
        machine!.Id.Should().Be("m-1");
        machine.PrivateIp.Should().Be("fdaa::1");

        handler.LastRequest!.RequestUri!.AbsoluteUri
            .Should().Be("https://api.machines.dev/v1/apps/test-app/machines/m-1");
    }

    [Fact]
    public async Task StartMachine_Authorized_Returns204()
    {
        var handler = new ScriptedHandler();
        handler.Enqueue(HttpStatusCode.OK, "{\"ok\":true}");
        WithFlyHttpHandler(handler);

        var (client, _) = await RegisterSuperAdminAsync();
        await SeedFlySettingsAsync();

        var response = await client.PostAsync("/api/admin/fly/machines/m-1/start", content: null);
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        handler.LastRequest!.Method.Should().Be(HttpMethod.Post);
        handler.LastRequest.RequestUri!.AbsoluteUri
            .Should().Be("https://api.machines.dev/v1/apps/test-app/machines/m-1/start");
    }

    [Fact]
    public async Task StopMachine_WithBody_ForwardsSignalAndTimeout()
    {
        var handler = new ScriptedHandler();
        handler.Enqueue(HttpStatusCode.OK, "{\"ok\":true}");
        WithFlyHttpHandler(handler);

        var (client, _) = await RegisterSuperAdminAsync();
        await SeedFlySettingsAsync();

        var response = await client.PostAsJsonAsync(
            "/api/admin/fly/machines/m-1/stop",
            new { signal = "SIGTERM", timeout = 30 });
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var body = await handler.LastRequest!.Content!.ReadAsStringAsync();
        body.Should().Contain("\"signal\":\"SIGTERM\"");
        body.Should().Contain("\"timeout\":30");
    }

    [Fact]
    public async Task SuspendMachine_Authorized_Returns204()
    {
        var handler = new ScriptedHandler();
        handler.Enqueue(HttpStatusCode.OK, "{\"ok\":true}");
        WithFlyHttpHandler(handler);

        var (client, _) = await RegisterSuperAdminAsync();
        await SeedFlySettingsAsync();

        var response = await client.PostAsync("/api/admin/fly/machines/m-1/suspend", content: null);
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        handler.LastRequest!.RequestUri!.AbsoluteUri
            .Should().Be("https://api.machines.dev/v1/apps/test-app/machines/m-1/suspend");
    }

    [Fact]
    public async Task DestroyMachine_WithForce_PassesForceTrue()
    {
        var handler = new ScriptedHandler();
        handler.Enqueue(HttpStatusCode.OK, "{\"ok\":true}");
        WithFlyHttpHandler(handler);

        var (client, _) = await RegisterSuperAdminAsync();
        await SeedFlySettingsAsync();

        var response = await client.DeleteAsync("/api/admin/fly/machines/m-1?force=true");
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        handler.LastRequest!.Method.Should().Be(HttpMethod.Delete);
        handler.LastRequest.RequestUri!.AbsoluteUri
            .Should().Be("https://api.machines.dev/v1/apps/test-app/machines/m-1?force=true");
    }

    [Fact]
    public async Task DestroyMachine_WithoutForce_OmitsForceQueryString()
    {
        var handler = new ScriptedHandler();
        handler.Enqueue(HttpStatusCode.OK, "{\"ok\":true}");
        WithFlyHttpHandler(handler);

        var (client, _) = await RegisterSuperAdminAsync();
        await SeedFlySettingsAsync();

        var response = await client.DeleteAsync("/api/admin/fly/machines/m-1");
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        handler.LastRequest!.RequestUri!.AbsoluteUri
            .Should().Be("https://api.machines.dev/v1/apps/test-app/machines/m-1");
    }

    [Fact]
    public async Task ListVolumes_Authorized_ReturnsOk()
    {
        var handler = new ScriptedHandler();
        handler.Enqueue(HttpStatusCode.OK, """
            [{"id":"vol-1","name":"data","region":"arn","size_gb":10,"state":"created","attached_machine_id":null,"encrypted":true,"created_at":"2026-05-08T10:00:00Z"}]
            """);
        WithFlyHttpHandler(handler);

        var (client, _) = await RegisterSuperAdminAsync();
        await SeedFlySettingsAsync();

        var response = await client.GetAsync("/api/admin/fly/volumes");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var volumes = await response.Content.ReadFromJsonAsync<List<FlyVolume>>();
        volumes!.Should().HaveCount(1);
        volumes[0].Id.Should().Be("vol-1");
        volumes[0].SizeGb.Should().Be(10);
    }

    [Fact]
    public async Task DestroyVolume_Authorized_Returns204()
    {
        var handler = new ScriptedHandler();
        handler.Enqueue(HttpStatusCode.OK, "{\"ok\":true}");
        WithFlyHttpHandler(handler);

        var (client, _) = await RegisterSuperAdminAsync();
        await SeedFlySettingsAsync();

        var response = await client.DeleteAsync("/api/admin/fly/volumes/vol-1");
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        handler.LastRequest!.RequestUri!.AbsoluteUri
            .Should().Be("https://api.machines.dev/v1/apps/test-app/volumes/vol-1");
    }

    [Fact]
    public async Task ExtendVolume_Authorized_ReturnsOkWithNewSize()
    {
        var handler = new ScriptedHandler();
        handler.Enqueue(HttpStatusCode.OK, """
            {"id":"vol-1","name":"data","region":"arn","size_gb":25,"state":"created","attached_machine_id":"m-1","encrypted":true,"created_at":"2026-05-08T10:00:00Z"}
            """);
        WithFlyHttpHandler(handler);

        var (client, _) = await RegisterSuperAdminAsync();
        await SeedFlySettingsAsync();

        var response = await client.PostAsJsonAsync(
            "/api/admin/fly/volumes/vol-1/extend",
            new ExtendVolumeRequest(25));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var volume = await response.Content.ReadFromJsonAsync<FlyVolume>();
        volume!.Id.Should().Be("vol-1");
        volume.SizeGb.Should().Be(25);

        handler.LastRequest!.RequestUri!.AbsoluteUri
            .Should().Be("https://api.machines.dev/v1/apps/test-app/volumes/vol-1/extend");
        handler.LastRequest.Method.Should().Be(HttpMethod.Put);
    }

    [Fact]
    public async Task ExtendVolume_Unauthenticated_Returns401()
    {
        var response = await Client.PostAsJsonAsync(
            "/api/admin/fly/volumes/vol-1/extend",
            new ExtendVolumeRequest(25));
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task ExtendVolume_NonPositiveSize_Returns400(int sizeGb)
    {
        var (client, _) = await RegisterSuperAdminAsync();
        await SeedFlySettingsAsync();

        var response = await client.PostAsJsonAsync(
            "/api/admin/fly/volumes/vol-1/extend",
            new ExtendVolumeRequest(sizeGb));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ExtendVolume_FlyRejectsShrink_Surfaces5xx()
    {
        // Fly returns 422 when you try to shrink. The thin passthrough lets the
        // FlyApiException bubble — the operator gets a 5xx (default ASP.NET error
        // handling) which is fine for an admin-only surface.
        var handler = new ScriptedHandler();
        handler.Enqueue(HttpStatusCode.UnprocessableEntity,
            "{\"error\":\"size_gb must be greater than current size\"}");
        WithFlyHttpHandler(handler);

        var (client, _) = await RegisterSuperAdminAsync();
        await SeedFlySettingsAsync();

        var response = await client.PostAsJsonAsync(
            "/api/admin/fly/volumes/vol-1/extend",
            new ExtendVolumeRequest(5));

        ((int)response.StatusCode).Should().BeGreaterThanOrEqualTo(500);
    }

    [Fact]
    public async Task GetApp_Authorized_ReturnsOk()
    {
        var handler = new ScriptedHandler();
        handler.Enqueue(HttpStatusCode.OK, """
            {"name":"test-app","organization":{"slug":"personal"},"status":"deployed"}
            """);
        WithFlyHttpHandler(handler);

        var (client, _) = await RegisterSuperAdminAsync();
        await SeedFlySettingsAsync();

        var response = await client.GetAsync("/api/admin/fly/app");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        handler.LastRequest!.RequestUri!.AbsoluteUri
            .Should().Be("https://api.machines.dev/v1/apps/test-app");
    }

    [Fact]
    public async Task EnsureApp_WhenAppExists_ReturnsOk()
    {
        // EnsureApp does a GET first; on 200 it returns immediately without a follow-up POST.
        var handler = new ScriptedHandler();
        handler.Enqueue(HttpStatusCode.OK, """
            {"name":"test-app","organization":{"slug":"personal"},"status":"deployed"}
            """);
        WithFlyHttpHandler(handler);

        var (client, _) = await RegisterSuperAdminAsync();
        await SeedFlySettingsAsync();

        var response = await client.PostAsync("/api/admin/fly/app/ensure", content: null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        handler.CallCount.Should().Be(1, "EnsureApp short-circuits when the GET returns 200");
    }

    // ----------------------------------------------------------------------
    // Operations (audit) — filtering + paging
    // ----------------------------------------------------------------------

    [Fact]
    public async Task ListOperations_FiltersByStatus()
    {
        var (client, _) = await RegisterSuperAdminAsync();

        await SeedOperationsAsync(
            (FlyOperationStatus.Succeeded, "CreateMachine", null),
            (FlyOperationStatus.Failed, "CreateMachine", null),
            (FlyOperationStatus.Succeeded, "GetMachine", null),
            (FlyOperationStatus.Pending, "ListMachines", null));

        var response = await client.GetAsync("/api/admin/fly/operations?status=Succeeded");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var page = await response.Content.ReadFromJsonAsync<FlyOperationsResponse>(JsonOptions);
        page!.Total.Should().Be(2, "two Succeeded rows were seeded");
        page.Items.Should().HaveCount(2);
        page.Items.Should().OnlyContain(o => o.Status == FlyOperationStatus.Succeeded);
    }

    [Fact]
    public async Task ListOperations_FiltersBySince()
    {
        var (client, _) = await RegisterSuperAdminAsync();

        // Seed one row from yesterday, two from today. Backdate by mutating CreatedAt
        // after the initial save (the IAuditable interceptor stamps it on insert).
        await SeedOperationsAsync(
            (FlyOperationStatus.Succeeded, "CreateMachine", null),
            (FlyOperationStatus.Succeeded, "GetMachine", null),
            (FlyOperationStatus.Succeeded, "ListMachines", null));

        using (var scope = CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var oldest = await db.FlyOperations.OrderBy(o => o.CreatedAt).FirstAsync();
            oldest.CreatedAt = DateTime.UtcNow.AddDays(-2);
            await db.SaveChangesAsync();
        }

        var sinceIso = Uri.EscapeDataString(DateTime.UtcNow.AddDays(-1).ToString("o"));
        var response = await client.GetAsync($"/api/admin/fly/operations?since={sinceIso}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var page = await response.Content.ReadFromJsonAsync<FlyOperationsResponse>(JsonOptions);
        page!.Total.Should().Be(2, "the day-old row should be filtered out by since");
        page.Items.Should().HaveCount(2);
    }

    [Fact]
    public async Task ListOperations_FiltersByRuntimeId()
    {
        var (client, _) = await RegisterSuperAdminAsync();
        var targetRuntime = Guid.NewGuid();
        var otherRuntime = Guid.NewGuid();

        await SeedOperationsAsync(
            (FlyOperationStatus.Succeeded, "CreateMachine", targetRuntime),
            (FlyOperationStatus.Succeeded, "GetMachine", targetRuntime),
            (FlyOperationStatus.Succeeded, "CreateMachine", otherRuntime),
            (FlyOperationStatus.Succeeded, "ListMachines", null));

        var response = await client.GetAsync($"/api/admin/fly/operations?runtimeId={targetRuntime}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var page = await response.Content.ReadFromJsonAsync<FlyOperationsResponse>(JsonOptions);
        page!.Total.Should().Be(2);
        page.Items.Should().OnlyContain(o => o.RuntimeId == targetRuntime);
    }

    [Fact]
    public async Task ListOperations_PaginatesCorrectly()
    {
        var (client, _) = await RegisterSuperAdminAsync();

        // 100 rows. Page 1 (size 20) and page 2 should hold disjoint items.
        var seeds = Enumerable.Range(0, 100)
            .Select(i => (FlyOperationStatus.Succeeded, $"Op{i}", (Guid?)null))
            .ToArray();
        await SeedOperationsAsync(seeds);

        var page1Resp = await client.GetAsync("/api/admin/fly/operations?page=1&pageSize=20");
        var page2Resp = await client.GetAsync("/api/admin/fly/operations?page=2&pageSize=20");
        page1Resp.StatusCode.Should().Be(HttpStatusCode.OK);
        page2Resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var page1 = await page1Resp.Content.ReadFromJsonAsync<FlyOperationsResponse>(JsonOptions);
        var page2 = await page2Resp.Content.ReadFromJsonAsync<FlyOperationsResponse>(JsonOptions);

        page1!.Total.Should().Be(100);
        page1.Page.Should().Be(1);
        page1.PageSize.Should().Be(20);
        page1.Items.Should().HaveCount(20);

        page2!.Total.Should().Be(100);
        page2.Page.Should().Be(2);
        page2.Items.Should().HaveCount(20);

        // Disjoint slices — page 1 IDs must not overlap page 2 IDs.
        var page1Ids = page1.Items.Select(o => o.Id).ToHashSet();
        var page2Ids = page2.Items.Select(o => o.Id).ToHashSet();
        page1Ids.Should().NotIntersectWith(page2Ids);
    }

    [Fact]
    public async Task ListOperations_PageSizeCappedAt200()
    {
        var (client, _) = await RegisterSuperAdminAsync();

        // 250 rows. Even with pageSize=1000 the response must hold at most 200.
        var seeds = Enumerable.Range(0, 250)
            .Select(i => (FlyOperationStatus.Succeeded, $"Op{i}", (Guid?)null))
            .ToArray();
        await SeedOperationsAsync(seeds);

        var response = await client.GetAsync("/api/admin/fly/operations?pageSize=1000");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var page = await response.Content.ReadFromJsonAsync<FlyOperationsResponse>(JsonOptions);
        page!.Total.Should().Be(250);
        page.PageSize.Should().Be(200, "pageSize is hard-capped at 200");
        page.Items.Should().HaveCount(200);
    }

    [Fact]
    public async Task ListOperations_UnknownStatus_IsIgnoredNotRejected()
    {
        var (client, _) = await RegisterSuperAdminAsync();
        await SeedOperationsAsync(
            (FlyOperationStatus.Succeeded, "Op1", null),
            (FlyOperationStatus.Failed, "Op2", null));

        // Garbage status query should NOT 400 — it should be ignored, returning all rows.
        var response = await client.GetAsync("/api/admin/fly/operations?status=garbage");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var page = await response.Content.ReadFromJsonAsync<FlyOperationsResponse>(JsonOptions);
        page!.Total.Should().Be(2);
    }

    [Fact]
    public async Task ListOperations_OrdersByCreatedAtDescending()
    {
        var (client, _) = await RegisterSuperAdminAsync();

        await SeedOperationsAsync(
            (FlyOperationStatus.Succeeded, "First", null),
            (FlyOperationStatus.Succeeded, "Second", null),
            (FlyOperationStatus.Succeeded, "Third", null));

        // Backdate the first two so "Third" is the newest.
        using (var scope = CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var rows = await db.FlyOperations.OrderBy(o => o.Operation).ToListAsync();
            rows[0].CreatedAt = DateTime.UtcNow.AddMinutes(-10);  // First
            rows[1].CreatedAt = DateTime.UtcNow.AddMinutes(-5);   // Second
            // Third keeps "now"
            await db.SaveChangesAsync();
        }

        var response = await client.GetAsync("/api/admin/fly/operations");
        var page = await response.Content.ReadFromJsonAsync<FlyOperationsResponse>(JsonOptions);
        page!.Items.Select(i => i.Operation).Should().ContainInOrder("Third", "Second", "First");
    }

    // ----------------------------------------------------------------------
    // helpers
    // ----------------------------------------------------------------------

    /// <summary>
    /// Splice the supplied <see cref="HttpMessageHandler"/> in below the FlyClient's
    /// resilience pipeline. Mirrors how <see cref="FlyResiliencePipelineTests"/> tests the
    /// same client. Must be called before the first <see cref="IntegrationTestBase.Client"/>
    /// access (the base class enforces this).
    /// </summary>
    private void WithFlyHttpHandler(HttpMessageHandler handler)
    {
        WithServiceFactory(services =>
        {
            services.AddHttpClient<FlyClient>()
                .ConfigurePrimaryHttpMessageHandler(() => handler);
        });
    }

    private async Task SeedOperationsAsync(params (FlyOperationStatus Status, string Operation, Guid? RuntimeId)[] rows)
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        foreach (var (status, op, runtimeId) in rows)
        {
            db.FlyOperations.Add(new FlyOperation
            {
                Id = Guid.NewGuid(),
                Operation = op,
                Status = status,
                RuntimeId = runtimeId,
                RequestPayload = "{}",
                ResponsePayload = status == FlyOperationStatus.Pending ? null : "{}",
                HttpStatusCode = status switch
                {
                    FlyOperationStatus.Succeeded => 200,
                    FlyOperationStatus.Failed => 500,
                    _ => null,
                },
                LatencyMs = status == FlyOperationStatus.Pending ? null : 100,
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

    /// <summary>
    /// Scripted handler that returns canned responses in FIFO order and records the last
    /// request seen. Throws on overflow so a test that under-mocks fails loudly rather than
    /// hanging.
    /// </summary>
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new();
        public int CallCount { get; private set; }
        public HttpRequestMessage? LastRequest { get; private set; }

        public void Enqueue(HttpStatusCode status, string body)
        {
            _responses.Enqueue(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            // Buffer the body so test assertions can read it after the handler returns.
            if (request.Content is not null)
            {
                var copy = await request.Content.ReadAsStringAsync(cancellationToken);
                LastRequest = new HttpRequestMessage(request.Method, request.RequestUri)
                {
                    Content = new StringContent(copy, Encoding.UTF8, "application/json"),
                };
            }
            else
            {
                LastRequest = new HttpRequestMessage(request.Method, request.RequestUri);
            }

            if (_responses.Count == 0)
            {
                throw new InvalidOperationException(
                    $"ScriptedHandler exhausted after {CallCount} calls — test under-mocked.");
            }
            return _responses.Dequeue();
        }
    }
}
