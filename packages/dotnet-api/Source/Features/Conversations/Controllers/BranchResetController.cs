using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Source.Features.Conversations.Commands;
using Source.Infrastructure;
using Source.Infrastructure.Extensions;

namespace Source.Features.Conversations.Controllers;

/// <summary>
/// User-facing HTTP surface for the "cancel everything on this branch" action.
/// Single endpoint at
/// <c>POST /api/projects/{projectId}/branches/{branchId}/reset</c> that walks
/// every Pending / Running / Canceling <see cref="Models.AgentSession"/> bound
/// to the branch's runtime and cancels them in one transaction. The runtime
/// itself is NOT restarted and the working tree is NOT touched — this is
/// purely a logical reset of the agent's in-flight state.
///
/// <para><b>Why a dedicated controller.</b> Mirrors
/// <see cref="Source.Features.RuntimeLifecycle.Controllers.RuntimeRestartController"/>:
/// the user thinks in <c>(projectId, branchId)</c> terms, not in the internal
/// <c>runtimeId</c>, so the URL takes the same two params; and the Swagger
/// surface stays clean by keeping branch-scoped destructive actions on their
/// own tag rather than mixed in with the per-session cancel on
/// <see cref="SessionsController"/>.</para>
///
/// <para><b>Authorisation.</b> Default JWT bearer plus per-project ownership
/// gating via <see cref="OwnershipExtensions.CallerOwnsProjectAsync"/> —
/// non-owners (and probes for non-existent projects) get a uniform <c>404</c>
/// so we don't leak project/branch existence cross-tenant. Same convention as
/// <see cref="Source.Features.RuntimeLifecycle.Controllers.RuntimeRestartController"/>
/// and <see cref="Source.Features.Diffs.Controllers.DiffsController"/>.</para>
///
/// <para><b>Idempotency.</b> Safe to call repeatedly — a second call on the
/// same branch with nothing in flight returns <c>(0, 0)</c>. Same shape as
/// the per-session cancel: a destructive action whose post-conditions are
/// expressible as a state, not a delta.</para>
/// </summary>
[ApiController]
[Route("api/projects/{projectId:guid}/branches/{branchId:guid}")]
[Authorize]
[Tags("BranchReset")]
public class BranchResetController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly ApplicationDbContext _db;
    private readonly ILogger<BranchResetController> _logger;

    public BranchResetController(
        IMediator mediator,
        ApplicationDbContext db,
        ILogger<BranchResetController> logger)
    {
        _mediator = mediator;
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Cancel every in-flight (Running / Canceling) and queued (Pending)
    /// <see cref="Models.AgentSession"/> on the branch's runtime. Returns the
    /// counts so the UI can render a "canceled 3 running and 7 queued" toast
    /// without re-fetching.
    ///
    /// <para><b>Behaviour:</b>
    /// <list type="bullet">
    ///   <item>Pending sessions → terminal <see cref="Models.AgentSessionStatus.Canceled"/>
    ///         in one DB write per session.</item>
    ///   <item>Running sessions → intermediate <see cref="Models.AgentSessionStatus.Canceling"/>
    ///         plus a <c>CancelTurn</c> push to the daemon group; the daemon's
    ///         <c>turn_canceled</c> confirmation flips them to terminal later.</item>
    ///   <item>Already-Canceling sessions → counted but not re-pushed (the
    ///         daemon already has the cancel from the earlier call).</item>
    ///   <item>Terminal sessions (Succeeded / Failed / Canceled) → ignored
    ///         entirely; not counted, not touched.</item>
    /// </list></para>
    ///
    /// <para><b>404 path.</b> Returned both when the caller doesn't own the
    /// project (uniform 404 — never 403) and when there is no runtime row for
    /// the <c>(projectId, branchId)</c> pair. A torn-down or never-spawned
    /// branch has nothing to reset.</para>
    /// </summary>
    [HttpPost("reset")]
    [ProducesResponseType(typeof(ResetBranchStateResponse), 200)]
    [ProducesResponseType(401)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<ResetBranchStateResponse>> Reset(
        Guid projectId,
        Guid branchId,
        CancellationToken ct)
    {
        // Project-ownership gate — non-owners (and probes for non-existent
        // projects) collapse to 404 so we don't leak existence. Runs BEFORE
        // the MediatR dispatch so the handler never sees an unowned project id.
        if (!await _db.CallerOwnsProjectAsync(User, projectId, ct))
        {
            return NotFound();
        }

        var result = await _mediator.Send(new ResetBranchStateCommand(projectId, branchId), ct);

        if (!result.IsSuccess)
        {
            // The only failure surface today is "runtime not found" — collapsed
            // to a uniform 404 alongside the ownership-gate miss. Logged at
            // Information because a curious probe for a non-existent branch is
            // harmless but worth tracing.
            _logger.LogInformation(
                "ResetBranchState 404: project {ProjectId}, branch {BranchId} — {Error}",
                projectId, branchId, result.Error);
            return NotFound(new { error = result.Error });
        }

        return Ok(result.Value);
    }
}
