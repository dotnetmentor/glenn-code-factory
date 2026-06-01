using System.Security.Claims;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Source.Features.RuntimeCuration.Commands;
using Source.Features.RuntimePresets.Contracts;
using Source.Features.RuntimeTokens.Models;
using Source.Infrastructure.Extensions;

namespace Source.Features.RuntimeCuration.Controllers;

/// <summary>
/// Daemon-facing endpoint for the <c>propose_runtime_spec</c> custom tool. The
/// in-runtime daemon calls <c>POST /api/runtimes/{runtimeId}/proposals</c> with
/// a <see cref="RuntimeSpecV3"/> body, and we (a) persist a
/// <c>RuntimeProposal</c> in <c>Pending</c> and (b) fan it out to the project's
/// SignalR group so the user sees a confirmation card in the chat panel.
///
/// <para><b>Auth.</b> Same RuntimeToken JWT pattern as
/// <see cref="Source.Features.Mcp.Controllers.BootstrapMcpConfigController"/> /
/// <see cref="Source.Features.RuntimeLifecycle.Controllers.RuntimeStatusController"/>:
/// JWT bearer middleware verifies signature, lifetime, issuer, audience and
/// revocation; this controller enforces that the token's <c>rt_runtime</c>
/// claim matches the path id (a daemon can only propose for itself).
/// Mismatched claim → 403, not 401, since the caller IS authenticated.</para>
///
/// <para><b>The daemon proposes, the main API installs.</b> The user resolves
/// the proposal (Approve / Edit / Reject) via
/// <see cref="RuntimeProposalDecisionsController"/>, and the main API pushes
/// a daemon-bound V2 delta back to the daemon over the existing RuntimeHub
/// channel (the V3 is expanded server-side at propose time and persisted on
/// the proposal row). The daemon never shells out to mise / supervisord on
/// its own.</para>
/// </summary>
[ApiController]
[Route("api/runtimes/{runtimeId:guid}/proposals")]
[Tags("RuntimeProposals")]
public class RuntimeProposalsController : ControllerBase
{
    private readonly IMediator _mediator;

    public RuntimeProposalsController(IMediator mediator)
    {
        _mediator = mediator;
    }

    /// <summary>
    /// Persist a new <c>RuntimeProposal</c> in <c>Pending</c> and broadcast to
    /// the project's SignalR group. 200 returns the new proposal id; 400 covers
    /// V3 structural validation failures (missing/duplicate service names,
    /// missing kinds) AND expander failures (unknown preset slug, missing
    /// required parameters, type mismatches); 401 is the middleware layer
    /// (token missing/invalid/expired/revoked); 403 is the claim cross-check;
    /// 404 means the runtime row is gone (including soft-deleted, via the
    /// global query filter).
    /// </summary>
    [HttpPost]
    [Authorize(AuthenticationSchemes = RuntimeTokenAuthenticationDefaults.SchemeName)]
    [ProducesResponseType(typeof(CreateRuntimeProposalResponse), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(401)]
    [ProducesResponseType(403)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<CreateRuntimeProposalResponse>> Create(
        Guid runtimeId,
        [FromBody] CreateRuntimeProposalRequest request,
        CancellationToken ct)
    {
        // Claim cross-check — same shape as BootstrapMcpConfigController.Get
        // and RuntimeStatusController.GetActiveSession. A daemon can only
        // propose for the runtime its token was issued for.
        var claimRaw = User.FindFirstValue(RuntimeTokenClaimNames.RuntimeId);
        if (!Guid.TryParse(claimRaw, out var claimRuntimeId) || claimRuntimeId != runtimeId)
        {
            return Forbid();
        }

        if (request?.ProposedSpec is null)
        {
            return BadRequest(new { error = "spec_required" });
        }

        var result = await _mediator.Send(new CreateRuntimeProposalCommand(
            runtimeId,
            request.ProposedSpec,
            request.Reason ?? string.Empty), ct);

        if (!result.IsSuccess)
        {
            var err = result.Error ?? "unknown_error";
            return err == "not_found"
                ? NotFound(new { error = err })
                : BadRequest(new { error = err });
        }

        return Ok(result.Value);
    }
}

/// <summary>
/// Wire shape posted by the daemon's <c>propose_runtime_spec</c> tool.
/// <see cref="ProposedSpec"/> is a <see cref="RuntimeSpecV3"/> document —
/// preset-based: each service references a preset by slug and supplies its
/// parameter values. Structural invariants enforced by
/// <see cref="RuntimeSpecV3.Validate"/>; preset existence / parameter typing
/// validated by <see cref="Source.Features.RuntimePresets.Services.IPresetExpander"/>
/// inside the create command.
/// </summary>
public record CreateRuntimeProposalRequest(
    RuntimeSpecV3 ProposedSpec,
    string? Reason);
