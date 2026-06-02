using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Web;
using Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Source.Features.GitHub;
using Source.Features.GitHub.Commands;
using Source.Features.GitHub.Configuration;
using Source.Features.GitHub.Models;
using Source.Features.GitHub.Queries;
using Source.Features.GitHub.Services;
using Source.Features.GitHub.Services.Dtos;
using Source.Features.Users.Models;
using Source.Features.Workspaces.Models;
using Source.Infrastructure;

namespace Api.Tests.Features.GitHub;

/// <summary>
/// End-to-end tests for the workspace-scoped GitHub install endpoints + the public install
/// callback. <see cref="IGithubApiClient"/> and <see cref="IGithubAppTokenService"/> are mocked
/// so the tests stay offline.
/// </summary>
[Collection(HangfireTestCollection.Name)]
public class GithubInstallEndpointsTests : IntegrationTestBase
{
    private const string Password = "Password123!";
    private const string AppSlug = "my-test-app";

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly Mock<IGithubApiClient> _ghApi = new(MockBehavior.Loose);
    private readonly Mock<IGithubAppTokenService> _ghTokens = new(MockBehavior.Loose);

    public GithubInstallEndpointsTests()
    {
        var apiMock = _ghApi;
        var tokensMock = _ghTokens;
        tokensMock.Setup(t => t.GetInstallationTokenAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("ghs_fake_install_token");

        WithServiceFactory(services =>
        {
            services.RemoveAll<IGithubApiClient>();
            services.AddScoped<IGithubApiClient>(_ => apiMock.Object);
            services.RemoveAll<IGithubAppTokenService>();
            services.AddScoped<IGithubAppTokenService>(_ => tokensMock.Object);
        });
    }

    private async Task SeedDefaultGithubOptionsAsync()
    {
        await SeedGithubSystemSettingsAsync(new GithubOptions
        {
            AppSlug = AppSlug,
            WebhookSecret = "test-webhook-secret-for-state-tokens",
            AppInstallRedirectUri = "https://localhost/api/github/install/callback",
        });
    }

    // -----------------------------------------------------------------------
    // GET /api/workspaces/{slug}/github/install/start
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Start_endpoint_redirects_to_github_install_url_with_state_cookie()
    {
        var alice = await RegisterUserAsync();
        await SeedDefaultGithubOptionsAsync();

        var client = NoFollowRedirectClient(alice.AuthCookie);
        var response = await client.GetAsync($"/api/workspaces/{alice.Slug}/github/install/start");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect, await response.Content.ReadAsStringAsync());

        var location = response.Headers.Location!.ToString();
        location.Should().StartWith($"https://github.com/apps/{AppSlug}/installations/new?");
        location.Should().Contain("state=");

        var setCookies = response.Headers.GetValues("Set-Cookie").ToList();
        setCookies.Should().Contain(c => c.StartsWith("gh_install_state=", StringComparison.Ordinal));
        setCookies.Single(c => c.StartsWith("gh_install_state=", StringComparison.Ordinal))
            .Should().Contain("path=/api/github");
    }

