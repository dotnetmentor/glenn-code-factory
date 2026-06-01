using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Source.Features.RuntimeImages.Models;
using Source.Features.SystemSettings.Services;
using Source.Features.Users.Models;
using Source.Infrastructure;
using Source.Infrastructure.AuthorizationModels;

namespace Api.Tests.Features.RuntimeImages;

/// <summary>
/// End-to-end HTTP tests for the admin surface at <c>/api/admin/runtime-images</c>. Auth
/// is real: SuperAdmin tests register a user via <c>/api/auth/register</c> and add the
/// <see cref="RoleConstants.SuperAdmin"/> role through <see cref="UserManager{T}"/>, then
/// re-issue a login so the JWT cookie carries the role claim.
///
/// <para>The previous <c>X-Publisher-Token</c> CI bypass path was retired: registration is
/// now SuperAdmin-JWT-only, mirroring every other admin endpoint. The corresponding
/// <c>Register_WithValidPublisherToken_*</c> / <c>Register_WithWrongPublisherToken_*</c>
/// tests were removed along with the auth path.</para>
///
/// <para>Mirrors <see cref="Api.Tests.Features.FlyManagement.FlyAdminControllerTests"/>.</para>
/// </summary>
[Collection(HangfireTestCollection.Name)]
public class RuntimeImagesControllerTests : IntegrationTestBase
{
    private const string Password = "Password123!";

    /// <summary>
    /// Mirrors the API's controller JSON config (<c>AddJsonOptions</c> in Program.cs) so the
    /// test client deserialises <see cref="RuntimeImageStatus"/> from the string form the
    /// server emits ("Active") instead of the default integer form.
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private static RegisterRuntimeImageRequest BuildValidRequest(string? tag = null) => new(
        Tag: tag ?? $"2026.05.08-{Guid.NewGuid():N}",
        Digest: "sha256:abcdef0123456789",
        Registry: "registry.fly.io/fwd-runtime",
        GitSha: "7af3b21",
        BuiltAt: DateTime.UtcNow,
        SizeMb: 248,
        Notes: null);

    // ----------------------------------------------------------------------
    // POST /api/admin/runtime-images — SuperAdmin-only
    // ----------------------------------------------------------------------

    [Fact]
    public async Task Register_NoAuth_Returns401()
    {
        // No JWT — must reject with 401. The publisher-token bypass was retired.
        var response = await Client.PostAsJsonAsync("/api/admin/runtime-images", BuildValidRequest());
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Register_AsSuperAdmin_Returns201()
    {
        var (client, _) = await RegisterSuperAdminAsync();

        var req = BuildValidRequest("2026.05.08-superadmin");
        var response = await client.PostAsJsonAsync("/api/admin/runtime-images", req);
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());

        var image = await response.Content.ReadFromJsonAsync<RuntimeImage>(JsonOptions);
        image!.Tag.Should().Be("2026.05.08-superadmin");
        image.Status.Should().Be(RuntimeImageStatus.Active);

        // Location header must point at the GET-by-id route.
        response.Headers.Location.Should().NotBeNull();
        response.Headers.Location!.ToString().Should().Contain($"/api/admin/runtime-images/{image.Id}");
    }

