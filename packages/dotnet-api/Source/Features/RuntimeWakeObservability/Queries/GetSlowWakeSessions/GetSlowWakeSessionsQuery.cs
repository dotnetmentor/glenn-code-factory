using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.RuntimeWakeObservability.Queries.GetSlowWakeSessions;

/// <summary>
/// Top-N slowest completed wake sessions over a rolling time window, optionally
/// filtered by region. Backs
/// <c>GET /api/admin/runtime-wake-observability/slow-sessions</c> — powers the
/// "Recent slow sessions" list whose rows link into the existing runtime
/// timeline drawer.
/// </summary>
/// <param name="Window">Window token. Must be one of <c>1h</c> / <c>24h</c> / <c>7d</c>.</param>
/// <param name="Region">Optional exact-match on <see cref="Source.Features.RuntimeLifecycle.Models.ProjectRuntime.Region"/>.</param>
/// <param name="Limit">Number of rows to return — controller clamps to <c>[1, 100]</c>; default 20.</param>
public sealed record GetSlowWakeSessionsQuery(
    string Window,
    string? Region,
    int Limit)
    : IQuery<Result<SlowWakeSessionsResponse>>;

/// <summary>
/// Wire shape for the slow-sessions list. <see cref="Sessions"/> is sorted by
/// <see cref="SlowWakeSession.DurationMs"/> descending.
/// </summary>
public sealed record SlowWakeSessionsResponse(
    List<SlowWakeSession> Sessions,
    DateTime AsOf);

/// <summary>
/// One row in the slow-sessions list. The drawer link is keyed on
/// <see cref="RuntimeId"/> + <see cref="StartedAt"/>.
///
/// <para><see cref="DominantStageName"/> is the <c>stageName</c> of the
/// <c>BootstrapStageCompleted</c> row with the largest <c>DurationMs</c>
/// inside this wake window. Null when the wake recorded no completed stages —
/// either the daemon emitted no stage events (very short wake) or the events
/// got evicted by the per-runtime 5000-event ring buffer cap.</para>
/// </summary>
public sealed record SlowWakeSession(
    Guid RuntimeId,
    Guid ProjectId,
    Guid BranchId,
    string Region,
    DateTime StartedAt,
    long DurationMs,
    string? DominantStageName);
