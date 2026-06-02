using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Source.Features.RuntimeLifecycle.Commands.ForceStopRuntime;
using Source.Features.RuntimeLifecycle.Commands.RestartRuntime;
using Source.Features.RuntimeLifecycle.Commands.SuspendRuntime;
using Source.Features.RuntimeLifecycle.Models;
using Source.Infrastructure;
using Source.Infrastructure.Extensions;
using Source.Shared.Controllers;

namespace Source.Features.RuntimeLifecycle.Controllers;

/// <summary>
/// User-facing HTTP surface for restarting a project's runtime when it is
/// stuck in <see cref="RuntimeState.Failed"/> or <see cref="RuntimeState.Crashed"/>.
/// The endpoint walks the runtime to <c>Pending</c> and the existing
/// <c>RuntimeProvisionerJob</c> (re)spawns a fresh Fly machine on the existing
/// volume on its next 60s tick. <see cref="RuntimeState.Suspended"/> runtimes
/// are delegated to the wake path instead — the machine + volume are already
/// intact and only the VM needs starting.
///
/// <para><b>Authorisation.</b> Default JWT bearer plus per-project ownership
/// gating via <see cref="OwnershipExtensions.CallerOwnsProjectAsync"/> — both
/// "no such project" and "exists but not yours" surface as <c>404</c> so we
/// don't leak project/runtime existence cross-tenant. Same convention as
/// <see cref="Source.Features.Diffs.Controllers.DiffsController"/>.</para>
///
/// <para><b>Why a dedicated controller.</b> The restart endpoint lives on
/// <c>POST /api/projects/{projectId}/branches/{branchId}/runtime/restart</c>
/// rather than next to the operator-only transitions on
/// <see cref="RuntimeAdminController"/> because:
/// <list type="bullet">
///   <item>the consumer is the project owner, not an operator, so a separate
///         route + tag keeps the Swagger surface for non-admin users clean;</item>
///   <item>the gating differs (Authorize vs SuperAdmin) — colocating would
///         force per-action policies which the rest of this codebase avoids;</item>
///   <item>the addressing differs too: this endpoint takes <c>projectId</c> +
///         <c>branchId</c> from the URL (the user thinks in terms of "the
///         branch I'm looking at") rather than the internal <c>runtimeId</c>
///         which they have no reason to know.</item>
/// </list></para>
/// </summary>
[ApiController]
[Route("api/projects/{projectId:guid}/branches/{branchId:guid}/runtime")]
[Authorize]
[Tags("RuntimeRestart")]
public class RuntimeRestartController : BaseApiController
{
    private readonly ApplicationDbContext _db;

    public RuntimeRestartController(
        IMediator mediator,
        ILogger<RuntimeRestartController> logger,
        ApplicationDbContext db)
        : base(mediator, logger)
    {
        _db = db;
    }

