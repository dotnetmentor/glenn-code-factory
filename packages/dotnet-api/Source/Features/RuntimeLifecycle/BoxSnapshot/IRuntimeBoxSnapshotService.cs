namespace Source.Features.RuntimeLifecycle.BoxSnapshot;

/// <summary>
/// Service seam for <c>GET /api/admin/runtimes/{runtimeId}/box-snapshot</c>. Exists
/// so the controller stays a thin passthrough and so the "load runtime + call Box +
/// pull recent ops" work can be mocked in controller / integration tests without
/// dragging in a live <see cref="Source.Features.BoxManagement.BoxClient"/>.
/// </summary>
public interface IRuntimeBoxSnapshotService
{
    /// <summary>
    /// Build the single-runtime snapshot. Returns <c>null</c> when the runtime row
    /// does not exist (so the controller can map to 404). Otherwise returns a fully
    /// populated <see cref="BoxSnapshotResponse"/> — with <see cref="BoxSnapshotResponse.BoxView"/>
    /// nullable when the Box half couldn't be resolved (machine vanished or upstream
    /// unreachable). The Fly call failure is logged but NOT re-thrown: a Box outage
    /// must not nuke the panel, since the DB half is still triage-worthy data.
    /// </summary>
    Task<BoxSnapshotResponse?> GetAsync(Guid runtimeId, CancellationToken ct = default);
}
