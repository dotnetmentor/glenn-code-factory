namespace Source.Features.RuntimeLifecycle.BoxSnapshot;

/// <summary>
/// Envelope shape for <c>GET /api/admin/runtimes/{runtimeId}/box-snapshot</c> — the
/// operator's "reality check" view of a single runtime. Three panes side-by-side:
///
/// <list type="bullet">
///   <item><see cref="OurView"/> — what our DB thinks the runtime looks like.</item>
///   <item><see cref="BoxView"/> — what the Box API reports. Null when the
///         runtime has no box id yet, the machine has been destroyed, or
///         the Box call failed (the operator panel must keep rendering even when
///         Box is unreachable — the DB half is still useful triage data).</item>
///   <item><see cref="RecentOperations"/> — the last 20 <see cref="Source.Features.BoxManagement.Models.BoxOperation"/>
///         rows for this runtime, newest first. Caps the worst-case payload size
///         while still showing a meaningful timeline.</item>
/// </list>
///
/// <para><see cref="GeneratedAt"/> is captured once at the very top of the service
/// method (before any DB / Fly round trips) so the timestamp matches the actual
/// snapshot moment — same pattern as <see cref="Drift.RuntimeDriftListResponse"/>.</para>
/// </summary>
public sealed class BoxSnapshotResponse
{
    /// <summary>The runtime as our database sees it.</summary>
    public required OurRuntimeView OurView { get; init; }

    /// <summary>
    /// The runtime's box as the Box API sees it. Null when the runtime has
    /// never been provisioned (<see cref="OurRuntimeView.BoxId"/> is null),
    /// when Box returned 404 (machine destroyed), or when the Box API call failed —
    /// distinguished from a populated view by being literally <c>null</c>.
    /// </summary>
    public BoxVmView? BoxView { get; init; }

    /// <summary>The last 20 Box API operations targeting this runtime, newest first.</summary>
    public List<BoxOperationView> RecentOperations { get; init; } = new();

    /// <summary>UTC timestamp captured at the start of snapshot build.</summary>
    public DateTime GeneratedAt { get; init; }
}
