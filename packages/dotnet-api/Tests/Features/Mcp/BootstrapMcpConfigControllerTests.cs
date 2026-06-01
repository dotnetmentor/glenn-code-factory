using System.Security.Claims;
using Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Source.Features.Mcp.Controllers;
using Source.Features.Mcp.Models;
using Source.Features.RuntimeLifecycle.Models;
using Source.Features.RuntimeTokens.Models;

namespace Api.Tests.Features.Mcp;

/// <summary>
/// Unit tests for <see cref="BootstrapMcpConfigController.Get"/> — the
/// daemon-facing cold-boot MCP-catalog endpoint. We instantiate the controller
/// directly against a real in-memory <see cref="Source.Infrastructure.ApplicationDbContext"/>
/// (via <see cref="HandlerTestBase"/>) and stamp a <see cref="DefaultHttpContext"/>
/// pre-populated with a <see cref="ClaimsPrincipal"/> carrying the <c>rt_runtime</c>
/// claim — i.e. we test what the controller does *after* the JWT bearer
/// middleware has authenticated the caller. No HTTP pipeline, no SignalR.
///
/// <para>Mirrors
/// <see cref="Api.Tests.Features.Conversations.GetActiveSessionForRuntimeTests"/>
/// and
/// <see cref="Api.Tests.Features.ProjectSecrets.BootstrapEnvControllerTests"/>:
/// same controller-level claim cross-check pattern, same in-memory rig.</para>
///
/// <para><b>What this file does NOT cover:</b> the JWT bearer middleware itself
/// (missing/expired/revoked → 401). That wiring lives in
/// <c>AuthenticationExtensions.AddRuntimeTokenAuthScheme</c> and is exercised
/// indirectly by <c>RuntimeTokenServiceTests</c> + the revocation cache tests.
/// 401 here is purely middleware-level — re-asserting it would just re-test
/// JwtBearerHandler.</para>
/// </summary>
public class BootstrapMcpConfigControllerTests : HandlerTestBase
{
    private const string TestScheme = "http";
    private const string TestHost = "localhost";

