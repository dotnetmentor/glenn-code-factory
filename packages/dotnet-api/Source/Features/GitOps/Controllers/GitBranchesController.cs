using System.Security.Claims;
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
/// User-facing HTTP surface for branch-level git actions on a project's
/// runtime. v1 only carries the merge endpoint — branch list / create / delete
/// land in follow-up cards.
///
/// <para><b>Why no MediatR.</b> Same pragmatic reasoning as
/// <see cref="GitDestructiveOpsController"/> and
/// <see cref="Source.Features.RuntimeLifecycle.Controllers.RuntimeStatusController"/>:
/// thin runtime-resolution + single SignalR fan-out, no cross-slice events.
/// Wrapping it in a command/handler pair would add four files without
/// changing behaviour.</para>
///
/// <para><b>Authorisation.</b> Default JWT bearer (cookie-backed user session)
/// — explicitly NOT the RuntimeToken scheme. The daemon never calls this; it's
/// the user clicking "merge" in the UI. Per-project ownership is verified via
/// <see cref="OwnershipExtensions.CallerOwnsProjectAsync"/>; non-owners (and
/// callers naming a non-existent project) get a uniform <c>404</c> so we don't
/// leak project-id existence cross-tenant.</para>
/// </summary>
[ApiController]
[Route("api/projects/{projectId:guid}/branches")]
[Authorize]
[Tags("GitOps")]
public class GitBranchesController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    private readonly IHubContext<RuntimeHub, IRuntimeClient> _hub;
    private readonly ILogger<GitBranchesController> _logger;

    public GitBranchesController(
        ApplicationDbContext db,
        IHubContext<RuntimeHub, IRuntimeClient> hub,
        ILogger<GitBranchesController> logger)
    {
        _db = db;
        _hub = hub;
        _logger = logger;
    }

    /// <summary>
    /// Ask the project's runtime to merge <see cref="MergeBranchRequest.SourceBranch"/>
    /// into the path's <c>targetBranch</c>. Fan-out only: the merge runs
    /// asynchronously inside the daemon and surfaces back through the regular
    /// <c>GitOperationStarted</c> / <c>GitOperationCompleted</c> stream (and a
    /// <c>MergeConflict</c> event on conflicts).
    ///
    /// <para><b>v1 runtime resolution.</b> One runtime per project. We pick the
    /// most-recent (non-deleted) <see cref="Source.Features.RuntimeLifecycle.Models.ProjectRuntime"/>
    /// for the project — same convention as
    /// <see cref="Source.Features.RuntimeLifecycle.Controllers.RuntimeStatusController.GetStatus"/>.
    /// When per-project multi-runtime arrives, the path will need a runtime id
    /// segment.</para>
    ///
    /// <para><b>Ownership gate.</b> Verifies the caller owns the project via
    /// <see cref="OwnershipExtensions.CallerOwnsProjectAsync"/> before reading
    /// the runtime — both "no such project" and "not yours" surface as
    /// <c>404</c> so cross-tenant project-id existence isn't leaked.</para>
    /// </summary>
    [HttpPost("{targetBranch}/merge")]
    [ProducesResponseType(typeof(AcceptedResponse), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(401)]
    [ProducesResponseType(404)]
    [ProducesResponseType(409)]
    public async Task<ActionResult<AcceptedResponse>> Merge(
        Guid projectId,
        string targetBranch,
        [FromBody] MergeBranchRequest request,
        CancellationToken ct)
    {
        if (!await _db.CallerOwnsProjectAsync(User, projectId, ct))
        {
            return NotFound();
        }

        // Most-recent (non-deleted) runtime per project. Soft-deleted rows are
        // filtered out by the global query filter on ProjectRuntime, so a
        // 404 here is the right signal even after a teardown.
        var runtime = await _db.ProjectRuntimes
            .Where(r => r.ProjectId == projectId)
            .OrderByDescending(r => r.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (runtime is null)
        {
            return NotFound();
        }

        // Stamp the requesting user at the edge so the audit trail attributes
        // the merge to a real principal — the actual `git merge` runs as the
        // daemon process on the runtime, but the UI needs to know who asked.
        // "unknown" is the explicit fallback for tokens missing a sub claim;
        // surfacing a literal null in the audit row would be worse than a
        // typed sentinel.
        var requestedBy = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "unknown";

        var payload = new MergeBranchPayload(
            SourceBranch: request.SourceBranch,
            TargetBranch: targetBranch,
            RequestedBy: requestedBy);

        // Fan-out to the runtime's hub group. Best-effort: if the daemon is
        // offline the call is a no-op; the user retries when the runtime is
        // back. We don't queue a pending merge here — that would mean a second
        // source of truth alongside the daemon's own queue.
        try
        {
            await _hub.Clients
                .Group($"runtime-{runtime.Id}")
                .MergeBranch(payload);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "GitBranchesController: MergeBranch push failed for runtime {RuntimeId}, project {ProjectId}; user can retry once the daemon reconnects.",
                runtime.Id, projectId);
        }

        _logger.LogInformation(
            "GitBranchesController: queued merge {SourceBranch} -> {TargetBranch} on runtime {RuntimeId} (project {ProjectId}, requestedBy={RequestedBy}).",
            request.SourceBranch, targetBranch, runtime.Id, projectId, requestedBy);

        // The fan-out is asynchronous, so the only id we have to give the
        // caller for follow-up is the runtime — the daemon will mint the
        // GitOperation row when it actually starts the merge.
        return Ok(new AcceptedResponse(runtime.Id));
    }
}

/// <summary>
/// Request body for <see cref="GitBranchesController.Merge"/>. The target
/// branch is in the path; the source is the body. Keeping source in the body
/// lets us extend with merge options (squash, no-ff, message) later without
/// breaking the URL shape.
/// </summary>
public record MergeBranchRequest(string SourceBranch);
