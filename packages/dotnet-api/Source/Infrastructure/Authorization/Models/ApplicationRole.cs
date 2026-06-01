namespace Source.Infrastructure.AuthorizationModels;

public enum ApplicationRole
{
    SuperAdmin,
    TenantAdmin,
    /// <summary>
    /// Default role granted to every user on signup. Gates access to the
    /// `/w/:slug/*` workspace app on the frontend. Workspace-level RBAC
    /// (Owner/Admin/Member) is layered on top via WorkspaceMembership.
    /// </summary>
    WorkspaceUser,
}

public static class ApplicationRoleExtensions
{
    public static string ToRoleName(this ApplicationRole role)
    {
        return role.ToString();
    }

    public static ApplicationRole? FromRoleName(string roleName)
    {
        return Enum.TryParse<ApplicationRole>(roleName, true, out var role) ? role : null;
    }

    public static IEnumerable<ApplicationRole> GetAllRoles()
    {
        return Enum.GetValues<ApplicationRole>();
    }

    public static bool HasSuperAdminPrivileges(this ApplicationRole role)
    {
        return role is ApplicationRole.SuperAdmin;
    }
}
