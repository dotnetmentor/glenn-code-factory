namespace Source.Features.RuntimeLifecycle.Drift;

/// <summary>
/// Envelope shape for <c>GET /api/admin/runtimes/drift</c>. Wraps the per-row
/// <see cref="RuntimeDriftDto"/> list with summary counters and the snapshot
/// timestamp so the operator UI can show a "generated at HH:mm:ss UTC" header
/// without needing a follow-up roundtrip.
///
/// <para>This is a class (not a record) to match the other admin envelopes
/// in this feature (<see cref="Models.RuntimesListResponse"/>,
/// <see cref="Models.RuntimeDetailResponse"/>) and to keep init-only DTO
/// ergonomics on the frontend.</para>
/// </summary>
public sealed class RuntimeDriftListResponse
{
    /// <summary>
    /// All runtime + orphan rows in one list, sorted by <see cref="RuntimeDriftDto.DriftSeverity"/>
    /// descending (Critical first), then by <see cref="RuntimeDriftDto.SecondsSinceStateChange"/>
    /// descending so the longest-running incidents float to the top inside each severity bucket.
    /// </summary>
    public List<RuntimeDriftDto> Items { get; init; } = new();

    /// <summary>Total row count including healthy runtimes and orphans — equals <c>Items.Count</c>.</summary>
    public int TotalCount { get; init; }

    /// <summary>Subset of <see cref="Items"/> whose severity is anything other than <see cref="DriftSeverity.Ok"/>.</summary>
    public int DriftCount { get; init; }

    /// <summary>Server-side UTC timestamp captured at the start of the snapshot build.</summary>
    public DateTime GeneratedAt { get; init; }
}
