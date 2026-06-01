namespace Source.Infrastructure.Workspaces;

public static class WorkspaceServiceExtensions
{
    /// <summary>
    /// Registers the per-request <see cref="IWorkspaceContext"/>. The context is populated
    /// by <see cref="RequireWorkspaceRoleAttribute"/>; handlers consume it via constructor DI.
    /// </summary>
    public static IServiceCollection AddWorkspaceAuthorization(this IServiceCollection services)
    {
        services.AddScoped<IWorkspaceContext, WorkspaceContext>();
        return services;
    }
}
