using Microsoft.EntityFrameworkCore;
using Source.Features.Projects.Models;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.Projects.Queries.GetBranchRuntimeHardwareSnapshots;

public sealed class GetBranchRuntimeHardwareSnapshotsHandler
    : IQueryHandler<GetBranchRuntimeHardwareSnapshotsQuery, Result<IReadOnlyList<BranchRuntimeHardwareSnapshotDto>>>
{
    public const string NotFoundPrefix = "not-found:";

    private readonly ApplicationDbContext _db;

    public GetBranchRuntimeHardwareSnapshotsHandler(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<Result<IReadOnlyList<BranchRuntimeHardwareSnapshotDto>>> Handle(
        GetBranchRuntimeHardwareSnapshotsQuery request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.CallerUserId))
        {
            return Result.Failure<IReadOnlyList<BranchRuntimeHardwareSnapshotDto>>(
                $"{NotFoundPrefix} unauthenticated");
        }

        var workspaceId = await _db.Projects
            .AsNoTracking()
            .Where(p => p.Id == request.ProjectId)
            .Select(p => (Guid?)p.WorkspaceId)
            .SingleOrDefaultAsync(cancellationToken);

        if (workspaceId is null)
        {
            return Result.Failure<IReadOnlyList<BranchRuntimeHardwareSnapshotDto>>(
                $"{NotFoundPrefix} project {request.ProjectId} not found");
        }

        var isMember = await _db.WorkspaceMemberships
            .AsNoTracking()
            .AnyAsync(
                m => m.WorkspaceId == workspaceId && m.UserId == request.CallerUserId,
                cancellationToken);

        if (!isMember)
        {
            return Result.Failure<IReadOnlyList<BranchRuntimeHardwareSnapshotDto>>(
                $"{NotFoundPrefix} project {request.ProjectId} not found");
        }

        var snapshots = await _db.ProjectBranches
            .AsNoTracking()
            .Where(b => b.ProjectId == request.ProjectId && !b.IsArchived)
            .OrderByDescending(b => b.IsDefault)
            .ThenBy(b => b.Name)
            .Select(b => new
            {
                b.Id,
                b.Name,
                Runtime = _db.ProjectRuntimes
                    .Where(r => r.BranchId == b.Id)
                    .OrderByDescending(r => r.CreatedAt)
                    .Select(r => new
                    {
                        r.CpuKind,
                        r.Cpus,
                        r.MemoryMb,
                        r.VolumeSizeGb,
                        r.State,
                    })
                    .FirstOrDefault(),
            })
            .Where(x => x.Runtime != null)
            .Select(x => new BranchRuntimeHardwareSnapshotDto(
                x.Id,
                x.Name,
                x.Runtime!.CpuKind,
                x.Runtime.Cpus,
                x.Runtime.MemoryMb,
                x.Runtime.VolumeSizeGb,
                x.Runtime.State))
            .ToListAsync(cancellationToken);

        return Result.Success<IReadOnlyList<BranchRuntimeHardwareSnapshotDto>>(snapshots);
    }
}
