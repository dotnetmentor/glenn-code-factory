namespace Source.Infrastructure.Services.FileStorage;

/// <summary>
/// File storage service abstraction
/// </summary>
public interface IFileStorageService
{
    Task<string> SaveFileAsync(Stream fileStream, string fileName, string? folder = null, CancellationToken cancellationToken = default);
    Task<Stream> GetFileAsync(string filePath, CancellationToken cancellationToken = default);
    Task<bool> DeleteFileAsync(string filePath, CancellationToken cancellationToken = default);
    Task<bool> FileExistsAsync(string filePath, CancellationToken cancellationToken = default);
    Task<string> GetFileUrlAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Generate a short-lived presigned URL the browser can PUT bytes to
    /// directly — backend never proxies the body. Used by the chat-file-
    /// attachments flow. The returned URL is valid for <paramref name="ttl"/>;
    /// recommended 15 minutes for uploads.
    /// </summary>
    /// <param name="key">Object key inside the bucket / under the local
    /// storage root. For R2 this is the S3 key; for local storage it is the
    /// relative path under the configured base path.</param>
    /// <param name="contentType">Optional content type to bind into the
    /// signature (browsers must PUT with the same <c>Content-Type</c> header).
    /// Null means the URL accepts any content type.</param>
    /// <param name="ttl">How long the URL is valid for.</param>
    Task<string> GetPresignedPutUrlAsync(string key, string? contentType, TimeSpan ttl, CancellationToken cancellationToken = default);

    /// <summary>
    /// Generate a short-lived presigned URL the browser can GET / download
    /// directly. Used by past-message attachment chips so users can re-open
    /// files months after sending. Recommended 24 hour TTL.
    /// </summary>
    /// <param name="key">Object key (same convention as
    /// <see cref="GetPresignedPutUrlAsync"/>).</param>
    /// <param name="ttl">How long the URL is valid for.</param>
    Task<string> GetPresignedGetUrlAsync(string key, TimeSpan ttl, CancellationToken cancellationToken = default);
}

/// <summary>
/// File storage result model
/// </summary>
public record FileStorageResult(
    string FilePath,
    string FileName,
    long FileSize,
    string ContentType
); 