using Amazon.S3;
using Amazon.S3.Model;
using Amazon;
using Amazon.Runtime;

namespace Source.Infrastructure.Services.FileStorage;

/// <summary>
/// Cloudflare R2 storage service using AWS S3 SDK compatibility
/// Production-ready cloud storage with real file persistence
/// </summary>
public class CloudflareR2StorageService : IFileStorageService, IDisposable
{
    private readonly AmazonS3Client _s3Client;
    private readonly string _bucketName;
    private readonly string _publicUrl;
    private readonly ILogger<CloudflareR2StorageService> _logger;

    public CloudflareR2StorageService(IConfiguration configuration, ILogger<CloudflareR2StorageService> logger)
    {
        _logger = logger;
        
        // Get R2 configuration
        var accountId = configuration["FileStorage:R2:AccountId"] 
            ?? throw new InvalidOperationException("R2 AccountId is required");
        var accessKey = configuration["FileStorage:R2:AccessKey"] 
            ?? throw new InvalidOperationException("R2 AccessKey is required");
        var secretKey = configuration["FileStorage:R2:SecretKey"] 
            ?? throw new InvalidOperationException("R2 SecretKey is required");
        _bucketName = configuration["FileStorage:R2:BucketName"] 
            ?? throw new InvalidOperationException("R2 BucketName is required");
        _publicUrl = configuration["FileStorage:R2:PublicUrl"] ?? "";

        // Configure R2 client with AWS S3 SDK
        var credentials = new BasicAWSCredentials(accessKey, secretKey);
        var config = new AmazonS3Config
        {
            ServiceURL = $"https://{accountId}.r2.cloudflarestorage.com",
            ForcePathStyle = false
        };

        _s3Client = new AmazonS3Client(credentials, config);
        
        _logger.LogInformation("🌥️ Cloudflare R2 storage service initialized for bucket: {BucketName}", _bucketName);
    }

    public async Task<string> SaveFileAsync(Stream fileStream, string fileName, string? folder = null, CancellationToken cancellationToken = default)
    {
        var safeFileName = GetSafeFileName(fileName);
        var key = folder != null ? $"{folder}/{safeFileName}" : safeFileName;

        _logger.LogInformation("☁️ Uploading file to R2: {Key}", key);

        var request = new PutObjectRequest
        {
            BucketName = _bucketName,
            Key = key,
            InputStream = fileStream,
            ContentType = GetContentType(fileName),
            // R2 compatibility requirements
            DisablePayloadSigning = true,
            DisableDefaultChecksumValidation = true
        };

        try
        {
            var response = await _s3Client.PutObjectAsync(request, cancellationToken);
            _logger.LogInformation("✅ File uploaded successfully to R2: {Key} (ETag: {ETag})", key, response.ETag);
            return key;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Failed to upload file to R2: {Key}", key);
            throw;
        }
    }

    public async Task<Stream> GetFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("📥 Downloading file from R2: {FilePath}", filePath);

        var request = new GetObjectRequest
        {
            BucketName = _bucketName,
            Key = filePath
        };

