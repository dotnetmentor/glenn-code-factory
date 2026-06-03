# File Upload - Complete Reference

## Overview
Whenever the user needs to upload or download files, use this skill.
Switchable file storage abstraction that works with:
- **Cloudflare R2** (production) - S3-compatible cloud storage
- **Local filesystem** (development) - No cloud dependencies

The system switches automatically based on configuration.

---

## Quick Start: Secrets

**Secrets are hardcoded in `appsettings.json` for quick bootstrapping:**

```json
{
  "FileStorage": {
    "Provider": "R2",  // or "Local"
    "LocalPath": "uploads",
    "R2": {
      "AccountId": "your-account-id",
      "AccessKey": "your-access-key",
      "SecretKey": "your-secret-key",
      "BucketName": "your-bucket",
      "PublicUrl": "https://your-bucket.your-domain.com"
    }
  }
}
```

---

## 1. Service Interface

```csharp
public interface IFileStorageService
{
    Task<string> SaveFileAsync(Stream fileStream, string fileName, string? folder = null, CancellationToken ct = default);
    Task<Stream> GetFileAsync(string filePath, CancellationToken ct = default);
    Task<bool> DeleteFileAsync(string filePath, CancellationToken ct = default);
    Task<bool> FileExistsAsync(string filePath, CancellationToken ct = default);
    Task<string> GetFileUrlAsync(string filePath, CancellationToken ct = default);
}
```

---

## 2. Backend: Upload Command

```csharp
using Source.Infrastructure.Services.FileStorage;
using Source.Shared.CQRS;
using Source.Shared.Results;

public record UploadFileCommand(
    Stream FileStream,
    string FileName,
    string? Folder
) : ICommand<Result<UploadFileResponse>>;

public record UploadFileResponse
{
    public required string FileUrl { get; init; }
    public required string FileName { get; init; }
    public required long FileSize { get; init; }
}

public class UploadFileCommandHandler : ICommandHandler<UploadFileCommand, Result<UploadFileResponse>>
{
    private readonly IFileStorageService _fileStorage;

    public UploadFileCommandHandler(IFileStorageService fileStorage)
    {
        _fileStorage = fileStorage;
    }

    public async Task<Result<UploadFileResponse>> Handle(UploadFileCommand request, CancellationToken ct)
    {
        var fileSize = request.FileStream.Length;

        // Upload to R2 or Local based on config
        var filePath = await _fileStorage.SaveFileAsync(
            request.FileStream,
            request.FileName,
            request.Folder ?? "uploads",
            ct);

        var fileUrl = await _fileStorage.GetFileUrlAsync(filePath, ct);

        return Result.Success(new UploadFileResponse
        {
            FileUrl = fileUrl,
            FileName = request.FileName,
            FileSize = fileSize
        });
    }
}
```

---

## 3. Backend: Controller Endpoint

```csharp
[ApiController]
[Route("api/files")]
[Authorize]
public class FilesController : ControllerBase
{
    private readonly IMediator _mediator;

    [HttpPost("upload")]
    [RequestSizeLimit(10 * 1024 * 1024)] // 10MB
    public async Task<ActionResult<UploadFileResponse>> Upload(
        IFormFile file,
        [FromQuery] string? folder = null)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new { error = "No file provided" });

        using var stream = file.OpenReadStream();
        var command = new UploadFileCommand(stream, file.FileName, folder);
        var result = await _mediator.Send(command);

        if (!result.IsSuccess)
            return BadRequest(new { error = result.Error });

        return Ok(result.Value);
    }
}
```

---

## 4. Frontend: Upload Component

```tsx
import { usePostApiFilesUpload } from '../api/queries-commands'

function FileUpload({ onUploaded }: { onUploaded: (url: string) => void }) {
  const uploadMutation = usePostApiFilesUpload()

  const handleFileSelect = async (e: React.ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0]
    if (!file) return

    const formData = new FormData()
    formData.append('file', file)

    uploadMutation.mutate(
      { data: formData },
      {
        onSuccess: (response) => onUploaded(response.fileUrl),
      }
    )
  }

  return (
    <div>
      <input type="file" onChange={handleFileSelect} />
      {uploadMutation.isPending && <span>Uploading...</span>}
    </div>
  )
}
```

---

## 5. Frontend: Image Upload with Preview

```tsx
function ImageUpload({ onUpload, currentUrl }: { onUpload: (url: string) => void; currentUrl?: string }) {
  const [preview, setPreview] = useState<string | null>(currentUrl ?? null)
  const [isUploading, setIsUploading] = useState(false)

  const handleSelect = async (e: React.ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0]
    if (!file) return

    // Show preview immediately
    const reader = new FileReader()
    reader.onload = () => setPreview(reader.result as string)
    reader.readAsDataURL(file)

    // Upload
    setIsUploading(true)
    const formData = new FormData()
    formData.append('file', file)

    const response = await fetch('/api/files/upload?folder=images', {
      method: 'POST',
      body: formData,
      credentials: 'include',
    })

    const data = await response.json()
    onUpload(data.fileUrl)
    setIsUploading(false)
  }

  return (
    <div>
      {preview && <img src={preview} alt="Preview" className="w-32 h-32 object-cover" />}
      <input type="file" accept="image/*" onChange={handleSelect} />
      {isUploading && <span>Uploading...</span>}
    </div>
  )
}
```

---

## 6. Tenant-Isolated Uploads

```csharp
public class TenantUploadHandler : ICommandHandler<UploadTenantFileCommand, Result<UploadFileResponse>>
{
    private readonly IFileStorageService _fileStorage;
    private readonly ITenantContext _tenantContext;

    public async Task<Result<UploadFileResponse>> Handle(UploadTenantFileCommand request, CancellationToken ct)
    {
        // Organize by tenant
        var folder = $"tenants/{_tenantContext.TenantId}/{request.Folder}";

        var filePath = await _fileStorage.SaveFileAsync(
            request.FileStream,
            request.FileName,
            folder,
            ct);

        // ...
    }
}
```

---

## 7. File Validation

```csharp
private static readonly HashSet<string> AllowedImages = new(StringComparer.OrdinalIgnoreCase)
{
    ".jpg", ".jpeg", ".png", ".gif", ".webp"
};

public Result ValidateImageFile(IFormFile file)
{
    var ext = Path.GetExtension(file.FileName);
    if (!AllowedImages.Contains(ext))
        return Result.Failure($"File type {ext} not allowed");

    if (file.Length > 5 * 1024 * 1024) // 5MB
        return Result.Failure("File too large (max 5MB)");

    return Result.Success();
}
```

---

## 8. Local Development

For local dev, files are saved to `./uploads/` and served via:

```csharp
// In Program.cs
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(
        Path.Combine(Directory.GetCurrentDirectory(), "uploads")),
    RequestPath = "/api/files"
});
```

---

## 9. R2 Setup in Cloudflare

1. Create bucket in Cloudflare R2
2. Create API token with R2 read/write permissions
3. (Optional) Add custom domain for public URLs
4. Update `appsettings.json` with credentials

---

## 10. Troubleshooting

### Large uploads timeout
```csharp
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 50 * 1024 * 1024; // 50MB
});
```

### R2 "Access Denied"
Check bucket permissions and CORS in Cloudflare dashboard.

### Local files not accessible
Ensure static files middleware is configured with correct path.
