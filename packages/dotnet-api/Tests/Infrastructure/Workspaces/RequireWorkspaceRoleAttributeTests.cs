using System.Security.Claims;
using Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Source.Features.Users.Models;
using Source.Features.Workspaces.Models;
using Source.Infrastructure;
using Source.Infrastructure.AuthorizationModels;
using Source.Infrastructure.Workspaces;

namespace Api.Tests.Infrastructure.Workspaces;

/// <summary>
/// Direct unit tests for the workspace authorization filter. We exercise the filter without
/// MVC routing by constructing an <see cref="AuthorizationFilterContext"/> by hand and
/// providing a per-test <see cref="ServiceProvider"/> with an InMemory DbContext + scoped
/// <see cref="IWorkspaceContext"/>.
///
/// The filter is the single chokepoint for all workspace authorization, so we verify all six
/// outcomes here:
///   - 401 when no authenticated user
///   - 404 when the workspace doesn't exist
///   - 403 when the user has no membership
///   - 403 when membership role is below the requested minimum
///   - 200 (no result set) when authorized — context populated
///   - SuperAdmin bypasses membership entirely with Owner-equivalent context
/// </summary>
public class RequireWorkspaceRoleAttributeTests : IDisposable
{
    private readonly ApplicationDbContext _db;
    private readonly ServiceProvider _services;

    public RequireWorkspaceRoleAttributeTests()
    {
        _db = TestDbContextFactory.Create();

        var collection = new ServiceCollection();
        collection.AddSingleton(_db);
        collection.AddScoped<IWorkspaceContext, WorkspaceContext>();
        _services = collection.BuildServiceProvider();
    }

    [Fact]
    public async Task Returns_401_when_user_not_authenticated()
    {
        var ctx = await RunFilter("any-slug", user: null, minimum: WorkspaceRole.Member);
        ctx.Result.Should().BeOfType<UnauthorizedObjectResult>();
    }

