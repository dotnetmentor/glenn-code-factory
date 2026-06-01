using System.Net;
using System.Net.Http.Json;
using Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Source.Features.GitOps.Controllers;
using Source.Features.GitOps.Models;
using Source.Features.RuntimeLifecycle.Models;
using Source.Features.SignalR.Contracts;
using Source.Features.SignalR.Hubs;
using Source.Features.Users.Models;
using Source.Infrastructure;
using Source.Infrastructure.AuthorizationModels;

namespace Api.Tests.Features.GitOps;

/// <summary>
/// End-to-end HTTP tests for the admin write surface at
/// <c>PUT /api/admin/runtimes/{id}/git/auto-commit</c> +
/// <c>/git/deploy-key</c>. Mirrors
/// <see cref="Api.Tests.Features.Hooks.HookConfigAdminControllerTests"/>
/// for auth + DB seeding + the SignalR mock chain.
///
/// <para>The real <see cref="IHubContext{THub, T}"/> is swapped out via
/// <see cref="IntegrationTestBase.WithServiceFactory"/> so we can assert on the
/// exact <see cref="ConfigUpdatePayload"/> the controller pushes.</para>
///
/// <para>Skipped: 404 — both endpoints upsert, so the only "missing"
/// case is a missing <see cref="ProjectRuntime"/>; that case is covered by
/// the <c>Returns404_WhenRuntimeMissing</c> tests below. There is no
/// "missing config row" 404 because writes are always upserts.</para>
/// </summary>
[Collection(HangfireTestCollection.Name)]
public class GitConfigAdminControllerTests : IntegrationTestBase
{
    private const string Password = "Password123!";

    // A realistic-looking ED25519 private key. The body is gibberish — the
    // server only sniffs the header line; full SSH validation is the daemon's
    // job at first push. Multi-line so the redactor's Singleline regex is
    // exercised in production-shaped input.
    private const string SampleEd25519Key = """
        -----BEGIN OPENSSH PRIVATE KEY-----
        b3BlbnNzaC1rZXktdjEAAAAABG5vbmUAAAAEbm9uZQAAAAAAAAABAAAAMwAAAAtzc2gtZW
        QyNTUxOQAAACAabcdefGHIJKLmnopQRSTuvwxYZ0123456789AAAAAAAAAAA==
        -----END OPENSSH PRIVATE KEY-----
        """;

    private readonly Mock<IHubContext<RuntimeHub, IRuntimeClient>> _runtimeHub = new();
    private readonly Mock<IHubClients<IRuntimeClient>> _hubClients = new();
    private readonly Mock<IRuntimeClient> _groupClient = new();

    public GitConfigAdminControllerTests()
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
    // Auth gating — auto-commit endpoint
    // ----------------------------------------------------------------------

