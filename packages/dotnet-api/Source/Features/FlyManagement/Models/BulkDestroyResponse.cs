namespace Source.Features.FlyManagement.Models;

/// <summary>
/// Result of a bulk-destroy batch. Per-item failures are isolated — one stuck VM
/// (e.g. "machine is attached to volume vol_xyz", "volume in use") doesn't fail
/// the rest of the batch; the caller sees a single 200 response with the failed
/// ids and reasons enumerated in <see cref="Failed"/>.
///
/// <para><b>Counts.</b> <see cref="Requested"/> is the input size after the
/// 100-id cap (so always 1..=100). <see cref="Succeeded"/> + <c>Failed.Count</c>
/// always equals <see cref="Requested"/> — useful invariant for UI badges
/// ("23 of 25 cleaned up").</para>
///
/// <para><b>Audit trail.</b> Each underlying destroy call still goes through
/// <see cref="FlyClient.DestroyMachineAsync"/> / <see cref="FlyClient.DestroyVolumeAsync"/>,
/// which write one <see cref="FlyOperation"/> row per item — the existing audit
/// surface (queryable via <c>/api/admin/fly/operations</c>) captures the full
/// per-item story, this DTO is just the inline summary for the UI.</para>
/// </summary>
public record BulkDestroyResponse(
    int Requested,
    int Succeeded,
    List<BulkDestroyFailure> Failed);

/// <summary>
/// One row of the <see cref="BulkDestroyResponse.Failed"/> array. <see cref="Error"/>
/// is the Fly-side reason if we got one (<see cref="FlyApiException.Message"/>'s
/// status + error code), otherwise the transport-level exception message — kept as
/// a free-form string because Fly's error vocabulary isn't an enum we want to pin.
/// </summary>
public record BulkDestroyFailure(
    string Id,
    string Error);
