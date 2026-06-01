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
/// User-facing HTTP surface for individual <see cref="AgentSession"/> control
/// actions. Today the only endpoint is <c>POST /api/sessions/{id}/cancel</c> —
/// a parallel surface to <see cref="Source.Features.SignalR.Hubs.AgentHub.CancelTurn"/>
/// for callers that aren't on a live SignalR connection (e.g. an admin REST
/// client, a curl smoke test, or the chat panel falling back when the hub is
/// disconnected).
///
/// <para><b>Why MediatR here.</b> Read-only conversation/session GETs sit on
/// <see cref="ConversationsController"/> as thin DbContext passthroughs. This
/// endpoint is different: it has real branching (Pending vs Running vs
/// idempotent terminal) plus a SignalR fan-out side effect. Wrapping that in
/// a command/handler keeps the controller a one-liner, matches the rest of
/// the project's CQRS pattern, and gives us a unit-testable handler.</para>
///
/// <para><b>Authorisation.</b> Default JWT bearer plus per-session ownership
/// gating via <see cref="OwnershipExtensions.ResolveOwnedSessionAsync"/> —
/// the session's parent conversation's project's <c>OwnerUserId</c> must
/// match the caller. Both "no such session" and "exists but not yours"
/// surface as <c>404</c> so cross-tenant existence isn't leaked — same
/// convention as <see cref="Source.Features.Diffs.Controllers.DiffsController"/>.</para>
/// </summary>
[ApiController]
[Route("api/sessions")]
[Authorize]
[Tags("Sessions")]
public class SessionsController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly ApplicationDbContext _db;

    public SessionsController(IMediator mediator, ApplicationDbContext db)
    {
        _mediator = mediator;
        _db = db;
    }

    /// <summary>
    /// Cancel an in-flight or queued session. Behaviour depends on the
    /// session's current status:
    ///
    /// <list type="bullet">
    ///   <item><b>Pending</b> → terminal <see cref="AgentSessionStatus.Canceled"/>
    ///         in one step (no daemon involved).</item>
    ///   <item><b>Running</b> → intermediate <see cref="AgentSessionStatus.Canceling"/>;
    ///         the daemon's <c>turn_canceled</c> confirmation flips it to
    ///         terminal Canceled later.</item>
    ///   <item><b>Canceling / Canceled / Succeeded / Failed</b> → idempotent
    ///         no-op, returns the unchanged status. Repeated cancel clicks /
    ///         retries are safe.</item>
    /// </list>
    ///
    /// <para><c>Reason</c> is optional — the controller defaults to
    /// <c>"user"</c> when the body is missing or empty so the audit trail
    /// always has a non-null reason on the resulting Cancel/Canceling row.</para>
    ///
    /// <para><b>Ownership gate.</b> Verifies the caller owns the session's
    /// parent conversation's project via
    /// <see cref="OwnershipExtensions.ResolveOwnedSessionAsync"/> before
    /// dispatching the command — non-owners (and probes for non-existent
    /// sessions) get <c>404</c>, never <c>403</c>.</para>
    /// </summary>
    [HttpPost("{sessionId:guid}/cancel")]
    [ProducesResponseType(typeof(CancelSessionResponse), 200)]
    [ProducesResponseType(401)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<CancelSessionResponse>> Cancel(
        Guid sessionId,
        [FromBody] CancelSessionRequest? request,
        CancellationToken ct)
    {
        // Ownership gate — null on either "no such session" or "not yours",
        // both surfaced uniformly as 404. Runs BEFORE the MediatR dispatch so
        // the handler never sees an unowned session id.
        if (await _db.ResolveOwnedSessionAsync(User, sessionId, ct) is null)
        {
            return NotFound();
        }

        // Default reason is "user" — the cancel button on the chat panel
        // has no UI to capture a free-text reason today, but the column on
        // AgentSession.CancelReason is non-empty by contract for audit clarity.
        var reason = string.IsNullOrWhiteSpace(request?.Reason) ? "user" : request!.Reason!;

        var result = await _mediator.Send(new CancelSessionCommand(sessionId, reason), ct);
        if (!result.IsSuccess)
        {
            return NotFound(new { error = result.Error });
        }

        return Ok(result.Value);
    }

    /// <summary>
    /// Single-session view returning the session's current status, queue
    /// position (null when not queued), and the total queued length on its
    /// runtime. Drives the chat panel's "you're #3 in the queue" copy on a
    /// Pending session and the "running" / terminal copy otherwise.
    ///
    /// <para><b>Why no MediatR.</b> Same pragmatic reasoning as
    /// <see cref="QueueController.GetQueue"/> and
    /// <see cref="Source.Features.RuntimeLifecycle.Controllers.RuntimeStatusController.GetStatus"/>:
    /// two reads, one projection, no branching. A command/handler wrapper
    /// would add files without changing behaviour.</para>
    ///
    /// <para><b>IgnoreQueryFilters().</b> A user might archive a conversation
    /// while its session is still running or queued; the chat panel needs to
    /// keep rendering the position copy, so we bypass the
    /// <see cref="Conversation"/> archived filter. Defensive but cheap —
    /// <see cref="AgentSession"/> itself has no soft-delete filter today.</para>
    ///
    /// <para><b>Ownership gate.</b> Verifies the caller owns the session's
    /// parent conversation's project via
    /// <see cref="OwnershipExtensions.ResolveOwnedSessionAsync"/> — non-owners
    /// (and probes for non-existent sessions) get <c>404</c>, never <c>403</c>.</para>
    /// </summary>
    [HttpGet("{sessionId:guid}/position")]
    [ProducesResponseType(typeof(SessionPositionResponse), 200)]
    [ProducesResponseType(401)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<SessionPositionResponse>> GetPosition(
        Guid sessionId,
        CancellationToken ct)
    {
        // Ownership gate — null on either "no such session" or "not yours",
        // both surfaced uniformly as 404 so cross-tenant existence isn't leaked.
        if (await _db.ResolveOwnedSessionAsync(User, sessionId, ct) is null)
        {
            return NotFound();
        }

        // Project just the columns the response shape needs — no need to
        // materialise a full AgentSession with its (potentially long) prompt
        // for a snapshot the UI polls every few seconds.
        var session = await _db.AgentSessions
            .IgnoreQueryFilters()
            .Where(s => s.Id == sessionId)
            .Select(s => new { s.Id, s.RuntimeId, s.Status, s.QueuePosition })
            .FirstOrDefaultAsync(ct);

        if (session is null)
        {
            return NotFound();
        }

        // Total queued length on the same runtime — Pending + non-null
        // QueuePosition. Hits IX_AgentSessions_Runtime_Status_QueuePosition.
        // Includes the session itself when it's Pending+queued; the UI
        // can derive "ahead of me" as (RuntimeQueueLength - QueuePosition)
        // when needed.
        var queueLength = await _db.AgentSessions
            .IgnoreQueryFilters()
            .CountAsync(
                s => s.RuntimeId == session.RuntimeId
                  && s.Status == AgentSessionStatus.Pending
                  && s.QueuePosition != null,
                ct);

        return Ok(new SessionPositionResponse(
            session.Id,
            session.Status,
            session.QueuePosition,
            queueLength));
    }
}

/// <summary>
/// Body shape for <c>POST /api/sessions/{sessionId}/cancel</c>. <c>Reason</c>
/// is optional — null / empty / whitespace bodies fall back to <c>"user"</c>
/// at the controller. A single nullable field keeps the request minimal and
/// gives Orval a clean optional-prop on the generated client.
/// </summary>
public record CancelSessionRequest(string? Reason);
