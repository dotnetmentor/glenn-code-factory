namespace Source.Features.RuntimeLifecycle.Models;

/// <summary>
/// Paged response shape for <c>GET /api/admin/runtimes</c>. Mirrors
/// <see cref="Source.Features.FlyManagement.Models.FlyOperationsResponse"/> and
/// <see cref="Source.Features.RuntimeBootstrap.Models.BootstrapRunsResponse"/> exactly so
/// the operator-facing list views share one rendering pattern.
///
/// <para><see cref="Items"/> carries the slice for the current page, ordered by
/// <see cref="ProjectRuntime.UpdatedAt"/> descending so the runtimes that just changed
/// state surface first — that's the dominant operator triage question. <see cref="Total"/>
/// is the unpaged row count so the UI can render an "x of N" counter without a follow-up
/// COUNT round trip.</para>
/// </summary>
public record RuntimesListResponse(
    List<ProjectRuntime> Items,
    int Total,
    int Page,
    int PageSize);
