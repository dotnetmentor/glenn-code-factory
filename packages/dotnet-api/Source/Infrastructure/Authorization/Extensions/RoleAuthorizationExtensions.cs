using Microsoft.AspNetCore.Identity;
using Source.Infrastructure.AuthorizationModels;
using Source.Features.Users.Models;
using System.Security.Claims;

namespace Source.Infrastructure.AuthorizationExtensions;

public static class RoleAuthorizationExtensions
{
    public static async Task<bool> HasRoleAsync(this UserManager<User> userManager, User user, ApplicationRole role)
    {
        return await userManager.IsInRoleAsync(user, role.ToRoleName());
    }

    public static async Task<bool> HasSuperAdminPrivilegesAsync(this UserManager<User> userManager, User user)
    {
        return await userManager.HasRoleAsync(user, ApplicationRole.SuperAdmin);
    }

    public static async Task<List<ApplicationRole>> GetApplicationRolesAsync(this UserManager<User> userManager, User user)
    {
        var roleNames = await userManager.GetRolesAsync(user);
        var applicationRoles = new List<ApplicationRole>();

        foreach (var roleName in roleNames)
        {
            var role = ApplicationRoleExtensions.FromRoleName(roleName);
            if (role.HasValue)
            {
                applicationRoles.Add(role.Value);
            }
        }

        return applicationRoles;
    }

    public static bool HasRole(this ClaimsPrincipal principal, ApplicationRole role)
    {
        return principal.IsInRole(role.ToRoleName());
    }

    public static bool HasSuperAdminPrivileges(this ClaimsPrincipal principal)
    {
        return principal.HasRole(ApplicationRole.SuperAdmin);
    }
}
