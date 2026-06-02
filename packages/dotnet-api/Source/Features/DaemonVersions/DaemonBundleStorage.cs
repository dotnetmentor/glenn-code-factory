namespace Source.Features.DaemonVersions;

/// <summary>
/// Shared constants and validation for daemon bundle objects in
/// <see cref="Source.Infrastructure.Services.FileStorage.IFileStorageService"/>.
/// </summary>
public static class DaemonBundleStorage
{
    public const string Folder = "daemon-bundles";

    public static string BuildStorageKey(string fileName) => $"{Folder}/{fileName}";

    /// <summary>
    /// Validates a single URL path segment (no directory separators). Rejects
    /// traversal attempts before any storage or DB lookup.
    /// </summary>
    public static bool IsSafeFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        if (fileName.Length > 256)
        {
            return false;
        }

        if (fileName.Contains('/', StringComparison.Ordinal)
            || fileName.Contains('\\', StringComparison.Ordinal)
            || fileName.Contains("..", StringComparison.Ordinal)
            || fileName.Contains('%', StringComparison.Ordinal))
        {
            return false;
        }

        if (!fileName.EndsWith(".tar.gz", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var c in fileName)
        {
            if (char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')
            {
                continue;
            }

            return false;
        }

        return true;
    }

    /// <summary>
    /// Ensures a DB-stored key points at exactly one file directly under
    /// <see cref="Folder"/> — no nested paths, no escape from the folder.
    /// </summary>
    public static bool IsAllowedStorageKey(string? storageKey)
    {
        if (string.IsNullOrWhiteSpace(storageKey))
        {
            return false;
        }

        if (storageKey.Contains("..", StringComparison.Ordinal)
            || storageKey.Contains('\\', StringComparison.Ordinal))
        {
            return false;
        }

        var slash = storageKey.IndexOf('/');
        if (slash <= 0 || slash >= storageKey.Length - 1)
        {
            return false;
        }

        if (storageKey.IndexOf('/', slash + 1) >= 0)
        {
            return false;
        }

        var folder = storageKey[..slash];
        var fileName = storageKey[(slash + 1)..];

        return string.Equals(folder, Folder, StringComparison.Ordinal)
               && IsSafeFileName(fileName);
    }
}
