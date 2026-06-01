using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Source.Features.Hooks.Controllers;
using Source.Features.Hooks.Models;
using Source.Features.RuntimeLifecycle.Models;
using Source.Features.SignalR.Contracts;
using Source.Features.SignalR.Hubs;
using Source.Features.Users.Models;
using Source.Infrastructure;
using Source.Infrastructure.AuthorizationModels;

namespace Api.Tests.Features.Hooks;

/// <summary>
/// End-to-end HTTP tests for the admin write surface at
/// <c>PUT /api/admin/runtimes/{id}/hooks</c>. Mirrors
/// <see cref="Api.Tests.Features.RuntimeBootstrap.BootstrapRunsControllerTests"/>
/// for auth + DB seeding, and
/// <see cref="Api.Tests.Features.RuntimeTokens.TokenRotationJobTests"/>
/// for the SignalR mock chain (<c>hub.Clients.Group("...").UpdateConfig(...)</c>).
///
/// <para>The real <see cref="IHubContext{THub, T}"/> is swapped out via
/// <see cref="IntegrationTestBase.WithServiceFactory"/> so we can assert on the
/// exact <see cref="ConfigUpdatePayload"/> the controller pushes — the in-memory
/// host has no actual daemons connected, so the production registration would
/// just no-op.</para>
/// </summary>
[Collection(HangfireTestCollection.Name)]
public class HookConfigAdminControllerTests : IntegrationTestBase
{
    private const string Password = "Password123!";

    // SignalR mock chain — hub.Clients.Group("runtime-{id}").UpdateConfig(payload).
    private readonly Mock<IHubContext<RuntimeHub, IRuntimeClient>> _runtimeHub = new();
    private readonly Mock<IHubClients<IRuntimeClient>> _hubClients = new();
    private readonly Mock<IRuntimeClient> _groupClient = new();

    public HookConfigAdminControllerTests()
    {
        _runtimeHub.SetupGet(h => h.Clients).Returns(_hubClients.Object);
        _hubClients.Setup(c => c.Group(It.IsAny<string>())).Returns(_groupClient.Object);
        _groupClient
            .Setup(c => c.UpdateConfig(It.IsAny<ConfigUpdatePayload>()))
            .Returns(Task.CompletedTask);

        WithServiceFactory(services =>
        {
            services.RemoveAll<IHubContext<RuntimeHub, IRuntimeClient>>();
            services.AddSingleton(_runtimeHub.Object);
        });
    }

    // ----------------------------------------------------------------------
    // Auth gating
    // ----------------------------------------------------------------------

