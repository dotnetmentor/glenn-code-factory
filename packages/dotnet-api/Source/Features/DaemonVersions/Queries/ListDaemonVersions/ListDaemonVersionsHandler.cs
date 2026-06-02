using Microsoft.EntityFrameworkCore;
using Source.Features.DaemonVersions.Models;
using Source.Infrastructure;
using Source.Infrastructure.Services.FileStorage;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.DaemonVersions.Queries.ListDaemonVersions;

/// <summary>
/// Handler for <see cref="ListDaemonVersionsQuery"/>. Loads every row in
/// reverse chronological order and resolves a public download URL per row via
/// <c>IFileStorageService.GetFileUrlAsync</c>. The URL resolution is cheap
/// (string manipulation in the R2 implementation) so doing it in a loop is
/// acceptable for the v1 admin listing.
/// </summary>
public sealed class ListDaemonVersionsHandler
    : IQueryHandler<ListDaemonVersionsQuery, Result<List<DaemonVersionDto>>>
{
    private readonly ApplicationDbContext _db;
    private readonly IFileStorageService _fileStorage;

    public ListDaemonVersionsHandler(
        ApplicationDbContext db,
        IFileStorageService fileStorage)
    {
        _db = db;
        _fileStorage = fileStorage;
    }

    public async Task<Result<List<DaemonVersionDto>>> Handle(
        ListDaemonVersionsQuery request,
        CancellationToken cancellationToken)
    {
        var rows = await _db.DaemonVersions
            .AsNoTracking()
            .OrderByDescending(v => v.ReleasedAt)
            .ToListAsync(cancellationToken);

        var dtos = new List<DaemonVersionDto>(rows.Count);
        foreach (var v in rows)
        {
            var url = await _fileStorage.GetFileUrlAsync(v.BundleStorageKey, cancellationToken);
            dtos.Add(new DaemonVersionDto
            {
                Id = v.Id,
                Version = v.Version,
                Channel = v.Channel,
                DownloadUrl = url,
                Sha256 = v.BundleSha256,
                SizeBytes = v.BundleSizeBytes,
                ReleasedAt = v.ReleasedAt,
                IsActive = v.IsActive,
                Notes = v.Notes,
                GitSha = v.GitSha,
            });
        }

        return Result.Success(dtos);
    }
}
