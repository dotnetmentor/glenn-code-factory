using Source.Features.ProjectKanban.Events;
using Source.Shared;
using Source.Shared.Events;
using Source.Shared.Results;

namespace Source.Features.ProjectKanban.Models;

/// <summary>
/// One card on a project's kanban board. The kanban MCP (Card 3 of this spec)
/// reads and mutates these rows via the MCP framework.
///
/// <list type="bullet">
///   <item><see cref="ProjectId"/> is a plain Guid (no FK). The Project entity is
///         owned by another feature slice; mirrors the
///         <c>ProjectRuntime.ProjectId</c> / <c>ProjectSecret.ProjectId</c>
///         convention — the card row outlives any future project hard-delete.</item>
///   <item><see cref="Title"/> is required, capped at 500 chars.
///         <see cref="Description"/> is unbounded user text (Postgres <c>text</c>
///         column) — no <c>jsonb</c> here because the body is plain markdown / text,
///         not structured data, mirroring <c>AgentSession.Prompt</c>.</item>
///   <item><see cref="Status"/> + <see cref="Position"/> together place the card on
///         the board. Position is 0-based within a column. The composite index
///         <c>(ProjectId, Status, Position)</c> matches the dominant read pattern
///         "list cards in column X of project Y in display order".</item>
///   <item><see cref="Priority"/> + <see cref="DueDate"/> are Card 2 additions —
///         displayed on the card chip; planning bias signal for the daemon.
///         Priority defaults to <see cref="ProjectKanbanCardPriority.Medium"/> on
///         creation; <see cref="DueDate"/> is optional.</item>
///   <item><see cref="CreatedBy"/> is the FK to <c>User.Id</c> recorded for "who
///         opened this card". Optional reference (Identity user ids are strings up
///         to 450 chars); nullable for system-seeded rows. <c>OnDelete=Restrict</c>
///         mirrors <see cref="Source.Features.ProjectSecrets.Models.ProjectSecret"/>
///         so deleting a user doesn't cascade-nuke their cards.</item>
///   <item>Soft-deletable so cards removed from the board stay queryable for audit
///         and don't break references; the global query filter hides deleted rows
///         from default queries.</item>
/// </list>
///
/// <para><b>Rich entity (Card 2 refactor).</b> Inherits <see cref="Entity"/>
/// and exposes state transitions as instance methods that raise domain events
/// from the same place they mutate fields — <see cref="Create"/>,
/// <see cref="UpdateMetadata"/>, <see cref="Move"/>, <see cref="MarkDeleted"/>.
/// Card 3 of the spec wires SignalR broadcasts to those events; this card just
/// emits them and lets the <c>DomainEventInterceptor</c> persist them to the
/// <c>StoredDomainEvents</c> audit table.</para>
/// </summary>
public class ProjectKanbanCard : Entity, IAuditable, ISoftDelete
{
    public const int MaxTitleLength = 500;

    public Guid Id { get; private set; } = Guid.NewGuid();

    /// <summary>
    /// Project the card belongs to. Plain Guid (no FK) — the Project entity is
    /// owned by another slice and the card row must outlive a project hard-delete.
    /// </summary>
    public Guid ProjectId { get; private set; }

    /// <summary>Required short summary, capped at 500 chars.</summary>
    public string Title { get; private set; } = string.Empty;

    /// <summary>
    /// Optional long-form description. Postgres <c>text</c> column — plain markdown /
    /// text, not structured data, so no <c>jsonb</c>.
    /// </summary>
    public string? Description { get; private set; }

    /// <summary>Column / lifecycle bucket. See <see cref="ProjectKanbanCardStatus"/>.</summary>
    public ProjectKanbanCardStatus Status { get; private set; }

    /// <summary>0-based position within the card's current <see cref="Status"/> column.</summary>
    public int Position { get; private set; }

    /// <summary>
    /// Card 2: priority bucket. Default <see cref="ProjectKanbanCardPriority.Medium"/>
    /// on creation. Persisted as <c>int</c>.
    /// </summary>
    public ProjectKanbanCardPriority Priority { get; private set; } = ProjectKanbanCardPriority.Medium;

    /// <summary>Card 2: optional due date. UTC. Nullable.</summary>
    public DateTime? DueDate { get; private set; }

    /// <summary>
    /// FK to <c>User.Id</c> — the principal that opened the card. Optional
    /// reference; null for system-seeded rows. Identity user ids are strings up
    /// to 450 chars. <c>OnDelete=Restrict</c> on the FK.
    /// </summary>
    public string? CreatedBy { get; private set; }

    /// <summary>
    /// Provenance: did a human (REST controller) or the agent (kanban MCP)
    /// open this card? Defaults to <see cref="ProjectKanbanCardSource.Human"/>
    /// for legacy rows. Set at creation, immutable thereafter — see the
    /// <c>kanban-card-provenance</c> spec for the rationale.
    /// </summary>
    public ProjectKanbanCardSource Source { get; private set; }

