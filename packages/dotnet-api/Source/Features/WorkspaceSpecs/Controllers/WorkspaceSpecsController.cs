using System.ComponentModel.DataAnnotations;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Source.Features.WorkspaceSpecs.Commands;
using Source.Features.WorkspaceSpecs.Queries;
using Source.Shared.Controllers;
using Source.Shared.Results;

namespace Source.Features.WorkspaceSpecs.Controllers;

/// <summary>
/// REST endpoints for the workspace spec catalog. All routes scope by
/// <c>workspaceId</c> in the URL; every handler verifies the caller is a
/// member of that workspace before doing anything else. Non-member access
/// (including cross-workspace specId lookups) is mapped to <c>403</c>.
/// </summary>
[ApiController]
[Route("api/workspaces/{workspaceId:guid}/specs")]
[Authorize]
[Tags("WorkspaceSpecs")]
public class WorkspaceSpecsController : BaseApiController
{
    public WorkspaceSpecsController(IMediator mediator, ILogger<WorkspaceSpecsController> logger)
        : base(mediator, logger) { }

    /// <summary>
    /// List every catalog spec belonging to the workspace. Excludes the
    /// (potentially large) jsonb <c>Content</c> blob — use
    /// <see cref="GetOne"/> to load full content on demand.
    /// </summary>
    [HttpGet]
    [ProducesResponseType<List<WorkspaceSpecListItem>>(200)]
    [ProducesResponseType(401)]
    [ProducesResponseType(403)]
    public async Task<ActionResult<List<WorkspaceSpecListItem>>> List(Guid workspaceId)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        var result = await Mediator.Send(new ListWorkspaceSpecsQuery(workspaceId, userId));
        return MapResult(result);
    }

    /// <summary>Get one catalog spec with full <c>Content</c>.</summary>
    [HttpGet("{specId:guid}")]
    [ProducesResponseType<WorkspaceSpecDetail>(200)]
    [ProducesResponseType(401)]
    [ProducesResponseType(403)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<WorkspaceSpecDetail>> GetOne(Guid workspaceId, Guid specId)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        var result = await Mediator.Send(new GetWorkspaceSpecQuery(workspaceId, specId, userId));
        return MapResult(result);
    }

    /// <summary>Create a new catalog spec.</summary>
    [HttpPost]
    [ProducesResponseType<WorkspaceSpecDetail>(201)]
    [ProducesResponseType(400)]
    [ProducesResponseType(401)]
    [ProducesResponseType(403)]
    [ProducesResponseType(409)]
    public async Task<ActionResult<WorkspaceSpecDetail>> Create(
        Guid workspaceId,
        [FromBody] CreateWorkspaceSpecRequest request)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        var result = await Mediator.Send(new CreateWorkspaceSpecCommand(
            WorkspaceId: workspaceId,
            CallerUserId: userId,
            Name: request.Name,
            Description: request.Description,
            Content: request.Content));

        if (result.IsSuccess)
        {
            return CreatedAtAction(nameof(GetOne),
                new { workspaceId, specId = result.Value.Id },
                result.Value);
        }
        return MapFailure<WorkspaceSpecDetail>(result.Error);
    }

    /// <summary>Update a catalog spec's name, description, and content.</summary>
    [HttpPut("{specId:guid}")]
    [ProducesResponseType<WorkspaceSpecDetail>(200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(401)]
    [ProducesResponseType(403)]
    [ProducesResponseType(404)]
    [ProducesResponseType(409)]
    public async Task<ActionResult<WorkspaceSpecDetail>> Update(
        Guid workspaceId,
        Guid specId,
        [FromBody] UpdateWorkspaceSpecRequest request)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        var result = await Mediator.Send(new UpdateWorkspaceSpecCommand(
            WorkspaceId: workspaceId,
            SpecId: specId,
            CallerUserId: userId,
            Name: request.Name,
            Description: request.Description,
            Content: request.Content));

        return MapResult(result);
    }

    /// <summary>
    /// Hard-delete a catalog spec. Existing branches that were previously
    /// forked from it are unaffected — the snapshot semantic means the spec
    /// was copied in, not linked.
    /// </summary>
    [HttpDelete("{specId:guid}")]
    [ProducesResponseType(204)]
    [ProducesResponseType(401)]
    [ProducesResponseType(403)]
    [ProducesResponseType(404)]
    public async Task<ActionResult> Delete(Guid workspaceId, Guid specId)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        var result = await Mediator.Send(new DeleteWorkspaceSpecCommand(workspaceId, specId, userId));
        if (result.IsSuccess) return NoContent();
        return MapFailureNonGeneric(result.Error);
    }

    /// <summary>
    /// Duplicate a catalog spec. <c>Content</c> is copied verbatim into a new
    /// row; the duplicate is independent from the source after this point.
    /// </summary>
    [HttpPost("{specId:guid}/duplicate")]
    [ProducesResponseType<WorkspaceSpecDetail>(201)]
    [ProducesResponseType(400)]
    [ProducesResponseType(401)]
    [ProducesResponseType(403)]
    [ProducesResponseType(404)]
    [ProducesResponseType(409)]
    public async Task<ActionResult<WorkspaceSpecDetail>> Duplicate(
        Guid workspaceId,
        Guid specId,
        [FromBody] DuplicateWorkspaceSpecRequest request)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        var result = await Mediator.Send(new DuplicateWorkspaceSpecCommand(
            WorkspaceId: workspaceId,
            SourceSpecId: specId,
            CallerUserId: userId,
            NewName: request.Name,
            NewDescription: request.Description));

        if (result.IsSuccess)
        {
            return CreatedAtAction(nameof(GetOne),
                new { workspaceId, specId = result.Value.Id },
                result.Value);
        }
        return MapFailure<WorkspaceSpecDetail>(result.Error);
    }

    /// <summary>
    /// "Save current as catalog spec" — promote a running runtime's <c>Spec</c>
    /// into a new named catalog entry in the runtime's owning workspace. The
    /// route is intentionally rooted at <c>/api/runtimes/{runtimeId}/…</c> so
    /// the action is discoverable from runtime context (the spec drawer), even
    /// though the resource it creates belongs to <c>WorkspaceSpecs</c>.
    ///
    /// <para>Authorisation flows from the runtime's <c>TenantId</c> (workspace
    /// id) — the handler verifies the caller is a member of that workspace.</para>
    /// </summary>
    [HttpPost("/api/runtimes/{runtimeId:guid}/save-as-catalog-spec")]
    [ProducesResponseType<WorkspaceSpecDetail>(201)]
    [ProducesResponseType(400)]
    [ProducesResponseType(401)]
    [ProducesResponseType(403)]
    [ProducesResponseType(404)]
    [ProducesResponseType(409)]
    public async Task<ActionResult<WorkspaceSpecDetail>> SaveRuntimeSpecToCatalog(
        Guid runtimeId,
        [FromBody] SaveRuntimeSpecToCatalogRequest request)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        var result = await Mediator.Send(new SaveRuntimeSpecToCatalogCommand(
            RuntimeId: runtimeId,
            CallerUserId: userId,
            Name: request.Name,
            Description: request.Description));

        if (result.IsSuccess)
        {
            return CreatedAtAction(nameof(GetOne),
                new { workspaceId = result.Value.WorkspaceId, specId = result.Value.Id },
                result.Value);
        }
        return MapFailure<WorkspaceSpecDetail>(result.Error);
    }

    // -------- Error code mapping --------
    //
    // Handlers return stable error codes; the controller is the only place
    // we translate those into HTTP status codes. Keeping this in one helper
    // keeps the controller methods declarative.
    //
    // not_a_member        -> 403 (also used for cross-workspace specId access)
    // spec_not_found      -> 404
    // runtime_not_found   -> 404 (save-as-catalog-spec)
    // name_taken          -> 409
    // runtime_has_no_spec -> 400 (save-as-catalog-spec)
    // invalid_*           -> 400
    // service_*           -> 400 (V2 validator stable codes)

    private ActionResult<T> MapResult<T>(Result<T> result)
    {
        if (result.IsSuccess) return Ok(result.Value);
        return MapFailure<T>(result.Error);
    }

    private ActionResult<T> MapFailure<T>(string? error)
    {
        var body = new { error = error ?? "unknown_error" };
        return error switch
        {
            "not_a_member" => StatusCode(StatusCodes.Status403Forbidden, body),
            "spec_not_found" => NotFound(body),
            "runtime_not_found" => NotFound(body),
            "name_taken" => Conflict(body),
            _ => BadRequest(body),
        };
    }

    private ActionResult MapFailureNonGeneric(string? error)
    {
        var body = new { error = error ?? "unknown_error" };
        return error switch
        {
            "not_a_member" => StatusCode(StatusCodes.Status403Forbidden, body),
            "spec_not_found" => NotFound(body),
            "runtime_not_found" => NotFound(body),
            "name_taken" => Conflict(body),
            _ => BadRequest(body),
        };
    }
}