    [Fact]
    public async Task Unauthenticated_Returns401()
    {
        var response = await Client.PutAsJsonAsync(
            $"/api/admin/runtimes/{Guid.NewGuid()}/hooks",
            new { hooks = ValidHooksDoc() });
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task NonSuperAdmin_Returns403()
    {
        var (client, _) = await RegisterUserAsync();
        var response = await client.PutAsJsonAsync(
            $"/api/admin/runtimes/{Guid.NewGuid()}/hooks",
            new { hooks = ValidHooksDoc() });
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ----------------------------------------------------------------------
    // 404 — runtime missing / soft-deleted
    // ----------------------------------------------------------------------

    [Fact]
    public async Task Returns404_WhenRuntimeMissing()
    {
        var (client, _) = await RegisterSuperAdminAsync();
        var response = await client.PutAsJsonAsync(
            $"/api/admin/runtimes/{Guid.NewGuid()}/hooks",
            new { hooks = ValidHooksDoc() });
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // No persistence, no fan-out.
        _groupClient.Verify(
            c => c.UpdateConfig(It.IsAny<ConfigUpdatePayload>()),
            Times.Never);
    }

    [Fact]
    public async Task Returns404_WhenRuntimeSoftDeleted()
    {
        var (client, _) = await RegisterSuperAdminAsync();
        var runtime = await SeedRuntimeAsync(softDeleted: true);

        var response = await client.PutAsJsonAsync(
            $"/api/admin/runtimes/{runtime.Id}/hooks",
            new { hooks = ValidHooksDoc() });
        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "the global query filter on ProjectRuntime hides soft-deleted rows; admin must not edit them either");

        _groupClient.Verify(
            c => c.UpdateConfig(It.IsAny<ConfigUpdatePayload>()),
            Times.Never);
    }

    // ----------------------------------------------------------------------
    // 400 — top-level shape validation
    // ----------------------------------------------------------------------

    [Fact]
    public async Task Returns400_WhenHooksNotObject()
    {
        var (client, _) = await RegisterSuperAdminAsync();
        var runtime = await SeedRuntimeAsync();

        // hooks is an array, not an object — must reject.
        var response = await client.PutAsync(
            $"/api/admin/runtimes/{runtime.Id}/hooks",
            new StringContent("""{"hooks": []}""", Encoding.UTF8, "application/json"));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("hooks must be an object");
    }

    [Fact]
    public async Task Returns400_WhenRequiredKeyMissing()
    {
        var (client, _) = await RegisterSuperAdminAsync();
        var runtime = await SeedRuntimeAsync();

        // Missing beforeCommit.
        var json = """
            {
              "hooks": {
                "beforePrompt": [],
                "afterPrompt": [],
                "onFileChange": []
              }
            }
            """;
        var response = await client.PutAsync(
            $"/api/admin/runtimes/{runtime.Id}/hooks",
            new StringContent(json, Encoding.UTF8, "application/json"));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("missing required key: beforeCommit");
    }

    [Fact]
    public async Task Returns400_WhenRequiredKeyNotArray()
    {
        var (client, _) = await RegisterSuperAdminAsync();
        var runtime = await SeedRuntimeAsync();

        // beforePrompt is an object, not an array.
        var json = """
            {
              "hooks": {
                "beforePrompt": {},
                "afterPrompt": [],
                "onFileChange": [],
                "beforeCommit": []
              }
            }
            """;
        var response = await client.PutAsync(
            $"/api/admin/runtimes/{runtime.Id}/hooks",
            new StringContent(json, Encoding.UTF8, "application/json"));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("beforePrompt must be an array");
    }

    [Fact]
    public async Task Returns400_WhenUnknownTopLevelKey()
    {
        var (client, _) = await RegisterSuperAdminAsync();
        var runtime = await SeedRuntimeAsync();

        // "afterCommit" isn't in the closed set — typo of "beforeCommit".
        var json = """
            {
              "hooks": {
                "beforePrompt": [],
                "afterPrompt": [],
                "onFileChange": [],
                "beforeCommit": [],
                "afterCommit": []
              }
            }
            """;
        var response = await client.PutAsync(
            $"/api/admin/runtimes/{runtime.Id}/hooks",
            new StringContent(json, Encoding.UTF8, "application/json"));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("unknown top-level key: afterCommit");
    }

    // ----------------------------------------------------------------------
    // Upsert + push happy path
    // ----------------------------------------------------------------------

    [Fact]
    public async Task FirstWrite_InsertsRow_AndPushesUpdateConfig()
    {
        var (client, _) = await RegisterSuperAdminAsync();
        var runtime = await SeedRuntimeAsync();

        var hooks = new
        {
            beforePrompt = new[] { new { name = "lint", cmd = "npm run lint" } },
            afterPrompt = Array.Empty<object>(),
            onFileChange = Array.Empty<object>(),
            beforeCommit = Array.Empty<object>(),
        };

        var response = await client.PutAsJsonAsync(
            $"/api/admin/runtimes/{runtime.Id}/hooks",
            new { hooks });
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            await response.Content.ReadAsStringAsync());

        var payload = await response.Content.ReadFromJsonAsync<UpdateHookConfigResponse>();
        payload!.RuntimeId.Should().Be(runtime.Id);
        payload.Json.Should().Contain("\"name\":\"lint\"");

        // Exactly one row.
        using (var scope = CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var rows = await db.RuntimeHookConfigs.Where(c => c.RuntimeId == runtime.Id).ToListAsync();
            rows.Should().HaveCount(1);
            rows[0].Json.Should().Contain("\"name\":\"lint\"");
        }

        // Push to runtime-{id} group with the persisted JSON.
        _hubClients.Verify(c => c.Group($"runtime-{runtime.Id}"), Times.AtLeastOnce);
        _groupClient.Verify(
            c => c.UpdateConfig(It.Is<ConfigUpdatePayload>(p =>
                p.RuntimeId == runtime.Id
                && p.RuntimeToken == null
                && p.HooksJson != null
                && p.HooksJson.Contains("\"name\":\"lint\""))),
            Times.Once,
            "the controller must fan out the new config to the live daemon group");
    }

