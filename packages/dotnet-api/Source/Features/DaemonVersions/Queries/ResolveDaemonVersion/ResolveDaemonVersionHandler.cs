using Microsoft.EntityFrameworkCore;
using Source.Features.DaemonVersions.Models;
using Source.Infrastructure;
using Source.Infrastructure.Services.FileStorage;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.DaemonVersions.Queries.ResolveDaemonVersion;

/// <summary>
/// Handler for <see cref="ResolveDaemonVersionQuery"/>. Looks up the single
/// <c>IsActive=true</c> row in the requested channel and projects it onto
/// <see cref="DaemonVersionDto"/>, resolving the public download URL via
/// <c>IFileStorageService.GetFileUrlAsync</c>.
///
/// <para><b>Sentinel prefix.</b> "<c>not-found:</c>" lets the controller map
/// "no active version" to a 404 instead of a 400 — this is a normal state on
/// a brand-new install before the first bundle has been published.</para>
/// </summary>
public sealed class ResolveDaemonVersionHandler
    : IQueryHandler<ResolveDaemonVersionQuery, Result<DaemonVersionDto>>
{
    public const string NotFoundPrefix = "not-found:";

    private readonly ApplicationDbContext _db;
    private readonly IFileStorageService _fileStorage;

    public ResolveDaemonVersionHandler(
        ApplicationDbContext db,
        IFileStorageService fileStorage)
    {
        _db = db;
        _fileStorage = fileStorage;
    }

    public async Task<Result<DaemonVersionDto>> Handle(
        ResolveDaemonVersionQuery request,
        CancellationToken cancellationToken)
    {
        var channel = string.IsNullOrWhiteSpace(request.Channel)
            ? "stable"
            : request.Channel.Trim().ToLowerInvariant();

        var entity = await _db.DaemonVersions
            .AsNoTracking()
            .Where(v => v.Channel == channel && v.IsActive)
            .OrderByDescending(v => v.ReleasedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (entity is null)
        {
            return Result.Failure<DaemonVersionDto>(
                $"{NotFoundPrefix} no active daemon version published for channel '{channel}'");
        }

        var downloadUrl = await _fileStorage.GetFileUrlAsync(entity.BundleStorageKey, cancellationToken);

        return Result.Success(new DaemonVersionDto
        {
            Id = entity.Id,
            Version = entity.Version,
            Channel = entity.Channel,
            DownloadUrl = downloadUrl,
            Sha256 = entity.BundleSha256,
            SizeBytes = entity.BundleSizeBytes,
            ReleasedAt = entity.ReleasedAt,
            IsActive = entity.IsActive,
            Notes = entity.Notes,
        });
    }
}
