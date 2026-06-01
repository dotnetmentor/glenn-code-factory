using Source.Shared;
using Source.Shared.Events;
using Tapper;

namespace Source.Features.GitOps.Models;

/// <summary>
/// Type of git operation captured by the daemon. Persisted as <c>int</c> so
/// adding new ops later doesn't shift existing rows. The wire-side mirror
/// (<c>GitOpType</c>) lives in a later card; this enum is the source of truth.
/// </summary>
[TranspilationSource]
public enum GitOpType
{
    Clone = 0,
    Checkout = 1,
    Add = 2,
    Commit = 3,
    Push = 4,
    Fetch = 5,
    Merge = 6,
    BranchCreate = 7,
    BranchList = 8,
    Reset = 9,
    ForcePush = 10,
    BranchDelete = 11,
}

/// <summary>
/// Append-ish audit row capturing a single git invocation on a runtime — the
/// command that ran, when it ran, what it produced and whether the daemon
/// considered it destructive enough to require an out-of-band approval.
///
/// <list type="bullet">
///   <item>One row per git invocation. Inserted when the command starts; the
///         end fields (<see cref="EndedAt"/>, <see cref="ExitCode"/>,
///         <see cref="DurationMs"/>, <see cref="OutputTail"/>,
///         <see cref="OutputHash"/>) are filled in once it completes.</item>
///   <item>FK to <see cref="Source.Features.RuntimeLifecycle.Models.ProjectRuntime"/>
///         on <see cref="RuntimeId"/>; <c>OnDelete</c> is <c>NoAction</c> because
///         we soft-delete runtimes — the git history must outlive a Deleted
///         runtime within the 30-day window.</item>
///   <item><see cref="ConversationId"/> / <see cref="TurnId"/> are plain Guids
///         (no FK) — the git history must survive a hard-delete of either,
///         mirroring the <c>HookExecution</c> / <c>FlyOperation</c> /
///         <c>BootstrapRun</c> convention.</item>
///   <item><see cref="WasDestructive"/> is set by the daemon when the op type
///         is one of <see cref="GitOpType.Reset"/>, <see cref="GitOpType.ForcePush"/>
///         or <see cref="GitOpType.BranchDelete"/>; <see cref="ApprovalId"/>
///         correlates the row to a future <c>RequestDestructiveGitOp</c>
///         approval request (later card).</item>
///   <item>Soft-deletable so operators can hide noisy entries without losing
///         the underlying audit trail (and so the global query filter applies).</item>
/// </list>
///
/// <para>This card is intentionally <i>data only</i>. Commands, queries,
/// controllers and hub methods arrive in follow-up cards. The base class is
/// still <see cref="Entity"/> so future cards can raise events from instance
/// methods without a model change.</para>
/// </summary>
public class GitOperation : Entity, IAuditable, ISoftDelete
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Runtime this git command ran against. FK to <c>ProjectRuntime</c>;
    /// indexed for the dominant "show me git ops for runtime X" lookup.
    /// </summary>
    public Guid RuntimeId { get; set; }

    /// <summary>
    /// Conversation the git command ran for, when applicable. Plain Guid (no
    /// FK) so a hard-deleted conversation doesn't take its git history with
    /// it. Indexed for the "what git ops did this conversation do" query.
    /// </summary>
    public Guid? ConversationId { get; set; }

    /// <summary>
    /// Specific agent turn within the conversation, when applicable. Plain
    /// Guid (no FK) — same outlive-the-row reasoning as <see cref="ConversationId"/>.
    /// </summary>
    public Guid? TurnId { get; set; }

    /// <summary>The kind of git operation that ran.</summary>
    public GitOpType OpType { get; set; }

    /// <summary>
    /// The canonical command line that was executed, e.g.
    /// <c>git commit -m "..."</c>. Capped at 2000 chars — anything longer is
    /// misuse; daemon should truncate or reject upstream.
    /// </summary>
    public string CommandLine { get; set; } = string.Empty;

    /// <summary>UTC timestamp when the git process started.</summary>
    public DateTime StartedAt { get; set; }

    /// <summary>UTC timestamp when the git process ended. Null while still running.</summary>
    public DateTime? EndedAt { get; set; }

    /// <summary>Process exit code. Null while still running.</summary>
    public int? ExitCode { get; set; }

    /// <summary>Wall-clock duration in milliseconds. Null while still running.</summary>
    public int? DurationMs { get; set; }

    /// <summary>
    /// Last 16 KiB of combined stdout+stderr. Anything beyond that is dropped;
    /// callers wanting the full log should arrange off-box storage.
    /// </summary>
    public string OutputTail { get; set; } = string.Empty;

    /// <summary>SHA-256 hex of the full output (64 chars). Used to dedupe identical noisy ops.</summary>
    public string OutputHash { get; set; } = string.Empty;

    /// <summary>
    /// True when the op type is one the daemon considers destructive
    /// (<see cref="GitOpType.Reset"/>, <see cref="GitOpType.ForcePush"/>,
    /// <see cref="GitOpType.BranchDelete"/>). Set by the daemon when the row
    /// is inserted — kept as an explicit column instead of being derived so
    /// operators can filter destructive history without a CASE expression
    /// and so we can change the destructive-set later without re-writing
    /// historical rows.
    /// </summary>
    public bool WasDestructive { get; set; }

    /// <summary>
    /// Correlation id to a future <c>RequestDestructiveGitOp</c> approval
    /// request. Null for non-destructive ops and for destructive ops that
    /// pre-date the approval flow (later card). Plain Guid (no FK) — the
    /// approval table doesn't exist yet and the git history must outlive
    /// any future hard-delete of approval rows.
    /// </summary>
    public Guid? ApprovalId { get; set; }

    // -------- IAuditable --------
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // -------- ISoftDelete --------
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }
    public string? DeletedBy { get; set; }
}
