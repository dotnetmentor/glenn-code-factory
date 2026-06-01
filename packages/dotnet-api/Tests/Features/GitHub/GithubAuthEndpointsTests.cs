using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Web;
using Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Source.Features.GitHub.Configuration;
using Source.Features.GitHub.Models;
using Source.Features.GitHub.Services;
using Source.Features.GitHub.Services.Dtos;
using Source.Features.Users.Models;
using Source.Features.Workspaces.Models;
using Source.Infrastructure;
using Source.Infrastructure.AuthorizationModels;

namespace Api.Tests.Features.GitHub;

/// <summary>
/// End-to-end tests for the GitHub OAuth user-identity flow:
///   GET /api/github/login
///   GET /api/github/login/callback
/// Mocks <see cref="IGithubApiClient"/> to keep the tests offline.
/// </summary>
[Collection(HangfireTestCollection.Name)]
public class GithubAuthEndpointsTests : IntegrationTestBase
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly Mock<IGithubApiClient> _ghApi = new(MockBehavior.Strict);

    public GithubAuthEndpointsTests()
    {
        // Replace the production API client with our mock; GitHub options are seeded into the
        // SystemSettings store on first use rather than via IOptions binding.
        var apiMock = _ghApi;
        WithServiceFactory(services =>
        {
            services.RemoveAll<IGithubApiClient>();
            services.AddScoped<IGithubApiClient>(_ => apiMock.Object);
        });
    }

    private async Task SeedDefaultGithubOptionsAsync()
    {
        await SeedGithubSystemSettingsAsync(new GithubOptions
        {
            ClientId = "test-client-id",
            ClientSecret = "test-client-secret",
            OAuthRedirectUri = "https://localhost/api/github/login/callback",
        });
    }

    // -----------------------------------------------------------------------
    // GET /api/github/login
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Login_endpoint_redirects_to_github_with_state_cookie()
    {
        await SeedDefaultGithubOptionsAsync();
        var client = NoFollowRedirectClient();

        var response = await client.GetAsync("/api/github/login");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);

        var location = response.Headers.Location!.ToString();
        location.Should().StartWith("https://github.com/login/oauth/authorize?");
        location.Should().Contain("client_id=test-client-id");
        // The Location header may be normalised (%20 ↔ space); accept either form.
        (location.Contains("scope=read%3Auser%20user%3Aemail") || location.Contains("scope=read%3Auser user%3Aemail"))
            .Should().BeTrue("scope param must include both read:user and user:email");

        var stateFromUrl = ParseStateFromUrl(location);
        stateFromUrl.Should().NotBeNullOrEmpty();

        var setCookies = response.Headers.GetValues("Set-Cookie").ToList();
        var stateCookie = setCookies.FirstOrDefault(c => c.StartsWith("gh_oauth_state=", StringComparison.Ordinal));
        stateCookie.Should().NotBeNull();
        stateCookie.Should().Contain($"gh_oauth_state={stateFromUrl}");
        stateCookie.Should().Contain("path=/api/github", "scoped to the github callback paths");
    }

    [Fact]
    public async Task Login_endpoint_returns_400_when_clientId_not_configured()
    {
        // Don't seed — system-settings rows will be empty, ClientId is whitespace.
        var client = NoFollowRedirectClient();
        var response = await client.GetAsync("/api/github/login");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("not configured");
    }

    // -----------------------------------------------------------------------
    // GET /api/github/login/callback — failure cases
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Callback_returns_400_on_state_mismatch()
    {
        var client = NoFollowRedirectClient();
        client.DefaultRequestHeaders.Add("Cookie", "gh_oauth_state=" + UrlEncodeCookie("foo|"));

        var response = await client.GetAsync("/api/github/login/callback?code=abc&state=bar");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Invalid state");
    }

    [Fact]
    public async Task Callback_returns_400_on_missing_state_cookie()
    {
        var client = NoFollowRedirectClient();
        var response = await client.GetAsync("/api/github/login/callback?code=abc&state=foo");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Invalid state");
    }

    [Fact]
    public async Task Callback_returns_400_when_token_exchange_returns_empty()
    {
        await SeedRolesAsync();
        _ghApi.Setup(c => c.ExchangeOAuthCodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(string.Empty);

        var (client, _) = ClientWithStateCookie("state-empty-token", redirect: null);

        var response = await client.GetAsync("/api/github/login/callback?code=abc&state=state-empty-token");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Callback_returns_400_when_no_verified_primary_email()
    {
        await SeedRolesAsync();

        _ghApi.Setup(c => c.ExchangeOAuthCodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("ghs_test_token");
        _ghApi.Setup(c => c.GetCurrentUserAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GithubUserDto(Id: 1234L, Login: "ghost", AvatarUrl: null, Email: null));
        _ghApi.Setup(c => c.GetCurrentUserEmailsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<GithubEmailDto>
            {
                new("ghost@unverified.example", Primary: false, Verified: false, Visibility: null),
            });

        var (client, _) = ClientWithStateCookie("state-no-email", redirect: null);

        var response = await client.GetAsync("/api/github/login/callback?code=abc&state=state-no-email");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("primary email");
    }

    // -----------------------------------------------------------------------
    // GET /api/github/login/callback — happy paths
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Callback_creates_new_user_workspace_identity_when_unknown_github_user()
    {
        await SeedRolesAsync();

        const long ghId = 12345L;
        const string ghLogin = "alice";
        const string email = "alice@github.test";
        SetupHappyOAuthExchange(ghId, ghLogin, email, avatar: "https://avatars.example/alice.png");

        var (client, _) = ClientWithStateCookie("state-new-user", redirect: null);

        var response = await client.GetAsync("/api/github/login/callback?code=abc&state=state-new-user");
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.ToString().Should().Be("/", "default redirect when no caller-provided redirectTo");

        var setCookies = response.Headers.GetValues("Set-Cookie");
        setCookies.Should().Contain(c => c.StartsWith("auth-token=", StringComparison.Ordinal),
            "GitHub OAuth login should issue the same auth-token cookie as password login");

        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();

        var user = await userManager.FindByEmailAsync(email);
        user.Should().NotBeNull();
        var roles = await userManager.GetRolesAsync(user!);
        roles.Should().Contain(RoleConstants.WorkspaceUser);

        var ws = await db.Workspaces.SingleAsync(w => w.OwnerId == user!.Id);
        ws.Slug.Should().StartWith("alice");
        var membership = await db.WorkspaceMemberships.SingleAsync(m => m.WorkspaceId == ws.Id && m.UserId == user!.Id);
        membership.Role.Should().Be(WorkspaceRole.Owner);

        var identity = await db.GithubUserIdentities.SingleAsync(i => i.UserId == user!.Id);
        identity.GithubUserId.Should().Be(ghId);
        identity.GithubLogin.Should().Be(ghLogin);
    }

    [Fact]
    public async Task Callback_links_existing_user_by_email()
    {
        await SeedRolesAsync();

        // Pre-create Bob (no GH identity yet).
        const string bobEmail = "bob.existing@example.test";
        string bobId;
        using (var scope = CreateScope())
        {
            var um = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            var bob = new User { UserName = bobEmail, Email = bobEmail, EmailConfirmed = true };
            (await um.CreateAsync(bob, "TempPwd123!")).Succeeded.Should().BeTrue();
            bobId = bob.Id;
        }

        SetupHappyOAuthExchange(99L, "bob-gh", bobEmail, avatar: null);

        var (client, _) = ClientWithStateCookie("state-link-bob", redirect: null);

        var initialUserCount = await CountUsersAsync();

        var response = await client.GetAsync("/api/github/login/callback?code=abc&state=state-link-bob");
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.GetValues("Set-Cookie").Should().Contain(c => c.StartsWith("auth-token=", StringComparison.Ordinal));

        (await CountUsersAsync()).Should().Be(initialUserCount, "no new users — we linked");

        using var scope2 = CreateScope();
        var db = scope2.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var identity = await db.GithubUserIdentities.SingleAsync(i => i.GithubUserId == 99L);
        identity.UserId.Should().Be(bobId);
    }

    [Fact]
    public async Task Callback_logs_in_existing_linked_user_and_refreshes_login()
    {
        await SeedRolesAsync();

        // Seed a user + linked GitHub identity directly in DB.
        const string email = "linked@example.test";
        const long ghId = 42L;
        const string oldLogin = "bob";
        const string newLogin = "bob-renamed";
        string userId;
        Guid identityId;

        using (var scope = CreateScope())
        {
            var um = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            var u = new User { UserName = email, Email = email, EmailConfirmed = true };
            (await um.CreateAsync(u, "TempPwd123!")).Succeeded.Should().BeTrue();
            userId = u.Id;

            var seedDb = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            identityId = Guid.NewGuid();
            seedDb.GithubUserIdentities.Add(new GithubUserIdentity
            {
                Id = identityId,
                UserId = userId,
                GithubUserId = ghId,
                GithubLogin = oldLogin,
                AvatarUrl = "https://avatars.example/old.png",
            });
            await seedDb.SaveChangesAsync();
        }

        // Mock returns the same id but a *different* login.
        SetupHappyOAuthExchange(ghId, newLogin, email, avatar: "https://avatars.example/new.png");

        var (client, _) = ClientWithStateCookie("state-relogin", redirect: null);

        var initialUsers = await CountUsersAsync();
        var initialIdentities = await CountIdentitiesAsync();

        var response = await client.GetAsync("/api/github/login/callback?code=abc&state=state-relogin");
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);

        (await CountUsersAsync()).Should().Be(initialUsers);
        (await CountIdentitiesAsync()).Should().Be(initialIdentities);

        using var scope2 = CreateScope();
        var db = scope2.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var identity = await db.GithubUserIdentities.SingleAsync(i => i.Id == identityId);
        identity.GithubLogin.Should().Be(newLogin, "login should be refreshed on the existing identity row");
        identity.AvatarUrl.Should().Be("https://avatars.example/new.png");
    }

    [Fact]
    public async Task Callback_uses_redirect_when_safe_relative()
    {
        await SeedRolesAsync();
        SetupHappyOAuthExchange(7L, "carol", "carol@github.test", avatar: null);

        var (client, _) = ClientWithStateCookie("state-redirect-safe", redirect: "/w/somewhere");

        var response = await client.GetAsync("/api/github/login/callback?code=abc&state=state-redirect-safe");
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.ToString().Should().Be("/w/somewhere");
    }

    [Fact]
    public async Task Callback_rejects_absolute_redirect_uri()
    {
        await SeedRolesAsync();
        SetupHappyOAuthExchange(8L, "dave", "dave@github.test", avatar: null);

        var (client, _) = ClientWithStateCookie("state-redirect-evil", redirect: "https://evil.test/owned");

        var response = await client.GetAsync("/api/github/login/callback?code=abc&state=state-redirect-evil");
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.ToString().Should().Be("/", "open-redirect guard must collapse absolute URLs to '/'");
    }

    [Fact]
    public async Task Callback_falls_back_to_user_emails_when_user_email_is_null()
    {
        await SeedRolesAsync();

        const long ghId = 555L;
        const string ghLogin = "private-user";
        const string primaryEmail = "primary@github.test";

        _ghApi.Setup(c => c.ExchangeOAuthCodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("ghs_test_token");
        _ghApi.Setup(c => c.GetCurrentUserAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GithubUserDto(Id: ghId, Login: ghLogin, AvatarUrl: null, Email: null));
        _ghApi.Setup(c => c.GetCurrentUserEmailsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<GithubEmailDto>
            {
                new("noisy@github.test", Primary: false, Verified: true, Visibility: null),
                new(primaryEmail, Primary: true, Verified: true, Visibility: "private"),
            });

        var (client, _) = ClientWithStateCookie("state-private-email", redirect: null);

        var response = await client.GetAsync("/api/github/login/callback?code=abc&state=state-private-email");
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);

        using var scope = CreateScope();
        var um = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var user = await um.FindByEmailAsync(primaryEmail);
        user.Should().NotBeNull("the verified primary email should drive user creation");
    }

    // -----------------------------------------------------------------------
    // helpers
    // -----------------------------------------------------------------------

    private void SetupHappyOAuthExchange(long ghId, string ghLogin, string email, string? avatar)
    {
        _ghApi.Setup(c => c.ExchangeOAuthCodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("ghs_test_token");
        _ghApi.Setup(c => c.GetCurrentUserAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GithubUserDto(Id: ghId, Login: ghLogin, AvatarUrl: avatar, Email: email));
    }

    private (HttpClient client, string state) ClientWithStateCookie(string state, string? redirect)
    {
        var encodedRedirect = redirect is null ? string.Empty : HttpUtility.UrlEncode(redirect);
        var cookieValue = $"{state}|{encodedRedirect}";
        var client = NoFollowRedirectClient();
        client.DefaultRequestHeaders.Add("Cookie", $"gh_oauth_state={UrlEncodeCookie(cookieValue)}");
        return (client, state);
    }

    private HttpClient NoFollowRedirectClient()
    {
        // Match the existing factory's options but with auto-redirect off so we can inspect Location.
        return Factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
    }

    private async Task<int> CountUsersAsync()
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.Users.IgnoreQueryFilters().CountAsync();
    }

    private async Task<int> CountIdentitiesAsync()
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.GithubUserIdentities.CountAsync();
    }

    private static string? ParseStateFromUrl(string url)
    {
        var queryStart = url.IndexOf('?');
        if (queryStart < 0) return null;
        var qs = HttpUtility.ParseQueryString(url[(queryStart + 1)..]);
        return qs["state"];
    }

    /// <summary>The cookie value may contain '|' which is fine in raw cookies, but we URL-encode '|'
    /// defensively to keep the header parser happy.</summary>
    private static string UrlEncodeCookie(string raw)
    {
        var sb = new StringBuilder();
        foreach (var ch in raw)
        {
            if (char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' or '~' or '|' or '%')
            {
                sb.Append(ch);
            }
            else
            {
                sb.Append(Uri.EscapeDataString(ch.ToString()));
            }
        }
        return sb.ToString();
    }
}