    /// <summary>
    /// Git branch name the agent was on when it opened the card, captured
    /// from the daemon's <c>X-Daemon-Git-Branch</c> MCP request header. Always
    /// <c>null</c> when <see cref="Source"/> is
    /// <see cref="ProjectKanbanCardSource.Human"/> (branch is meaningless for
    /// REST-driven creates) and may also be <c>null</c> for agent creates when
    /// the daemon didn't supply the header (e.g. detached HEAD). Persisted as
    /// nullable Postgres <c>text</c>.
    /// </summary>
    public string? CreatedOnBranch { get; private set; }

    // -------- IAuditable --------
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // -------- ISoftDelete --------
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }
    public string? DeletedBy { get; set; }

    /// <summary>EF Core ctor — keep parameterless and private.</summary>
    private ProjectKanbanCard() { }

    /// <summary>
    /// Factory for a brand-new card. Validates the title, sets the
    /// caller-supplied bucket / priority / due date, and raises
    /// <see cref="CardCreated"/>. The caller is responsible for adding the
    /// returned instance to the DbContext and calling <c>SaveChangesAsync</c>;
    /// the interceptor handles event persistence and dispatch.
    ///
    /// <para>Throws <see cref="ArgumentException"/> on invalid title — the
    /// handler wraps that as a <c>Result.Failure("invalid_title")</c> so the
    /// wire contract stays uniform. Mirrors <c>Specification.Create</c>.</para>
    /// </summary>
    public static ProjectKanbanCard Create(
        Guid projectId,
        string title,
        string? description,
        ProjectKanbanCardStatus status,
        int position,
        ProjectKanbanCardPriority priority,
        DateTime? dueDate,
        string? createdBy,
        ProjectKanbanCardSource source,
        string? createdOnBranch)
    {
        ValidateTitle(title);

        // Defensive: branch name is provenance metadata that only makes sense
        // for agent-created rows. Force-null it for human creates so a buggy
        // caller can't accidentally stamp "main" on a UI-opened card.
        if (source == ProjectKanbanCardSource.Human)
        {
            createdOnBranch = null;
        }

        var card = new ProjectKanbanCard
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Title = title.Trim(),
            Description = description,
            Status = status,
            Position = position,
            Priority = priority,
            DueDate = dueDate,
            CreatedBy = createdBy,
            Source = source,
            CreatedOnBranch = createdOnBranch,
        };

        card.RaiseDomainEvent(new CardCreated(
            card.Id, card.ProjectId, card.Title, card.Status, card.Priority,
            card.Source, card.CreatedOnBranch));
        return card;
    }

    /// <summary>
    /// Update editable metadata (title / description / priority / due date).
    /// Raises <see cref="CardUpdated"/>. Returns <c>Result.Failure("invalid_title")</c>
    /// on blank or too-long title; otherwise success.
    /// </summary>
    public Result UpdateMetadata(
        string title,
        string? description,
        ProjectKanbanCardPriority priority,
        DateTime? dueDate)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return Result.Failure("invalid_title");
        }
        if (title.Length > MaxTitleLength)
        {
            return Result.Failure("invalid_title");
        }

        Title = title.Trim();
        Description = description;
        Priority = priority;
        DueDate = dueDate;

        RaiseDomainEvent(new CardUpdated(Id, ProjectId));
        return Result.Success();
    }

    /// <summary>
    /// Place the card at <paramref name="newStatus"/> / <paramref name="newPosition"/>.
    /// Position is set verbatim — the handler is responsible for the surrounding
    /// reorder of sibling rows. Raises <see cref="CardMoved"/> with the
    /// before / after status + position so subscribers (Card 3 SignalR) can render
    /// the move without a re-fetch.
    /// </summary>
    public Result Move(ProjectKanbanCardStatus newStatus, int newPosition)
    {
        var oldStatus = Status;
        var oldPosition = Position;

        Status = newStatus;
        Position = newPosition;

        RaiseDomainEvent(new CardMoved(Id, ProjectId, oldStatus, oldPosition, newStatus, newPosition));
        return Result.Success();
    }

    /// <summary>
    /// Bulk-shift helper for the move handler's sibling-reorder step. Adjusts
    /// the card's <see cref="Position"/> by <paramref name="delta"/> without
    /// raising an event — siblings inside a reorder operation are part of
    /// the moved-card's transaction, not standalone moves, so they don't
    /// generate their own <see cref="CardMoved"/> events.
    ///
    /// <para>This is the one seam where the "private setter, rich method"
    /// rule gives way to the practical reality of the position-shift loops in
    /// <c>MoveProjectKanbanCardCommandHandler</c>: every other entity in the
    /// affected bucket needs its position adjusted, and minting a full
    /// per-sibling event for an algorithmic side-effect would drown the
    /// audit trail in noise. The moved card itself still goes through
    /// <see cref="Move"/> and raises exactly one <see cref="CardMoved"/>.</para>
    /// </summary>
    public void ShiftPosition(int delta)
    {
        Position += delta;
    }

    /// <summary>
    /// Soft-delete the card. Flips <see cref="IsDeleted"/>; the DbContext
    /// stamps <see cref="DeletedAt"/> / <see cref="DeletedBy"/> via the
    /// <c>ISoftDelete</c> interceptor on SaveChanges. Raises
    /// <see cref="CardDeleted"/>.
    /// </summary>
    public Result MarkDeleted()
    {
        IsDeleted = true;
        RaiseDomainEvent(new CardDeleted(Id, ProjectId));
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
