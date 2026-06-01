namespace Source.Features.FlyManagement.Models;

/// <summary>
/// Admin-list-row projection of a Fly volume, enriched with our DB-side linkage —
/// twin of <see cref="FlyMachineAdminRow"/> for volumes. Drives the same
/// super-admin "Fly cleanup" table: detached volumes outlive their machines and
/// keep billing storage, so the orphan signal here matters at least as much.
///
/// <para><b>Shape.</b> Flat superset of <see cref="FlyVolume"/> — every original
/// field stays at the top level so older list-consumers don't break. Linkage
/// fields are appended and nullable; <see cref="IsOrphan"/> = <c>true</c> when
/// <see cref="LinkedRuntimeId"/> is <c>null</c>.</para>
///
/// <para><b>How linkage is resolved.</b> <see cref="Source.Features.RuntimeLifecycle.Models.ProjectRuntime"/>
/// stores the Fly volume id directly in <c>FlyVolumeId</c>. The list handler
/// resolves every link with one <c>WHERE FlyVolumeId IN (...)</c> over the set of
/// volumes returned from Fly; absent ids fall through as orphans. Soft-deleted
/// runtimes (default query filter strips <c>IsDeleted == true</c>) appear as
/// orphans too — intentional: a runtime in the 30-day janitor window still has
/// Fly resources you may want to clean up early.</para>
/// </summary>
public record FlyVolumeAdminRow(
    // ---- Original FlyVolume fields (verbatim, same names + types) ----
    string Id,
    string Name,
    string Region,
    int SizeGb,
    string State,
    string? AttachedMachineId,
    bool Encrypted,
    DateTime CreatedAt,
    // ---- Added linkage fields (all nullable; null == orphan) ----
    Guid? LinkedRuntimeId,
    Guid? LinkedProjectId,
    Guid? LinkedBranchId,
    string? LinkedProjectName,
    string? LinkedBranchName,
    bool IsOrphan);
