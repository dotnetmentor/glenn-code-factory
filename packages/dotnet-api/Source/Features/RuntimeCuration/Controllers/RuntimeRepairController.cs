using System.Security.Claims;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Source.Features.RuntimeCuration.Commands;
using Source.Infrastructure;
using Source.Infrastructure.Extensions;

namespace Source.Features.RuntimeCuration.Controllers;

/// <summary>
/// User-facing HTTP surface for the runtime self-heal flow
/// (self-healing-runtime-specs, cards B2 + B3):
/// <c>POST /api/runtimes/{runtimeId}/repair</c>. The amber degraded-banner's
/// "Let agent fix it" button hits this; the handler composes a diagnostic
/// prompt, dispatches a system turn into the runtime's conversation, and arms
/// budgeted auto-apply consent so the agent's corrected spec applies without a
/// second click. See <see cref="RepairRuntimeCommand"/> for the full flow.
///
/// <para><b>Why a separate controller from the daemon-facing
/// <see cref="RuntimeProposalsController"/>.</b> That one runs under the
/// <c>RuntimeToken</c> scheme (a daemon proposing for itself); this one is a
/// user JWT write surface (an operator clicking repair). Different auth model,
/// different audience — one controller per surface keeps the slice clean, same
/// convention as <see cref="Source.Features.Conversations.Controllers.UrgentPromptController"/>.</para>
///
/// <para><b>Authorisation.</b> Default JWT bearer plus per-runtime access
/// gating via <see cref="OwnershipExtensions.ResolveAccessibleRuntimeAsync"/>:
/// SuperAdmin OR the runtime's project owner OR a member of the project's
/// workspace. This matches the spec's "project owner / workspace member /
/// SuperAdmin" gate and the read-side observability convention from B1's
/// <see cref="Source.Features.RuntimeLifecycle.Controllers.RuntimeStatusController"/>
/// — the degraded banner is visible to every workspace teammate, so the repair
/// action is too. Both "no such runtime" and "exists but no access" surface as
/// <c>404</c> so cross-tenant runtime existence isn't leaked.</para>
/// </summary>
[ApiController]
[Route("api/runtimes")]
[Authorize]
[Tags("RuntimeRepair")]
public class RuntimeRepairController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly ApplicationDbContext _db;

    public RuntimeRepairController(IMediator mediator, ApplicationDbContext db)
    {
        _mediator = mediator;
        _db = db;
    }

    /// <summary>
    /// Trigger the agent self-heal loop for a degraded runtime: dispatch a
    /// diagnostic system turn and arm budgeted auto-apply consent. Returns the
    /// dispatched session id and the armed consent window/budget.
    ///
    /// <para>4xx semantics: 404 for a non-existent runtime OR one the caller
    /// can't access (uniform anti-leak); 409 when the runtime has exhausted its
    /// windowed repair budget (<c>repair_attempts_exhausted</c>); 400 for a
    /// dispatch failure (<c>dispatch_failed</c> — no live runtime for the branch,
    /// etc.).</para>
    /// </summary>
    [HttpPost("{runtimeId:guid}/repair")]
    [ProducesResponseType(typeof(RepairRuntimeResponse), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(401)]
    [ProducesResponseType(404)]
    [ProducesResponseType(409)]
    public async Task<ActionResult<RepairRuntimeResponse>> Repair(
        Guid runtimeId,
        CancellationToken ct)
    {
        // Access gate — SuperAdmin / project owner / workspace member. Null on
        // either "no such runtime" or "not yours", both surfaced as 404 so
        // cross-tenant existence isn't leaked. Runs before the dispatch so the
        // handler never sees an inaccessible runtime id.
        if (await _db.ResolveAccessibleRuntimeAsync(User, runtimeId, ct) is null)
        {
            return NotFound();
        }

        var actorUserId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "unknown";

        var result = await _mediator.Send(new RepairRuntimeCommand(runtimeId, actorUserId), ct);

        if (!result.IsSuccess)
        {
            return result.Error switch
            {
                "not_found" => NotFound(),
                "repair_attempts_exhausted" => Conflict(new { error = result.Error }),
                _ => BadRequest(new { error = result.Error }),
            };
        }

        return Ok(result.Value);
    }
}
