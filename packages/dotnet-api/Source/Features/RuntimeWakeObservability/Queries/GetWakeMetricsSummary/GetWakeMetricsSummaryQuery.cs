using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.RuntimeWakeObservability.Queries.GetWakeMetricsSummary;

/// <summary>
/// Fleet-wide p50 / p95 / count of wake durations over a rolling time window,
/// optionally filtered by <see cref="Region"/>. Backs
/// <c>GET /api/admin/runtime-wake-observability/summary</c> — the dashboard's
/// summary header.
/// </summary>
/// <param name="Window">Window token. Must be one of <c>1h</c> / <c>24h</c> / <c>7d</c>.</param>
/// <param name="Region">Optional exact-match on <see cref="Source.Features.RuntimeLifecycle.Models.ProjectRuntime.Region"/>.</param>
public sealed record GetWakeMetricsSummaryQuery(
    string Window,
    string? Region)
    : IQuery<Result<WakeMetricsSummaryResponse>>;

/// <summary>
/// Wire shape for the summary header.
///
/// <para><see cref="P50Ms"/> / <see cref="P95Ms"/> are nearest-rank percentiles
/// over the wake durations in the resolved window. <see cref="Count"/> is the
/// number of completed wakes (Suspended-&gt;Waking paired with a later
/// *-&gt;Online); incomplete wakes are dropped per spec.</para>
///
/// <para><see cref="AsOf"/> is the UTC instant the snapshot was taken — the
/// frontend renders it as "as of HH:MM:SS" next to the manual refresh button.</para>
///
/// <para>When <see cref="Count"/> is 0, percentiles are returned as 0. The
/// frontend treats <c>Count == 0</c> as the empty-state cue rather than parsing
/// magic sentinel values.</para>
/// </summary>
public sealed record WakeMetricsSummaryResponse(
    long P50Ms,
    long P95Ms,
    int Count,
    DateTime AsOf);
