namespace Source.Features.RuntimeLifecycle.Drift;

/// <summary>
/// Service seam for <c>GET /api/admin/runtimes/drift</c>. Exists so the
/// controller stays a thin passthrough and so the heavy "load runtimes + call
/// Box + evaluate rules" work can be mocked in controller / integration tests
/// without dragging in a live <see cref="Source.Features.BoxManagement.BoxClient"/>.
/// </summary>
public interface IRuntimeDriftQueryService
{
    /// <summary>
    /// Build the full drift snapshot in one shot. One DB query for runtimes
    /// (eager-loading Project + Workspace + Branch), one Box
    /// <c>ListMachines</c> call, then a pure in-memory rule walk via
    /// <see cref="DriftEvaluator"/>. Throws
    /// <see cref="Source.Features.BoxManagement.BoxApiException"/> when the Box
    /// API call fails — the controller maps that to 502.
    /// </summary>
    Task<RuntimeDriftListResponse> BuildSnapshotAsync(CancellationToken ct = default);
}
