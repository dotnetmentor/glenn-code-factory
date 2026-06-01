using Source.Features.ProjectKanban.Events;
using Source.Shared;
using Source.Shared.Events;
using Source.Shared.Results;

namespace Source.Features.ProjectKanban.Models;

/// <summary>
/// A checklist item nested inside a <see cref="ProjectKanbanCard"/>. Subtasks
/// give the board a "2/5" completion badge per card without forcing the user
/// to crack the card open.
///
/// <list type="bullet">
///   <item><see cref="ProjectKanbanCardId"/> is a real FK with
///         <c>OnDelete=Cascade</c> — when a parent card is hard-deleted the
///         children go with it. The parent normally <i>soft</i>-deletes, in
///         which case both rows stay queryable via the global filter; the
///         cascade only kicks in if someone hard-deletes the parent (e.g. a
///         dev tool, never a runtime call).</item>
///   <item><see cref="Position"/> is 0-based within the parent card's
///         subtask list. The composite index <c>(ProjectKanbanCardId,
///         Position)</c> matches the dominant read pattern "render this card's
///         checklist in order".</item>
///   <item>Soft-deletable so removed subtasks stay queryable for audit and
///         don't break the parent card's history; the global query filter
///         hides deleted rows from default queries.</item>
/// </list>
///
/// <para><b>Rich entity.</b> Inherits <see cref="Entity"/> so every state
/// transition raises a domain event in the same place it mutates fields —
/// <see cref="Create"/>, <see cref="Toggle"/>, <see cref="MarkDeleted"/>.
/// Card 3 of the platform-planning-kanban spec wires SignalR broadcasts to
/// those events; this card just emits them and lets the
/// <c>DomainEventInterceptor</c> persist them to the <c>StoredDomainEvents</c>
/// audit table.</para>
/// </summary>
public class ProjectKanbanCardSubtask : Entity, IAuditable, ISoftDelete
{
    public const int MaxTitleLength = 500;

    public Guid Id { get; private set; } = Guid.NewGuid();

    /// <summary>FK to the parent <see cref="ProjectKanbanCard.Id"/>; cascades on hard-delete.</summary>
    public Guid ProjectKanbanCardId { get; private set; }

    /// <summary>Required short summary, capped at <see cref="MaxTitleLength"/> chars.</summary>
    public string Title { get; private set; } = string.Empty;

    /// <summary>Tick-state for the checklist row.</summary>
    public bool IsCompleted { get; private set; }

    /// <summary>0-based position within the parent card's subtask list.</summary>
    public int Position { get; private set; }

    // -------- IAuditable --------
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // -------- ISoftDelete --------
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }
    public string? DeletedBy { get; set; }

    /// <summary>EF Core ctor — keep parameterless and private.</summary>
    private ProjectKanbanCardSubtask() { }

    /// <summary>
    /// Factory for a brand-new subtask. Validates the title, sets defaults,
    /// and raises <see cref="SubtaskCreated"/>. The caller is responsible for
    /// adding the returned instance to the DbContext and calling
    /// <c>SaveChangesAsync</c>; the interceptor handles event persistence and
    /// dispatch. Throws <see cref="ArgumentException"/> on invalid title — the
    /// handler wraps that as a Result.Failure shape so the wire contract stays
    /// uniform with the rest of the slice.
    /// </summary>
    public static ProjectKanbanCardSubtask Create(Guid cardId, string title, int position)
    {
        ValidateTitle(title);

        var subtask = new ProjectKanbanCardSubtask
        {
            Id = Guid.NewGuid(),
            ProjectKanbanCardId = cardId,
            Title = title.Trim(),
            IsCompleted = false,
            Position = position,
        };

        subtask.RaiseDomainEvent(new SubtaskCreated(subtask.Id, cardId, subtask.Title));
        return subtask;
    }

    /// <summary>
    /// Flip <see cref="IsCompleted"/>. Raises <see cref="SubtaskToggled"/> with
    /// the new state so subscribers (Card 3 SignalR) can render the change
    /// without an extra read.
    /// </summary>
    public Result Toggle()
    {
        IsCompleted = !IsCompleted;
        RaiseDomainEvent(new SubtaskToggled(Id, ProjectKanbanCardId, IsCompleted));
        return Result.Success();
    }

    /// <summary>
    /// Soft-delete the subtask. Flips <see cref="IsDeleted"/>; the DbContext
    /// stamps <see cref="DeletedAt"/> / <see cref="DeletedBy"/> via the
    /// <c>ISoftDelete</c> interceptor on SaveChanges. Raises
    /// <see cref="SubtaskDeleted"/>.
    /// </summary>
    public Result MarkDeleted()
    {
        IsDeleted = true;
        RaiseDomainEvent(new SubtaskDeleted(Id, ProjectKanbanCardId));
        return Result.Success();
    }

    private static void ValidateTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException("Title must not be empty.", nameof(title));
        }
        if (title.Length > MaxTitleLength)
        {
            throw new ArgumentException(
                $"Title must be {MaxTitleLength} characters or fewer.",
                nameof(title));
        }
    }
}
