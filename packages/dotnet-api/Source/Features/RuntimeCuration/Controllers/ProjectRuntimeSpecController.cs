using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Source.Features.RuntimeCuration.Commands;
using Source.Features.RuntimePresets.Contracts;
using Source.Infrastructure;
using Source.Infrastructure.Extensions;

namespace Source.Features.RuntimeCuration.Controllers;

/// <summary>
/// Write-side companion to <see cref="RuntimeProposalsReadController"/>'s
/// <c>GET runtime/spec</c> endpoint. Hosts the single
/// <c>PUT runtime/spec</c> action that lets a user push a
/// <see cref="RuntimeSpecV3"/> directly to a project, bypassing the
/// propose-and-approve flow.
///
/// <para><b>Why split from the read controller.</b> The read controller is
/// named <c>RuntimeProposalsReadController</c> and conceptually owns
/// observability surfaces (proposals list + current spec). Adding a destructive
/// PUT alongside the reads would muddy that contract, so the write path lives
/// in its own controller next to it. Same route prefix
/// (<c>api/projects/{projectId:guid}</c>) so the Swagger group lines up.</para>
///
/// <para><b>Auth + access.</b> Default JWT bearer plus per-project access via
/// <see cref="OwnershipExtensions.CallerCanAccessProjectAsync"/> — SuperAdmin,
/// project owner, or workspace member. Same gate
/// <see cref="RuntimeProposalsReadController.GetSpec"/> uses on the matching
/// read, so a caller who can see the current spec can also overwrite it.
/// Non-members (and probes for non-existent projects) get <c>404</c>, never
/// <c>403</c> — same anti-leak convention as the proposal endpoints.</para>
/// </summary>
[ApiController]
[Route("api/projects/{projectId:guid}")]
[Authorize]
[Tags("ProjectRuntimeSpec")]
public class ProjectRuntimeSpecController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly ApplicationDbContext _db;

    public ProjectRuntimeSpecController(IMediator mediator, ApplicationDbContext db)
    {
        _mediator = mediator;
        _db = db;
    }

    /// <summary>
    /// Replace the project's persisted <see cref="RuntimeSpecV3"/> wholesale.
    /// Bumps <c>Project.SpecVersion</c> by one and pushes a delta to the
    /// project's most-recent live runtime (if any) — same SignalR fan-out as
    /// <see cref="ApproveProposalCommand"/>, just without the intermediate
    /// proposal row.
    ///
    /// <list type="bullet">
    ///   <item><b>200</b>: returns the new <see cref="SetProjectSpecResponse.SpecVersion"/>
    ///         + <see cref="SetProjectSpecResponse.UpdatedAt"/> so the
    ///         Settings page can render the bump without a refetch.</item>
    ///   <item><b>400</b>: <see cref="RuntimeSpecV3.Validate"/> rejected the
    ///         body (duplicate service name, missing command, etc.). The error
    ///         payload carries the validator's stable code.</item>
    ///   <item><b>404</b>: project not found OR the caller doesn't have
    ///         workspace-membership access. Uniform shape so existence isn't
    ///         leaked.</item>
    /// </list>
    /// </summary>
    [HttpPut("runtime/spec")]
    [ProducesResponseType(typeof(SetProjectSpecResponse), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(401)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<SetProjectSpecResponse>> SetSpec(
        Guid projectId,
        [FromBody] SetProjectSpecRequest request,
        CancellationToken ct)
    {
        if (!await _db.CallerCanAccessProjectAsync(User, projectId, ct))
        {
            return NotFound();
        }

        var result = await _mediator.Send(
            new SetProjectSpecCommand(projectId, request.Spec), ct);

        if (!result.IsSuccess)
        {
            return result.Error == "not_found"
                ? NotFound(new { error = result.Error })
                : BadRequest(new { error = result.Error });
        }

        return Ok(result.Value);
    }
}

/// <summary>
/// Request envelope for <see cref="ProjectRuntimeSpecController.SetSpec"/>.
/// Single-field record so the wire shape stays forward-compatible if we ever
/// add side metadata (reason, source = manual / paste / template, etc.)
/// without breaking existing callers.
/// </summary>
public record SetProjectSpecRequest(RuntimeSpecV3 Spec);
