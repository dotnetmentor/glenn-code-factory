using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Source.Features.RuntimeWakeObservability.Queries.GetSlowWakeSessions;
using Source.Features.RuntimeWakeObservability.Queries.GetWakeMetricsSummary;
using Source.Features.RuntimeWakeObservability.Queries.GetWakeStageBreakdown;
using Source.Infrastructure.AuthorizationModels;

namespace Source.Features.RuntimeWakeObservability.Controllers;

/// <summary>
/// Super-admin read surface for the Runtime Wake Observability dashboard
/// (spec: <c>runtime-wake-observability</c>).
///
/// <para>Three read-only endpoints power the dashboard:</para>
/// <list type="bullet">
///   <item><c>GET /api/admin/runtime-wake-observability/summary</c> — p50 / p95 / count.</item>
///   <item><c>GET /api/admin/runtime-wake-observability/stage-breakdown</c> — per-stage p50 / p95.</item>
///   <item><c>GET /api/admin/runtime-wake-observability/slow-sessions</c> — top-N slowest wakes.</item>
/// </list>
///
/// <para><b>Authorization.</b> <see cref="RoleConstants.SuperAdmin"/>, matching
/// every other <c>/api/admin/...</c> controller
/// (<see cref="Source.Features.RuntimeLifecycle.Controllers.RuntimeAdminController"/>,
/// <see cref="Source.Features.FlyManagement.Controllers.FlyAdminController"/>,
/// <see cref="Source.Features.RuntimeBootstrap.Controllers.ForceRebootstrapAdminController"/>,
/// …). Non-admins are rejected at the middleware layer with 403 before any
/// handler runs.</para>
///
/// <para><b>v1 vs v2.</b> Aggregates are computed on read from
/// <c>RuntimeStateEvents</c> + <c>RuntimeEvents</c> per the spec's "Architectural
/// Guidelines" section. A <c>RuntimeMetricsRollup</c> hourly bucket table is the
/// documented v2 once read-time aggregation becomes a cost concern.</para>
/// </summary>
[ApiController]
[Route("api/admin/runtime-wake-observability")]
[Authorize(Roles = RoleConstants.SuperAdmin)]
[Tags("RuntimeWakeObservabilityAdmin")]
public sealed class RuntimeWakeObservabilityAdminController : ControllerBase
{
    /// <summary>
    /// Hard ceiling on <c>slow-sessions</c> page size. The list is a UI
    /// affordance, not a bulk export; 100 is more than the dashboard ever
    /// renders and bounds the per-request work.
    /// </summary>
    private const int MaxSlowSessionsLimit = 100;

    /// <summary>Default <c>slow-sessions</c> page size when the caller omits <c>limit</c>.</summary>
    private const int DefaultSlowSessionsLimit = 20;

    private readonly IMediator _mediator;

    public RuntimeWakeObservabilityAdminController(IMediator mediator)
    {
        _mediator = mediator;
    }

    /// <summary>
    /// p50 / p95 / count over completed wake durations in the requested window.
    /// Empty result (Count = 0, percentiles = 0) when no wakes occurred — the
    /// frontend renders the empty state from that shape.
    /// </summary>
    /// <param name="window">Required. One of <c>1h</c> / <c>24h</c> / <c>7d</c>.</param>
    /// <param name="region">Optional. Exact-match on <c>ProjectRuntimes.Region</c>.</param>
    [HttpGet("summary")]
    [ProducesResponseType(typeof(WakeMetricsSummaryResponse), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(401)]
    [ProducesResponseType(403)]
    public async Task<ActionResult<WakeMetricsSummaryResponse>> GetSummary(
        [FromQuery] string window,
        [FromQuery] string? region = null,
        CancellationToken ct = default)
    {
        var result = await _mediator.Send(
            new GetWakeMetricsSummaryQuery(window, region),
            ct);

        return result.IsSuccess
            ? Ok(result.Value)
            : BadRequest(new { error = result.Error });
    }

    /// <summary>
    /// Per-stage p50 / p95 / count over <c>BootstrapStageCompleted</c> rows
    /// emitted inside the completed wake windows in the requested span. Sorted
    /// by p95 desc so the dominant stage renders first.
    /// </summary>
    /// <param name="window">Required. One of <c>1h</c> / <c>24h</c> / <c>7d</c>.</param>
    /// <param name="region">Optional. Exact-match on <c>ProjectRuntimes.Region</c>.</param>
    [HttpGet("stage-breakdown")]
    [ProducesResponseType(typeof(WakeStageBreakdownResponse), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(401)]
    [ProducesResponseType(403)]
    public async Task<ActionResult<WakeStageBreakdownResponse>> GetStageBreakdown(
        [FromQuery] string window,
        [FromQuery] string? region = null,
        CancellationToken ct = default)
    {
        var result = await _mediator.Send(
            new GetWakeStageBreakdownQuery(window, region),
            ct);

        return result.IsSuccess
            ? Ok(result.Value)
            : BadRequest(new { error = result.Error });
    }

    /// <summary>
    /// Top-N slowest completed wake sessions in the requested window. Each row
    /// carries the <c>RuntimeId</c> + <c>StartedAt</c> the existing runtime
    /// timeline drawer needs to deep-link.
    /// </summary>
    /// <param name="window">Required. One of <c>1h</c> / <c>24h</c> / <c>7d</c>.</param>
    /// <param name="region">Optional. Exact-match on <c>ProjectRuntimes.Region</c>.</param>
    /// <param name="limit">Optional. Defaults to 20; clamped to <c>[1, 100]</c>.</param>
    [HttpGet("slow-sessions")]
    [ProducesResponseType(typeof(SlowWakeSessionsResponse), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(401)]
    [ProducesResponseType(403)]
    public async Task<ActionResult<SlowWakeSessionsResponse>> GetSlowSessions(
        [FromQuery] string window,
        [FromQuery] string? region = null,
        [FromQuery] int limit = DefaultSlowSessionsLimit,
        CancellationToken ct = default)
    {
        // Clamp here as well as in the handler — a 0 or negative limit is a
        // pure client error and shouldn't trigger a 400 round-trip.
        var clampedLimit = Math.Clamp(limit, 1, MaxSlowSessionsLimit);

        var result = await _mediator.Send(
            new GetSlowWakeSessionsQuery(window, region, clampedLimit),
            ct);

        return result.IsSuccess
            ? Ok(result.Value)
            : BadRequest(new { error = result.Error });
    }
}
