using MediatR;
using Microsoft.AspNetCore.Mvc;
using Source.Features.Mcp.Framework;
using Source.Features.ProjectKanban.Commands;
using Source.Features.ProjectKanban.Models;
using Source.Features.ProjectKanban.Queries;
using Source.Infrastructure;
using Source.Shared.Results;

namespace Source.Features.ProjectKanban.Mcp;

/// <summary>
/// First reference consumer of the <see cref="McpControllerBase"/> framework
/// (Spec 15 Card 3). Exposes the project's kanban board as an MCP, backed by
/// the <see cref="Commands"/> / <see cref="Queries"/> CQRS layer, all
/// force-scoped to the runtime token's project via
/// <see cref="McpControllerBase.ProjectId"/>.
///
/// <para><b>How project scope is enforced.</b> Every action passes
/// <c>this.ProjectId</c> (claims-derived) into the command/query as the
/// authoritative project filter — never reads it from the request body.
/// The framework's forbidden-field strip zeroes any client-supplied
/// <c>projectId</c> on the input record before the handler runs; the
/// handler-side filter is the second line of defence, ensuring even a
/// reflection-evading payload couldn't leak past the project boundary.</para>
///
/// <para><b>Actor recording.</b> MCP calls have no end-user identity — the
/// caller is a runtime, not a person — and the <c>CreatedBy</c> column FKs
/// into <c>AspNetUsers</c>, so we can't slot a synthetic
/// <c>"runtime:&lt;id&gt;"</c> string in there (the FK rejects it at
/// <c>SaveChanges</c>). The MCP controller passes <c>null</c> for the actor
/// on every command; the per-call audit row in <c>McpCall</c> (written by
/// the framework around <see cref="McpControllerBase.InvokeAsync{TIn,TOut}"/>)
/// is the authoritative record of "which runtime touched which card", so we
/// don't lose attribution.</para>
///
/// <para><b>Routing.</b> Sits under <c>api/mcp/kanban/v1</c> — note the
/// <c>api/</c> prefix is required so the route rides the same Cloudflare
/// tunnel ingress rule as every other backend endpoint (the tunnel forwards
/// only <c>/api/*</c> to upstream; bare <c>/mcp/*</c> 404s at the edge before
/// it ever reaches Kestrel — the regression spec
/// <c>mcp-streamable-http-transport</c> Card faf5297f was opened to fix).
/// The version segment matches the <see cref="McpServerAttribute"/> version
/// so a future v2 of the kanban MCP can ship side-by-side without breaking
/// the daemon.</para>
///
/// <para><b>Card 2 surface.</b> Adds <c>getKanbanBoard</c>,
/// <c>getColumnCards</c>, <c>getCardDetails</c>, and the three subtask
/// methods (<c>createSubtask</c> / <c>toggleSubtask</c> / <c>deleteSubtask</c>).
/// Extends <c>createCard</c> / <c>updateCard</c> with optional
/// <c>Priority</c> + <c>DueDate</c> fields (defaults: Medium / null).</para>
/// </summary>
[ApiController]
[Route("api/mcp/kanban/v1")]
[McpServer(name: "kanban", version: "v1")]
[Tags("KanbanMcp")]
public class KanbanMcpController : McpControllerBase
{
    /// <summary>
    /// Transport-layer header the daemon's MCP HTTP client stamps on every
    /// outbound request, carrying the active git branch in the daemon's
    /// workspace dir. Captured here for <c>createCard</c> only — the
    /// <c>kanban-card-provenance</c> spec scopes branch tracking to card
    /// creation, not every mutation. Header is intentionally NOT a tool input
    /// (no <c>gitBranch</c> field on <see cref="Mcp.CreateCardInput"/>) — it's
    /// transport metadata supplied by the daemon, not a parameter the LLM
    /// chooses.
    /// </summary>
    public const string DaemonGitBranchHeader = "X-Daemon-Git-Branch";

    private readonly IMediator _mediator;

    public KanbanMcpController(
        IMediator mediator,
        ApplicationDbContext db,
        ILogger<McpControllerBase> logger,
        McpRateLimiter rateLimiter)
        : base(db, logger, rateLimiter)
    {
        _mediator = mediator;
    }

    /// <summary>List cards on the runtime's project board, optionally filtered by status.</summary>
    [HttpPost("listCards")]
    [ProducesResponseType(typeof(McpResponse<List<ProjectKanbanCardListItemDto>>), 200)]
    public Task<IActionResult> ListCards(
        [FromBody] ListCardsInput? input,
        CancellationToken ct) =>
        InvokeAsync(
            method: "listCards",
            input: input,
            handler: async i => await _mediator.Send(
                new ListProjectKanbanCardsQuery(ProjectId, i?.Status), ct),
            ct: ct);

