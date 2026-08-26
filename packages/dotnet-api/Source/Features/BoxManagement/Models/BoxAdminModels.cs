namespace Source.Features.BoxManagement.Models;

/// <summary>
/// One row of the super-admin "Box cleanup" boxes table: a live box on the
/// account enriched with our DB-side linkage (which runtime / project / branch
/// it belongs to). Orphans — boxes with no live runtime row — are the page's
/// whole reason to exist. Note the TTL guardrail means an orphan box archives
/// itself and stops billing; cleanup here is hygiene plus snapshot storage, not
/// a fire drill.
/// </summary>
public record BoxAdminRow(
    string Id,
    string? Name,
    string Status,
    string? Size,
    string? Region,
    long? TtlSeconds,
    DateTime? CreatedAt,
    Guid? LinkedRuntimeId,
    Guid? LinkedProjectId,
    Guid? LinkedBranchId,
    string? LinkedProjectName,
    string? LinkedBranchName,
    bool IsTemplate,
    bool IsOrphan);

/// <summary>
/// One row of the super-admin "Box cleanup" snapshots table. Snapshots are a
/// per-box resource (<c>GET /boxes/{boxId}/snapshots</c>) and are deleted
/// together with their box — cleaning up an orphan means deleting the box.
/// </summary>
public record BoxSnapshotAdminRow(
    string Id,
    string? BoxId,
    long? SizeBytes,
    DateTime? CreatedAt,
    Guid? LinkedRuntimeId,
    bool IsOrphan);

/// <summary>
/// Body of <c>POST /api/admin/box/boxes/bulk-delete</c>. The super-admin Box
/// cleanup page builds these by check-boxing rows from the corresponding list
/// endpoint and sending the resource ids back as a single batch.
///
/// <para><b>Hard cap.</b> The controller refuses bodies with more than 100 ids —
/// safety net for a UI typo or runaway "select all".</para>
/// </summary>
public record BulkDeleteRequest(List<string> Ids);

/// <summary>
/// Result of a bulk-delete batch. Per-item failures are isolated — one stuck box
/// doesn't fail the rest; the caller sees a single 200 with the failed ids and
/// reasons in <see cref="Failed"/>. <see cref="Succeeded"/> + <c>Failed.Count</c>
/// always equals <see cref="Requested"/>.
/// </summary>
public record BulkDeleteResponse(
    int Requested,
    int Succeeded,
    List<BulkDeleteFailure> Failed);

/// <summary>One row of the <see cref="BulkDeleteResponse.Failed"/> array.</summary>
public record BulkDeleteFailure(
    string Id,
    string Error);

/// <summary>
/// Structured result of the admin "test configuration" probe for the Box API key.
/// Always returned with HTTP 200 — failures are reported in the body so the UI
/// renders a checklist instead of a toast.
/// </summary>
public record BoxTestConnectionResponse(
    bool ApiKeySet,
    bool PingSucceeded,
    string? PingError,
    bool IsValid,
    string Message);

/// <summary>Paged envelope for the <see cref="BoxOperation"/> audit-log admin view.</summary>
public record BoxOperationsResponse(
    List<BoxOperation> Items,
    int Total,
    int Page,
    int PageSize);