        try
        {
            var response = await _s3Client.GetObjectAsync(request, cancellationToken);
            _logger.LogInformation("✅ File downloaded successfully from R2: {FilePath}", filePath);
            return response.ResponseStream;
        }
        catch (AmazonS3Exception ex) when (ex.ErrorCode == "NoSuchKey")
        {
            _logger.LogWarning("⚠️ File not found in R2: {FilePath}", filePath);
            throw new FileNotFoundException($"File not found in R2: {filePath}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Failed to download file from R2: {FilePath}", filePath);
            throw;
        }
    }

    public async Task<bool> DeleteFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("🗑️ Deleting file from R2: {FilePath}", filePath);

        var request = new DeleteObjectRequest
        {
            BucketName = _bucketName,
            Key = filePath
        };

        try
        {
            await _s3Client.DeleteObjectAsync(request, cancellationToken);
            _logger.LogInformation("✅ File deleted successfully from R2: {FilePath}", filePath);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Failed to delete file from R2: {FilePath}", filePath);
            return false;
        }
    }

    public async Task<bool> FileExistsAsync(string filePath, CancellationToken cancellationToken = default)
    {
        try
        {
            var request = new GetObjectMetadataRequest
            {
                BucketName = _bucketName,
                Key = filePath
            };

            await _s3Client.GetObjectMetadataAsync(request, cancellationToken);
            return true;
        }
        catch (AmazonS3Exception ex) when (ex.ErrorCode == "NotFound")
        {
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Error checking file existence in R2: {FilePath}", filePath);
            return false;
        }
    }

    public Task<string> GetFileUrlAsync(string filePath, CancellationToken cancellationToken = default)
    {
        // If public URL is configured, use it; otherwise generate a presigned URL
        if (!string.IsNullOrEmpty(_publicUrl))
        {
            var url = $"{_publicUrl.TrimEnd('/')}/{filePath}";
            return Task.FromResult(url);
        }

        // Generate presigned URL for private buckets (valid for 1 hour)
        var request = new GetPreSignedUrlRequest
        {
            BucketName = _bucketName,
            Key = filePath,
            Verb = HttpVerb.GET,
            Expires = DateTime.UtcNow.AddHours(1)
        };

        var presignedUrl = _s3Client.GetPreSignedURL(request);
        return Task.FromResult(presignedUrl);
    }

    public Task<string> GetPresignedPutUrlAsync(string key, string? contentType, TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        // Browser PUTs the bytes directly to this URL. R2 is S3-compatible so
        // the standard GetPreSignedURL works here; we bind ContentType into
        // the signature when supplied so the browser must send the matching
        // header (defence-in-depth — prevents a tampered client from
        // uploading a different MIME under the same key).
        var request = new GetPreSignedUrlRequest
        {
            BucketName = _bucketName,
            Key = key,
            Verb = HttpVerb.PUT,
            Expires = DateTime.UtcNow.Add(ttl),
        };

        if (!string.IsNullOrWhiteSpace(contentType))
        {
            request.ContentType = contentType;
        }

        var url = _s3Client.GetPreSignedURL(request);
        _logger.LogInformation("🔗 Generated presigned PUT URL for R2 key {Key} (ttl {Ttl})", key, ttl);
        return Task.FromResult(url);
    }

    public Task<string> GetPresignedGetUrlAsync(string key, TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        // Always emit a presigned URL — even if a public CDN URL is configured
        // we want the short-lived signed variant so old links eventually expire
        // (matches how the chat-file-attachments past-message chips work: the
        // backend re-issues a fresh signed URL on each GET /api/attachments/{id}).
        var request = new GetPreSignedUrlRequest
        {
            BucketName = _bucketName,
            Key = key,
            Verb = HttpVerb.GET,
            Expires = DateTime.UtcNow.Add(ttl),
        };

        var url = _s3Client.GetPreSignedURL(request);
        _logger.LogInformation("🔗 Generated presigned GET URL for R2 key {Key} (ttl {Ttl})", key, ttl);
        return Task.FromResult(url);
    }

    private static string GetSafeFileName(string fileName)
    {
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        var extension = Path.GetExtension(fileName);
        var nameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
        
        // Remove invalid characters for S3 keys
        var safeName = string.Join("_", nameWithoutExtension.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        
        return $"{timestamp}_{safeName}{extension}";
    }

    private static string GetContentType(string fileName)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        return extension switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".svg" => "image/svg+xml",
            ".pdf" => "application/pdf",
            ".txt" => "text/plain",
            ".json" => "application/json",
            ".xml" => "application/xml",
            ".zip" => "application/zip",
            _ => "application/octet-stream"
        };
    }

    public void Dispose()
    {
        _s3Client?.Dispose();
    }
} 