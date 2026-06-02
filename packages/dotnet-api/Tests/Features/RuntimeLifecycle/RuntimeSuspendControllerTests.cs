using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Source.Features.Projects.Models;
using Source.Features.RuntimeLifecycle.Models;
using Source.Features.Users.Models;
using Source.Infrastructure;

namespace Api.Tests.Features.RuntimeLifecycle;

[Collection(HangfireTestCollection.Name)]
public class RuntimeSuspendControllerTests : IntegrationTestBase
{
    private const string Password = "Password123!";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    [Fact]
    public async Task Suspend_Unauthenticated_Returns401()
    {
        var response = await Client.PostAsync(
            $"/api/projects/{Guid.NewGuid()}/branches/{Guid.NewGuid()}/runtime/suspend",
            content: null);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Suspend_NonOwner_Returns404()
    {
        var (client, _) = await RegisterUserAsync();
        var projectId = Guid.NewGuid();
        var branchId = Guid.NewGuid();

        await SeedOwnedProjectAsync(projectId, "someone-else");
        await SeedRuntimeAsync(RuntimeState.Online, projectId, branchId);

        var response = await client.PostAsync(
            $"/api/projects/{projectId}/branches/{branchId}/runtime/suspend",
            content: null);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Suspend_OnlineRuntime_TransitionsToSuspending()
    {
        var (client, userId) = await RegisterUserAsync();
        var projectId = Guid.NewGuid();
        var branchId = Guid.NewGuid();

        await SeedOwnedProjectAsync(projectId, userId);
        await SeedRuntimeAsync(RuntimeState.Online, projectId, branchId);

        var response = await client.PostAsync(
            $"/api/projects/{projectId}/branches/{branchId}/runtime/suspend",
            content: null);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        var body = await response.Content.ReadFromJsonAsync<RuntimeStatusResponse>(JsonOptions);
        body!.State.Should().Be(RuntimeState.Suspending);
    }

    [Fact]
    public async Task Suspend_BootingRuntime_Returns409()
    {
        var (client, userId) = await RegisterUserAsync();
        var projectId = Guid.NewGuid();
        var branchId = Guid.NewGuid();

        await SeedOwnedProjectAsync(projectId, userId);
        await SeedRuntimeAsync(RuntimeState.Booting, projectId, branchId);

        var response = await client.PostAsync(
            $"/api/projects/{projectId}/branches/{branchId}/runtime/suspend",
            content: null);
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Restart_OnlineRuntime_TransitionsToPending()
    {
        var (client, userId) = await RegisterUserAsync();
        var projectId = Guid.NewGuid();
        var branchId = Guid.NewGuid();

        await SeedOwnedProjectAsync(projectId, userId);
        await SeedRuntimeAsync(RuntimeState.Online, projectId, branchId);

        var response = await client.PostAsync(
            $"/api/projects/{projectId}/branches/{branchId}/runtime/restart",
            content: null);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        var body = await response.Content.ReadFromJsonAsync<RuntimeStatusResponse>(JsonOptions);
        body!.State.Should().Be(RuntimeState.Pending);
    }

    [Fact]
    public async Task ForceStop_OnlineRuntime_TransitionsToSuspending()
    {
        var (client, userId) = await RegisterUserAsync();
        var projectId = Guid.NewGuid();
        var branchId = Guid.NewGuid();

        await SeedOwnedProjectAsync(projectId, userId);
        await SeedRuntimeAsync(RuntimeState.Online, projectId, branchId);

        var response = await client.PostAsync(
            $"/api/projects/{projectId}/branches/{branchId}/runtime/force-stop",
            content: null);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        var body = await response.Content.ReadFromJsonAsync<RuntimeStatusResponse>(JsonOptions);
        body!.State.Should().Be(RuntimeState.Suspending);
    }

    private async Task<ProjectRuntime> SeedRuntimeAsync(
        RuntimeState state,
        Guid projectId,
        Guid branchId)
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
            FlyMachineId = "machine-test",
        };
        db.ProjectRuntimes.Add(runtime);
        await db.SaveChangesAsync();
        return runtime;
    }

    private async Task SeedOwnedProjectAsync(Guid projectId, string ownerUserId)
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.Projects.Add(new Project
        {
            Id = projectId,
            WorkspaceId = Guid.NewGuid(),
            OwnerUserId = ownerUserId,
            Name = "test-project",
            GithubRepoOwner = "test",
            GithubRepoName = "repo",
            GithubInstallationId = Guid.NewGuid(),
        });
        await db.SaveChangesAsync();
    }

    private async Task<(HttpClient Client, string UserId)> RegisterUserAsync()
    {
        await SeedRolesAsync();

        var email = $"user-{Guid.NewGuid():N}@example.com";
        var registerClient = Factory.CreateClient();
        var response = await registerClient.PostAsJsonAsync(
            "/api/auth/register",
            new { email, password = Password });
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
}
