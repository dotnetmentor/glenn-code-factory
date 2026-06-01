using System.Net;
using System.Net.Http.Json;
using Api.Tests.Infrastructure;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Source.Features.GitOps.Controllers;
using Source.Features.GitOps.Models;
using Source.Features.RuntimeLifecycle.Models;
using Source.Features.SignalR.Hubs;
using Source.Features.Users.Models;
using Source.Infrastructure;

namespace Api.Tests.Features.GitOps;

/// <summary>
/// End-to-end HTTP tests for the destructive-git-op approval surface at
/// <c>POST /api/projects/{projectId}/git/destructive-ops/{opId}/approve</c>.
///
/// <para>Mirrors <see cref="Api.Tests.Features.Hooks.HookConfigAdminControllerTests"/>
/// for the SignalR mock chain (<c>hub.Clients.Group("...").MethodName(...)</c>) —
/// the in-memory test host has no actual daemon connections, so the production
/// <see cref="IHubContext{THub, T}"/> registration would just no-op. We swap in
/// a Moq stand-in via <see cref="IntegrationTestBase.WithServiceFactory"/> so we
/// can assert on the exact <see cref="IRuntimeClient.ExecuteDestructiveGitOp"/>
/// call the controller emits.</para>
///
/// <para><b>Test scope.</b> Happy path + the two edge cases the controller
/// branches on (op missing → 404, op already completed → 409). The "not
/// destructive" 400 path is covered indirectly by the seed shape — adding a
/// dedicated test would just exercise the same branch with a different flag,
/// so we keep the file tight.</para>
/// </summary>
[Collection(HangfireTestCollection.Name)]
public class GitDestructiveOpsControllerTests : IntegrationTestBase
{
    private const string Password = "Password123!";

    // SignalR mock chain — hub.Clients.Group("runtime-{id}").ExecuteDestructiveGitOp(opId).
    private readonly Mock<IHubContext<RuntimeHub, IRuntimeClient>> _runtimeHub = new();
    private readonly Mock<IHubClients<IRuntimeClient>> _hubClients = new();
    private readonly Mock<IRuntimeClient> _groupClient = new();

    public GitDestructiveOpsControllerTests()
    {
        _runtimeHub.SetupGet(h => h.Clients).Returns(_hubClients.Object);
        _hubClients.Setup(c => c.Group(It.IsAny<string>())).Returns(_groupClient.Object);
        _groupClient
            .Setup(c => c.ExecuteDestructiveGitOp(It.IsAny<Guid>()))
            .Returns(Task.CompletedTask);

        WithServiceFactory(services =>
        {
            services.RemoveAll<IHubContext<RuntimeHub, IRuntimeClient>>();
            services.AddSingleton(_runtimeHub.Object);
        });
    }

    // ----------------------------------------------------------------------
    // Happy path
    // ----------------------------------------------------------------------

    [Fact]
    public async Task Approve_HappyPath_Returns200_AndPushesExecuteToRuntimeGroup()
    {
        var (client, _) = await RegisterUserAsync();
        var (runtime, op) = await SeedDestructiveOpAsync();

        var response = await client.PostAsync(
            $"/api/projects/{runtime.ProjectId}/git/destructive-ops/{op.Id}/approve",
            content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            await response.Content.ReadAsStringAsync());

        var payload = await response.Content.ReadFromJsonAsync<AcceptedResponse>();
        payload!.OperationId.Should().Be(op.Id);

        // Push to runtime-{id} group with the op id as the only argument.
        _hubClients.Verify(c => c.Group($"runtime-{runtime.Id}"), Times.AtLeastOnce);
        _groupClient.Verify(
            c => c.ExecuteDestructiveGitOp(op.Id),
            Times.Once,
            "the controller must fan out the approval to the live daemon group");
    }

    // ----------------------------------------------------------------------
    // 404 — op missing / wrong project
    // ----------------------------------------------------------------------