    [Fact]
    public async Task SetAutoCommit_Unauthenticated_Returns401()
    {
        var response = await Client.PutAsJsonAsync(
            $"/api/admin/runtimes/{Guid.NewGuid()}/git/auto-commit",
            new { enabled = false });
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task SetAutoCommit_NonSuperAdmin_Returns403()
    {
        var (client, _) = await RegisterUserAsync();
        var response = await client.PutAsJsonAsync(
            $"/api/admin/runtimes/{Guid.NewGuid()}/git/auto-commit",
            new { enabled = false });
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ----------------------------------------------------------------------
    // Auth gating — deploy-key endpoint
    // ----------------------------------------------------------------------

    [Fact]
    public async Task SetDeployKey_Unauthenticated_Returns401()
    {
        var response = await Client.PutAsJsonAsync(
            $"/api/admin/runtimes/{Guid.NewGuid()}/git/deploy-key",
            new { privateKey = SampleEd25519Key, hostKey = (string?)null });
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task SetDeployKey_NonSuperAdmin_Returns403()
    {
        var (client, _) = await RegisterUserAsync();
        var response = await client.PutAsJsonAsync(
            $"/api/admin/runtimes/{Guid.NewGuid()}/git/deploy-key",
            new { privateKey = SampleEd25519Key, hostKey = (string?)null });
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ----------------------------------------------------------------------
    // 400 — deploy-key validation
    // ----------------------------------------------------------------------

    [Fact]
    public async Task SetDeployKey_EmptyPrivateKey_Returns400()
    {
        var (client, _) = await RegisterSuperAdminAsync();
        var runtime = await SeedRuntimeAsync();

        var response = await client.PutAsJsonAsync(
            $"/api/admin/runtimes/{runtime.Id}/git/deploy-key",
            new { privateKey = "", hostKey = (string?)null });
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("privateKey is required");

        _groupClient.Verify(
            c => c.UpdateConfig(It.IsAny<ConfigUpdatePayload>()),
            Times.Never);
    }

    [Fact]
    public async Task SetDeployKey_GarbagePrivateKey_Returns400()
    {
        var (client, _) = await RegisterSuperAdminAsync();
        var runtime = await SeedRuntimeAsync();

        var response = await client.PutAsJsonAsync(
            $"/api/admin/runtimes/{runtime.Id}/git/deploy-key",
            new { privateKey = "not a real key", hostKey = (string?)null });
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("does not look like an OpenSSH/RSA/DSA/EC/ED25519 private key");

        // Nothing persisted, nothing pushed.
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db.RuntimeGitConfigs.AnyAsync(c => c.RuntimeId == runtime.Id)).Should().BeFalse();

        _groupClient.Verify(
            c => c.UpdateConfig(It.IsAny<ConfigUpdatePayload>()),
            Times.Never);
    }

    // ----------------------------------------------------------------------
    // 404 — runtime missing / soft-deleted
    // ----------------------------------------------------------------------

    [Fact]
    public async Task SetAutoCommit_RuntimeMissing_Returns404()
    {
        var (client, _) = await RegisterSuperAdminAsync();

        var response = await client.PutAsJsonAsync(
            $"/api/admin/runtimes/{Guid.NewGuid()}/git/auto-commit",
            new { enabled = false });
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        _groupClient.Verify(
            c => c.UpdateConfig(It.IsAny<ConfigUpdatePayload>()),
            Times.Never);
    }

    [Fact]
    public async Task SetDeployKey_RuntimeMissing_Returns404()
    {
        var (client, _) = await RegisterSuperAdminAsync();

        var response = await client.PutAsJsonAsync(
            $"/api/admin/runtimes/{Guid.NewGuid()}/git/deploy-key",
            new { privateKey = SampleEd25519Key, hostKey = (string?)null });
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        _groupClient.Verify(
            c => c.UpdateConfig(It.IsAny<ConfigUpdatePayload>()),
            Times.Never);
    }

    // ----------------------------------------------------------------------
    // Happy path — auto-commit
    // ----------------------------------------------------------------------

    [Fact]
    public async Task SetAutoCommit_FirstWrite_InsertsRow_AndPushesUpdateConfig()
    {
        var (client, _) = await RegisterSuperAdminAsync();
        var runtime = await SeedRuntimeAsync();

        var response = await client.PutAsJsonAsync(
            $"/api/admin/runtimes/{runtime.Id}/git/auto-commit",
            new { enabled = false });
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            await response.Content.ReadAsStringAsync());

        var payload = await response.Content.ReadFromJsonAsync<UpdatedGitConfigResponse>();
        payload!.RuntimeId.Should().Be(runtime.Id);

        using (var scope = CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var rows = await db.RuntimeGitConfigs.Where(c => c.RuntimeId == runtime.Id).ToListAsync();
            rows.Should().HaveCount(1);
            rows[0].AutoCommit.Should().BeFalse();
            rows[0].DeployKey.Should().BeNull();
        }

        _hubClients.Verify(c => c.Group($"runtime-{runtime.Id}"), Times.AtLeastOnce);
        _groupClient.Verify(
            c => c.UpdateConfig(It.Is<ConfigUpdatePayload>(p =>
                p.RuntimeId == runtime.Id
                && p.AutoCommit == false
                && p.DeployKey == null
                && p.HooksJson == null
                && p.RuntimeToken == null)),
            Times.Once,
            "the controller must fan out the new config to the live daemon group");
    }

    [Fact]
    public async Task SetAutoCommit_SecondWrite_UpdatesExistingRow()
    {
        var (client, _) = await RegisterSuperAdminAsync();
        var runtime = await SeedRuntimeAsync();

        // Write 1: disable
        (await client.PutAsJsonAsync(
            $"/api/admin/runtimes/{runtime.Id}/git/auto-commit", new { enabled = false }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        // Write 2: re-enable
        (await client.PutAsJsonAsync(
            $"/api/admin/runtimes/{runtime.Id}/git/auto-commit", new { enabled = true }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var rows = await db.RuntimeGitConfigs.Where(c => c.RuntimeId == runtime.Id).ToListAsync();
        rows.Should().HaveCount(1, "second write must be an upsert, not an insert");
        rows[0].AutoCommit.Should().BeTrue();

        _groupClient.Verify(
            c => c.UpdateConfig(It.IsAny<ConfigUpdatePayload>()),
            Times.Exactly(2));
    }

    // ----------------------------------------------------------------------
    // Happy path — deploy-key
    // ----------------------------------------------------------------------

    [Fact]
    public async Task SetDeployKey_FirstWrite_InsertsRow_AndPushesUpdateConfig()
    {
        var (client, _) = await RegisterSuperAdminAsync();
        var runtime = await SeedRuntimeAsync();

        var response = await client.PutAsJsonAsync(
            $"/api/admin/runtimes/{runtime.Id}/git/deploy-key",
            new { privateKey = SampleEd25519Key, hostKey = "github.com ssh-ed25519 AAAA..." });
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            await response.Content.ReadAsStringAsync());

        var payload = await response.Content.ReadFromJsonAsync<UpdatedGitConfigResponse>();
        payload!.RuntimeId.Should().Be(runtime.Id);

        // Crucial: the response body must NOT contain the key bytes.
        var raw = await response.Content.ReadAsStringAsync();
        raw.Should().NotContain("BEGIN OPENSSH",
            "deploy key is write-only — the response body must not echo it back");

        using (var scope = CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var rows = await db.RuntimeGitConfigs.Where(c => c.RuntimeId == runtime.Id).ToListAsync();
            rows.Should().HaveCount(1);
            rows[0].DeployKey.Should().Be(SampleEd25519Key);
            rows[0].DeployKeyHostKey.Should().Be("github.com ssh-ed25519 AAAA...");
            rows[0].AutoCommit.Should().BeTrue("default-on baseline preserved on a deploy-key-only write");
        }

        _groupClient.Verify(
            c => c.UpdateConfig(It.Is<ConfigUpdatePayload>(p =>
                p.RuntimeId == runtime.Id
                && p.DeployKey == SampleEd25519Key
                && p.AutoCommit == null)),
            Times.Once);
    }

    [Fact]
    public async Task SetDeployKey_AllValidPemHeaders_AreAccepted()
    {
        // The closed set we accept on the regex: OPENSSH | RSA | DSA | EC | ED25519.
        var (client, _) = await RegisterSuperAdminAsync();
        var headers = new[] { "OPENSSH", "RSA", "DSA", "EC", "ED25519" };

        foreach (var header in headers)
        {
            var runtime = await SeedRuntimeAsync();
            var key = $"-----BEGIN {header} PRIVATE KEY-----\nbody\n-----END {header} PRIVATE KEY-----";

            var response = await client.PutAsJsonAsync(
                $"/api/admin/runtimes/{runtime.Id}/git/deploy-key",
                new { privateKey = key, hostKey = (string?)null });
            response.StatusCode.Should().Be(HttpStatusCode.OK,
                $"the regex must accept {header} as a valid private-key header. Body: {await response.Content.ReadAsStringAsync()}");
        }
    }

    // ----------------------------------------------------------------------
    // helpers
    // ----------------------------------------------------------------------

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
