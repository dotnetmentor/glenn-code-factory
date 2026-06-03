namespace Source.Features.GitHub;

/// <summary>
/// Frontend SPA paths for workspace-scoped redirects issued by GitHub OAuth callbacks.
/// </summary>
public static class WorkspaceFrontendRoutes
{
    public static string Home(string slug) => $"/w/{slug}";

    public static string HomeWithQuery(string slug, string key, string value) =>
        $"/w/{slug}?{Uri.EscapeDataString(key)}={Uri.EscapeDataString(value)}";

    /// <summary>
    /// Build a workspace-home redirect with several query params. Null/empty
    /// values are skipped so callers can pass optional detail (e.g. the
    /// conflicting account login) without emitting empty params.
    /// </summary>
    public static string HomeWithQuery(string slug, IEnumerable<KeyValuePair<string, string?>> queryParams)
    {
        var query = string.Join(
            "&",
            queryParams
                .Where(kv => !string.IsNullOrEmpty(kv.Value))
                .Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value!)}"));
        return string.IsNullOrEmpty(query) ? $"/w/{slug}" : $"/w/{slug}?{query}";
    }
}