// -------- Request DTOs --------

public class CreateWorkspaceSpecRequest
{
    /// <summary>Catalog name, unique within the workspace. 1–100 chars.</summary>
    [Required, StringLength(100, MinimumLength = 1)]
    public required string Name { get; init; }

    /// <summary>Optional one-line description. Max 500 chars.</summary>
    [StringLength(500)]
    public string? Description { get; init; }

    /// <summary>
    /// Full V2 RuntimeSpec document as JSON. Validated server-side against
    /// the V2 validator — invalid content is rejected with a stable error
    /// code (<c>invalid_spec_json</c> for parse failures, otherwise the
    /// validator's own code like <c>service_command_required: foo</c>).
    /// </summary>
    [Required]
    public required string Content { get; init; }
}

public class UpdateWorkspaceSpecRequest
{
    [Required, StringLength(100, MinimumLength = 1)]
    public required string Name { get; init; }

    [StringLength(500)]
    public string? Description { get; init; }

    [Required]
    public required string Content { get; init; }
}

public class DuplicateWorkspaceSpecRequest
{
    /// <summary>New catalog name for the duplicate. Must be unique within the workspace.</summary>
    [Required, StringLength(100, MinimumLength = 1)]
    public required string Name { get; init; }

    /// <summary>
    /// Optional override for the duplicate's description. If omitted, the
    /// source's description is carried over.
    /// </summary>
    [StringLength(500)]
    public string? Description { get; init; }
}

public class SaveRuntimeSpecToCatalogRequest
{
    /// <summary>Catalog name, unique within the runtime's workspace. 1–100 chars.</summary>
    [Required, StringLength(100, MinimumLength = 1)]
    public required string Name { get; init; }

    /// <summary>Optional one-line description. Max 500 chars.</summary>
    [StringLength(500)]
    public string? Description { get; init; }
}
