namespace Source.Features.RuntimeLifecycle.FlySnapshot;

/// <summary>
/// Service seam for <c>GET /api/admin/runtimes/{runtimeId}/fly-snapshot</c>. Exists
/// so the controller stays a thin passthrough and so the "load runtime + call Fly +
/// pull recent ops" work can be mocked in controller / integration tests without
/// dragging in a live <see cref="Source.Features.FlyManagement.FlyClient"/>.
/// </summary>
public interface IRuntimeFlySnapshotService
{
    /// <summary>
    /// Build the single-runtime snapshot. Returns <c>null</c> when the runtime row
    /// does not exist (so the controller can map to 404). Otherwise returns a fully
    /// populated <see cref="FlySnapshotResponse"/> — with <see cref="FlySnapshotResponse.FlyView"/>
    /// nullable when the Fly half couldn't be resolved (machine vanished or upstream
    /// unreachable). The Fly call failure is logged but NOT re-thrown: a Fly outage
    /// must not nuke the panel, since the DB half is still triage-worthy data.
    /// </summary>
    Task<FlySnapshotResponse?> GetAsync(Guid runtimeId, CancellationToken ct = default);
}