    [Fact]
    public async Task Approve_Returns404_WhenOpMissing()
    {
        var (client, _) = await RegisterUserAsync();

        var response = await client.PostAsync(
            $"/api/projects/{Guid.NewGuid()}/git/destructive-ops/{Guid.NewGuid()}/approve",
            content: null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // No fan-out for a missing op.
        _groupClient.Verify(
            c => c.ExecuteDestructiveGitOp(It.IsAny<Guid>()),
            Times.Never);
    }

    [Fact]
    public async Task Approve_Returns404_WhenOpBelongsToDifferentProject()
    {
        // Cross-project lookups must collapse to 404 — we don't want to leak
        // the existence of an op that belongs to a different project.
        var (client, _) = await RegisterUserAsync();
        var (_, op) = await SeedDestructiveOpAsync();

        var response = await client.PostAsync(
            $"/api/projects/{Guid.NewGuid()}/git/destructive-ops/{op.Id}/approve",
            content: null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        _groupClient.Verify(
            c => c.ExecuteDestructiveGitOp(It.IsAny<Guid>()),
            Times.Never);
    }

    // ----------------------------------------------------------------------
    // 400 — op exists but isn't destructive
    // ----------------------------------------------------------------------

    [Fact]
    public async Task Approve_Returns400_WhenOpNotDestructive()
    {
        var (client, _) = await RegisterUserAsync();
        var (runtime, op) = await SeedDestructiveOpAsync(wasDestructive: false);

        var response = await client.PostAsync(
            $"/api/projects/{runtime.ProjectId}/git/destructive-ops/{op.Id}/approve",
            content: null);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _groupClient.Verify(
            c => c.ExecuteDestructiveGitOp(It.IsAny<Guid>()),
            Times.Never);
    }

    // ----------------------------------------------------------------------
    // 409 — op already completed (EndedAt set)
    // ----------------------------------------------------------------------

    [Fact]
    public async Task Approve_Returns409_WhenOpAlreadyCompleted()
    {
        var (client, _) = await RegisterUserAsync();
        var (runtime, op) = await SeedDestructiveOpAsync(endedAt: DateTime.UtcNow.AddMinutes(-1));

        var response = await client.PostAsync(
            $"/api/projects/{runtime.ProjectId}/git/destructive-ops/{op.Id}/approve",
            content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        _groupClient.Verify(
            c => c.ExecuteDestructiveGitOp(It.IsAny<Guid>()),
            Times.Never);
    }

    // ----------------------------------------------------------------------
    // Auth gating
    // ----------------------------------------------------------------------

    [Fact]
    public async Task Approve_Unauthenticated_Returns401()
    {
        var response = await Client.PostAsync(
            $"/api/projects/{Guid.NewGuid()}/git/destructive-ops/{Guid.NewGuid()}/approve",
            content: null);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ----------------------------------------------------------------------
    // helpers
    // ----------------------------------------------------------------------

    /// <summary>
    /// Seed a runtime + a fresh destructive git op pointing at it. Defaults
    /// produce an "open" destructive op (WasDestructive=true, EndedAt=null);
    /// pass <paramref name="wasDestructive"/>=false or a non-null
    /// <paramref name="endedAt"/> to drive the 400/409 branches.
    /// </summary>
    private async Task<(ProjectRuntime Runtime, GitOperation Op)> SeedDestructiveOpAsync(
        bool wasDestructive = true,
        DateTime? endedAt = null)
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
        };
        db.ProjectRuntimes.Add(runtime);

        var op = new GitOperation
        {
            Id = Guid.NewGuid(),
            RuntimeId = runtime.Id,
            OpType = GitOpType.Reset,
            CommandLine = "git reset --hard HEAD~1",
            StartedAt = DateTime.UtcNow.AddMinutes(-2),
            EndedAt = endedAt,
            WasDestructive = wasDestructive,
            ApprovalId = Guid.NewGuid(),
            OutputTail = string.Empty,
            OutputHash = string.Empty,
        };
        db.GitOperations.Add(op);

        await db.SaveChangesAsync();
        return (runtime, op);
    }

    /// <summary>
    /// Register a plain user and return a cookie-authenticated client. The
    /// approve endpoint has no role gate — basic [Authorize] is enough.
    /// </summary>
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
        var um = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<User>>();
        var user = await um.FindByEmailAsync(email);
        return (client, user!.Id);
    }
}