    /// <summary>Fetch a single card by id. Returns <c>not_found</c> if the card belongs to another project.</summary>
    [HttpPost("getCard")]
    [ProducesResponseType(typeof(McpResponse<ProjectKanbanCardDto>), 200)]
    public Task<IActionResult> GetCard(
        [FromBody] GetCardInput input,
        CancellationToken ct) =>
        InvokeAsync(
            method: "getCard",
            input: input,
            handler: async i =>
            {
                if (i is null)
                {
                    return Result.Failure<ProjectKanbanCardDto>("invalid_input");
                }
                return await _mediator.Send(
                    new GetProjectKanbanCardQuery(ProjectId, i.CardId), ct);
            },
            ct: ct);

    /// <summary>
    /// Create a new card. Position is appended to the end of the chosen status
    /// bucket. Card 2: accepts <c>priority</c> + <c>dueDate</c>.
    ///
    /// <para><b>Provenance.</b> Every MCP-originated create stamps
    /// <see cref="ProjectKanbanCardSource.Agent"/> on the row. The git branch
    /// the daemon was on comes from the <see cref="DaemonGitBranchHeader"/>
    /// request header; missing / blank header means we record <c>null</c> and
    /// the badge degrades to "🤖" with no branch suffix.</para>
    /// </summary>
    [HttpPost("createCard")]
    [ProducesResponseType(typeof(McpResponse<ProjectKanbanCardDto>), 200)]
    public Task<IActionResult> CreateCard(
        [FromBody] CreateCardInput input,
        CancellationToken ct)
    {
        // Read the branch off the request once at the action boundary — the
        // closure inside InvokeAsync runs after the handler dispatcher has
        // already inspected headers, but we want the captured value to be
        // identical to what the real request carried. Treat empty/whitespace
        // as null so accidental blank headers from the daemon don't pollute
        // the column with empty strings.
        string? branch = null;
        if (Request.Headers.TryGetValue(DaemonGitBranchHeader, out var branchHeader))
        {
            var raw = branchHeader.ToString();
            if (!string.IsNullOrWhiteSpace(raw))
            {
                branch = raw.Trim();
            }
        }

        return InvokeAsync(
            method: "createCard",
            input: input,
            handler: async i =>
            {
                if (i is null)
                {
                    return Result.Failure<ProjectKanbanCardDto>("invalid_input");
                }
                return await _mediator.Send(
                    new CreateProjectKanbanCardCommand(
                        ProjectId,
                        i.Title,
                        i.Description,
                        i.Status,
                        // ActorUserId must be null on the MCP path — the
                        // entity's CreatedBy column FKs into AspNetUsers and
                        // the daemon's runtime token has no Identity user
                        // behind it. Runtime attribution lives in the
                        // McpCall audit row.
                        ActorUserId: null,
                        i.Priority,
                        i.DueDate,
                        Source: ProjectKanbanCardSource.Agent,
                        CreatedOnBranch: branch),
                    ct);
            },
            ct: ct);
    }

    /// <summary>Partial-update a card. <c>null</c> fields are unchanged. Card 2: accepts <c>priority</c>, <c>dueDate</c>, <c>clearDueDate</c>.</summary>
    [HttpPost("updateCard")]
    [ProducesResponseType(typeof(McpResponse<ProjectKanbanCardDto>), 200)]
    public Task<IActionResult> UpdateCard(
        [FromBody] UpdateCardInput input,
        CancellationToken ct) =>
        InvokeAsync(
            method: "updateCard",
            input: input,
            handler: async i =>
            {
                if (i is null)
                {
                    return Result.Failure<ProjectKanbanCardDto>("invalid_input");
                }
                return await _mediator.Send(
                    new UpdateProjectKanbanCardCommand(
                        ProjectId,
                        i.CardId,
                        i.Title,
                        i.Description,
                        // ActorUserId null on the MCP path — runtime tokens
                        // have no Identity user. The McpCall audit row is
                        // the authoritative breadcrumb. (Update's actor is
                        // decorative — the handler doesn't write it to a
                        // column — but we keep the null convention uniform
                        // across the slice.)
                        ActorUserId: null,
                        i.Priority,
                        i.DueDate,
                        i.ClearDueDate),
                    ct);
            },
            ct: ct);

    /// <summary>Move a card to a new status column and position. Reorder is atomic.</summary>
    [HttpPost("moveCard")]
    [ProducesResponseType(typeof(McpResponse<ProjectKanbanCardDto>), 200)]
    public Task<IActionResult> MoveCard(
        [FromBody] MoveCardInput input,
        CancellationToken ct) =>
        InvokeAsync(
            method: "moveCard",
            input: input,
            handler: async i =>
            {
                if (i is null)
                {
                    return Result.Failure<ProjectKanbanCardDto>("invalid_input");
                }
                return await _mediator.Send(
                    new MoveProjectKanbanCardCommand(
                        ProjectId,
                        i.CardId,
                        i.NewStatus,
                        i.NewPosition,
                        // ActorUserId null on the MCP path — see CreateCard above.
                        ActorUserId: null),
                    ct);
            },
            ct: ct);