    [Fact]
    public async Task Register_AsPlainUser_Returns403()
    {
        // Authenticated but no SuperAdmin role — must be 403, not 401.
        var (client, _) = await RegisterUserAsync();

        var response = await client.PostAsJsonAsync("/api/admin/runtime-images", BuildValidRequest());
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Register_DuplicateTag_Returns409()
    {
        var (client, _) = await RegisterSuperAdminAsync();

        var req = BuildValidRequest("2026.05.08-dup");
        var first = await client.PostAsJsonAsync("/api/admin/runtime-images", req);
        first.StatusCode.Should().Be(HttpStatusCode.Created);

        // Second push with the same tag → conflict (the natural idempotency key).
        var second = await client.PostAsJsonAsync("/api/admin/runtime-images", req);
        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    // ----------------------------------------------------------------------
    // GET /api/admin/runtime-images — list with paging + filtering
    // ----------------------------------------------------------------------

    [Fact]
    public async Task List_AsSuperAdmin_ReturnsPaginatedResults()
    {
        var (client, _) = await RegisterSuperAdminAsync();

        // 30 rows. Page 2 (size 10) should hold 10 rows, total = 30.
        await SeedImagesAsync(Enumerable.Range(0, 30)
            .Select(i => (RuntimeImageStatus.Active, $"tag-{i}", DateTime.UtcNow.AddMinutes(-i)))
            .ToArray());

        var response = await client.GetAsync("/api/admin/runtime-images?page=2&pageSize=10");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var page = await response.Content.ReadFromJsonAsync<RuntimeImagesResponse>(JsonOptions);
        page!.Total.Should().Be(30);
        page.Page.Should().Be(2);
        page.PageSize.Should().Be(10);
        page.Items.Should().HaveCount(10);
    }

    [Fact]
    public async Task List_FiltersByStatus()
    {
        var (client, _) = await RegisterSuperAdminAsync();

        await SeedImagesAsync(
            (RuntimeImageStatus.Active, "active-1", DateTime.UtcNow),
            (RuntimeImageStatus.Active, "active-2", DateTime.UtcNow.AddMinutes(-1)),
            (RuntimeImageStatus.Deprecated, "deprecated-1", DateTime.UtcNow.AddMinutes(-2)),
            (RuntimeImageStatus.Yanked, "yanked-1", DateTime.UtcNow.AddMinutes(-3)));

        var response = await client.GetAsync("/api/admin/runtime-images?status=Active");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var page = await response.Content.ReadFromJsonAsync<RuntimeImagesResponse>(JsonOptions);
        page!.Total.Should().Be(2);
        page.Items.Should().OnlyContain(i => i.Status == RuntimeImageStatus.Active);
    }

    [Fact]
    public async Task List_PageSizeCappedAt200()
    {
        var (client, _) = await RegisterSuperAdminAsync();

        // 250 rows. Even with pageSize=1000 the response must hold at most 200.
        var seeds = Enumerable.Range(0, 250)
            .Select(i => (RuntimeImageStatus.Active, $"tag-{i}", DateTime.UtcNow.AddSeconds(-i)))
            .ToArray();
        await SeedImagesAsync(seeds);

        var response = await client.GetAsync("/api/admin/runtime-images?pageSize=1000");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var page = await response.Content.ReadFromJsonAsync<RuntimeImagesResponse>(JsonOptions);
        page!.Total.Should().Be(250);
        page.PageSize.Should().Be(200, "pageSize is hard-capped at 200");
        page.Items.Should().HaveCount(200);
    }

    // ----------------------------------------------------------------------
    // GET /api/admin/runtime-images/{id}
    // ----------------------------------------------------------------------

    [Fact]
    public async Task GetById_AsSuperAdmin_ReturnsImage()
    {
        var (client, _) = await RegisterSuperAdminAsync();

        var seeded = await SeedSingleAsync(RuntimeImageStatus.Active, "fetch-me", DateTime.UtcNow);

        var response = await client.GetAsync($"/api/admin/runtime-images/{seeded.Id}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var image = await response.Content.ReadFromJsonAsync<RuntimeImage>(JsonOptions);
        image!.Id.Should().Be(seeded.Id);
        image.Tag.Should().Be("fetch-me");
    }

    [Fact]
    public async Task GetById_NotFound_Returns404()
    {
        var (client, _) = await RegisterSuperAdminAsync();
        var response = await client.GetAsync($"/api/admin/runtime-images/{Guid.NewGuid()}");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ----------------------------------------------------------------------
    // GET /api/admin/runtime-images/latest-active
    // ----------------------------------------------------------------------

    [Fact]
    public async Task LatestActive_NoActiveImages_Returns404()
    {
        var (client, _) = await RegisterSuperAdminAsync();

        // Only non-Active rows.
        await SeedImagesAsync(
            (RuntimeImageStatus.Deprecated, "dep-1", DateTime.UtcNow),
            (RuntimeImageStatus.Yanked, "yank-1", DateTime.UtcNow.AddMinutes(-1)));

        var response = await client.GetAsync("/api/admin/runtime-images/latest-active");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task LatestActive_WithMultipleActive_ReturnsNewestByBuiltAt()
    {
        var (client, _) = await RegisterSuperAdminAsync();

        var newest = DateTime.UtcNow;
        await SeedImagesAsync(
            (RuntimeImageStatus.Active, "older", newest.AddHours(-2)),
            (RuntimeImageStatus.Active, "newest-active", newest),
            (RuntimeImageStatus.Active, "middle", newest.AddHours(-1)),
            // Deprecated row built AFTER the newest active — must NOT be returned.
            (RuntimeImageStatus.Deprecated, "deprecated-but-newer", newest.AddHours(1)));

        var response = await client.GetAsync("/api/admin/runtime-images/latest-active");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var image = await response.Content.ReadFromJsonAsync<RuntimeImage>(JsonOptions);
        image!.Tag.Should().Be("newest-active");
        image.Status.Should().Be(RuntimeImageStatus.Active);
    }

    [Fact]
    public async Task LatestActive_AnyAuthenticatedUser_Allowed()
    {
        // A non-SuperAdmin authenticated user must still be able to look up the
        // default spawn target — backend services call this endpoint.
        var (client, _) = await RegisterUserAsync();

        await SeedImagesAsync((RuntimeImageStatus.Active, "default-spawn", DateTime.UtcNow));

        var response = await client.GetAsync("/api/admin/runtime-images/latest-active");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var image = await response.Content.ReadFromJsonAsync<RuntimeImage>(JsonOptions);
        image!.Tag.Should().Be("default-spawn");
    }

    // ----------------------------------------------------------------------
    // POST /api/admin/runtime-images/{id}/deprecate
    // ----------------------------------------------------------------------

    [Fact]
    public async Task Deprecate_AsSuperAdmin_UpdatesStatus()
    {
        var (client, _) = await RegisterSuperAdminAsync();

        var seeded = await SeedSingleAsync(RuntimeImageStatus.Active, "to-deprecate", DateTime.UtcNow);

        var response = await client.PostAsync($"/api/admin/runtime-images/{seeded.Id}/deprecate", content: null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var image = await response.Content.ReadFromJsonAsync<RuntimeImage>(JsonOptions);
        image!.Status.Should().Be(RuntimeImageStatus.Deprecated);

        // Confirm persistence — re-read through a fresh scope.
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var stored = await db.RuntimeImages.FindAsync(seeded.Id);
        stored!.Status.Should().Be(RuntimeImageStatus.Deprecated);
    }

    // ----------------------------------------------------------------------
    // POST /api/admin/runtime-images/{id}/yank
    // ----------------------------------------------------------------------

    [Fact]
    public async Task Yank_AsSuperAdmin_UpdatesStatus()
    {
        var (client, _) = await RegisterSuperAdminAsync();

        var seeded = await SeedSingleAsync(RuntimeImageStatus.Active, "to-yank", DateTime.UtcNow);

        var response = await client.PostAsync($"/api/admin/runtime-images/{seeded.Id}/yank", content: null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var image = await response.Content.ReadFromJsonAsync<RuntimeImage>(JsonOptions);
        image!.Status.Should().Be(RuntimeImageStatus.Yanked);
    }

    [Fact]
    public async Task Yank_NotFound_Returns404()
    {
        var (client, _) = await RegisterSuperAdminAsync();
        var response = await client.PostAsync($"/api/admin/runtime-images/{Guid.NewGuid()}/yank", content: null);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ----------------------------------------------------------------------
    // PATCH /api/admin/runtime-images/{id}/status — activation flow
    // ----------------------------------------------------------------------

    [Fact]
    public async Task UpdateStatus_PromoteToActive_DemotesPreviousActiveRows()
    {
        var (client, _) = await RegisterSuperAdminAsync();

        // Seed two existing Active rows + one Deprecated row we'll promote.
        var older = await SeedSingleAsync(RuntimeImageStatus.Active, "old-1", DateTime.UtcNow.AddHours(-2));
        var newer = await SeedSingleAsync(RuntimeImageStatus.Active, "old-2", DateTime.UtcNow.AddHours(-1));
        var target = await SeedSingleAsync(RuntimeImageStatus.Deprecated, "to-promote", DateTime.UtcNow);

        var response = await client.PatchAsJsonAsync(
            $"/api/admin/runtime-images/{target.Id}/status",
            new { status = "Active" });

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        var image = await response.Content.ReadFromJsonAsync<RuntimeImage>(JsonOptions);
        image!.Status.Should().Be(RuntimeImageStatus.Active);

        // Confirm single-Active invariant landed in the DB.
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var rows = await db.RuntimeImages.AsNoTracking().ToListAsync();
        rows.Count(i => i.Status == RuntimeImageStatus.Active).Should().Be(1);
        rows.Single(i => i.Id == target.Id).Status.Should().Be(RuntimeImageStatus.Active);
        rows.Single(i => i.Id == older.Id).Status.Should().Be(RuntimeImageStatus.Deprecated);
        rows.Single(i => i.Id == newer.Id).Status.Should().Be(RuntimeImageStatus.Deprecated);
    }

    [Fact]
    public async Task UpdateStatus_NotFound_Returns404()
    {
        var (client, _) = await RegisterSuperAdminAsync();

        var response = await client.PatchAsJsonAsync(
            $"/api/admin/runtime-images/{Guid.NewGuid()}/status",
            new { status = "Deprecated" });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task UpdateStatus_AsPlainUser_Returns403()
    {
        var (client, _) = await RegisterUserAsync();
        var seeded = await SeedSingleAsync(RuntimeImageStatus.Deprecated, "deprecated", DateTime.UtcNow);

        var response = await client.PatchAsJsonAsync(
            $"/api/admin/runtime-images/{seeded.Id}/status",
            new { status = "Active" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ----------------------------------------------------------------------
    // GET /api/admin/runtime-images/registry-tags — auth gating only
    // ----------------------------------------------------------------------
    // The Fly registry HTTP wiring lives in IFlyRegistryClient and is covered by
    // FlyRegistryClientTests; here we only assert the endpoint's auth gate so the
    // "human picks a tag" flow can't be reached without a SuperAdmin JWT.

    [Fact]
    public async Task RegistryTags_NoAuth_Returns401()
    {
        var response = await Client.GetAsync("/api/admin/runtime-images/registry-tags");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task RegistryTags_AsPlainUser_Returns403()
    {
        var (client, _) = await RegisterUserAsync();
        var response = await client.GetAsync("/api/admin/runtime-images/registry-tags");
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ----------------------------------------------------------------------
    // helpers
    // ----------------------------------------------------------------------

    private async Task SeedImagesAsync(params (RuntimeImageStatus Status, string Tag, DateTime BuiltAt)[] rows)
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        foreach (var (status, tag, builtAt) in rows)
        {
            db.RuntimeImages.Add(new RuntimeImage
            {
                Id = Guid.NewGuid(),
                Tag = tag,
                Digest = $"sha256:{tag}",
                Registry = "registry.fly.io/fwd-runtime",
                GitSha = "abcdef",
                BuiltAt = builtAt,
                SizeMb = 100,
                Status = status,
            });
        }
        await db.SaveChangesAsync();
    }

    private async Task<RuntimeImage> SeedSingleAsync(RuntimeImageStatus status, string tag, DateTime builtAt)
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var image = new RuntimeImage
        {
            Id = Guid.NewGuid(),
            Tag = tag,
            Digest = $"sha256:{tag}",
            Registry = "registry.fly.io/fwd-runtime",
            GitSha = "abcdef",
            BuiltAt = builtAt,
            SizeMb = 100,
            Status = status,
        };
        db.RuntimeImages.Add(image);
        await db.SaveChangesAsync();
        return image;
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
