namespace Source.Features.FlyManagement.Models;

/// <summary>
/// Paged audit-log response shape for <c>GET /api/admin/fly/operations</c>.
/// <para><see cref="Items"/> carries the slice for the current page, ordered by
/// <see cref="FlyOperation.CreatedAt"/> descending so the newest call lands first
/// — operators triaging an incident almost always want the most recent rows.
/// <see cref="Total"/> is the unpaged row count so the UI can render a "x of N"
/// counter without a follow-up COUNT round trip.</para>
/// </summary>
public record FlyOperationsResponse(
    List<FlyOperation> Items,
    int Total,
    int Page,
    int PageSize);
