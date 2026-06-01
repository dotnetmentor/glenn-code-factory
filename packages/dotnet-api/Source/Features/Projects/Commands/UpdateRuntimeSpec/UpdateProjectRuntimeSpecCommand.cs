using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.Projects.Commands.UpdateRuntimeSpec;

/// <summary>
/// Update a project's default <b>runtime spec</b> — the CPU class, vCPU count,
/// RAM and volume size used when a fresh <c>ProjectRuntime</c> is created for
/// this project. Backs <c>PATCH /api/projects/{projectId}/runtime-spec</c>.
///
/// <para><b>Why a dedicated command (separate from rename / preview-port).</b>
/// The runtime spec is a knob the user labs with — they want a Performance tab
/// in project settings to tune CPU/RAM/disk for the next branch they spin up.
/// It carries the same per-project / snapshot-on-create semantics as
/// <c>PreviewPort</c>, but with four correlated fields and Fly-side validation
/// invariants (performance class needs <c>memoryMb &gt;= 2048 * cpus</c>) that
/// live on <c>Project.SetRuntimeSpec(...)</c> as a rich method.</para>
///
/// <para><b>Effect on existing runtimes.</b> None. Live runtimes keep the spec
/// they booted with — the snapshot pattern in <c>CreateProjectHandler</c>,
/// <c>CopyBranchHandler</c>, <c>ForkBranchFromGitHandler</c>,
/// <c>AttachGitBranchHandler</c> and <c>RuntimeProvisionController</c> copies
/// the project values onto the <c>ProjectRuntime</c> row at creation time so
/// the project-default change cannot retro-resize live machines. Force-respawn
/// reads from the runtime row, not the project, so respawn also preserves the
/// original spec. The new spec applies to <i>subsequent</i> runtime rows only.</para>
///
/// <para><b>Validation.</b> Delegated to <c>Project.SetRuntimeSpec(...)</c>
/// which returns sentinel-prefixed error codes the controller maps to 400:
/// <c>invalid_cpu_kind</c>, <c>invalid_cpu_count</c>, <c>invalid_memory_mb</c>,
/// <c>invalid_volume_size_gb</c>, <c>performance_memory_too_low</c>.</para>
/// </summary>
public sealed record UpdateProjectRuntimeSpecCommand(
    Guid ProjectId,
    string CpuKind,
    int Cpus,
    int MemoryMb,
    int VolumeSizeGb
) : ICommand<Result<UpdateProjectRuntimeSpecResponse>>;

/// <summary>
/// Response shape for <see cref="UpdateProjectRuntimeSpecCommand"/>. Mirrors
/// the on-disk fields so the frontend can confirm the persisted state without
/// a follow-up GET. Returned by both the no-op and the mutating path.
/// </summary>
public sealed record UpdateProjectRuntimeSpecResponse(
    Guid ProjectId,
    string CpuKind,
    int Cpus,
    int MemoryMb,
    int VolumeSizeGb);
