namespace Source.Features.RuntimeLifecycle.FlySnapshot;

/// <summary>
/// Envelope shape for <c>GET /api/admin/runtimes/{runtimeId}/fly-snapshot</c> — the
/// operator's "reality check" view of a single runtime. Three panes side-by-side:
///
/// <list type="bullet">
///   <item><see cref="OurView"/> — what our DB thinks the runtime looks like.</item>
///   <item><see cref="FlyView"/> — what Fly's machines API reports. Null when the
///         runtime has no Fly machine id yet, the machine has been destroyed, or
///         the Fly call failed (the operator panel must keep rendering even when
///         Fly is unreachable — the DB half is still useful triage data).</item>
///   <item><see cref="RecentOperations"/> — the last 20 <see cref="Source.Features.FlyManagement.Models.FlyOperation"/>
///         rows for this runtime, newest first. Caps the worst-case payload size
///         while still showing a meaningful timeline.</item>
/// </list>
///
/// <para><see cref="GeneratedAt"/> is captured once at the very top of the service
/// method (before any DB / Fly round trips) so the timestamp matches the actual
/// snapshot moment — same pattern as <see cref="Drift.RuntimeDriftListResponse"/>.</para>
/// </summary>
public sealed class FlySnapshotResponse
{
    /// <summary>The runtime as our database sees it.</summary>
    public required OurRuntimeView OurView { get; init; }

    /// <summary>
    /// The runtime's Fly machine as the Fly API sees it. Null when the runtime has
    /// never been provisioned (<see cref="OurRuntimeView.FlyMachineId"/> is null),
    /// when Fly returned 404 (machine destroyed), or when the Fly API call failed —
    /// distinguished from a populated view by being literally <c>null</c>.
    /// </summary>
    public FlyMachineView? FlyView { get; init; }

    /// <summary>The last 20 Fly API operations targeting this runtime, newest first.</summary>
    public List<FlyOperationView> RecentOperations { get; init; } = new();

    /// <summary>UTC timestamp captured at the start of snapshot build.</summary>
    public DateTime GeneratedAt { get; init; }
}
