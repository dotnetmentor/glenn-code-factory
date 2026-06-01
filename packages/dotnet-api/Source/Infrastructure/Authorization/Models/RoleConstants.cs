namespace Source.Infrastructure.AuthorizationModels;

public static class RoleConstants
{
    public const string SuperAdmin = nameof(ApplicationRole.SuperAdmin);
    public const string TenantAdmin = nameof(ApplicationRole.TenantAdmin);
    public const string WorkspaceUser = nameof(ApplicationRole.WorkspaceUser);

    // Combined for endpoints accessible by both
    public const string AdminRoles = $"{SuperAdmin},{TenantAdmin}";

    public static readonly string[] AllRoles = [SuperAdmin, TenantAdmin, WorkspaceUser];
}
