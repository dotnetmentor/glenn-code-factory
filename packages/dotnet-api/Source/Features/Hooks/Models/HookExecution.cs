using Source.Shared;
using Source.Shared.Events;
using Tapper;

namespace Source.Features.Hooks.Models;

/// <summary>
/// Lifecycle point at which a hook is invoked relative to the agent loop.
/// Persisted as <c>int</c> so adding new points later doesn't shift existing rows.
/// </summary>
[TranspilationSource]
public enum HookPoint
{
    BeforePrompt = 0,
    AfterPrompt = 1,
    OnFileChange = 2,
    BeforeCommit = 3,
}

/// <summary>
/// How the hook's output is surfaced back to the agent.
/// <list type="bullet">
///   <item><see cref="OnFailure"/> — only show output when the hook fails (non-zero exit).</item>
///   <item><see cref="Always"/> — always feed the output back into the prompt.</item>
///   <item><see cref="Silent"/> — never feed output back; we still record it for diagnostics.</item>
/// </list>
/// </summary>
[TranspilationSource]
public enum HookFeedbackMode
{
    OnFailure = 0,
    Always = 1,
    Silent = 2,
}

/// <summary>
/// Append-ish audit row capturing a single hook invocation on a runtime — the
/// command that ran, when it ran, what it produced and how the daemon decided
/// to feed that output back to the agent.
///
/// <list type="bullet">
///   <item>One row per hook invocation. Inserted when the hook starts; the
///         end fields (<see cref="EndedAt"/>, <see cref="ExitCode"/>,
///         <see cref="DurationMs"/>, <see cref="OutputTail"/>,
///         <see cref="OutputHash"/>) are filled in once it completes.</item>
///   <item>FK to <see cref="Source.Features.RuntimeLifecycle.Models.ProjectRuntime"/>
///         on <see cref="RuntimeId"/>; <c>OnDelete</c> is <c>NoAction</c> because
///         we soft-delete runtimes — the hook history must outlive a Deleted
///         runtime within the 30-day window.</item>
///   <item><see cref="ConversationId"/> / <see cref="TurnId"/> are plain Guids
///         (no FK) — the hook history must survive a hard-delete of either,
///         mirroring the <c>FlyOperation.RuntimeId</c> /
///         <c>BootstrapRun.RuntimeId</c> convention.</item>
///   <item>Soft-deletable so operators can hide noisy entries without losing
///         the underlying audit trail (and so the global query filter applies).</item>
/// </list>
///
/// <para>This card is intentionally <i>data only</i>. Commands, queries,
/// controllers and hub methods arrive in follow-up cards. The base class is
/// still <see cref="Entity"/> so future cards can raise events from instance
/// methods without a model change.</para>
/// </summary>
public class HookExecution : Entity, IAuditable, ISoftDelete
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Runtime this hook ran against. FK to <c>ProjectRuntime</c>; indexed
    /// for the dominant "show me hooks for runtime X" lookup.
    /// </summary>
    public Guid RuntimeId { get; set; }

    /// <summary>
    /// Conversation the hook ran for, when applicable. Plain Guid (no FK)
    /// so a hard-deleted conversation doesn't take its hook history with it.
    /// Indexed for the "what did the hooks say in this conversation" query.
    /// </summary>
    public Guid? ConversationId { get; set; }

    /// <summary>
    /// Specific agent turn within the conversation, when applicable. Plain
    /// Guid (no FK) — same outlive-the-row reasoning as <see cref="ConversationId"/>.
    /// </summary>
    public Guid? TurnId { get; set; }

    /// <summary>Lifecycle point at which the hook fired.</summary>
    public HookPoint HookPoint { get; set; }

    /// <summary>
    /// Operator-defined name of the hook (e.g. <c>"prettier"</c>, <c>"eslint"</c>).
    /// Free-form, capped at 200 chars.
    /// </summary>
    public string HookName { get; set; } = string.Empty;

    /// <summary>
    /// The shell command that was executed. Capped at 2000 chars — anything
    /// longer is misuse; daemon should truncate or reject upstream.
    /// </summary>
    public string Cmd { get; set; } = string.Empty;

    /// <summary>UTC timestamp when the hook process started.</summary>
    public DateTime StartedAt { get; set; }

    /// <summary>UTC timestamp when the hook process ended. Null while still running.</summary>
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

    /// <summary>SHA-256 hex of the full output (64 chars). Used to dedupe identical noisy hooks.</summary>
    public string OutputHash { get; set; } = string.Empty;

    /// <summary>How this hook's output should be fed back to the agent.</summary>
    public HookFeedbackMode FeedbackMode { get; set; }

    /// <summary>
    /// True if the hook invocation itself was misconfigured (e.g. command not
    /// found, malformed config). Distinct from a hook that ran and failed
    /// — operators want to filter the two separately.
    /// </summary>
    public bool WasConfigError { get; set; }

    // -------- IAuditable --------
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // -------- ISoftDelete --------
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }
    public string? DeletedBy { get; set; }
}
