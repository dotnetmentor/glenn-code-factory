using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.Projects.Commands.UpdateRuntimeSpec;

/// <summary>
/// Update a project's default runtime spec and optionally reprovision existing
/// branch runtimes so live machines pick up the new CPU/RAM sizing.
/// </summary>
public sealed record UpdateProjectRuntimeSpecCommand(
    Guid ProjectId,
    string CpuKind,
    int Cpus,
    int MemoryMb,
    int VolumeSizeGb,
    bool ApplyToExistingBranches,
    Guid UserId
) : ICommand<Result<UpdateProjectRuntimeSpecResponse>>;

public sealed record UpdateProjectRuntimeSpecResponse(
    Guid ProjectId,
    string CpuKind,
    int Cpus,
    int MemoryMb,
    int VolumeSizeGb,
    int AppliedToExistingBranchCount,
    IReadOnlyList<string> RestartedBranchNames,
    string? VolumeSizeNote);
