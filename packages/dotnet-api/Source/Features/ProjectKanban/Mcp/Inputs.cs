using Source.Features.ProjectKanban.Models;

namespace Source.Features.ProjectKanban.Mcp;

/// <summary>
/// Wire-level input records for the kanban MCP. These are thin DTOs the daemon
/// posts as the body of each MCP method call. They deliberately do <b>not</b>
/// carry <c>ProjectId</c> / <c>RuntimeId</c> / <c>TenantId</c> — those are
/// server-derived from the runtime token claim. If a malicious or buggy client
/// adds one, <see cref="Source.Features.Mcp.Framework.McpControllerBase"/>'s
/// forbidden-field strip zeroes it before the handler runs (with a structured
/// warning).
///
/// <para><b>Why mutable records.</b> The framework uses reflection to clear
/// forbidden fields on the input. Init-only properties would prevent the strip.
/// We accept the mild mutability tradeoff because the input lifetime is one
/// request and the records are not shared across handlers.</para>
/// </summary>
public record ListCardsInput
{
    public ProjectKanbanCardStatus? Status { get; set; }
}

public record GetCardInput
{
    public Guid CardId { get; set; }
}

/// <summary>
/// Create-card input. Card 2: accepts <see cref="Priority"/> (default
/// <see cref="ProjectKanbanCardPriority.Medium"/>) and optional
/// <see cref="DueDate"/>.
/// </summary>
public record CreateCardInput
{
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public ProjectKanbanCardStatus Status { get; set; }
    public ProjectKanbanCardPriority Priority { get; set; } = ProjectKanbanCardPriority.Medium;
    public DateTime? DueDate { get; set; }
}

/// <summary>
/// Update-card input. Card 2 adds optional <see cref="Priority"/> and
/// <see cref="DueDate"/>. A null priority / null due-date means "leave
/// unchanged". To explicitly clear a previously-set due date, set
/// <see cref="ClearDueDate"/> to true.
/// </summary>
public record UpdateCardInput
{
    public Guid CardId { get; set; }
    public string? Title { get; set; }
    public string? Description { get; set; }
    public ProjectKanbanCardPriority? Priority { get; set; }
    public DateTime? DueDate { get; set; }
    public bool ClearDueDate { get; set; }
}

public record MoveCardInput
{
    public Guid CardId { get; set; }
    public ProjectKanbanCardStatus NewStatus { get; set; }
    public int NewPosition { get; set; }
}

public record DeleteCardInput
{
    public Guid CardId { get; set; }
}

/// <summary>Card 2: column-cards input. Filters the board read by status.</summary>
public record GetColumnCardsInput
{
    public ProjectKanbanCardStatus Status { get; set; }
}

/// <summary>Card 2: card-detail input. Returns the full card incl. subtasks.</summary>
public record GetCardDetailsInput
{
    public Guid CardId { get; set; }
}

/// <summary>Card 2: append-subtask input.</summary>
public record CreateSubtaskInput
{
    public Guid CardId { get; set; }
    public string Title { get; set; } = string.Empty;
}

/// <summary>Card 2: toggle-completed input on a subtask.</summary>
public record ToggleSubtaskInput
{
    public Guid SubtaskId { get; set; }
}

/// <summary>Card 2: soft-delete a subtask.</summary>
public record DeleteSubtaskInput
{
    public Guid SubtaskId { get; set; }
}
