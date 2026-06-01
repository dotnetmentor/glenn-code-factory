using System.Security.Claims;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Source.Features.Specifications.Commands;
using Source.Features.Specifications.Queries;
using Source.Infrastructure;
using Source.Infrastructure.Extensions;

namespace Source.Features.Specifications.Controllers;

/// <summary>
/// User-facing HTTP surface for project specifications. Mediates over the
/// <see cref="SaveSpecificationCommand"/> / <see cref="DeleteSpecificationCommand"/>
/// commands and the <see cref="ListSpecificationsQuery"/> /
/// <see cref="ReadSpecificationQuery"/> queries.
///
/// <para><b>Routing.</b> Lives under <c>api/projects/{projectId:guid}/specifications</c>
/// so the project scope is part of the URL — mirrors
/// <see cref="Source.Features.ProjectSecrets.Controllers.ProjectSecretsController"/>.
/// The MCP equivalent lives at <c>api/mcp/specifications/v1</c> and force-scopes via
/// the JWT claim instead.</para>
///
/// <para><b>Authorisation.</b> Every action gates on
/// <see cref="OwnershipExtensions.CallerCanAccessProjectAsync"/> — the
/// caller must be a SuperAdmin, the project owner, or a member of the
/// project's workspace. Non-members get a uniform 404 (never 403) so
/// cross-tenant existence cannot be probed. Mirrors
/// <see cref="Source.Features.Projects.Queries.GetProject.GetProjectHandler"/>
/// and the read-side gate on observability endpoints.</para>
///
/// <para><b>Typed returns.</b> Every action returns
/// <see cref="ActionResult{TValue}"/> with a concrete DTO so Swagger generates
/// usable schemas and Orval generates clean React Query hooks (per the
/// codebase's standing rule in <c>dotnet-api/CLAUDE.md</c>).</para>
/// </summary>
[ApiController]
[Route("api/projects/{projectId:guid}/specifications")]
[Authorize]
[Tags("Specifications")]
public class SpecificationsController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly ApplicationDbContext _db;

    public SpecificationsController(IMediator mediator, ApplicationDbContext db)
    {
        _mediator = mediator;
        _db = db;
    }

    /// <summary>
    /// Returns 404 when the caller cannot access the project (missing row,
    /// non-member). Uniform shape so existence cannot be probed cross-tenant.
    /// </summary>
    private async Task<ActionResult?> EnforceProjectAccessAsync(
        Guid projectId,
        CancellationToken ct)
    {
        if (!await _db.CallerCanAccessProjectAsync(User, projectId, ct))
        {
            return NotFound();
        }

        return null;
    }

    /// <summary>
    /// List every (non-soft-deleted) spec for a project, ordered by most
    /// recently updated. <c>Content</c> is omitted from the summary DTO to
    /// keep the response small — clients hit <see cref="Read"/> when they
    /// need the body.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(List<SpecificationSummaryDto>), 200)]
    [ProducesResponseType(401)]
    public async Task<ActionResult<List<SpecificationSummaryDto>>> List(
        Guid projectId,
        CancellationToken ct)
    {
        var deny = await EnforceProjectAccessAsync(projectId, ct);
        if (deny is not null) return deny;

        var result = await _mediator.Send(new ListSpecificationsQuery(projectId), ct);
        if (!result.IsSuccess)
        {
            return BadRequest(new { error = result.Error });
        }
        return Ok(result.Value);
    }

    /// <summary>
    /// Read one spec by slug. 404 when missing (same convention as the rest of
    /// the slice — cross-project lookups also surface as 404 to avoid leaking
    /// existence cross-tenant).
    /// </summary>
    [HttpGet("{slug}")]
    [ProducesResponseType(typeof(SpecificationDto), 200)]
    [ProducesResponseType(401)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<SpecificationDto>> Read(
        Guid projectId,
        string slug,
        CancellationToken ct)
    {
        var deny = await EnforceProjectAccessAsync(projectId, ct);
        if (deny is not null) return deny;

        var result = await _mediator.Send(new ReadSpecificationQuery(projectId, slug), ct);
        if (!result.IsSuccess)
        {
            return NotFound(new { error = result.Error });
        }
        return Ok(result.Value);
    }

    /// <summary>
    /// Idempotent upsert: <c>PUT /specifications/{slug}</c> with the desired
    /// name + body. Returns 201 on a fresh create and 200 on an update so
    /// clients can distinguish the two without round-tripping; the
    /// <see cref="SaveSpecificationResponse.Created"/> flag carries the same
    /// signal inside the body.
    /// </summary>
    [HttpPut("{slug}")]
    [ProducesResponseType(typeof(SaveSpecificationResponse), 200)]
    [ProducesResponseType(typeof(SaveSpecificationResponse), 201)]
    [ProducesResponseType(400)]
    [ProducesResponseType(401)]
    public async Task<ActionResult<SaveSpecificationResponse>> Save(
        Guid projectId,
        string slug,
        [FromBody] SaveSpecificationRequest request,
        CancellationToken ct)
    {
        var deny = await EnforceProjectAccessAsync(projectId, ct);
        if (deny is not null) return deny;

        if (request is null)
        {
            return BadRequest(new { error = "invalid_body" });
        }

        var actor = User.FindFirstValue(ClaimTypes.NameIdentifier);

        var result = await _mediator.Send(
            new SaveSpecificationCommand(
                projectId,
                slug,
                request.Name ?? string.Empty,
                request.Content ?? string.Empty,
                actor),
            ct);

        if (!result.IsSuccess)
        {
            return BadRequest(new { error = result.Error });
        }

        return result.Value.Created
            ? StatusCode(StatusCodes.Status201Created, result.Value)
            : Ok(result.Value);
    }

    /// <summary>
    /// Soft-delete a spec by slug. 204 on success, 404 when the slug is
    /// unknown or already deleted.
    /// </summary>
    [HttpDelete("{slug}")]
    [ProducesResponseType(204)]
    [ProducesResponseType(401)]
    [ProducesResponseType(404)]
    public async Task<ActionResult> Delete(
        Guid projectId,
        string slug,
        CancellationToken ct)
    {
        var deny = await EnforceProjectAccessAsync(projectId, ct);
        if (deny is not null) return deny;

        var result = await _mediator.Send(new DeleteSpecificationCommand(projectId, slug), ct);
        if (!result.IsSuccess)
        {
            return result.Error == "not_found"
                ? NotFound(new { error = result.Error })
                : BadRequest(new { error = result.Error });
        }
        return NoContent();
    }
}

/// <summary>
/// Body shape for <see cref="SpecificationsController.Save"/>. <c>Slug</c> is
/// taken from the route, not the body, so it can't conflict with itself.
/// </summary>
public record SaveSpecificationRequest
{
    public string? Name { get; init; }
    public string? Content { get; init; }
}
