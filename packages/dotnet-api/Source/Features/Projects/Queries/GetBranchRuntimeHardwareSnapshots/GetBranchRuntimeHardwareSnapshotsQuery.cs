using Source.Features.Projects.Models;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.Projects.Queries.GetBranchRuntimeHardwareSnapshots;

public sealed record GetBranchRuntimeHardwareSnapshotsQuery(
    Guid ProjectId,
    string CallerUserId
) : IQuery<Result<IReadOnlyList<BranchRuntimeHardwareSnapshotDto>>>;
