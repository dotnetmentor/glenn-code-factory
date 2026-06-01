namespace Source.Features.Workspaces.Models;

/// <summary>
/// Workspace-scoped RBAC roles. Lower numeric value = higher privilege.
/// Comparisons go through <see cref="WorkspaceRoleExtensions.IsAtLeast"/>.
/// </summary>
public enum WorkspaceRole
{
    Owner = 0,
    Admin = 1,
    Member = 2,
}

public static class WorkspaceRoleExtensions
{
    /// <summary>
    /// Returns true if <paramref name="actual"/> grants at least the privileges of <paramref name="required"/>.
    /// Owner ⊇ Admin ⊇ Member, so e.g. Admin.IsAtLeast(Member) == true and Member.IsAtLeast(Admin) == false.
    /// </summary>
    public static bool IsAtLeast(this WorkspaceRole actual, WorkspaceRole required)
    {
        return (int)actual <= (int)required;
    }
}
