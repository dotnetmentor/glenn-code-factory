using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Source.Features.GitOps.Models;
using Source.Features.SignalR.Hubs;
using Source.Infrastructure;
using Source.Infrastructure.Extensions;

namespace Source.Features.GitOps.Controllers;

/// <summary>
/// User-facing HTTP surface for the destructive-git-op approval flow. The
/// daemon emits a <c>RequestDestructiveGitOp</c> roundtrip when it wants to
/// run a <c>reset</c>, <c>force-push</c> or <c>branch-delete</c>; that lands a
/// stub <see cref="GitOperation"/> row tagged with <see cref="GitOperation.WasDestructive"/>
/// and a fresh <see cref="GitOperation.ApprovalId"/>. This controller is the
/// half of the loop that actually unblocks the daemon: a user reviews the
/// request, decides "yes, run it", and POSTs to this endpoint. The hub then
/// fans out an <see cref="IRuntimeClient.ExecuteDestructiveGitOp"/> call to the
/// runtime's group, the daemon picks it up off its in-memory approval map and
/// proceeds with the held-pending command.
///
/// <para><b>Why no MediatR.</b> Same pragmatic reasoning as
/// <see cref="Source.Features.Hooks.Controllers.HookConfigAdminController"/> and
/// <see cref="Source.Features.RuntimeLifecycle.Controllers.RuntimeStatusController"/>:
/// this is a thin existence/state check followed by a single SignalR fan-out.
/// Wrapping it in a command/handler pair would add four files without changing
/// behaviour — the slice stays thin and the controller talks straight to the
/// DbContext + hub.</para>
///
/// <para><b>Authorisation.</b> Default JWT bearer (cookie-backed user session)
/// — explicitly NOT the RuntimeToken scheme. This is a user-facing surface;
/// the daemon never calls it. Per-project ownership is verified via
/// <see cref="OwnershipExtensions.CallerOwnsProjectAsync"/> on the project id
/// in the route, AND we verify the targeted op belongs to that project (defense
/// in depth — a leaked op id from another tenant must not slip through). Both
/// "no such project" and "not yours" surface as <c>404</c>.</para>
///
/// <para><b>Denial path.</b> Out of scope for this card. A "deny" / DELETE
/// counterpart is deferred to a follow-up — for now an unapproved request just
/// times out on the daemon's in-memory map (daemon-side detail) and the audit
/// row's <see cref="GitOperation.EndedAt"/> stays null until the daemon either
/// runs it or marks it expired on its own clock.</para>
/// </summary>
[ApiController]
[Route("api/projects/{projectId:guid}/git/destructive-ops")]
[Authorize]
[Tags("GitOps")]
public class GitDestructiveOpsController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    private readonly IHubContext<RuntimeHub, IRuntimeClient> _hub;
    private readonly ILogger<GitDestructiveOpsController> _logger;

    public GitDestructiveOpsController(
        ApplicationDbContext db,
        IHubContext<RuntimeHub, IRuntimeClient> hub,
        ILogger<GitDestructiveOpsController> logger)
    {
        _db = db;
        _hub = hub;
        _logger = logger;
    }

    /// <summary>
    /// Approve a destructive git operation that the daemon has previously
    /// requested. Returns 200 with the approved op id once the fan-out has
    /// been queued on the runtime's hub group; the actual execution is
    /// asynchronous and surfaces back through the regular
    /// <c>GitOperationStarted</c> / <c>GitOperationCompleted</c> events.
    ///
    /// <para><b>Status taxonomy.</b></para>
    /// <list type="bullet">
    ///   <item><b>404</b> — op not found, or the op's runtime doesn't belong to
    ///         <paramref name="projectId"/>. We don't differentiate cross-tenant
    ///         existence leaks from genuine 404s; both collapse to 404 by design.</item>
    ///   <item><b>400</b> — op exists but isn't flagged destructive. Approving a
    ///         non-destructive op is a programming error in the caller, not a
    ///         business rule, so we surface it with a clear message.</item>
    ///   <item><b>409</b> — op already executed or expired (i.e.
    ///         <see cref="GitOperation.EndedAt"/> is set). Approving a closed op
    ///         is a no-op on the daemon's side anyway, but we make the conflict
    ///         explicit so the UI can refresh state instead of silently moving on.</item>
    /// </list>
    ///
    /// <para><b>Ownership gate.</b> Verifies the caller owns the project via
    /// <see cref="OwnershipExtensions.CallerOwnsProjectAsync"/> AND scopes the
    /// op-existence query by <paramref name="projectId"/> (defense in depth —
    /// a leaked op id from another tenant must collapse to <c>404</c>, not
    /// 403). Both "no such project / op" and "not yours" return <c>404</c> so
    /// cross-tenant existence isn't leaked.</para>
    /// </summary>
    [HttpPost("{opId:guid}/approve")]
    [ProducesResponseType(typeof(AcceptedResponse), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(401)]
    [ProducesResponseType(404)]
    [ProducesResponseType(409)]
    public async Task<ActionResult<AcceptedResponse>> Approve(
        Guid projectId,
        Guid opId,
        CancellationToken ct)
    {
        // 0. Project ownership gate. Returning 404 (not 403) on ownership
        // mismatch so a probing client can't distinguish "no such project" from
        // "exists but not yours".
        if (!await _db.CallerOwnsProjectAsync(User, projectId, ct))
        {
            return NotFound();
        }

        // 1. Op existence — and a defense-in-depth filter that the op's runtime
        // belongs to the route's projectId. Soft-deleted rows are filtered by
        // the global query filter on GitOperation, so a janitor-marked row
        // falls through to 404 just like a hard-missing one.
        var op = await _db.GitOperations
            .AsNoTracking()
            .Where(o => o.Id == opId
                     && _db.ProjectRuntimes.Any(r => r.Id == o.RuntimeId && r.ProjectId == projectId))
            .FirstOrDefaultAsync(ct);
        if (op is null)
        {
            return NotFound();
        }

        // 2. Must be a destructive op. A non-destructive op has no in-memory
        // approval record on the daemon side, so approving it would be a no-op
        // at best and a confusing audit trail at worst. Surface the mismatch
        // explicitly so the caller fixes their UI rather than silently moving on.
        if (!op.WasDestructive)
        {
            return BadRequest(new { error = "operation is not flagged destructive; nothing to approve" });
        }

        // 3. Must still be open. Once EndedAt is set the row is immutable and
        // the daemon's approval map has either consumed the entry (executed) or
        // dropped it (expired). Re-approving wouldn't reach a live request.
        if (op.EndedAt is not null)
        {
            return Conflict(new { error = "operation already executed or expired" });
        }

        // 4. Fan-out to the runtime's hub group. Best-effort: if the daemon is
        // offline at the moment of approval, the call is a no-op and the user
        // can re-approve once the daemon reconnects (the audit row is still
        // open because EndedAt is still null). We don't try to queue a
        // pending-approval delivery here — the daemon's in-memory map is the
        // authoritative store for "what's waiting on a decision".
        try
        {
            await _hub.Clients
                .Group($"runtime-{op.RuntimeId}")
                .ExecuteDestructiveGitOp(opId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "GitDestructiveOpsController: ExecuteDestructiveGitOp push failed for runtime {RuntimeId}, op {OpId}; the user can re-approve once the daemon reconnects.",
                op.RuntimeId, opId);
        }

        _logger.LogInformation(
            "GitDestructiveOpsController: approved destructive op {OpId} on runtime {RuntimeId} for project {ProjectId}.",
            opId, op.RuntimeId, projectId);

        return Ok(new AcceptedResponse(opId));
    }
}

/// <summary>
/// 200 response shape for fan-out endpoints in this controller. Carries the
/// id the caller can use to follow the operation through the standard git-op
/// event stream — for the destructive approval flow this is the
/// <see cref="GitOperation.Id"/>.
/// </summary>
public record AcceptedResponse(Guid OperationId);
