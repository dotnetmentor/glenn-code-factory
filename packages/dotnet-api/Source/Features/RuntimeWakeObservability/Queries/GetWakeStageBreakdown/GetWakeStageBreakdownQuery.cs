using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.RuntimeWakeObservability.Queries.GetWakeStageBreakdown;

/// <summary>
/// Per-stage p50 / p95 / count of <c>BootstrapStageCompleted</c> durations,
/// scoped to events that fall inside a <i>completed</i> wake window for their
/// runtime. Backs
/// <c>GET /api/admin/runtime-wake-observability/stage-breakdown</c> — powers the
/// dashboard's stacked-bar breakdown chart.
/// </summary>
public sealed record GetWakeStageBreakdownQuery(
    string Window,
    string? Region)
    : IQuery<Result<WakeStageBreakdownResponse>>;

/// <summary>
/// Wire shape for the stage breakdown chart.
///
/// <para><see cref="Stages"/> is sorted by <see cref="WakeStageMetric.P95Ms"/>
/// descending so the rendering layer can render the dominant stage first
/// without re-sorting. Empty when the window has no completed wakes (or no
/// matching stage events) — the frontend renders the same empty state used by
/// the summary header.</para>
/// </summary>
public sealed record WakeStageBreakdownResponse(
    List<WakeStageMetric> Stages,
    DateTime AsOf);

/// <summary>
/// One row in the breakdown — aggregated <c>BootstrapStageCompleted</c> rows
/// sharing a <c>Payload.stageName</c> value.
///
/// <para><see cref="StageName"/> is the raw <c>stageName</c> string the daemon
/// emitted (e.g. <c>cloning-repo</c>, <c>installing-deps</c>,
/// <c>cloning-repo:background-fetch</c>, <c>__bootstrap__</c>). We don't
/// remap or normalise — the daemon owns the taxonomy.</para>
/// </summary>
public sealed record WakeStageMetric(
    string StageName,
    long P50Ms,
    long P95Ms,
    int Count);