    [Fact]
    public async Task Returns_404_when_workspace_slug_does_not_exist()
    {
        var user = NewUser("u1");

        var ctx = await RunFilter("ghost", user, WorkspaceRole.Member);
        ctx.Result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task Returns_403_when_user_is_not_a_member()
    {
        await SeedWorkspace("acme", ownerId: "owner");
        var user = NewUser("stranger");

        var ctx = await RunFilter("acme", user, WorkspaceRole.Member);
        var obj = ctx.Result.Should().BeOfType<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(403);
    }

    [Fact]
    public async Task Returns_403_when_member_below_minimum_role()
    {
        var ws = await SeedWorkspace("acme", ownerId: "owner");
        var user = NewUser("alice");
        await AddMember(ws.Id, user.UserId, WorkspaceRole.Member);

        var ctx = await RunFilter("acme", user, WorkspaceRole.Admin);
        var obj = ctx.Result.Should().BeOfType<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(403);
    }

    [Fact]
    public async Task Returns_200_and_populates_context_when_member_meets_minimum()
    {
        var ws = await SeedWorkspace("acme", ownerId: "owner");
        var user = NewUser("admin-1");
        await AddMember(ws.Id, user.UserId, WorkspaceRole.Admin);

        var ctx = await RunFilter("acme", user, WorkspaceRole.Member);
        ctx.Result.Should().BeNull("authorized requests pass through with no Result set");

        var wsCtx = ctx.HttpContext.RequestServices.GetRequiredService<IWorkspaceContext>();
        wsCtx.IsResolved.Should().BeTrue();
        wsCtx.Id.Should().Be(ws.Id);
        wsCtx.Slug.Should().Be("acme");
        wsCtx.Role.Should().Be(WorkspaceRole.Admin);
        wsCtx.IsSuperAdmin.Should().BeFalse();
        wsCtx.UserId.Should().Be(user.UserId);
    }

    [Fact]
    public async Task SuperAdmin_bypasses_membership_check()
    {
        await SeedWorkspace("acme", ownerId: "owner");
        var user = NewUser("admin-x", roles: [RoleConstants.SuperAdmin]);

        var ctx = await RunFilter("acme", user, WorkspaceRole.Owner);
        ctx.Result.Should().BeNull();

        var wsCtx = ctx.HttpContext.RequestServices.GetRequiredService<IWorkspaceContext>();
        wsCtx.IsSuperAdmin.Should().BeTrue();
        wsCtx.Role.Should().Be(WorkspaceRole.Owner, "super admins act as Owner-equivalent");
    }

    [Fact]
    public async Task Returns_500_when_route_has_no_slug_value()
    {
        var user = NewUser("u1");
        var http = BuildHttpContext(user);
        // Note: no "slug" route value
        var routeData = new RouteData();
        var actionDescriptor = new ActionDescriptor();
        var filterContext = new AuthorizationFilterContext(
            new ActionContext(http, routeData, actionDescriptor),
            new List<IFilterMetadata>());

        var attr = new RequireWorkspaceRoleAttribute(WorkspaceRole.Member);
        await attr.OnAuthorizationAsync(filterContext);

        var obj = filterContext.Result.Should().BeOfType<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(500);
    }

    // ---- helpers ----

    private async Task<Workspace> SeedWorkspace(string slug, string ownerId)
    {
        if (await _db.Users.FindAsync(ownerId) is null)
        {
            _db.Users.Add(new User { Id = ownerId, UserName = $"{ownerId}@x.com", Email = $"{ownerId}@x.com" });
        }

        var ws = new Workspace { Id = Guid.NewGuid(), Slug = slug, Name = slug, OwnerId = ownerId };
        _db.Workspaces.Add(ws);
        _db.WorkspaceMemberships.Add(new WorkspaceMembership
        {
            Id = Guid.NewGuid(),
            WorkspaceId = ws.Id,
            UserId = ownerId,
            Role = WorkspaceRole.Owner,
        });
        await _db.SaveChangesAsync();
        return ws;
    }

    private async Task AddMember(Guid workspaceId, string userId, WorkspaceRole role)
    {
        if (await _db.Users.FindAsync(userId) is null)
        {
            _db.Users.Add(new User { Id = userId, UserName = $"{userId}@x.com", Email = $"{userId}@x.com" });
        }
        _db.WorkspaceMemberships.Add(new WorkspaceMembership
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            UserId = userId,
            Role = role,
        });
        await _db.SaveChangesAsync();
    }

    private record TestUser(string UserId, ClaimsPrincipal Principal);

    private static TestUser NewUser(string id, params string[] roles)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, id),
            new(ClaimTypes.Email, $"{id}@x.com"),
        };
        foreach (var role in roles)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }
        var identity = new ClaimsIdentity(claims, authenticationType: "TestScheme");
        return new TestUser(id, new ClaimsPrincipal(identity));
    }

    private async Task<AuthorizationFilterContext> RunFilter(string slug, TestUser? user, WorkspaceRole minimum)
    {
        var http = BuildHttpContext(user);
        var routeData = new RouteData();
        routeData.Values["slug"] = slug;
        var actionDescriptor = new ActionDescriptor();
        var filterContext = new AuthorizationFilterContext(
            new ActionContext(http, routeData, actionDescriptor),
            new List<IFilterMetadata>());

        var attr = new RequireWorkspaceRoleAttribute(minimum);
        await attr.OnAuthorizationAsync(filterContext);
        return filterContext;
    }

    private DefaultHttpContext BuildHttpContext(TestUser? user)
    {
        var scope = _services.CreateScope();
        var http = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
            User = user?.Principal ?? new ClaimsPrincipal(new ClaimsIdentity()),
        };
        return http;
    }

    public void Dispose()
    {
        _services.Dispose();
        _db.Dispose();
        GC.SuppressFinalize(this);
    }
}
