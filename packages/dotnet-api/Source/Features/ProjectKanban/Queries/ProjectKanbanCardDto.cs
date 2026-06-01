using Source.Features.ProjectKanban.Models;
using Tapper;

namespace Source.Features.ProjectKanban.Queries;

/// <summary>
/// Read-side projection of a <see cref="ProjectKanbanCard"/>. Returned by every
/// kanban MCP query/command that surfaces a single card to the daemon.
///
/// <para><b>Card 2 additions.</b> <see cref="Priority"/> and <see cref="DueDate"/>
/// are persisted on the entity (defaults: Medium / null). <see cref="Subtasks"/>
/// is the eagerly-loaded checklist for the card detail view; the list endpoint
/// returns a leaner DTO with just counts (see
/// <see cref="ProjectKanbanCardListItemDto"/>) so the board doesn't pay for
/// hundreds of subtasks it never renders.</para>
///
/// <para>Audit fields (<c>CreatedBy</c> / <c>UpdatedBy</c> / <c>DeletedBy</c>) are
/// deliberately omitted — the daemon never needs to know who edited the card,
/// and exposing identity strings cross-tenant would be a leak. The persisted
/// row keeps the audit trail; the wire DTO does not.</para>
///
/// <para><b>[TranspilationSource].</b> The daemon reads this shape from its
/// generated TS layer; Tapper transpiles the record per the
/// <see cref="Source.Features.Mcp.Framework.McpResponse{T}"/> /
/// <see cref="Source.Features.ProjectSecrets.Models.EnvVarDelta"/> precedent.</para>
/// </summary>
[TranspilationSource]
public record ProjectKanbanCardDto(
    Guid Id,
    Guid ProjectId,
    string Title,
    string? Description,
    ProjectKanbanCardStatus Status,
    int Position,
    ProjectKanbanCardPriority Priority,
    DateTime? DueDate,
    ProjectKanbanCardSource Source,
    string? CreatedOnBranch,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    List<ProjectKanbanCardSubtaskDto> Subtasks);

/// <summary>
/// Wire DTO for a <see cref="ProjectKanbanCardSubtask"/>. Embedded in
/// <see cref="ProjectKanbanCardDto"/> when the card detail is loaded; the
/// board-level list omits subtasks entirely (counts only — see
/// <see cref="ProjectKanbanCardListItemDto"/>).
/// </summary>
[TranspilationSource]
public record ProjectKanbanCardSubtaskDto(
    Guid Id,
    Guid CardId,
    string Title,
    bool IsCompleted,
    int Position);

/// <summary>
/// Lean board-row projection of a card. Used by
/// <see cref="ListProjectKanbanCardsQuery"/> so the board can render the "2/5"
/// completion badge without an N+1 read per card. Subtask details are fetched
/// only when the user opens a card.
/// </summary>
[TranspilationSource]
public record ProjectKanbanCardListItemDto(
    Guid Id,
    Guid ProjectId,
    string Title,
    string? Description,
    ProjectKanbanCardStatus Status,
    int Position,
    ProjectKanbanCardPriority Priority,
    DateTime? DueDate,
    ProjectKanbanCardSource Source,
    string? CreatedOnBranch,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    int SubtaskCount,
    int SubtaskCompletedCount);
