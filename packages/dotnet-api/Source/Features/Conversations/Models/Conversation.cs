using Source.Features.Conversations.Events;
using Source.Features.Projects.Models;
using Source.Shared;
using Source.Shared.Events;
using Source.Shared.Results;

namespace Source.Features.Conversations.Models;

/// <summary>
/// A user's thread of intent inside a single project + branch. One row per
/// conversation. Owns 1..N <see cref="AgentSession"/> rows, each one
/// representing a single prompt → outcome round-trip.
///
/// <list type="bullet">
///   <item><see cref="ProjectId"/> is a plain Guid — no FK — because the
///         Project entity belongs to a future spec. Mirrors the
///         <c>FlyOperation.RuntimeId</c> / <c>ProjectRuntime.ProjectId</c>
///         convention.</item>
///   <item><see cref="BranchId"/> is now a real FK to <see cref="ProjectBranch"/>
///         (promoted from a free-form string in the e2e-smoketest spec).
///         Conversations are scoped per <c>(project, branch)</c>.</item>
///   <item>Lifecycle is tracked via <see cref="ConversationStatus"/>. There is
///         no <c>ISoftDelete</c>: archive is the lifecycle flag and a global
///         query filter at the model level hides archived rows from default
///         queries.</item>
///   <item><see cref="LastActivityAt"/> and <see cref="EventCount"/> are
///         denormalized for list-view efficiency — kept in sync by the
///         event-ingestion path (a follow-up card).</item>
/// </list>
///
/// <para>Data only for this card. Behaviour (rename, archive, activity bump)
/// arrives in the command-handler cards.</para>
/// </summary>
public class Conversation : Entity, IAuditable
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The project this conversation belongs to. Plain Guid (no FK).</summary>
    public Guid ProjectId { get; set; }

    /// <summary>
    /// Branch this conversation is scoped to. FK to <see cref="ProjectBranch"/> —
    /// promoted from a free-form string in the e2e-smoketest spec.
    /// </summary>
    public Guid BranchId { get; set; }

    /// <summary>Navigation to the scoping branch.</summary>
    public ProjectBranch Branch { get; set; } = null!;

    /// <summary>
    /// Auto-derived from the first prompt; the user can rename later via the
    /// <c>RenameConversation</c> command (follow-up card).
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Lifecycle flag. Persisted as a string. Archived rows are hidden by the
    /// global query filter; admin queries pass <c>IgnoreQueryFilters()</c>.
    /// </summary>
    public ConversationStatus Status { get; set; } = ConversationStatus.Active;

    /// <summary>
    /// Last time any session under this conversation emitted an event.
    /// Denormalized so the conversation-list query doesn't need a join.
    /// </summary>
    public DateTime LastActivityAt { get; set; }

    /// <summary>
    /// Total <see cref="AgentEvent"/> count across all sessions in this
    /// conversation. Denormalized counter, updated by the event-ingestion path.
    /// </summary>
    public int EventCount { get; set; }

    /// <summary>
    /// UTC timestamp when the conversation was archived. <c>null</c> while the
    /// conversation is <see cref="ConversationStatus.Active"/>; stamped when
    /// <see cref="Status"/> transitions to <see cref="ConversationStatus.Archived"/>.
    /// Indexed for the archived-filter sidebar query. Distinct from the
    /// <c>IAuditable</c> timestamps so an archive can be distinguished from any
    /// other update on the row.
    /// </summary>
    public DateTime? ArchivedAt { get; set; }

    /// <summary>
    /// True when <see cref="Title"/> was auto-derived from the first prompt
    /// (the default). Flipped to <c>false</c> when the user renames the
    /// conversation, or when a future LLM-rewrite job upgrades the title.
    /// Defaults to <c>true</c> so freshly-created conversations are eligible
    /// for the rewrite job out of the box.
    /// </summary>
    public bool IsAutoTitled { get; set; } = true;

    // -------- IAuditable --------
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>Sessions belonging to this conversation. Cascade-deleted with the parent.</summary>
    public ICollection<AgentSession> Sessions { get; set; } = new List<AgentSession>();

    // ----------------------------------------------------------------------
    // Behaviour: rename / auto-retitle
    // ----------------------------------------------------------------------

    /// <summary>
    /// User-initiated rename. Trims, enforces non-empty + ≤200 char invariants,
    /// flips <see cref="IsAutoTitled"/> to <c>false</c> so the auto-retitle
    /// heuristic (see <see cref="AutoRetitle"/>) won't subsequently overwrite
    /// the user's choice, and raises <see cref="ConversationRenamed"/> for
    /// live SignalR fan-out.
    ///
    /// <para>Returns <see cref="Result.Success()"/> on a valid rename, or a
    /// validation failure with a human-readable error message when the title
    /// is empty / whitespace-only / longer than 200 chars. The caller is
    /// expected to translate that into a 4xx HTTP response.</para>
    /// </summary>
    public Result Rename(string newTitle)
    {
        if (string.IsNullOrWhiteSpace(newTitle))
        {
            return Result.Failure("Title cannot be empty");
        }

        var trimmed = newTitle.Trim();
        if (trimmed.Length > 200)
        {
            return Result.Failure("Title too long (max 200 chars)");
        }

        Title = trimmed;
        IsAutoTitled = false;

        RaiseDomainEvent(new ConversationRenamed(Id, ProjectId, BranchId, Title, IsAutoTitled));
        return Result.Success();
    }

    /// <summary>
    /// One-shot heuristic auto-retitle. Called by the
    /// <c>AutoRetitleOnFirstAssistantTextHandler</c> the first time the agent
    /// emits assistant text on a conversation that still has the placeholder
    /// title. Caps the title at 60 chars (truncates rather than fails — the
    /// caller has already shaped the candidate via the first-sentence heuristic)
    /// and refuses to run when the conversation has already been renamed
    /// (<see cref="IsAutoTitled"/> == false).
    ///
    /// <para>Flips <see cref="IsAutoTitled"/> to <c>false</c> so this method
    /// is idempotent: a second AssistantText event on the same conversation
    /// returns a failure result and the title is left alone.</para>
    ///
    /// <para>Returns <see cref="Result.Success()"/> when the title was applied,
    /// or a failure when the conversation is not eligible (empty candidate,
    /// already renamed). Failures from this method are advisory — the auto-
    /// retitle handler logs and swallows them; they must never block the
    /// user's turn or poison the event chain.</para>
    /// </summary>
    public Result AutoRetitle(string newTitle)
    {
        if (!IsAutoTitled)
        {
            return Result.Failure("Conversation has already been renamed; not eligible for auto-retitle.");
        }

        if (string.IsNullOrWhiteSpace(newTitle))
        {
            return Result.Failure("Auto-retitle candidate was empty");
        }

        var trimmed = newTitle.Trim();
        if (trimmed.Length > 60)
        {
            // Truncate, don't fail — the heuristic upstream may have produced
            // a candidate one char over the line. Try to truncate at a word
            // boundary inside the last 10 chars before the 60-char cap; fall
            // back to a hard cut. Append an ellipsis to signal truncation.
            var cap = 60;
            var hardCut = trimmed.Substring(0, cap);
            var lastSpace = hardCut.LastIndexOf(' ');
            if (lastSpace >= cap - 10 && lastSpace > 0)
            {
                hardCut = hardCut.Substring(0, lastSpace);
            }
            trimmed = hardCut.TrimEnd() + "…";
        }

        Title = trimmed;
        IsAutoTitled = false;

        RaiseDomainEvent(new ConversationRenamed(Id, ProjectId, BranchId, Title, IsAutoTitled));
        return Result.Success();
    }
}
