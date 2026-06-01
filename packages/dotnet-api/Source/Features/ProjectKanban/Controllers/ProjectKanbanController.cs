using System.Security.Claims;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Source.Features.ProjectKanban.Commands;
using Source.Features.ProjectKanban.Models;
using Source.Features.ProjectKanban.Queries;
using Source.Infrastructure;
using Source.Infrastructure.Extensions;

namespace Source.Features.ProjectKanban.Controllers;

/// <summary>
/// User-facing HTTP surface for the project kanban board. Mediates over the
/// same <see cref="Commands"/> / <see cref="Queries"/> CQRS layer the
/// <see cref="Source.Features.ProjectKanban.Mcp.KanbanMcpController"/> uses,
/// but with project scope coming from the route (super-admin browser session
/// with a user JWT) instead of the runtime-token claim.
///
/// <para><b>Why this exists.</b> The MCP controller force-scopes to the
/// runtime token's project — the super-admin frontend has only a user JWT
/// and can't call it. This REST surface lets the planning UI hit the same
/// handlers with <c>projectId</c> taken from the URL, mirroring the dual
/// pattern already in place for
/// <see cref="Source.Features.Specifications.Controllers.SpecificationsController"/>
/// + <see cref="Source.Features.Specifications.Mcp.SpecificationsMcpController"/>.</para>
///
/// <para><b>Authorisation.</b> Every action gates on
/// <see cref="OwnershipExtensions.CallerCanAccessProjectAsync"/> — SuperAdmin,
/// project owner, or workspace member. Non-members get a uniform 404.</para>
///
/// <para><b>Typed returns.</b> Every action returns
/// <see cref="ActionResult{TValue}"/> with a concrete DTO so Swagger emits
/// usable schemas and Orval generates clean React Query hooks — the
/// codebase's standing rule from <c>dotnet-api/CLAUDE.md</c>.</para>
/// </summary>
[ApiController]
[Authorize]
[Route("api/projects/{projectId:guid}/kanban")]
[Tags("ProjectKanban")]
public class ProjectKanbanController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly ApplicationDbContext _db;

    public ProjectKanbanController(IMediator mediator, ApplicationDbContext db)
    {
        _mediator = mediator;
        _db = db;
    }

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
    /// Board overview: the four canonical status columns with non-deleted
    /// card counts. Cards themselves are fetched per column via
    /// <see cref="GetColumnCards"/>; this endpoint just gives the column
    /// shells the board UI needs to render before any cards arrive.
    /// </summary>
    [HttpGet("board")]
    [ProducesResponseType(typeof(List<KanbanBoardColumnDto>), 200)]
    [ProducesResponseType(401)]
    public async Task<ActionResult<List<KanbanBoardColumnDto>>> GetBoard(
        Guid projectId,
        CancellationToken ct)
    {
        var deny = await EnforceProjectAccessAsync(projectId, ct);
        if (deny is not null) return deny;

        var result = await _mediator.Send(new GetKanbanBoardQuery(projectId), ct);
        if (!result.IsSuccess)
        {
            return BadRequest(new { error = result.Error });
        }
        return Ok(result.Value);
    }

    /// <summary>
    /// Lean per-column listing of cards, ordered by <c>Position</c>. Each
    /// row carries the subtask count + completed-count so the board chip
    /// can render the "2/5" completion badge without an N+1 read.
    /// </summary>
    [HttpGet("columns/{status}/cards")]
    [ProducesResponseType(typeof(List<ProjectKanbanCardListItemDto>), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(401)]
    public async Task<ActionResult<List<ProjectKanbanCardListItemDto>>> GetColumnCards(
        Guid projectId,
        ProjectKanbanCardStatus status,
        CancellationToken ct)
    {
        var deny = await EnforceProjectAccessAsync(projectId, ct);
        if (deny is not null) return deny;

        var result = await _mediator.Send(
            new ListProjectKanbanCardsQuery(projectId, status), ct);

        if (!result.IsSuccess)
        {
            return BadRequest(new { error = result.Error });
        }
        return Ok(result.Value);
    }

    /// <summary>
    /// Fetch a single card by id, including its (non-deleted) subtasks.
    /// 404 when the card doesn't exist or belongs to another project —
    /// uniform "not_found" stance to avoid cross-tenant existence leaks.
    /// </summary>
    [HttpGet("cards/{cardId:guid}")]
    [ProducesResponseType(typeof(ProjectKanbanCardDto), 200)]
    [ProducesResponseType(401)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<ProjectKanbanCardDto>> GetCard(
        Guid projectId,
        Guid cardId,
        CancellationToken ct)
    {
        var deny = await EnforceProjectAccessAsync(projectId, ct);
        if (deny is not null) return deny;

        var result = await _mediator.Send(
            new GetProjectKanbanCardQuery(projectId, cardId), ct);

        if (!result.IsSuccess)
        {
            return result.Error == "not_found"
                ? NotFound(new { error = result.Error })
                : BadRequest(new { error = result.Error });
        }
        return Ok(result.Value);
    }

    /// <summary>
    /// Create a new card. <see cref="ProjectKanbanCardPriority"/> defaults
    /// to <see cref="ProjectKanbanCardPriority.Medium"/>;
    /// <see cref="CreateProjectKanbanCardRequest.Status"/> defaults to
    /// <see cref="ProjectKanbanCardStatus.Backlog"/> when omitted. Position
    /// is computed server-side (append-to-end of the chosen bucket).
    ///
    /// <para><b>Provenance.</b> REST creates always set
    /// <see cref="ProjectKanbanCardSource.Human"/> with no branch — by
    /// definition this surface is hit by a UI user, not the agent.
    /// Agent-originated creates flow through
    /// <see cref="Source.Features.ProjectKanban.Mcp.KanbanMcpController"/>
    /// which reads the <c>X-Daemon-Git-Branch</c> header off the MCP request
    /// and stamps <see cref="ProjectKanbanCardSource.Agent"/> + the branch
    /// name on the row.</para>
    /// </summary>
    [HttpPost("cards")]
    [ProducesResponseType(typeof(ProjectKanbanCardDto), 201)]
    [ProducesResponseType(400)]
    [ProducesResponseType(401)]
    public async Task<ActionResult<ProjectKanbanCardDto>> CreateCard(
        Guid projectId,
        [FromBody] CreateProjectKanbanCardRequest request,
        CancellationToken ct)
    {
        var deny = await EnforceProjectAccessAsync(projectId, ct);
        if (deny is not null) return deny;

        if (request is null)
        {
            return BadRequest(new { error = "invalid_body" });
        }

        var actor = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "user:anonymous";

        var result = await _mediator.Send(
            new CreateProjectKanbanCardCommand(
                projectId,
                request.Title,
                request.Description,
                request.Status ?? ProjectKanbanCardStatus.Backlog,
                actor,
                request.Priority ?? ProjectKanbanCardPriority.Medium,
                request.DueDate,
                Source: ProjectKanbanCardSource.Human,
                CreatedOnBranch: null),
            ct);

        if (!result.IsSuccess)
        {
            return BadRequest(new { error = result.Error });
        }

        return StatusCode(StatusCodes.Status201Created, result.Value);
    }

    /// <summary>
    /// Partial-update a card's editable metadata. The request body carries
    /// the full desired state for the editable fields (the handler still
    /// treats nulls as "leave unchanged" for description, and pairs
    /// <see cref="UpdateProjectKanbanCardRequest.DueDate"/> with
    /// <see cref="UpdateProjectKanbanCardRequest.ClearDueDate"/> to
    /// disambiguate "unchanged" from "clear it").
    /// </summary>
    [HttpPut("cards/{cardId:guid}")]
    [ProducesResponseType(typeof(ProjectKanbanCardDto), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(401)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<ProjectKanbanCardDto>> UpdateCard(
        Guid projectId,
        Guid cardId,
        [FromBody] UpdateProjectKanbanCardRequest request,
        CancellationToken ct)
    {
        var deny = await EnforceProjectAccessAsync(projectId, ct);
        if (deny is not null) return deny;

        if (request is null)
        {
            return BadRequest(new { error = "invalid_body" });
        }

        var actor = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "user:anonymous";

        var result = await _mediator.Send(
            new UpdateProjectKanbanCardCommand(
                projectId,
                cardId,
                request.Title,
                request.Description,
                actor,
                request.Priority,
                request.DueDate,
                request.ClearDueDate),
            ct);

        if (!result.IsSuccess)
        {
            return result.Error == "not_found"
                ? NotFound(new { error = result.Error })
                : BadRequest(new { error = result.Error });
        }
        return Ok(result.Value);
    }

    /// <summary>
    /// Move a card to a new column / position. Atomic across the source
    /// and destination buckets — siblings are shifted in the same write.
    /// 204 on success; the caller refetches the board for the
    /// post-move state.
    /// </summary>
    [HttpPut("cards/{cardId:guid}/move")]
    [ProducesResponseType(204)]
    [ProducesResponseType(400)]
    [ProducesResponseType(401)]
    [ProducesResponseType(404)]
    public async Task<ActionResult> MoveCard(
        Guid projectId,
        Guid cardId,
        [FromBody] MoveProjectKanbanCardRequest request,
        CancellationToken ct)
    {
        var deny = await EnforceProjectAccessAsync(projectId, ct);
        if (deny is not null) return deny;

        if (request is null)
        {
            return BadRequest(new { error = "invalid_body" });
        }

        var actor = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "user:anonymous";

        var result = await _mediator.Send(
            new MoveProjectKanbanCardCommand(
                projectId,
                cardId,
                request.NewStatus,
                request.NewPosition,
                actor),
            ct);

        if (!result.IsSuccess)
        {
            return result.Error == "not_found"
                ? NotFound(new { error = result.Error })
                : BadRequest(new { error = result.Error });
        }
        return NoContent();
    }

    /// <summary>
    /// Soft-delete a card. 204 on success, 404 when the card is unknown
    /// or already deleted.
    /// </summary>
    [HttpDelete("cards/{cardId:guid}")]
    [ProducesResponseType(204)]
    [ProducesResponseType(401)]
    [ProducesResponseType(404)]
    public async Task<ActionResult> DeleteCard(
        Guid projectId,
        Guid cardId,
        CancellationToken ct)
    {
        var deny = await EnforceProjectAccessAsync(projectId, ct);
        if (deny is not null) return deny;

        var actor = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "user:anonymous";

        var result = await _mediator.Send(
            new DeleteProjectKanbanCardCommand(projectId, cardId, actor), ct);

        if (!result.IsSuccess)
        {
            return result.Error == "not_found"
                ? NotFound(new { error = result.Error })
                : BadRequest(new { error = result.Error });
        }
        return NoContent();
    }

    /// <summary>
    /// Append a checklist item to a card. The created-subtask command
    /// returns only the id; we re-fetch the parent card and project the
    /// new subtask out of <see cref="ProjectKanbanCardDto.Subtasks"/> so
    /// the response is fully typed for Orval.
    /// </summary>
    [HttpPost("cards/{cardId:guid}/subtasks")]
    [ProducesResponseType(typeof(ProjectKanbanCardSubtaskDto), 201)]
    [ProducesResponseType(400)]
    [ProducesResponseType(401)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<ProjectKanbanCardSubtaskDto>> CreateSubtask(
        Guid projectId,
        Guid cardId,
        [FromBody] CreateSubtaskRequest request,
        CancellationToken ct)
    {
        var deny = await EnforceProjectAccessAsync(projectId, ct);
        if (deny is not null) return deny;

        if (request is null)
        {
            return BadRequest(new { error = "invalid_body" });
        }

        var createResult = await _mediator.Send(
            new CreateSubtaskCommand(projectId, cardId, request.Title), ct);

        if (!createResult.IsSuccess)
        {
            return createResult.Error == "not_found"
                ? NotFound(new { error = createResult.Error })
                : BadRequest(new { error = createResult.Error });
        }

        var subtaskId = createResult.Value;

        // Re-read the card so we can hand the caller the freshly created
        // subtask in its persisted shape (Position is server-assigned).
        var cardResult = await _mediator.Send(
            new GetProjectKanbanCardQuery(projectId, cardId), ct);

        if (!cardResult.IsSuccess || cardResult.Value is null)
        {
            return BadRequest(new { error = "subtask_created_card_unavailable" });
        }

        var subtask = cardResult.Value.Subtasks.FirstOrDefault(s => s.Id == subtaskId);
        if (subtask is null)
        {
            return BadRequest(new { error = "subtask_created_not_found" });
        }

        return StatusCode(StatusCodes.Status201Created, subtask);
    }

    /// <summary>
    /// Flip a subtask's <c>IsCompleted</c> flag. 204 on success — the
    /// caller refetches the parent card for the post-toggle state (or
    /// relies on SignalR to push the change).
    /// </summary>
    [HttpPut("cards/{cardId:guid}/subtasks/{subtaskId:guid}/toggle")]
    [ProducesResponseType(204)]
    [ProducesResponseType(401)]
    [ProducesResponseType(404)]
    public async Task<ActionResult> ToggleSubtask(
        Guid projectId,
        Guid cardId,
        Guid subtaskId,
        CancellationToken ct)
    {
        var deny = await EnforceProjectAccessAsync(projectId, ct);
        if (deny is not null) return deny;

        // cardId is a route parameter rather than a body field so the URL
        // is unambiguously project- and card-scoped; the toggle command
        // itself only needs the subtaskId + projectId.
        _ = cardId;

        var result = await _mediator.Send(
            new ToggleSubtaskCommand(projectId, subtaskId), ct);

        if (!result.IsSuccess)
        {
            return result.Error == "not_found"
                ? NotFound(new { error = result.Error })
                : BadRequest(new { error = result.Error });
        }
        return NoContent();
    }

    /// <summary>
    /// Soft-delete a subtask. 204 on success, 404 when unknown.
    /// </summary>
    [HttpDelete("cards/{cardId:guid}/subtasks/{subtaskId:guid}")]
    [ProducesResponseType(204)]
    [ProducesResponseType(401)]
    [ProducesResponseType(404)]
    public async Task<ActionResult> DeleteSubtask(
        Guid projectId,
        Guid cardId,
        Guid subtaskId,
        CancellationToken ct)
    {
        var deny = await EnforceProjectAccessAsync(projectId, ct);
        if (deny is not null) return deny;

        _ = cardId;

        var result = await _mediator.Send(
            new DeleteSubtaskCommand(projectId, subtaskId), ct);

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
/// Body shape for <see cref="ProjectKanbanController.CreateCard"/>.
/// <c>Status</c> + <c>Priority</c> are optional — handler defaults are
/// <see cref="ProjectKanbanCardStatus.Backlog"/> +
/// <see cref="ProjectKanbanCardPriority.Medium"/>.
/// </summary>
public record CreateProjectKanbanCardRequest
{
    public string Title { get; init; } = string.Empty;
    public string? Description { get; init; }
    public ProjectKanbanCardStatus? Status { get; init; }
    public ProjectKanbanCardPriority? Priority { get; init; }
    public DateTime? DueDate { get; init; }
}

/// <summary>
/// Body shape for <see cref="ProjectKanbanController.UpdateCard"/>. The
/// handler treats null description as "leave unchanged" — empty string
/// is a deliberate clear. <see cref="ClearDueDate"/> = true forces the
/// due date back to null even when <see cref="DueDate"/> is also null
/// (disambiguates "unchanged" from "clear it").
/// </summary>
public record UpdateProjectKanbanCardRequest
{
    public string? Title { get; init; }
    public string? Description { get; init; }
    public ProjectKanbanCardPriority? Priority { get; init; }
    public DateTime? DueDate { get; init; }
    public bool ClearDueDate { get; init; }
}

/// <summary>
/// Body shape for <see cref="ProjectKanbanController.MoveCard"/>. The
/// command clamps out-of-range <c>NewPosition</c> values to the bucket
/// size so a caller passing 9999 lands at the end rather than 400ing.
/// </summary>
public record MoveProjectKanbanCardRequest
{
    public ProjectKanbanCardStatus NewStatus { get; init; }
    public int NewPosition { get; init; }
}

/// <summary>
/// Body shape for <see cref="ProjectKanbanController.CreateSubtask"/>.
/// </summary>
public record CreateSubtaskRequest
{
    public string Title { get; init; } = string.Empty;
}
