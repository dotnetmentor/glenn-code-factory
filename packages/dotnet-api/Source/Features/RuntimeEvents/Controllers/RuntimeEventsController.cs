using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Source.Features.RuntimeEvents.Models;
using Source.Features.RuntimeEvents.Queries;
using Source.Infrastructure;
using Source.Infrastructure.Extensions;

namespace Source.Features.RuntimeEvents.Controllers;

/// <summary>
/// User-facing read surface over the runtime event store. Backs the runtime
/// drawer's Timeline tab: a single
/// <c>GET /api/runtime-events?runtimeId=&amp;limit=&amp;before=&amp;type=&amp;severity=</c>
/// endpoint returning a reverse-chronological page of events with cursor
/// pagination on <c>Timestamp</c>.
///
/// <para><b>Why no project scoping in the route.</b> The endpoint is keyed on
/// <c>runtimeId</c> (not <c>projectId</c>) because <see cref="RuntimeEvent"/>
/// is intentionally append-only with no FK to <c>ProjectRuntime</c> — events
/// outlive the runtime row (same convention as <c>RuntimeStateEvent</c>,
/// <c>RuntimeErrorReport</c>, <c>BootstrapRun</c>). The drawer always knows
/// which runtime it is rendering for, so the query parameter is the natural
/// shape.</para>
///
/// <para><b>Auth.</b> Default JWT bearer plus per-runtime access gating via
/// <see cref="OwnershipExtensions.ResolveAccessibleRuntimeAsync"/> —
/// SuperAdmin OR project owner OR a member of the runtime's owning workspace.
/// Read-only observability surface consumed by the in-workspace debug panel's
/// Timeline tab; the wider gate matches
/// <see cref="Source.Features.RuntimeLifecycle.Controllers.RuntimeStatusController.GetStatus"/>
/// and the SignalR <c>runtime-events</c> group join (see
/// <c>workspace-runtime-observability</c> spec, Section E). Both "no such
/// runtime" and "exists but no access" surface as <c>404</c> so cross-tenant
/// runtime-id existence isn't leaked.</para>
/// </summary>
[ApiController]
[Route("api/runtime-events")]
[Authorize]
[Tags("RuntimeEvents")]
public class RuntimeEventsController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly ApplicationDbContext _db;

    public RuntimeEventsController(IMediator mediator, ApplicationDbContext db)
    {
        _mediator = mediator;
        _db = db;
    }

    /// <summary>
    /// Reverse-chronological page of runtime events for the given runtime.
    ///
    /// <list type="bullet">
    ///   <item><paramref name="runtimeId"/> — required.</item>
    ///   <item><paramref name="limit"/> — defaults to 200, clamped to [1, 500].</item>
    ///   <item><paramref name="before"/> — optional cursor; only events with
    ///         <c>Timestamp &lt; before</c> are returned. Pass the oldest
    ///         timestamp from the previous page to fetch the next page.</item>
    ///   <item><paramref name="type"/> — optional event-type filter; should be
    ///         one of the <see cref="RuntimeEventTypes"/> constants but is
    ///         not validated server-side so the daemon can emit new types
    ///         without a coordinated deploy.</item>
    ///   <item><paramref name="severity"/> — optional severity filter
    ///         (<c>Info</c> / <c>Warn</c> / <c>Error</c>).</item>
    /// </list>
    ///
    /// <para>Returns <see cref="ListRuntimeEventsResponse"/> with <c>HasMore</c>
    /// set when another page is available. Empty <c>Events</c> is the correct
    /// shape for "no events for this runtime yet".</para>
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(ListRuntimeEventsResponse), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(401)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<ListRuntimeEventsResponse>> List(
        [FromQuery] Guid runtimeId,
        [FromQuery] int limit = 200,
        [FromQuery] DateTime? before = null,
        [FromQuery] string? type = null,
        [FromQuery] RuntimeEventSeverity? severity = null,
        CancellationToken ct = default)
    {
        if (runtimeId == Guid.Empty)
        {
            return BadRequest(new { error = "runtimeId is required" });
        }

        // Access gate — SuperAdmin OR project owner OR workspace member.
        // Null on either "no such runtime" or "no access"; both surfaced as 404
        // so cross-tenant runtime-id existence isn't leaked.
        if (await _db.ResolveAccessibleRuntimeAsync(User, runtimeId, ct) is null)
        {
            return NotFound();
        }

        var result = await _mediator.Send(
            new GetRuntimeEventsQuery(
                RuntimeId: runtimeId,
                Limit: limit,
                Before: before,
                Type: type,
                Severity: severity),
            ct);

        return result.IsSuccess
            ? Ok(result.Value)
            : BadRequest(new { error = result.Error });
    }
}
