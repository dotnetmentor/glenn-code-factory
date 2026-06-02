namespace Source.Features.GitHub;

/// <summary>
/// Frontend SPA paths for workspace-scoped redirects issued by GitHub OAuth callbacks.
/// </summary>
public static class WorkspaceFrontendRoutes
{
    public static string Home(string slug) => $"/w/{slug}";

    public static string HomeWithQuery(string slug, string key, string value) =>
        $"/w/{slug}?{Uri.EscapeDataString(key)}={Uri.EscapeDataString(value)}";
}
