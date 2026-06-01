namespace Source.Features.RuntimeBootstrap.Models;

/// <summary>
/// Paged audit-log response shape for <c>GET /api/admin/bootstrap-runs</c>.
/// <para><see cref="Items"/> carries the slice for the current page, ordered by
/// <see cref="BootstrapRun.StartedAt"/> descending so the newest attempt lands first
/// — operators triaging a stuck/failed boot almost always want the most recent rows.
/// <see cref="Total"/> is the unpaged row count so the UI can render a "x of N"
/// counter without a follow-up COUNT round trip.</para>
///
/// <para>Mirrors <see cref="Source.Features.FlyManagement.Models.FlyOperationsResponse"/>
/// deliberately — both surfaces page over thin audit tables and the UI shares the same
/// list/detail pattern.</para>
/// </summary>
public record BootstrapRunsResponse(
    List<BootstrapRun> Items,
    int Total,
    int Page,
    int PageSize);
