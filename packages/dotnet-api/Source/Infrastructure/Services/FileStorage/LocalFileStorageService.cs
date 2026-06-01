namespace Source.Infrastructure.Services.FileStorage;

/// <summary>
/// Offline-first file storage using local filesystem
/// Perfect for development - no cloud dependencies!
/// </summary>
public class LocalFileStorageService : IFileStorageService
{
    private readonly string _basePath;
    private readonly ILogger<LocalFileStorageService> _logger;

    public LocalFileStorageService(IConfiguration configuration, ILogger<LocalFileStorageService> logger)
    {
        _basePath = configuration["FileStorage:LocalPath"] ?? Path.Combine(Directory.GetCurrentDirectory(), "uploads");
        _logger = logger;
        
        // Ensure uploads directory exists
        Directory.CreateDirectory(_basePath);
    }

    public async Task<string> SaveFileAsync(Stream fileStream, string fileName, string? folder = null, CancellationToken cancellationToken = default)
    {
        var safeFileName = GetSafeFileName(fileName);
        var relativePath = folder != null ? Path.Combine(folder, safeFileName) : safeFileName;
        var fullPath = Path.Combine(_basePath, relativePath);
        
        // Create directory if it doesn't exist
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _logger.LogInformation("💾 Saving file to local storage: {RelativePath}", relativePath);

        await using var fileOutput = File.Create(fullPath);
        await fileStream.CopyToAsync(fileOutput, cancellationToken);

        _logger.LogInformation("✅ File saved successfully: {RelativePath}", relativePath);
        return relativePath;
    }

    public Task<Stream> GetFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.Combine(_basePath, filePath);
        
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"File not found: {filePath}");
        }

        _logger.LogInformation("📂 Retrieved file from local storage: {FilePath}", filePath);
        return Task.FromResult<Stream>(File.OpenRead(fullPath));
    }

    public Task<bool> DeleteFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.Combine(_basePath, filePath);
        
        if (File.Exists(fullPath))
        {
            File.Delete(fullPath);
            _logger.LogInformation("🗑️ Deleted file from local storage: {FilePath}", filePath);
            return Task.FromResult(true);
        }

        _logger.LogWarning("⚠️ File not found for deletion: {FilePath}", filePath);
        return Task.FromResult(false);
    }

    public Task<bool> FileExistsAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.Combine(_basePath, filePath);
        return Task.FromResult(File.Exists(fullPath));
    }

    public Task<string> GetFileUrlAsync(string filePath, CancellationToken cancellationToken = default)
    {
        // For local development, return a local URL
        var url = $"/api/files/{filePath.Replace('\\', '/')}";
        return Task.FromResult(url);
    }

    public Task<string> GetPresignedPutUrlAsync(string key, string? contentType, TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        // Local dev has no presigning concept — return a local PUT-target URL
        // pointing at a (yet-to-exist) local upload sink. The chat-file-
        // attachments feature is meant for R2 in real use; the local
        // implementation is a stub so the rest of the system compiles and
        // unit-tests can run.
        var url = $"/api/files/local-put/{key.Replace('\\', '/')}";
        _logger.LogInformation("📎 Issued (stub) local presigned PUT URL for {Key}", key);
        return Task.FromResult(url);
    }

    public Task<string> GetPresignedGetUrlAsync(string key, TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        // Local dev: just hand back the static-files URL. There's no expiry
        // to enforce locally — the URL is "valid" for as long as the dev
        // process is running.
        var url = $"/api/files/{key.Replace('\\', '/')}";
        return Task.FromResult(url);
    }

    private static string GetSafeFileName(string fileName)
    {
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        var extension = Path.GetExtension(fileName);
        var nameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
        
        // Remove invalid characters
        var invalidChars = Path.GetInvalidFileNameChars();
        var safeName = string.Join("_", nameWithoutExtension.Split(invalidChars, StringSplitOptions.RemoveEmptyEntries));
        
        return $"{timestamp}_{safeName}{extension}";
    }
} 