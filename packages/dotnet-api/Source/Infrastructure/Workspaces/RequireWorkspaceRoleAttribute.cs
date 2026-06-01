using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using Source.Features.Workspaces.Models;
using Source.Infrastructure.AuthorizationModels;

namespace Source.Infrastructure.Workspaces;

/// <summary>
/// One uniform way to gate a workspace-scoped endpoint. Apply at controller or action level.
/// The filter:
///   1. requires authentication (401 otherwise);
///   2. looks up the workspace by the route value <c>slug</c> — 404 if missing;
///   3. verifies the calling user has a <see cref="WorkspaceMembership"/> meeting the requested
///      minimum role (Owner ⊃ Admin ⊃ Member) — 403 if not;
///   4. populates the request-scoped <see cref="IWorkspaceContext"/> so handlers receive it via DI.
/// SuperAdmin users bypass the membership check entirely; the context is populated with
/// <see cref="IWorkspaceContext.IsSuperAdmin"/> == true and <see cref="IWorkspaceContext.Role"/> == Owner.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class RequireWorkspaceRoleAttribute : Attribute, IAsyncAuthorizationFilter
{
    private readonly WorkspaceRole _minimum;

    public RequireWorkspaceRoleAttribute(WorkspaceRole minimum = WorkspaceRole.Member)
    {
        _minimum = minimum;
    }

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var http = context.HttpContext;

        // 1. Authentication
        var userId = http.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId) || http.User.Identity?.IsAuthenticated != true)
        {
            context.Result = new UnauthorizedObjectResult(new { error = "Authentication required" });
            return;
        }

        // 2. Slug from route
        if (!context.RouteData.Values.TryGetValue("slug", out var slugObj) || slugObj is not string slug || string.IsNullOrWhiteSpace(slug))
        {
            // Route is misconfigured — the attribute requires a {slug} route segment.
            context.Result = new ObjectResult(new { error = "Workspace slug missing from route" }) { StatusCode = 500 };
            return;
        }

        var services = http.RequestServices;
        var db = services.GetRequiredService<ApplicationDbContext>();

        var workspace = await db.Workspaces.AsNoTracking().SingleOrDefaultAsync(w => w.Slug == slug);
        if (workspace is null)
        {
            context.Result = new NotFoundObjectResult(new { error = $"Workspace '{slug}' not found" });
            return;
        }

        var isSuperAdmin = http.User.IsInRole(RoleConstants.SuperAdmin);

        WorkspaceRole effectiveRole;
        if (isSuperAdmin)
        {
            effectiveRole = WorkspaceRole.Owner;
        }
        else
        {
            var membership = await db.WorkspaceMemberships
                .AsNoTracking()
                .SingleOrDefaultAsync(m => m.WorkspaceId == workspace.Id && m.UserId == userId);

            if (membership is null)
            {
                context.Result = new ObjectResult(new { error = "You are not a member of this workspace" })
                {
                    StatusCode = 403,
                };
                return;
            }

            if (!membership.Role.IsAtLeast(_minimum))
            {
                context.Result = new ObjectResult(new
                {
                    error = $"This action requires the {_minimum} role; you have {membership.Role}",
                })
                { StatusCode = 403 };
                return;
            }

            effectiveRole = membership.Role;
        }

        // 4. Populate request-scoped context.
        var ctx = services.GetRequiredService<IWorkspaceContext>();
        if (ctx is WorkspaceContext mutable)
        {
            mutable.Set(workspace.Id, workspace.Slug, effectiveRole, isSuperAdmin, userId);
        }
    }
}