    [Fact]
    public async Task SecondWrite_UpdatesExistingRow_AndPushesAgain()
    {
        var (client, _) = await RegisterSuperAdminAsync();
        var runtime = await SeedRuntimeAsync();

        // First write.
        var first = new
        {
            beforePrompt = new[] { new { name = "v1", cmd = "echo v1" } },
            afterPrompt = Array.Empty<object>(),
            onFileChange = Array.Empty<object>(),
            beforeCommit = Array.Empty<object>(),
        };
        (await client.PutAsJsonAsync(
            $"/api/admin/runtimes/{runtime.Id}/hooks", new { hooks = first }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        // Second write — different content.
        var second = new
        {
            beforePrompt = Array.Empty<object>(),
            afterPrompt = new[] { new { name = "v2", cmd = "echo v2" } },
            onFileChange = Array.Empty<object>(),
            beforeCommit = Array.Empty<object>(),
        };
        var response = await client.PutAsJsonAsync(
            $"/api/admin/runtimes/{runtime.Id}/hooks", new { hooks = second });
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // Still exactly one row, content updated.
        using (var scope = CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var rows = await db.RuntimeHookConfigs.Where(c => c.RuntimeId == runtime.Id).ToListAsync();
            rows.Should().HaveCount(1, "second write must be an upsert, not an insert");
            rows[0].Json.Should().Contain("\"name\":\"v2\"");
            rows[0].Json.Should().NotContain("\"name\":\"v1\"");
        }

        // Two pushes total.
        _groupClient.Verify(
            c => c.UpdateConfig(It.IsAny<ConfigUpdatePayload>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task EmptyArrays_AreAccepted()
    {
        // The "no hooks configured" baseline must be a valid write — an
        // operator should be able to clear the config without DELETE.
        var (client, _) = await RegisterSuperAdminAsync();
        var runtime = await SeedRuntimeAsync();

        var response = await client.PutAsJsonAsync(
            $"/api/admin/runtimes/{runtime.Id}/hooks",
            new { hooks = ValidHooksDoc() });
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        _groupClient.Verify(
            c => c.UpdateConfig(It.Is<ConfigUpdatePayload>(p => p.RuntimeId == runtime.Id)),
            Times.Once);
    }

    // ----------------------------------------------------------------------
    // helpers
    // ----------------------------------------------------------------------

    private static object ValidHooksDoc() => new
    {
        beforePrompt = Array.Empty<object>(),
        afterPrompt = Array.Empty<object>(),
        onFileChange = Array.Empty<object>(),
        beforeCommit = Array.Empty<object>(),
    };

    private async Task<ProjectRuntime> SeedRuntimeAsync(bool softDeleted = false)
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var runtime = new ProjectRuntime
        {
            Id = Guid.NewGuid(),
            ProjectId = Guid.NewGuid(),
            Region = "arn",
            VolumeSizeGb = 1,
            State = RuntimeState.Online,
            IsDeleted = softDeleted,
            DeletedAt = softDeleted ? DateTime.UtcNow : null,
        };
        db.ProjectRuntimes.Add(runtime);
        await db.SaveChangesAsync();
        return runtime;
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

    private async Task<(HttpClient Client, string UserId)> RegisterSuperAdminAsync()
    {
        await SeedRolesAsync();

        var email = $"admin-{Guid.NewGuid():N}@example.com";
        var registerClient = Factory.CreateClient();
        var response = await registerClient.PostAsJsonAsync(
            "/api/auth/register", new { email, password = Password });
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            await response.Content.ReadAsStringAsync());

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
        var loginResponse = await loginClient.PostAsJsonAsync(
            "/api/auth/login", new { email, password = Password });
        loginResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            await loginResponse.Content.ReadAsStringAsync());

        var cookies = loginResponse.Headers.GetValues("Set-Cookie");
        var authCookie = cookies.First(c => c.StartsWith("auth-token=", StringComparison.Ordinal));
        var cookieValue = authCookie.Split(';')[0];

        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", cookieValue);
        return (client, userId);
    }
}