    /// <summary>Soft-delete a card. The card no longer appears in <c>listCards</c>.</summary>
    [HttpPost("deleteCard")]
    [ProducesResponseType(typeof(McpResponse<Unit>), 200)]
    public Task<IActionResult> DeleteCard(
        [FromBody] DeleteCardInput input,
        CancellationToken ct) =>
        InvokeAsync(
            method: "deleteCard",
            input: input,
            handler: async i =>
            {
                if (i is null)
                {
                    return Result.Failure<Unit>("invalid_input");
                }
                return await _mediator.Send(
                    new DeleteProjectKanbanCardCommand(
                        ProjectId,
                        i.CardId,
                        // ActorUserId null on the MCP path — see CreateCard above.
                        ActorUserId: null),
                    ct);
            },
            ct: ct);

    /// <summary>
    /// Card 2: return the four virtual board columns with card counts. No
    /// cards in this response — drill into <see cref="GetColumnCards"/> for
    /// the actual rows.
    /// </summary>
    [HttpPost("getKanbanBoard")]
    [ProducesResponseType(typeof(McpResponse<List<KanbanBoardColumnDto>>), 200)]
    public Task<IActionResult> GetKanbanBoard(CancellationToken ct) =>
        InvokeAsync<object?, List<KanbanBoardColumnDto>>(
            method: "getKanbanBoard",
            input: null,
            handler: async _ => await _mediator.Send(
                new GetKanbanBoardQuery(ProjectId), ct),
            ct: ct);

    /// <summary>
    /// Card 2: return the cards in one column for the runtime's project,
    /// ordered by <c>Position</c>. Each card carries title, priority,
    /// dueDate, subtaskCount + subtaskCompletedCount — board chip shape.
    /// </summary>
    [HttpPost("getColumnCards")]
    [ProducesResponseType(typeof(McpResponse<List<ProjectKanbanCardListItemDto>>), 200)]
    public Task<IActionResult> GetColumnCards(
        [FromBody] GetColumnCardsInput input,
        CancellationToken ct) =>
        InvokeAsync(
            method: "getColumnCards",
            input: input,
            handler: async i =>
            {
                if (i is null)
                {
                    return Result.Failure<List<ProjectKanbanCardListItemDto>>("invalid_input");
                }
                return await _mediator.Send(
                    new ListProjectKanbanCardsQuery(ProjectId, i.Status), ct);
            },
            ct: ct);

    /// <summary>
    /// Card 2: return the full card detail including subtasks. Cross-project
    /// lookups return <c>not_found</c> uniformly.
    /// </summary>
    [HttpPost("getCardDetails")]
    [ProducesResponseType(typeof(McpResponse<ProjectKanbanCardDto>), 200)]
    public Task<IActionResult> GetCardDetails(
        [FromBody] GetCardDetailsInput input,
        CancellationToken ct) =>
        InvokeAsync(
            method: "getCardDetails",
            input: input,
            handler: async i =>
            {
                if (i is null)
                {
                    return Result.Failure<ProjectKanbanCardDto>("invalid_input");
                }
                return await _mediator.Send(
                    new GetProjectKanbanCardQuery(ProjectId, i.CardId), ct);
            },
            ct: ct);

    /// <summary>Card 2: append a checklist item to a card. Returns the new subtask id.</summary>
    [HttpPost("createSubtask")]
    [ProducesResponseType(typeof(McpResponse<Guid>), 200)]
    public Task<IActionResult> CreateSubtask(
        [FromBody] CreateSubtaskInput input,
        CancellationToken ct) =>
        InvokeAsync(
            method: "createSubtask",
            input: input,
            handler: async i =>
            {
                if (i is null)
                {
                    return Result.Failure<Guid>("invalid_input");
                }
                return await _mediator.Send(
                    new CreateSubtaskCommand(ProjectId, i.CardId, i.Title), ct);
            },
            ct: ct);

    /// <summary>Card 2: flip a subtask's completed flag. Returns the new state.</summary>
    [HttpPost("toggleSubtask")]
    [ProducesResponseType(typeof(McpResponse<bool>), 200)]
    public Task<IActionResult> ToggleSubtask(
        [FromBody] ToggleSubtaskInput input,
        CancellationToken ct) =>
        InvokeAsync(
            method: "toggleSubtask",
            input: input,
            handler: async i =>
            {
                if (i is null)
                {
                    return Result.Failure<bool>("invalid_input");
                }
                return await _mediator.Send(
                    new ToggleSubtaskCommand(ProjectId, i.SubtaskId), ct);
            },
            ct: ct);

    /// <summary>Card 2: soft-delete a subtask. The row stops appearing in card details.</summary>
    [HttpPost("deleteSubtask")]
    [ProducesResponseType(typeof(McpResponse<Unit>), 200)]
    public Task<IActionResult> DeleteSubtask(
        [FromBody] DeleteSubtaskInput input,
        CancellationToken ct) =>
        InvokeAsync(
            method: "deleteSubtask",
            input: input,
            handler: async i =>
            {
                if (i is null)
                {
                    return Result.Failure<Unit>("invalid_input");
                }
                return await _mediator.Send(
                    new DeleteSubtaskCommand(ProjectId, i.SubtaskId), ct);
            },
            ct: ct);

}
