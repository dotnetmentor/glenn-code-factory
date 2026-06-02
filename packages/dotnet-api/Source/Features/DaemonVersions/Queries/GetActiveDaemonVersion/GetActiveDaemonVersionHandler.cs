using Microsoft.EntityFrameworkCore;
using Source.Features.DaemonVersions.Models;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.DaemonVersions.Queries.GetActiveDaemonVersion;

public sealed class GetActiveDaemonVersionHandler
    : IQueryHandler<GetActiveDaemonVersionQuery, Result<DaemonVersion?>>
{
    private readonly ApplicationDbContext _db;

    public GetActiveDaemonVersionHandler(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<Result<DaemonVersion?>> Handle(
        GetActiveDaemonVersionQuery request,
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

        return Result.Success(entity);
    }
}
