using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Source.Features.CiPublish;
using Source.Features.CiPublish.Models;
using Source.Features.DaemonVersions.Models;
using Source.Features.RuntimeImages.Models;
using Source.Features.SystemSettings.Services;
using Source.Features.Users.Models;
using Source.Infrastructure;
using Source.Infrastructure.AuthorizationModels;

namespace Api.Tests.Features.CiPublish;

[Collection(HangfireTestCollection.Name)]
public class CiPublishControllerTests : IntegrationTestBase
{
    private const string Password = "Password123!";
    private const string ValidKey = "ci-publish-integration-test-key-32chars!!";

    private HttpClient CiClient()
    {
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Add(CiPublishAuthenticationDefaults.HeaderName, ValidKey);
        return client;
    }

    [Fact]
    public async Task PublishStatus_WithoutKey_Returns401()
    {
        var response = await Client.GetAsync("/api/ci/publish-status");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task PublishStatus_WithInvalidKey_Returns401()
    {
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Add(CiPublishAuthenticationDefaults.HeaderName, "wrong-key");
        var response = await client.GetAsync("/api/ci/publish-status");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task PublishStatus_BearerJwt_IsNotAcceptedForCiEndpoints()
    {
        var (client, _) = await RegisterSuperAdminAsync();
        var response = await client.GetAsync("/api/ci/publish-status");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task PublishStatus_WithValidKey_ReturnsGitShaFromColumns()
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        db.DaemonVersions.Add(new DaemonVersion
        {
            Id = Guid.NewGuid(),
            Channel = "stable",
            Version = "2026.06.02.120000",
            IsActive = true,
            GitSha = "abc123fullsha",
            Notes = "human notes only",
            BundleSha256 = new string('a', 64),
            BundleSizeBytes = 1,
            BundleStorageKey = "test/key",
            ReleasedAt = DateTime.UtcNow,
        });
        db.RuntimeImages.Add(new RuntimeImage
        {
            Id = Guid.NewGuid(),
            Tag = "2026.06.02-deadbee",
            Digest = "sha256:abc",
            Registry = "registry.fly.io/glenn-runtime-base",
            GitSha = "abc123fullsha",
            BuiltAt = DateTime.UtcNow,
            SizeMb = 100,
            Status = RuntimeImageStatus.Active,
        });
        await db.SaveChangesAsync();

        var response = await CiClient().GetAsync("/api/ci/publish-status?gitSha=abc123fullsha");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<CiPublishStatusDto>();
        body!.DaemonPublishedForRequestedSha.Should().BeTrue();
        body.RuntimePublishedForRequestedSha.Should().BeTrue();
        body.DaemonStableGitSha.Should().Be("abc123fullsha");
        body.RuntimeActiveGitSha.Should().Be("abc123fullsha");
    }

    [Fact]
    public async Task PublishStatus_WhenInactiveRowHasGitSha_StillReportsPublished()
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        db.DaemonVersions.Add(new DaemonVersion
        {
            Id = Guid.NewGuid(),
            Channel = "stable",
            Version = "2026.06.01.120000",
            IsActive = false,
            GitSha = "abc123fullsha",
            Notes = "superseded",
            BundleSha256 = new string('c', 64),
            BundleSizeBytes = 1,
            BundleStorageKey = "test/key-old",
            ReleasedAt = DateTime.UtcNow.AddDays(-1),
        });
        db.DaemonVersions.Add(new DaemonVersion
        {
            Id = Guid.NewGuid(),
            Channel = "stable",
            Version = "2026.06.02.120002",
            IsActive = true,
            GitSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            Notes = "current active",
            BundleSha256 = new string('d', 64),
            BundleSizeBytes = 1,
            BundleStorageKey = "test/key-new",
            ReleasedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var response = await CiClient().GetAsync(
            "/api/ci/publish-status?gitSha=abc123fullsha");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<CiPublishStatusDto>();
        body!.DaemonPublishedForRequestedSha.Should().BeTrue();
        body.DaemonStableGitSha.Should().Be("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
    }

    [Fact]
    public async Task PublishStatus_WhenDaemonHasNoGitSha_ReturnsNullDaemonSha()
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        db.DaemonVersions.Add(new DaemonVersion
        {
            Id = Guid.NewGuid(),
            Channel = "stable",
            Version = "2026.06.02.120001",
            IsActive = true,
            GitSha = null,
            Notes = "legacy row",
            BundleSha256 = new string('b', 64),
            BundleSizeBytes = 1,
            BundleStorageKey = "test/key2",
            ReleasedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var response = await CiClient().GetAsync("/api/ci/publish-status?gitSha=abc123fullsha");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<CiPublishStatusDto>();
        body!.DaemonStableGitSha.Should().BeNull();
        body.DaemonPublishedForRequestedSha.Should().BeFalse();
    }

    [Fact]
    public async Task RegistryCredentials_WithValidKey_ReturnsFlyLoginMaterial()
    {
        await SeedFlySettingsAsync();

        var response = await CiClient().GetAsync("/api/ci/registry-credentials");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<CiRegistryCredentialsDto>();
        body!.RegistryHost.Should().Be("registry.fly.io");
        body.Username.Should().Be("x");
        body.Password.Should().Be("fly_pat_secret_xyz");
    }

    [Fact]
    public async Task ListDaemonVersions_WithCiKey_Returns401()
    {
        var response = await CiClient().GetAsync("/api/daemon-versions");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task RegisterRuntimeImage_WithCiKey_Returns201()
    {
        var req = new
        {
            tag = $"2026.06.02-{Guid.NewGuid():N}",
            digest = "sha256:ci-test",
            registry = "registry.fly.io/glenn-runtime-base",
            gitSha = "ci-sha",
            builtAt = DateTime.UtcNow,
            sizeMb = 10,
            notes = "ci test",
        };

        var response = await CiClient().PostAsJsonAsync("/api/admin/runtime-images", req);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task PublishDaemon_WithCiKey_Returns200AndStoresGitSha()
    {
        var bundlePath = Path.Combine(Path.GetTempPath(), $"daemon-ci-test-{Guid.NewGuid():N}.tar.gz");
        await File.WriteAllBytesAsync(bundlePath, [0x1f, 0x8b, 0x08, 0x00]);

        try
        {
            await using var stream = File.OpenRead(bundlePath);
            using var content = new MultipartFormDataContent
            {
                { new StreamContent(stream), "file", "daemon-bundle.tar.gz" },
                { new StringContent("stable"), "channel" },
                { new StringContent("ci publish test"), "notes" },
                { new StringContent("abc123deadbeef"), "gitSha" },
            };

            var request = new HttpRequestMessage(HttpMethod.Post, "/api/daemon-versions")
            {
                Content = content,
            };
            request.Headers.Add(CiPublishAuthenticationDefaults.HeaderName, ValidKey);

            var response = await Factory.CreateClient().SendAsync(request);
            response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

            using var scope = CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var row = await db.DaemonVersions
                .Where(v => v.Channel == "stable" && v.IsActive)
                .OrderByDescending(v => v.ReleasedAt)
                .FirstAsync();
            row.GitSha.Should().Be("abc123deadbeef");
        }
        finally
        {
            File.Delete(bundlePath);
        }
    }

    private async Task SeedFlySettingsAsync()
    {
        using var scope = CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISystemSettingsService>();
        await settings.SetAsync("Fly:ApiToken", "fly_pat_secret_xyz", isSecret: true);
        await settings.SetAsync("Fly:OrgSlug", "personal", isSecret: false);
        await settings.SetAsync("Fly:AppName", "test-app", isSecret: false);
        await settings.SetAsync("Fly:DefaultRegion", "arn", isSecret: false);
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

        var loginClient = Factory.CreateClient();
        var loginResponse = await loginClient.PostAsJsonAsync("/api/auth/login", new { email, password = Password });
        loginResponse.StatusCode.Should().Be(HttpStatusCode.OK, await loginResponse.Content.ReadAsStringAsync());

        var authCookie = loginResponse.Headers.GetValues("Set-Cookie").First(c => c.StartsWith("auth-token=", StringComparison.Ordinal));
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", authCookie.Split(';')[0]);
        return (client, userId);
    }
}