    [Fact]
    public async Task Start_endpoint_returns_403_for_non_admin_member()
    {
        var alice = await RegisterUserAsync();
        var bob = await RegisterUserAsync();
        await SeedMembershipAsync(alice.Slug, bob.UserId, WorkspaceRole.Member);

        var client = NoFollowRedirectClient(bob.AuthCookie);
        var response = await client.GetAsync($"/api/workspaces/{alice.Slug}/github/install/start");
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Start_endpoint_returns_400_when_AppSlug_not_configured()
    {
        // Don't seed AppSlug — DB rows are empty / null for GitHub keys, so AppSlug is empty.
        var alice = await RegisterUserAsync();

        var client = NoFollowRedirectClient(alice.AuthCookie);
        var response = await client.GetAsync($"/api/workspaces/{alice.Slug}/github/install/start");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // -----------------------------------------------------------------------
    // GET /api/github/install/callback
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Callback_persists_installation_and_repos()
    {
        var alice = await RegisterUserAsync();

        var (state, _) = await IssueStateForWorkspaceAsync(alice.WorkspaceId);
        SetupHappyInstallApi(installationId: 555L, accountLogin: "alice-org", repoCount: 3);

        var client = NoFollowRedirectClient(authCookie: null);
        client.DefaultRequestHeaders.Add("Cookie", $"gh_install_state={state}");

        var response = await client.GetAsync($"/api/github/install/callback?installation_id=555&setup_action=install&state={Uri.EscapeDataString(state)}");
        response.StatusCode.Should().Be(HttpStatusCode.Redirect, await response.Content.ReadAsStringAsync());
        response.Headers.Location!.ToString()
            .Should().StartWith(WorkspaceFrontendRoutes.HomeWithQuery(alice.Slug, "install", "success"));

        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var inst = await db.GithubInstallations.SingleAsync(i => i.WorkspaceId == alice.WorkspaceId);
        inst.InstallationId.Should().Be(555L);
        inst.AccountLogin.Should().Be("alice-org");

        var repos = await db.GithubRepositories.Where(r => r.GithubInstallationId == inst.Id).ToListAsync();
        repos.Should().HaveCount(3);
    }

    [Fact]
    public async Task Login_callback_with_install_params_redirects_to_workspace_home()
    {
        var alice = await RegisterUserAsync();

        var (installState, _) = await IssueStateForWorkspaceAsync(alice.WorkspaceId);
        SetupHappyInstallApi(installationId: 888L, accountLogin: "alice-org", repoCount: 1);

        var client = NoFollowRedirectClient(authCookie: null);
        client.DefaultRequestHeaders.Add("Cookie", $"gh_install_state={installState}");

        var response = await client.GetAsync(
            $"/api/github/login/callback?installation_id=888&setup_action=install&state={Uri.EscapeDataString(installState)}&code=oauth-code");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect, await response.Content.ReadAsStringAsync());
        response.Headers.Location!.ToString()
            .Should().StartWith(WorkspaceFrontendRoutes.HomeWithQuery(alice.Slug, "install", "success"));
    }

    [Fact]
    public async Task Callback_rejects_state_mismatch()
    {
        var alice = await RegisterUserAsync();
        var (cookieState, _) = await IssueStateForWorkspaceAsync(alice.WorkspaceId);

        var client = NoFollowRedirectClient(authCookie: null);
        client.DefaultRequestHeaders.Add("Cookie", $"gh_install_state={cookieState}");

        // state query param is something unrelated.
        var response = await client.GetAsync($"/api/github/install/callback?installation_id=42&state=bogus");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Invalid state");
    }

    [Fact]
    public async Task Callback_rejects_expired_state()
    {
        var alice = await RegisterUserAsync();

        // Issue a state that expired 1 second ago.
        using var scope = CreateScope();
        var stateSvc = scope.ServiceProvider.GetRequiredService<IGithubInstallStateService>();
        var token = stateSvc.Issue(alice.WorkspaceId, TimeSpan.FromSeconds(-1));

        var client = NoFollowRedirectClient(authCookie: null);
        client.DefaultRequestHeaders.Add("Cookie", $"gh_install_state={token}");

        var response = await client.GetAsync(
            $"/api/github/install/callback?installation_id=42&state={Uri.EscapeDataString(token)}");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("expired");
    }

    [Fact]
    public async Task Callback_idempotent_when_re_installed()
    {
        var alice = await RegisterUserAsync();
        SetupHappyInstallApi(installationId: 777L, accountLogin: "alice", repoCount: 2);

        // First call.
        var (state1, _) = await IssueStateForWorkspaceAsync(alice.WorkspaceId);
        var c1 = NoFollowRedirectClient(authCookie: null);
        c1.DefaultRequestHeaders.Add("Cookie", $"gh_install_state={state1}");
        (await c1.GetAsync($"/api/github/install/callback?installation_id=777&state={Uri.EscapeDataString(state1)}"))
            .StatusCode.Should().Be(HttpStatusCode.Redirect);

        // Second call — same workspace + same installation id.
        var (state2, _) = await IssueStateForWorkspaceAsync(alice.WorkspaceId);
        var c2 = NoFollowRedirectClient(authCookie: null);
        c2.DefaultRequestHeaders.Add("Cookie", $"gh_install_state={state2}");
        (await c2.GetAsync($"/api/github/install/callback?installation_id=777&state={Uri.EscapeDataString(state2)}"))
            .StatusCode.Should().Be(HttpStatusCode.Redirect);

        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var rows = await db.GithubInstallations.Where(i => i.InstallationId == 777L).ToListAsync();
        rows.Should().HaveCount(1, "callback is idempotent — second hit reuses the existing row");
    }

    [Fact]
    public async Task Callback_rejects_install_into_different_workspace()
    {
        var alice = await RegisterUserAsync();
        var bob = await RegisterUserAsync();

        SetupHappyInstallApi(installationId: 999L, accountLogin: "shared", repoCount: 1);

        // First, install successfully into Alice's workspace.
        var (aState, _) = await IssueStateForWorkspaceAsync(alice.WorkspaceId);
        var c1 = NoFollowRedirectClient(authCookie: null);
        c1.DefaultRequestHeaders.Add("Cookie", $"gh_install_state={aState}");
        (await c1.GetAsync($"/api/github/install/callback?installation_id=999&state={Uri.EscapeDataString(aState)}"))
            .StatusCode.Should().Be(HttpStatusCode.Redirect);

        // Then try to attach the *same* installation id to Bob's workspace.
        var (bState, _) = await IssueStateForWorkspaceAsync(bob.WorkspaceId);
        var c2 = NoFollowRedirectClient(authCookie: null);
        c2.DefaultRequestHeaders.Add("Cookie", $"gh_install_state={bState}");
        var response = await c2.GetAsync($"/api/github/install/callback?installation_id=999&state={Uri.EscapeDataString(bState)}");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("different workspace");
    }

    // -----------------------------------------------------------------------
    // GET /api/workspaces/{slug}/github/installations
    // -----------------------------------------------------------------------

    [Fact]
    public async Task List_installations_filters_by_workspace()
    {
        var alice = await RegisterUserAsync();
        var bob = await RegisterUserAsync();

        await SeedInstallationAsync(alice.WorkspaceId, installationId: 111L, login: "alice-org");
        await SeedInstallationAsync(bob.WorkspaceId, installationId: 222L, login: "bob-org");

        // Alice (owner of A) sees only A's.
        var aResponse = await alice.Client.GetFromJsonAsync<List<GithubInstallationListItem>>(
            $"/api/workspaces/{alice.Slug}/github/installations", JsonOpts);
        aResponse.Should().ContainSingle().Which.InstallationId.Should().Be(111L);

        // Bob sees only his.
        var bResponse = await bob.Client.GetFromJsonAsync<List<GithubInstallationListItem>>(
            $"/api/workspaces/{bob.Slug}/github/installations", JsonOpts);
        bResponse.Should().ContainSingle().Which.InstallationId.Should().Be(222L);
    }

    [Fact]
    public async Task List_installations_403_for_non_member()
    {
        var alice = await RegisterUserAsync();
        var bob = await RegisterUserAsync();

        var response = await bob.Client.GetAsync($"/api/workspaces/{alice.Slug}/github/installations");
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // -----------------------------------------------------------------------
    // GET /api/workspaces/{slug}/github/repositories
    // -----------------------------------------------------------------------

    [Fact]
    public async Task List_repositories_filters_by_workspace()
    {
        var alice = await RegisterUserAsync();
        var bob = await RegisterUserAsync();

        var aliceInst = await SeedInstallationAsync(alice.WorkspaceId, installationId: 1L, login: "alice");
        await SeedRepositoryAsync(aliceInst, repoId: 101L, fullName: "alice/foo");

        var bobInst = await SeedInstallationAsync(bob.WorkspaceId, installationId: 2L, login: "bob");
        await SeedRepositoryAsync(bobInst, repoId: 201L, fullName: "bob/bar");

        var aResponse = await alice.Client.GetFromJsonAsync<List<GithubRepositoryListItem>>(
            $"/api/workspaces/{alice.Slug}/github/repositories", JsonOpts);
        aResponse.Should().ContainSingle().Which.FullName.Should().Be("alice/foo");

        var bResponse = await bob.Client.GetFromJsonAsync<List<GithubRepositoryListItem>>(
            $"/api/workspaces/{bob.Slug}/github/repositories", JsonOpts);
        bResponse.Should().ContainSingle().Which.FullName.Should().Be("bob/bar");
    }

    [Fact]
    public async Task List_repositories_403_for_non_member()
    {
        var alice = await RegisterUserAsync();
        var bob = await RegisterUserAsync();

        var response = await bob.Client.GetAsync($"/api/workspaces/{alice.Slug}/github/repositories");
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // -----------------------------------------------------------------------
    // POST /api/workspaces/{slug}/github/repositories/sync
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Sync_upserts_repos_and_returns_diff_counts()
    {
        var alice = await RegisterUserAsync();
        var inst = await SeedInstallationAsync(alice.WorkspaceId, installationId: 33L, login: "alice");

        // Pre-seed: repo 1 (will stay unchanged), repo 2 (will be updated), repo 4 (will be removed).
        await SeedRepositoryAsync(inst, repoId: 1L, fullName: "alice/keep", defaultBranch: "main");
        await SeedRepositoryAsync(inst, repoId: 2L, fullName: "alice/update", defaultBranch: "master");
        await SeedRepositoryAsync(inst, repoId: 4L, fullName: "alice/gone", defaultBranch: "main");

        // Live response: 1 (unchanged), 2 (DefaultBranch changed), 3 (new). 4 is missing.
        _ghApi.Setup(c => c.ListInstallationRepositoriesAsync(33L, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<GithubRepoDto>
            {
                MakeRepoDto(1L, "alice/keep", "main"),
                MakeRepoDto(2L, "alice/update", "main"),  // master -> main
                MakeRepoDto(3L, "alice/new", "main"),
            });

        var response = await alice.Client.PostAsync(
            $"/api/workspaces/{alice.Slug}/github/repositories/sync?installationId={inst.Id}",
            content: null);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<SyncGithubRepositoriesResponse>(JsonOpts);
        body.Should().NotBeNull();
        body!.Added.Should().Be(1);
        body.Updated.Should().Be(1);
        body.Removed.Should().Be(1);
        body.Total.Should().Be(3);

        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var allRepos = await db.GithubRepositories.Where(r => r.GithubInstallationId == inst.Id).ToListAsync();
        allRepos.Should().HaveCount(3);
        allRepos.Should().AllSatisfy(r => r.LastSyncedAt.Should().NotBeNull());
        allRepos.Should().NotContain(r => r.GithubRepoId == 4L);
    }

    [Fact]
    public async Task Sync_returns_400_when_installationId_missing()
    {
        var alice = await RegisterUserAsync();

        var response = await alice.Client.PostAsync(
            $"/api/workspaces/{alice.Slug}/github/repositories/sync",
            content: null);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Sync_403_for_member()
    {
        var alice = await RegisterUserAsync();
        var bob = await RegisterUserAsync();
        await SeedMembershipAsync(alice.Slug, bob.UserId, WorkspaceRole.Member);

        var inst = await SeedInstallationAsync(alice.WorkspaceId, installationId: 50L, login: "alice");

        var response = await bob.Client.PostAsync(
            $"/api/workspaces/{alice.Slug}/github/repositories/sync?installationId={inst.Id}",
            content: null);
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // -----------------------------------------------------------------------
    // DELETE /api/workspaces/{slug}/github/installations/{id}
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Delete_installation_removes_installation_and_cascades_to_repos()
    {
        var alice = await RegisterUserAsync();
        var inst = await SeedInstallationAsync(alice.WorkspaceId, installationId: 60L, login: "alice");
        await SeedRepositoryAsync(inst, repoId: 901L, fullName: "alice/x");
        await SeedRepositoryAsync(inst, repoId: 902L, fullName: "alice/y");

        var response = await alice.Client.DeleteAsync(
            $"/api/workspaces/{alice.Slug}/github/installations/{inst.Id}");
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db.GithubInstallations.AnyAsync(i => i.Id == inst.Id)).Should().BeFalse();
        (await db.GithubRepositories.AnyAsync(r => r.GithubInstallationId == inst.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task Delete_installation_404_when_not_in_workspace()
    {
        var alice = await RegisterUserAsync();

        var response = await alice.Client.DeleteAsync(
            $"/api/workspaces/{alice.Slug}/github/installations/{Guid.NewGuid()}");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Delete_installation_403_for_member()
    {
        var alice = await RegisterUserAsync();
        var bob = await RegisterUserAsync();
        await SeedMembershipAsync(alice.Slug, bob.UserId, WorkspaceRole.Member);

        var inst = await SeedInstallationAsync(alice.WorkspaceId, installationId: 70L, login: "alice");

        var response = await bob.Client.DeleteAsync(
            $"/api/workspaces/{alice.Slug}/github/installations/{inst.Id}");
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // -----------------------------------------------------------------------
    // helpers
    // -----------------------------------------------------------------------

    private void SetupHappyInstallApi(long installationId, string accountLogin, int repoCount)
    {
        _ghApi.Setup(c => c.GetInstallationAsync(installationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GithubInstallationDto(
                Id: installationId,
                Account: new GithubAccountDto(
                    Id: 1, Login: accountLogin, Type: "Organization", AvatarUrl: null)));

        var repos = Enumerable.Range(1, repoCount)
            .Select(i => MakeRepoDto(installationId * 100 + i, $"{accountLogin}/repo{i}", "main"))
            .ToList();
        _ghApi.Setup(c => c.ListInstallationRepositoriesAsync(installationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(repos);
    }

    private static GithubRepoDto MakeRepoDto(long id, string fullName, string defaultBranch)
    {
        var parts = fullName.Split('/', 2);
        var owner = parts[0];
        var name = parts.Length > 1 ? parts[1] : fullName;
        return new GithubRepoDto(
            Id: id,
            Name: name,
            FullName: fullName,
            Private: false,
            DefaultBranch: defaultBranch,
            Owner: new GithubAccountDto(Id: 0, Login: owner, Type: "User", AvatarUrl: null));
    }

    private Task<(string token, Guid workspaceId)> IssueStateForWorkspaceAsync(Guid workspaceId)
    {
        using var scope = CreateScope();
        var stateSvc = scope.ServiceProvider.GetRequiredService<IGithubInstallStateService>();
        var token = stateSvc.Issue(workspaceId, TimeSpan.FromMinutes(10));
        return Task.FromResult((token, workspaceId));
    }

    private async Task<GithubInstallation> SeedInstallationAsync(Guid workspaceId, long installationId, string login)
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var row = new GithubInstallation
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            InstallationId = installationId,
            AccountLogin = login,
            AccountType = "Organization",
        };
        db.GithubInstallations.Add(row);
        await db.SaveChangesAsync();
        return row;
    }

    private async Task SeedRepositoryAsync(
        GithubInstallation inst,
        long repoId,
        string fullName,
        string? defaultBranch = "main")
    {
        var parts = fullName.Split('/', 2);
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.GithubRepositories.Add(new GithubRepository
        {
            Id = Guid.NewGuid(),
            GithubInstallationId = inst.Id,
            GithubRepoId = repoId,
            Owner = parts[0],
            Name = parts.Length > 1 ? parts[1] : fullName,
            FullName = fullName,
            Private = false,
            DefaultBranch = defaultBranch,
            LastSyncedAt = DateTime.UtcNow.AddDays(-1),
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedMembershipAsync(string slug, string userId, WorkspaceRole role)
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var ws = await db.Workspaces.SingleAsync(w => w.Slug == slug);
        db.WorkspaceMemberships.Add(new WorkspaceMembership
        {
            Id = Guid.NewGuid(),
            WorkspaceId = ws.Id,
            UserId = userId,
            Role = role,
        });
        await db.SaveChangesAsync();
    }

    private async Task<RegisteredUser> RegisterUserAsync()
    {
        await SeedRolesAsync();

        var emailLocal = $"user-{Guid.NewGuid():N}";
        var email = $"{emailLocal}@example.com";

        var registerClient = Factory.CreateClient();
        var response = await registerClient.PostAsJsonAsync("/api/auth/register", new { email, password = Password });
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        var setCookies = response.Headers.GetValues("Set-Cookie");
        var authCookie = setCookies.First(c => c.StartsWith("auth-token=", StringComparison.Ordinal));
        var cookieValue = authCookie.Split(';')[0];

        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", cookieValue);

        using var scope = CreateScope();
        var um = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var user = (await um.FindByEmailAsync(email))!;
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var ws = await db.Workspaces.SingleAsync(w => w.OwnerId == user.Id);

        return new RegisteredUser(client, user.Id, ws.Id, ws.Slug, cookieValue);
    }

    private HttpClient NoFollowRedirectClient(string? authCookie)
    {
        var c = Factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
        if (!string.IsNullOrEmpty(authCookie))
        {
            c.DefaultRequestHeaders.Add("Cookie", authCookie);
        }
        return c;
    }

    private sealed record RegisteredUser(HttpClient Client, string UserId, Guid WorkspaceId, string Slug, string AuthCookie);
}
