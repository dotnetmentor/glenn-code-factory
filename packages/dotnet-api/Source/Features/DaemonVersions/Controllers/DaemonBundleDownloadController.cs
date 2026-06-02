using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Source.Infrastructure;
using Source.Infrastructure.Services.FileStorage;

namespace Source.Features.DaemonVersions.Controllers;

/// <summary>
/// Public download surface for daemon bundles when
/// <see cref="Source.Infrastructure.Services.FileStorage.LocalFileStorageService"/>
/// resolves URLs to <c>/api/files/daemon-bundles/…</c>. Fly runtimes fetch the
/// tarball on cold-boot before they hold a runtime token — same auth model as
/// <see cref="DaemonVersionsController.Resolve"/>.
///
/// <para><b>Security.</b> Three layers: (1) route captures a single filename
/// segment only — no <c>/</c> in the path; (2)
/// <see cref="DaemonBundleStorage.IsSafeFileName"/> rejects traversal and odd
/// characters; (3) the file is served only when
/// <see cref="Models.DaemonVersion.BundleStorageKey"/> is registered in the DB
/// and passes <see cref="DaemonBundleStorage.IsAllowedStorageKey"/>. Storage
/// reads use the DB key, never a user-composed path.</para>
/// </summary>
[ApiController]
[Route("api/files/daemon-bundles")]
[AllowAnonymous]
[EnableRateLimiting("GeneralPolicy")]
[Tags("DaemonVersions")]
public class DaemonBundleDownloadController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    private readonly IFileStorageService _fileStorage;
    private readonly ILogger<DaemonBundleDownloadController> _logger;

    public DaemonBundleDownloadController(
        ApplicationDbContext db,
        IFileStorageService fileStorage,
        ILogger<DaemonBundleDownloadController> logger)
    {
        _db = db;
        _fileStorage = fileStorage;
        _logger = logger;
    }

    /// <summary>
    /// Download a published daemon bundle by filename. Anonymous — runtimes
    /// need this before bootstrap auth exists. Returns 404 for unknown,
    /// invalid, or unregistered names (same response for all — no enumeration).
    /// </summary>
    [HttpGet("{fileName}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Download(string fileName, CancellationToken ct)
    {
        if (!DaemonBundleStorage.IsSafeFileName(fileName))
        {
            return NotFound();
        }

        var storageKey = DaemonBundleStorage.BuildStorageKey(fileName);

        var version = await _db.DaemonVersions
            .AsNoTracking()
            .FirstOrDefaultAsync(v => v.BundleStorageKey == storageKey, ct);

        if (version is null || !DaemonBundleStorage.IsAllowedStorageKey(version.BundleStorageKey))
        {
            return NotFound();
        }

        Stream stream;
        try
        {
            stream = await _fileStorage.GetFileAsync(version.BundleStorageKey, ct);
        }
        catch (FileNotFoundException)
        {
            _logger.LogWarning(
                "Daemon bundle row {VersionId} points at missing storage key {StorageKey}",
                version.Id,
                version.BundleStorageKey);
            return NotFound();
        }

        Response.Headers.CacheControl = "public, max-age=3600, immutable";

        return File(
            stream,
            contentType: "application/gzip",
            fileDownloadName: fileName,
            enableRangeProcessing: true);
    }
}