    /// <summary>
    /// Restart the most-recent (non-deleted) runtime for the
    /// <c>(projectId, branchId)</c> pair. Suspended runtimes wake; Online,
    /// mid-boot, Failed, and Crashed runtimes hard-reboot on the existing
    /// volume (Fly machine stopped first when attached). Returns the
    /// <see cref="RuntimeStatusResponse"/> snapshot immediately.
    /// </summary>
    [HttpPost("restart")]
    [ProducesResponseType(typeof(RuntimeStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<RuntimeStatusResponse>> Restart(
        Guid projectId,
        Guid branchId,
        CancellationToken ct)
    {
        var userIdRaw = GetCurrentUserId();
        if (string.IsNullOrEmpty(userIdRaw) || !Guid.TryParse(userIdRaw, out var userId))
        {
            return Unauthorized();
        }

        // Project-ownership gate — non-owners (and probes for non-existent
        // projects) collapse to 404 so we don't leak project/runtime existence
        // cross-tenant. Runs before the MediatR dispatch so the handler never
        // sees an unowned project id.
        if (!await _db.CallerOwnsProjectAsync(User, projectId, ct))
        {
            return NotFound();
        }

        var result = await Mediator.Send(
            new RestartRuntimeCommand(projectId, branchId, userId),
            ct);

        if (result.IsSuccess)
        {
            return Ok(result.Value);
        }

        // Sentinel-prefix mapping — same shape as GetProjectHandler.NotFoundPrefix /
        // CopyBranchHandler.ForbiddenPrefix. Keeps the controller-side dispatch
        // declarative and the handler free of HTTP concerns.
        if (result.Error?.StartsWith(RestartRuntimeHandler.NotFoundPrefix, StringComparison.Ordinal) == true)
        {
            Logger.LogInformation(
                "RestartRuntime 404: project {ProjectId}, branch {BranchId}, user {UserId} — {Error}",
                projectId, branchId, userId, result.Error);
            return NotFound(new { error = result.Error });
        }

        if (result.Error?.StartsWith(RestartRuntimeHandler.ConflictPrefix, StringComparison.Ordinal) == true)
        {
            Logger.LogInformation(
                "RestartRuntime 409: project {ProjectId}, branch {BranchId}, user {UserId} — {Error}",
                projectId, branchId, userId, result.Error);
            return Conflict(new { error = result.Error });
        }

        Logger.LogWarning(
            "RestartRuntime failed: project {ProjectId}, branch {BranchId}, user {UserId} — {Error}",
            projectId, branchId, userId, result.Error);
        return BadRequest(new { error = result.Error });
    }

    /// <summary>
    /// Park the most-recent (non-deleted) runtime for the
    /// <c>(projectId, branchId)</c> pair. Legal only from
    /// <see cref="RuntimeState.Online"/> — mid-boot parking remains on the
    /// operator <see cref="RuntimeAdminController.ForceStop"/> surface.
    /// Returns the <see cref="RuntimeStatusResponse"/> snapshot immediately
    /// so the frontend can re-render to <c>Suspending</c> without a follow-up GET.
    /// </summary>
    [HttpPost("suspend")]
    [ProducesResponseType(typeof(RuntimeStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<RuntimeStatusResponse>> Suspend(
        Guid projectId,
        Guid branchId,
        CancellationToken ct)
    {
        var userIdRaw = GetCurrentUserId();
        if (string.IsNullOrEmpty(userIdRaw) || !Guid.TryParse(userIdRaw, out var userId))
        {
            return Unauthorized();
        }

        if (!await _db.CallerOwnsProjectAsync(User, projectId, ct))
        {
            return NotFound();
        }

        var result = await Mediator.Send(
            new SuspendRuntimeCommand(projectId, branchId, userId),
            ct);

        if (result.IsSuccess)
        {
            return Ok(result.Value);
        }

        if (result.Error?.StartsWith(SuspendRuntimeHandler.NotFoundPrefix, StringComparison.Ordinal) == true)
        {
            Logger.LogInformation(
                "SuspendRuntime 404: project {ProjectId}, branch {BranchId}, user {UserId} — {Error}",
                projectId, branchId, userId, result.Error);
            return NotFound(new { error = result.Error });
        }

        if (result.Error?.StartsWith(SuspendRuntimeHandler.ConflictPrefix, StringComparison.Ordinal) == true)
        {
            Logger.LogInformation(
                "SuspendRuntime 409: project {ProjectId}, branch {BranchId}, user {UserId} — {Error}",
                projectId, branchId, userId, result.Error);
            return Conflict(new { error = result.Error });
        }

        Logger.LogWarning(
            "SuspendRuntime failed: project {ProjectId}, branch {BranchId}, user {UserId} — {Error}",
            projectId, branchId, userId, result.Error);
        return BadRequest(new { error = result.Error });
    }

    /// <summary>
    /// Force-stop the most-recent (non-deleted) runtime for the
    /// <c>(projectId, branchId)</c> pair. Accepts <see cref="RuntimeState.Online"/>
    /// and mid-boot states — parks the Fly machine to <see cref="RuntimeState.Suspending"/>.
    /// </summary>
    [HttpPost("force-stop")]
    [ProducesResponseType(typeof(RuntimeStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<RuntimeStatusResponse>> ForceStop(
        Guid projectId,
        Guid branchId,
        CancellationToken ct)
    {
        var userIdRaw = GetCurrentUserId();
        if (string.IsNullOrEmpty(userIdRaw) || !Guid.TryParse(userIdRaw, out var userId))
        {
            return Unauthorized();
        }

        if (!await _db.CallerOwnsProjectAsync(User, projectId, ct))
        {
            return NotFound();
        }

        var result = await Mediator.Send(
            new ForceStopRuntimeCommand(projectId, branchId, userId),
            ct);

        if (result.IsSuccess)
        {
            return Ok(result.Value);
        }

        if (result.Error?.StartsWith(ForceStopRuntimeHandler.NotFoundPrefix, StringComparison.Ordinal) == true)
        {
            Logger.LogInformation(
                "ForceStopRuntime 404: project {ProjectId}, branch {BranchId}, user {UserId} — {Error}",
                projectId, branchId, userId, result.Error);
            return NotFound(new { error = result.Error });
        }

        if (result.Error?.StartsWith(ForceStopRuntimeHandler.ConflictPrefix, StringComparison.Ordinal) == true)
        {
            Logger.LogInformation(
                "ForceStopRuntime 409: project {ProjectId}, branch {BranchId}, user {UserId} — {Error}",
                projectId, branchId, userId, result.Error);
            return Conflict(new { error = result.Error });
        }

        Logger.LogWarning(
            "ForceStopRuntime failed: project {ProjectId}, branch {BranchId}, user {UserId} — {Error}",
            projectId, branchId, userId, result.Error);
        return BadRequest(new { error = result.Error });
    }
}
