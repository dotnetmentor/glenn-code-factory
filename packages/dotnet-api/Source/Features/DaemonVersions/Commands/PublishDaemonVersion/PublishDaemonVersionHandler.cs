using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Source.Features.DaemonVersions.Models;
using Source.Infrastructure;
using Source.Infrastructure.Services.FileStorage;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.DaemonVersions.Commands.PublishDaemonVersion;

/// <summary>
/// Handler for <see cref="PublishDaemonVersionCommand"/>. See the command's
/// summary for the contract; this class implements the steps in order.
///
/// <para><b>Atomicity.</b> The deactivate-old + insert-new pair runs in one
/// SaveChanges call. EF wraps that in a transaction by default, so the
/// "exactly one IsActive per channel" invariant is preserved even if the
/// bundle upload itself succeeded but the DB write fails — the orphaned blob
/// is harmless (no row points to it; it'll just sit in the bundles folder).</para>
/// </summary>
public sealed class PublishDaemonVersionHandler
    : ICommandHandler<PublishDaemonVersionCommand, Result<PublishDaemonVersionResponse>>
{
    private const string DefaultChannel = "stable";
    private const int MaxChannelLength = 32;
    private const int MaxNotesLength = 2000;
    private const string BundleFolder = DaemonBundleStorage.Folder;

    private readonly ApplicationDbContext _db;
    private readonly IFileStorageService _fileStorage;
    private readonly ILogger<PublishDaemonVersionHandler> _logger;

    public PublishDaemonVersionHandler(
        ApplicationDbContext db,
        IFileStorageService fileStorage,
        ILogger<PublishDaemonVersionHandler> logger)
    {
        _db = db;
        _fileStorage = fileStorage;
        _logger = logger;
    }

    public async Task<Result<PublishDaemonVersionResponse>> Handle(
        PublishDaemonVersionCommand request,
        CancellationToken cancellationToken)
    {
        // -------- 1. Validation --------
        if (request.BundleStream is null)
        {
            return Result.Failure<PublishDaemonVersionResponse>("Bundle stream is required");
        }

        var channel = string.IsNullOrWhiteSpace(request.Channel)
            ? DefaultChannel
            : request.Channel.Trim().ToLowerInvariant();

        if (channel.Length > MaxChannelLength)
        {
            return Result.Failure<PublishDaemonVersionResponse>(
                $"Channel name must be {MaxChannelLength} characters or fewer");
        }

        if (request.Notes is not null && request.Notes.Length > MaxNotesLength)
        {
            return Result.Failure<PublishDaemonVersionResponse>(
                $"Notes must be {MaxNotesLength} characters or fewer");
        }

        // -------- 2. Buffer + hash the bundle --------
        // We need both the bytes for SaveFileAsync and a SHA-256 over them. The
        // simplest correct path is "buffer to memory once". Daemon tarballs are
        // small (< 50MB realistically), so the memory cost is acceptable for v1.
        // If bundles balloon we can switch to a temp file + two-pass strategy.
        byte[] bundleBytes;
        await using (var ms = new MemoryStream())
        {
            await request.BundleStream.CopyToAsync(ms, cancellationToken);
            bundleBytes = ms.ToArray();
        }

        if (bundleBytes.Length == 0)
        {
            return Result.Failure<PublishDaemonVersionResponse>("Bundle is empty");
        }

        var sha256 = ComputeSha256Hex(bundleBytes);

        if (!string.IsNullOrWhiteSpace(request.PreComputedSha256))
        {
            var supplied = request.PreComputedSha256.Trim().ToLowerInvariant();
            if (!string.Equals(supplied, sha256, StringComparison.Ordinal))
            {
                return Result.Failure<PublishDaemonVersionResponse>(
                    $"SHA-256 mismatch: caller supplied {supplied} but computed {sha256}");
            }
        }

        // -------- 3. Auto-generate version --------
        // Format: MAJOR.MINOR.PATCH-prerelease (semver-compatible).
        // Daemon validates version against /^\d+\.\d+\.\d+(-[A-Za-z0-9.-]+)?$/.
        // We pack date as MAJOR.MINOR.PATCH (year.month.day, no leading zeros)
        // and time-of-day as the prerelease tag so each publish in the same day
        // sorts correctly and stays unique.
        var now = DateTime.UtcNow;
        var version = $"{now.Year}.{now.Month}.{now.Day}-{now:HHmmss}";

        // -------- 4. Upload to storage --------
        var fileName = $"daemon-{version}.tar.gz";
        string storageKey;
        string downloadUrl;
        try
        {
            await using var uploadStream = new MemoryStream(bundleBytes, writable: false);
            storageKey = await _fileStorage.SaveFileAsync(
                uploadStream,
                fileName,
                BundleFolder,
                cancellationToken);

            downloadUrl = await _fileStorage.GetFileUrlAsync(storageKey, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PublishDaemonVersion: storage upload failed for {FileName}", fileName);
            return Result.Failure<PublishDaemonVersionResponse>(
                $"Failed to upload bundle to storage: {ex.Message}");
        }

        // -------- 5. Deactivate previous active + insert new (single SaveChanges) --------
        var previousActive = await _db.DaemonVersions
            .Where(v => v.Channel == channel && v.IsActive)
            .ToListAsync(cancellationToken);

        foreach (var prev in previousActive)
        {
            prev.IsActive = false;
        }

        var entity = new DaemonVersion
        {
            Id = Guid.NewGuid(),
            Version = version,
            Channel = channel,
            BundleStorageKey = storageKey,
            BundleSha256 = sha256,
            BundleSizeBytes = bundleBytes.LongLength,
            Notes = request.Notes,
            ReleasedAt = now,
            IsActive = true,
        };

        _db.DaemonVersions.Add(entity);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            _logger.LogError(
                ex,
                "PublishDaemonVersion: DB write failed after upload (orphan blob: {StorageKey})",
                storageKey);
            return Result.Failure<PublishDaemonVersionResponse>(
                $"Failed to persist daemon version: {ex.Message}");
        }

        _logger.LogInformation(
            "PublishDaemonVersion: published {Version} to channel {Channel} (sha256={Sha256}, sizeBytes={Size})",
            version, channel, sha256, bundleBytes.LongLength);

        return Result.Success(new PublishDaemonVersionResponse
        {
            Id = entity.Id,
            Version = entity.Version,
            Channel = entity.Channel,
            DownloadUrl = downloadUrl,
            Sha256 = entity.BundleSha256,
            SizeBytes = entity.BundleSizeBytes,
            ReleasedAt = entity.ReleasedAt,
        });
    }

    private static string ComputeSha256Hex(byte[] bytes)
    {
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
