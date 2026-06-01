namespace Source.Features.Workspaces.Services;

/// <summary>
/// Generates a URL-safe, globally-unique kebab slug for a new workspace from a free-form seed
/// (typically the user's email local-part, GitHub login, or requested workspace name).
/// </summary>
public interface IWorkspaceSlugGenerator
{
    /// <summary>
    /// Sanitises the seed and returns a slug that is not already taken in the database.
    /// On collision, appends <c>-2</c>, <c>-3</c>, … until uniqueness is established.
    /// Falls back to <c>"workspace"</c> if the seed sanitises to empty.
    /// </summary>
    Task<string> GenerateAsync(string seed, CancellationToken cancellationToken = default);
}
