namespace Source.Features.FlyManagement.Models;

/// <summary>
/// Admin-list-row projection of a Fly machine, enriched with our DB-side linkage
/// (which <see cref="Source.Features.RuntimeLifecycle.Models.ProjectRuntime"/>,
/// project, and branch this machine maps to). Drives the super-admin "Fly cleanup"
/// table where the operator wants to spot orphans — machines lingering on Fly with
/// no live runtime row pointing at them, billed-for-nothing zombies safe to destroy.
///
/// <para><b>Why a flat superset and not <c>{ machine, linkage }</c>.</b> The list
/// endpoint's pre-cleanup shape was the bare <see cref="FlyMachine"/>. We deliberately
/// keep every original field at the top level so any older consumer reading the
/// response still sees its fields exactly where they used to be — only new linkage
/// fields are added. JSON-level backwards compatibility, no client breaks.</para>
///
/// <para><b>How linkage is resolved.</b> <see cref="Source.Features.RuntimeLifecycle.Models.ProjectRuntime"/>
/// stores the Fly machine id directly in <c>FlyMachineId</c> (indexed —
/// <c>IX_ProjectRuntimes_FlyMachineId</c>). A single <c>WHERE FlyMachineId IN (...)</c>
/// over the list resolves every link in one round trip; machines whose id is absent
/// (deleted runtime row, manually-created VM, leak) fall through as
/// <see cref="IsOrphan"/> = <c>true</c>.</para>
/// </summary>
public record FlyMachineAdminRow(
    // ---- Original FlyMachine fields (verbatim, same names + types) ----
    string Id,
    string Name,
    string State,
    string Region,
    string? InstanceId,
    string? PrivateIp,
    DateTime CreatedAt,
    // ---- Added linkage fields (all nullable; null == orphan) ----
    Guid? LinkedRuntimeId,
    Guid? LinkedProjectId,
    Guid? LinkedBranchId,
    string? LinkedProjectName,
    string? LinkedBranchName,
    bool IsOrphan);
