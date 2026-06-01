using System.Security.Claims;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Source.Features.Conversations.Commands;
using Source.Infrastructure;
using Source.Infrastructure.Extensions;

namespace Source.Features.Conversations.Controllers;

/// <summary>
/// User-facing HTTP surface for the <em>urgent prompt</em> flow:
/// <c>POST /api/conversations/{conversationId}/urgent-prompt</c>. The chat
/// panel uses this when the user types a new prompt while a turn is already
/// running and confirms "yes, cancel the running turn and run this one
/// next." See <see cref="SubmitUrgentPromptCommand"/> for the handler logic
/// (preempt + head-queue + best-effort SignalR fan-out).
///
/// <para><b>Why a REST endpoint and not a hub method.</b> Submitting a normal
/// prompt happens through <c>AgentHub.SubmitPrompt</c> over SignalR because
/// the response is a streaming continuation. The urgent flow is different —
/// the response confirms a state change (cancel + queue) and the streaming
/// of the urgent turn happens later, after the canceled session terminates
/// and the dispatch-next handler picks the urgent session off the queue.
/// REST is the right surface for the explicit confirmation request: a clear
/// 200/4xx response shape, a separate URL the confirmation modal can hit,
/// and one less SignalR contract to maintain.</para>
///
/// <para><b>Why a separate controller from <see cref="ConversationsController"/>.</b>
/// ConversationsController is a thin DbContext passthrough for read-mostly
/// endpoints. This endpoint mediates a real command with side effects
/// (SignalR fan-out, queue mutation, domain event publish) — same pattern as
/// <see cref="QueueController"/> and <see cref="SessionsController"/>: one
/// controller per write surface keeps the responsibilities clean.</para>
///
/// <para><b>Authorisation.</b> Default JWT bearer plus per-conversation
/// ownership gating via
/// <see cref="OwnershipExtensions.ResolveOwnedConversationAsync"/> (the
/// conversation's parent project's <c>OwnerUserId</c> must match the caller).
/// Both "no such conversation" and "exists but not yours" surface as
/// <c>404</c> so cross-tenant existence isn't leaked — same convention as
/// <see cref="Source.Features.Diffs.Controllers.DiffsController"/>.</para>
/// </summary>
[ApiController]
[Route("api/conversations")]
[Authorize]
[Tags("UrgentPrompt")]
public class UrgentPromptController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly ApplicationDbContext _db;

    public UrgentPromptController(IMediator mediator, ApplicationDbContext db)
    {
        _mediator = mediator;
        _db = db;
    }

    /// <summary>
    /// Submit an urgent prompt to a conversation. If the runtime is currently
    /// running (or already canceling) a session, that session is asked to
    /// cancel (reason <c>"urgent_preempted"</c>) and the urgent prompt is
    /// queued at position 1 — the dispatch-next handler picks it up once the
    /// canceled session terminates. If the runtime is idle, the urgent prompt
    /// dispatches immediately and <c>StartTurn</c> is pushed to the daemon.
    ///
    /// <para>Returns the new session id, the canceled session id (if any),
    /// and whether the urgent session was queued or dispatched immediately.
    /// The frontend uses <c>Queued</c> to distinguish "wait for the cancel to
    /// land" from "the turn is already streaming."</para>
    ///
    /// <para>4xx semantics: 400 for an empty / too-long prompt or a missing
    /// runtime; 404 for a non-existent conversation. The handler returns the
    /// failure reason verbatim — the controller distinguishes 404 from 400
    /// pragmatically by looking at the error message, mirroring how
    /// <see cref="SessionsController.Cancel"/> derives 404 from
    /// <c>"Session not found"</c>.</para>
    ///
    /// <para><b>Ownership gate.</b> Verifies the caller owns the conversation's
    /// parent project via
    /// <see cref="OwnershipExtensions.ResolveOwnedConversationAsync"/> before
    /// dispatching the command — non-owners (and probes for non-existent
    /// conversations) get <c>404</c>, never <c>403</c>.</para>
    /// </summary>
    [HttpPost("{conversationId:guid}/urgent-prompt")]
    [ProducesResponseType(typeof(SubmitUrgentPromptResponse), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(401)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<SubmitUrgentPromptResponse>> Submit(
        Guid conversationId,
        [FromBody] UrgentPromptRequest request,
        CancellationToken ct)
    {
        // Defensive — model binding will produce an empty Prompt for a missing
        // body. Surface a structured 400 rather than letting the handler do it,
        // so the response body shape is consistent with the other validation
        // failures in this controller.
        if (request is null)
        {
            return BadRequest(new { error = "Request body is required" });
        }

        // Ownership gate — null on either "no such conversation" or "not yours",
        // both surfaced uniformly as 404 so cross-tenant existence isn't leaked.
        // Runs before the MediatR dispatch so the handler never sees an unowned
        // conversation id.
        if (await _db.ResolveOwnedConversationAsync(User, conversationId, ct) is null)
        {
            return NotFound();
        }

        // "unknown" sentinel mirrors the cancel/reorder controllers: claim is
        // always present on an authenticated request, but we want a defined
        // value on the audit row even if the JWT shape ever drifts.
        var actorUserId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "unknown";

        var result = await _mediator.Send(
            new SubmitUrgentPromptCommand(conversationId, request.Prompt, actorUserId),
            ct);

        if (!result.IsSuccess)
        {
            // "Conversation not found" → 404; everything else (empty prompt,
            // too-long prompt, no runtime) → 400. Pragmatic message-substring
            // dispatch, same convention as the rest of the codebase.
            if (result.Error is not null
                && result.Error.Contains("not found", StringComparison.OrdinalIgnoreCase))
            {
                return NotFound(new { error = result.Error });
            }
            return BadRequest(new { error = result.Error });
        }

        return Ok(result.Value);
    }
}

/// <summary>
/// Body shape for <c>POST /api/conversations/{conversationId}/urgent-prompt</c>.
/// A bare prompt string is enough — non-empty / length validation lives in
/// the handler so the same rules apply if a future caller invokes the command
/// directly.
/// </summary>
public record UrgentPromptRequest(string Prompt);
