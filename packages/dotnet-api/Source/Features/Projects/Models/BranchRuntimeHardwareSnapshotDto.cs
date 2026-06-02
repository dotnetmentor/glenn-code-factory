using Source.Features.RuntimeLifecycle.Models;

namespace Source.Features.Projects.Models;

/// <summary>
/// Hardware sizing snapshotted on a branch's active <see cref="ProjectRuntime"/>
/// row. Powers the Performance settings tab drift check before save.
/// </summary>
public record BranchRuntimeHardwareSnapshotDto(
    Guid BranchId,
    string BranchName,
    string CpuKind,
    int Cpus,
    int MemoryMb,
    int VolumeSizeGb,
    RuntimeState State);
