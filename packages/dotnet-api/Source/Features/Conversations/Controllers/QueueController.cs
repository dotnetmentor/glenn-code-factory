using System.Security.Claims;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Source.Features.Conversations.Commands;
using Source.Features.Conversations.Models;
using Source.Infrastructure;
using Source.Infrastructure.Extensions;

namespace Source.Features.Conversations.Controllers;

/// <summary>
/// User-facing HTTP surface for per-runtime queue control. Today the only
/// endpoint is <c>PUT /api/runtimes/{runtimeId}/queue/reorder</c> — drag-drop
/// reordering of the Pending sessions queued behind a runtime's currently
/// running session.
///
/// <para><b>Why a separate controller from <see cref="SessionsController"/>.</b>
/// The route prefix is runtime-scoped (<c>/api/runtimes/{runtimeId}/...</c>),
/// not session-scoped. Mixing route templates inside a single controller
/// works but obscures the surface. As the queue grows endpoints
/// (peek/pause/clear in later cards) they all naturally hang off this same
/// controller.</para>
///
/// <para><b>Authorisation.</b> JWT bearer plus per-project ownership gating:
/// each endpoint resolves the runtime via
/// <see cref="OwnershipExtensions.ResolveOwnedRuntimeAsync"/>, which loads
/// the runtime with its <c>Project</c> nav and verifies the caller's
/// <c>NameIdentifier</c> claim matches <c>Project.OwnerUserId</c>. A
/// mismatch returns <c>404</c> (NOT 403) so we don't leak runtime-id
/// existence cross-tenant — same convention as
/// <see cref="Source.Features.Diffs.Controllers.DiffsController"/>.</para>
/// </summary>
[ApiController]
[Route("api/runtimes")]
[Authorize]
[Tags("Queue")]
public class QueueController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly ApplicationDbContext _db;

    public QueueController(IMediator mediator, ApplicationDbContext db)
    {
        _mediator = mediator;
        _db = db;
    }

    /// <summary>
    /// Reorder the queued (Pending + non-null <c>QueuePosition</c>) sessions
    /// on a runtime. Body carries the <em>full</em> new order — the handler
    /// rejects the request if it doesn't exactly match the current DB
    /// snapshot (extras, omissions, duplicates), returning 400 with
    /// <c>"queue mismatch — refresh"</c>. The frontend's response is to
    /// re-fetch and let the user retry.
    ///
    /// <para>Returns the applied order on success so the optimistic-UI'd
    /// frontend can confirm the server agreed.</para>
    ///
    /// <para><b>Ownership gate.</b> Resolves the runtime through
    /// <see cref="OwnershipExtensions.ResolveOwnedRuntimeAsync"/> before
    /// dispatching the command — caller must own the parent project. A
    /// mismatch surfaces as <c>404</c> (not 403) to avoid leaking runtime
    /// existence cross-tenant.</para>
    /// </summary>
    [HttpPut("{runtimeId:guid}/queue/reorder")]
    [ProducesResponseType(typeof(ReorderQueueResponse), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(401)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<ReorderQueueResponse>> Reorder(
        Guid runtimeId,
        [FromBody] ReorderQueueRequest request,
        CancellationToken ct)
    {
        var owned = await _db.ResolveOwnedRuntimeAsync(User, runtimeId, ct);
        if (owned is null)
        {
            return NotFound();
        }

        // "unknown" sentinel mirrors the cancel-by-controller convention:
        // claim is always present on an authenticated request, but we want
        // a defined value on the audit row even if the JWT shape ever drifts.
        var actorUserId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "unknown";

        var result = await _mediator.Send(
            new ReorderQueueCommand(runtimeId, request.SessionIds, actorUserId),
            ct);

        if (!result.IsSuccess)
        {
            // Mismatch is the only failure case today — surface as 400 so the
            // optimistic-UI frontend can distinguish "your snapshot is stale"
            // from a genuine 404 ("no such runtime") that lands later when
            // the ownership check is plumbed in.
            return BadRequest(new { error = result.Error });
        }

        return Ok(result.Value);
    }

    /// <summary>
    /// Read-only snapshot of the per-runtime queue: the active session ids
    /// (one Running, one Canceling — see
    /// <see cref="QueueResponse"/> for why both) plus the Pending tail
    /// ordered by <see cref="AgentSession.QueuePosition"/>. Drives the chat
    /// panel's queue indicator.
    ///
    /// <para><b>Why no MediatR.</b> Same pragmatic reasoning as
    /// <see cref="Source.Features.RuntimeLifecycle.Controllers.RuntimeStatusController.GetStatus"/>:
    /// two reads, two projections, no branching. A command/handler wrapper
    /// would add files without changing behaviour. The slice stays thin and
    /// the controller talks straight to the DbContext.</para>
    ///
    /// <para><b>404 on missing runtime, not 200-with-empty.</b> An unknown
    /// runtime id is a different signal from "runtime exists but the queue
    /// is empty" — the UI may want to redirect / error rather than poll an
    /// always-empty endpoint. Mirrors
    /// <c>RuntimeStatusController.GetStatus</c>.</para>
    ///
    /// <para><b>IgnoreQueryFilters().</b> <see cref="AgentSession"/> has no
    /// soft-delete filter today, but its parent <see cref="Conversation"/>
    /// does (archived conversations hidden). A user might archive a
    /// conversation while its session still occupies the runtime — the queue
    /// indicator must still show that session, so we bypass the filter on
    /// the read. Defensive but cheap.</para>
    ///
    /// <para><b>Ownership gate.</b> Resolves the runtime through
    /// <see cref="OwnershipExtensions.ResolveOwnedRuntimeAsync"/> — caller
    /// must own the parent project. Both "no such runtime" and "not yours"
    /// surface as <c>404</c> so cross-tenant existence isn't leaked.</para>
    /// </summary>
    [HttpGet("{runtimeId:guid}/queue")]
    [ProducesResponseType(typeof(QueueResponse), 200)]
    [ProducesResponseType(401)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<QueueResponse>> GetQueue(
        Guid runtimeId,
        CancellationToken ct)
    {
        // 404 vs empty: an unknown runtime is a different signal from a known
        // runtime with an empty queue. The ownership helper also acts as the
        // existence check — null on either gap, surfaced uniformly as 404.
        var owned = await _db.ResolveOwnedRuntimeAsync(User, runtimeId, ct);
        if (owned is null)
        {
            return NotFound();
        }

        // Active sessions: at most one Running and at most one Canceling on
        // a given runtime (DB-level invariant from Card 3 / Card 4). One
        // round-trip pulls both in a single index seek over
        // IX_AgentSessions_Runtime_Status_QueuePosition.
        var activeSessions = await _db.AgentSessions
            .IgnoreQueryFilters()
            .Where(s => s.RuntimeId == runtimeId
                     && (s.Status == AgentSessionStatus.Running
                      || s.Status == AgentSessionStatus.Canceling))
            .Select(s => new { s.Id, s.Status })
            .ToListAsync(ct);

        Guid? runningId = activeSessions
            .FirstOrDefault(x => x.Status == AgentSessionStatus.Running)?.Id;
        Guid? cancelingId = activeSessions
            .FirstOrDefault(x => x.Status == AgentSessionStatus.Canceling)?.Id;

        // Queued tail: Pending + non-null QueuePosition, ordered by position.
        // Truncate the prompt to 120 chars in SQL so we don't pay to ship a
        // 50KB prompt over the wire just to render the queue list. The UI
        // fetches the full prompt on click.
        var entries = await _db.AgentSessions
            .IgnoreQueryFilters()
            .Where(s => s.RuntimeId == runtimeId
                     && s.Status == AgentSessionStatus.Pending
                     && s.QueuePosition != null)
            .OrderBy(s => s.QueuePosition)
            .Select(s => new QueueEntryDto(
                s.Id,
                s.ConversationId,
                s.QueuePosition!.Value,
                s.Status,
                s.Prompt.Length > 120 ? s.Prompt.Substring(0, 120) + "..." : s.Prompt,
                s.CreatedAt))
            .ToListAsync(ct);

        return Ok(new QueueResponse(entries, runningId, cancelingId));
    }
}

/// <summary>
/// Body shape for <c>PUT /api/runtimes/{runtimeId}/queue/reorder</c>.
/// <see cref="SessionIds"/> is the full new order — index 0 is the next to
/// dispatch, position numbering is 1-based on the server.
/// </summary>
public record ReorderQueueRequest(IReadOnlyList<Guid> SessionIds);
