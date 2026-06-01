using Source.Features.Workspaces.Services;

namespace Source.Features.Workspaces.Extensions;

public static class WorkspacesFeatureExtensions
{
    public static IServiceCollection AddWorkspacesFeature(this IServiceCollection services)
    {
        services.AddScoped<IWorkspaceSlugGenerator, WorkspaceSlugGenerator>();
        return services;
    }
}
