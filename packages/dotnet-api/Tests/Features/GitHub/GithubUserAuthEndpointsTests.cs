using System.Net;
using System.Net.Http.Json;
using Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Source.Features.GitHub;
using Source.Features.GitHub.Models;
using Source.Features.GitHub.Services;
using Source.Features.GitHub.Services.Dtos;
using Source.Infrastructure;

namespace Api.Tests.Features.GitHub;

/// <summary>
/// End-to-end tests for the OAuth-only re-authorize callback:
///   GET /api/github/user-auth/callback
/// </summary>
[Collection(HangfireTestCollection.Name)]
public class GithubUserAuthEndpointsTests : IntegrationTestBase
{
    private readonly Mock<IGithubApiClient> _ghApi = new(MockBehavior.Loose);

    public GithubUserAuthEndpointsTests()
    {
        var apiMock = _ghApi;
        WithServiceFactory(services =>
        {
            services.RemoveAll<IGithubApiClient>();
            services.AddScoped<IGithubApiClient>(_ => apiMock.Object);
        });
    }

    [Fact]
    public async Task Callback_without_code_redirects_to_workspace_home_with_reauth_error()
    {
        var alice = await RegisterUserAsync();
        var inst = await SeedInstallationAsync(alice.WorkspaceId, installationId: 808L, login: "alice-org");
        var state = IssueReauthState(alice.WorkspaceId, inst.Id);

        var client = NoFollowRedirectClient();
        var response = await client.GetAsync(
            $"/api/github/user-auth/callback?state={Uri.EscapeDataString(state)}");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.ToString()
            .Should().Be(WorkspaceFrontendRoutes.HomeWithQuery(alice.Slug, "reauth", "error"));
    }

    [Fact]
    public async Task Callback_with_valid_code_redirects_to_workspace_home_with_reauth_success()
    {
        var alice = await RegisterUserAsync();
        var inst = await SeedInstallationAsync(alice.WorkspaceId, installationId: 909L, login: "alice-org");
        var state = IssueReauthState(alice.WorkspaceId, inst.Id);

        _ghApi.Setup(c => c.ExchangeOAuthCodeFullAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GithubUserAccessTokenPayload
            {
                AccessToken = "ghu_test",
                AccessTokenExpiresAt = DateTime.UtcNow.AddHours(8),
                RefreshToken = "ghr_test",
                RefreshTokenExpiresAt = DateTime.UtcNow.AddDays(180),
            });
        _ghApi.Setup(c => c.GetCurrentUserAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GithubUserDto(Id: 1L, Login: "alice", AvatarUrl: null, Email: null));

        var client = NoFollowRedirectClient();
        var response = await client.GetAsync(
            $"/api/github/user-auth/callback?code=oauth-code&state={Uri.EscapeDataString(state)}");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.ToString()
            .Should().Be(WorkspaceFrontendRoutes.HomeWithQuery(alice.Slug, "reauth", "success"));
    }

    private string IssueReauthState(Guid workspaceId, Guid installationId)
    {
        using var scope = CreateScope();
        var stateSvc = scope.ServiceProvider.GetRequiredService<IGithubInstallStateService>();
        return stateSvc.IssueReauth(workspaceId, installationId, TimeSpan.FromMinutes(10));
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

    private async Task<RegisteredUser> RegisterUserAsync()
    {
        await SeedRolesAsync();

        const string password = "Password123!";
        var emailLocal = $"user-{Guid.NewGuid():N}";
        var email = $"{emailLocal}@example.com";

        var registerClient = Factory.CreateClient();
        var response = await registerClient.PostAsJsonAsync("/api/auth/register", new { email, password });
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var user = await db.Users.SingleAsync(u => u.Email == email);
        var ws = await db.Workspaces.SingleAsync(w => w.OwnerId == user.Id);

        return new RegisteredUser(Factory.CreateClient(), user.Id, ws.Id, ws.Slug, string.Empty);
    }

    private HttpClient NoFollowRedirectClient()
    {
        return Factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
    }

    private sealed record RegisteredUser(
        HttpClient Client,
        string UserId,
        Guid WorkspaceId,
        string Slug,
        string AuthCookie);
}
