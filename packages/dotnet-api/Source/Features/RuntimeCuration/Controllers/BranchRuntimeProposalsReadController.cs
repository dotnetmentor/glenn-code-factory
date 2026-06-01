using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Source.Features.RuntimeCuration.Queries;
using Source.Infrastructure;
using Source.Infrastructure.Extensions;

namespace Source.Features.RuntimeCuration.Controllers;

/// <summary>
/// Branch-scoped read surface for runtime curation — the per-branch apply
/// history table rendered in the super-admin runtime drawer's "Spec" tab
/// (audit item A7). Project-scoped reads still live on
/// <see cref="RuntimeProposalsReadController"/>; this controller exists
/// because the drawer is opened from a specific branch tab and a project
/// with multiple branches has one <c>ProjectRuntime</c> per branch — folding
/// the route in here would surface sibling-branch proposals in the wrong tab.
///
/// <para><b>Auth + access.</b> Default JWT bearer plus per-project access
/// gating via <see cref="OwnershipExtensions.CallerCanAccessProjectAsync"/> —
/// SuperAdmin OR project owner OR a member of the project's workspace. Same
/// read-side widening as <see cref="Source.Features.RuntimeLifecycle.Controllers.RuntimeStatusController"/>:
/// the apply-history table is consumed by the in-workspace debug panel's Spec
/// view, and even though the UI gates the tab to super-admins initially, the
/// endpoint itself permits any workspace member so a future relaxation needs
/// no backend change (spec: workspace-runtime-observability, Section E).
/// Callers with no access (and probes for non-existent projects) get <c>404</c>,
/// never <c>403</c>, so cross-tenant project/branch existence isn't leaked.</para>
///
/// <para><b>Why the response is always 200 (even with no runtime).</b> The
/// drawer is the read-side for an audit table; an empty list is a perfectly
/// valid first-render state ("no apply history yet"). The handler resolves
/// the runtime internally and returns <c>[]</c> when none exists, so the
/// drawer never has to special-case a 404 on a tab it's just opening.</para>
/// </summary>
[ApiController]
[Route("api/projects/{projectId:guid}/branches/{branchId:guid}")]
[Authorize]
[Tags("RuntimeProposalsRead")]
public class BranchRuntimeProposalsReadController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly ApplicationDbContext _db;

    public BranchRuntimeProposalsReadController(IMediator mediator, ApplicationDbContext db)
    {
        _mediator = mediator;
        _db = db;
    }

    /// <summary>
    /// Apply history for the (project, branch) runtime — terminal-decided
    /// proposals (<c>Applied</c> and <c>Failed</c>), newest first, with the
    /// total apply duration and per-phase timings inlined from the matching
    /// <c>SpecDeltaApplied</c> / <c>SpecDeltaFailed</c> event payloads.
    ///
    /// <para>The <c>status</c> query parameter is accepted but the handler
    /// hard-codes the filter to <c>Applied,Failed</c> — the spec calls for it
    /// (audit item A7) and the surface is purpose-built for that pair. We
    /// keep the parameter in the route for documentation symmetry with the
    /// project-scoped sibling and so the spec's exact URL works verbatim.</para>
    ///
    /// <para><c>limit</c> defaults to 20 (per the spec) and is clamped to
    /// <c>[1, 100]</c> by the handler. Always returns 200 with a list — see
    /// the type doc above for why "no runtime" is an empty list, not a 404.</para>
    /// </summary>
    [HttpGet("runtime/proposals")]
    [ProducesResponseType(typeof(List<RuntimeApplyHistoryItem>), 200)]
    [ProducesResponseType(401)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<List<RuntimeApplyHistoryItem>>> GetApplyHistory(
        Guid projectId,
        Guid branchId,
        [FromQuery] string? status = null,
        [FromQuery] int limit = 20,
        CancellationToken ct = default)
    {
        if (!await _db.CallerCanAccessProjectAsync(User, projectId, ct))
        {
            return NotFound();
        }

        // The `status` query parameter is accepted to honour the spec's URL
        // shape (`?status=Applied,Failed`) but the handler hard-codes the
        // filter. We deliberately don't validate the value here — passing
        // anything is a no-op rather than a 400 — because the drawer always
        // sends the documented value and a stricter check would just create
        // brittleness on a stable contract.
        _ = status;

        var result = await _mediator.Send(
            new GetRuntimeApplyHistoryQuery(projectId, branchId, limit), ct);
        return result.IsSuccess
            ? Ok(result.Value)
            : BadRequest(new { error = result.Error });
    }
}