    /// <summary>
    /// Build a controller whose <see cref="ControllerBase.User"/> carries the
    /// supplied claim runtime id, with a <see cref="DefaultHttpContext"/>
    /// scheme/host stamped to match a real test-server style request URL
    /// (<c>http://localhost</c>). <paramref name="claimRuntimeId"/> = null
    /// simulates "authenticated principal but no rt_runtime claim".
    /// </summary>
    private BootstrapMcpConfigController CreateController(Guid? claimRuntimeId)
    {
        var identity = new ClaimsIdentity(authenticationType: "RuntimeToken");
        if (claimRuntimeId.HasValue)
        {
            identity.AddClaim(new Claim(RuntimeTokenClaimNames.RuntimeId, claimRuntimeId.Value.ToString()));
        }
        var principal = new ClaimsPrincipal(identity);

        var httpContext = new DefaultHttpContext
        {
            User = principal,
        };
        httpContext.Request.Scheme = TestScheme;
        httpContext.Request.Host = new HostString(TestHost);

        return new BootstrapMcpConfigController(Context)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = httpContext,
            },
        };
    }

    private async Task<ProjectRuntime> SeedRuntimeAsync(Guid? projectId = null, bool isDeleted = false)
    {
        var runtime = new ProjectRuntime
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId ?? Guid.NewGuid(),
            Region = "arn",
            IsDeleted = isDeleted,
        };
        Context.ProjectRuntimes.Add(runtime);
        await Context.SaveChangesAsync();
        return runtime;
    }

    private async Task<McpServer> SeedMcpServerAsync(string name, string version, bool defaultEnabled)
    {
        var server = new McpServer
        {
            Id = Guid.NewGuid(),
            Name = name,
            Version = version,
            DefaultEnabled = defaultEnabled,
        };
        Context.McpServers.Add(server);
        await Context.SaveChangesAsync();
        return server;
    }

    /// <summary>
    /// Seed the kanban catalog row with the same Guid the production migration
    /// uses (<c>c748477b-a644-461f-ac9d-04f739b29f6b</c>, see
    /// <c>20260509022338_AddMcpAndProjectKanban</c>'s raw <c>InsertData</c>).
    /// The in-memory EF provider runs <c>EnsureCreated()</c> rather than the
    /// migration pipeline, so the migration's <c>InsertData</c> seed is NOT
    /// applied to the test DB — we materialise it ourselves to mirror the
    /// production state on a freshly-migrated instance.
    /// </summary>
    private static readonly Guid ProductionKanbanId =
        Guid.Parse("c748477b-a644-461f-ac9d-04f739b29f6b");

    private async Task SeedProductionKanbanAsync()
    {
        Context.McpServers.Add(new McpServer
        {
            Id = ProductionKanbanId,
            Name = "kanban",
            Version = "v1",
            DefaultEnabled = true,
        });
        await Context.SaveChangesAsync();
    }

    // ----------------------------------------------------------------------

    [Fact]
    public async Task ClaimMismatchesPath_Returns403()
    {
        var runtime = await SeedRuntimeAsync();

        // Claim carries a different (still-valid) Guid — a daemon may only ask
        // about itself. 403, not 401: the caller IS authenticated, just
        // unauthorised for this resource.
        var controller = CreateController(claimRuntimeId: Guid.NewGuid());

        var result = await controller.Get(runtime.Id, CancellationToken.None);

        result.Result.Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public async Task ClaimMissing_Returns403()
    {
        // Authenticated principal but with NO rt_runtime claim — the auth
        // scheme would reject this in production (a valid RuntimeToken always
        // carries rt_runtime), but the controller still defends against a
        // malformed principal and returns 403.
        var runtime = await SeedRuntimeAsync();

        var controller = CreateController(claimRuntimeId: null);

        var result = await controller.Get(runtime.Id, CancellationToken.None);

        result.Result.Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public async Task RuntimeNotFound_Returns404()
    {
        var unknownRuntimeId = Guid.NewGuid();

        // Claim matches the path id, but no runtime row exists.
        var controller = CreateController(claimRuntimeId: unknownRuntimeId);

        var result = await controller.Get(unknownRuntimeId, CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task SoftDeletedRuntime_Returns404()
    {
        var runtime = await SeedRuntimeAsync(isDeleted: true);

        var controller = CreateController(claimRuntimeId: runtime.Id);

        var result = await controller.Get(runtime.Id, CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundResult>(
            "the global query filter on ProjectRuntime hides IsDeleted=true rows; a torn-down runtime has no bootstrap bundle");
    }

    [Fact]
    public async Task DefaultEnabledFalse_ServersExcluded()
    {
        var runtime = await SeedRuntimeAsync();

        await SeedMcpServerAsync("alpha", "v1", defaultEnabled: true);
        await SeedMcpServerAsync("beta", "v1", defaultEnabled: false);

        var controller = CreateController(claimRuntimeId: runtime.Id);

        var result = await controller.Get(runtime.Id, CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var payload = ok.Value.Should().BeOfType<BootstrapMcpConfigResponse>().Subject;

        payload.Servers.Should().HaveCount(1, "DefaultEnabled=false MCPs are excluded from the bootstrap bundle");
        payload.Servers.Single().Name.Should().Be("alpha");
    }

    [Fact]
    public async Task BaseUrl_BuiltFromRequestSchemeAndHost()
    {
        var runtime = await SeedRuntimeAsync();

        await SeedMcpServerAsync("kanban", "v1", defaultEnabled: true);

        var controller = CreateController(claimRuntimeId: runtime.Id);

        var result = await controller.Get(runtime.Id, CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var payload = ok.Value.Should().BeOfType<BootstrapMcpConfigResponse>().Subject;

        var entry = payload.Servers.Single();
        entry.BaseUrl.Should().Be(
            $"{TestScheme}://{TestHost}/api/mcp/kanban/v1",
            "BaseUrl is composed at request time from Request.Scheme + Request.Host + canonical /api/mcp/{name}/{version} path; the /api/ prefix is mandatory because the production Cloudflare tunnel forwards only /api/* to upstream");
    }

    [Fact]
    public async Task SeededKanbanEntry_ReturnedWhenNoOtherMcpsSeeded()
    {
        // Card 1's migration seeded a kanban server (Guid c748477b-..., Name=kanban,
        // Version=v1, DefaultEnabled=true). The in-memory provider doesn't run
        // migrations, so we materialise the same row ourselves with the
        // production Guid — what the daemon would see against a freshly migrated
        // production DB with no other MCPs registered yet.
        var runtime = await SeedRuntimeAsync();
        await SeedProductionKanbanAsync();

        var controller = CreateController(claimRuntimeId: runtime.Id);

        var result = await controller.Get(runtime.Id, CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var payload = ok.Value.Should().BeOfType<BootstrapMcpConfigResponse>().Subject;

        var kanban = payload.Servers.Should().ContainSingle(s => s.Name == "kanban").Subject;
        kanban.Version.Should().Be("v1");
        kanban.BaseUrl.Should().Be($"{TestScheme}://{TestHost}/api/mcp/kanban/v1");
    }

    [Fact]
    public async Task EmptyEnabledSet_Returns200_WithEmptyArray()
    {
        var runtime = await SeedRuntimeAsync();

        var controller = CreateController(claimRuntimeId: runtime.Id);

        var result = await controller.Get(runtime.Id, CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var payload = ok.Value.Should().BeOfType<BootstrapMcpConfigResponse>().Subject;
        payload.Servers.Should().NotBeNull();
        payload.Servers.Should().BeEmpty(
            "no enabled MCPs returns 200 with []; the daemon treats that as 'no MCPs to wire' rather than an error");
    }

    [Fact]
    public async Task MultipleEnabled_OrderedByName()
    {
        var runtime = await SeedRuntimeAsync();

        // Insert in non-alphabetical order to prove ordering is by Name asc.
        await SeedMcpServerAsync("zeta", "v1", defaultEnabled: true);
        await SeedMcpServerAsync("alpha", "v1", defaultEnabled: true);
        await SeedMcpServerAsync("middle", "v1", defaultEnabled: true);

        var controller = CreateController(claimRuntimeId: runtime.Id);

        var result = await controller.Get(runtime.Id, CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var payload = ok.Value.Should().BeOfType<BootstrapMcpConfigResponse>().Subject;

        payload.Servers.Select(s => s.Name)
            .Should().ContainInOrder("alpha", "middle", "zeta");
    }
}
