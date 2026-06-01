namespace Source.Features.Conversations.Models;

/// <summary>
/// Per-turn aggregate result row — one row per <see cref="AgentSession"/>
/// when the run reaches a terminal state. Captures Cursor SDK's
/// <c>RunResult</c> shape: duration, model, optional git provenance, and the
/// list of artifacts (files edited / created / deleted) as <c>jsonb</c>.
///
/// <para>This is the row the chat panel's "turn footer" reads to render:
/// <i>"Finished in 14.2s · claude-sonnet-4 · 5 files edited · view PR ↗"</i>
/// (see cursor-native-chat-ux §2 Scene 6). Distinct from
/// <see cref="AgentEvent"/> rows — those are the event stream; this is the
/// summary projection.</para>
///
/// <list type="bullet">
///   <item><see cref="SessionId"/> is both the PK and the FK to
///         <see cref="AgentSession"/> with cascade delete — one result per
///         session, no separate id needed.</item>
///   <item><see cref="ArtifactsJson"/> stays as <c>jsonb</c> because the
///         shape evolves per Cursor SDK release (file paths, line counts,
///         maybe diff hashes later). The chat panel parses on read; the
///         "N files edited" chip just <c>.length</c>s the array.</item>
///   <item>Only <see cref="CreatedAt"/> is tracked — rows are immutable once
///         written, same convention as <see cref="AgentEvent"/>.</item>
/// </list>
/// </summary>
public class RunResult
{
    /// <summary>
    /// Owning session. Both PK and FK with cascade — one result per session.
    /// </summary>
    public Guid SessionId { get; set; }

    /// <summary>
    /// Wall-clock duration of the turn in milliseconds, sourced from Cursor's
    /// <c>RunResult.duration_ms</c>. Frontend renders as "14.2s" in the
    /// turn footer.
    /// </summary>
    public long DurationMs { get; set; }

    /// <summary>
    /// Model slug the agent ran with (e.g. <c>"claude-sonnet-4"</c>). Sourced
    /// from Cursor's <c>RunResult.model</c>. Frontend renders verbatim in the
    /// turn footer.
    /// </summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>
    /// Optional git branch the agent worked on, sourced from Cursor's
    /// <c>RunResult.git.branches[0].name</c>. Nullable — many runs don't
    /// touch git at all.
    /// </summary>
    public string? GitBranch { get; set; }

    /// <summary>
    /// Optional PR URL, sourced from Cursor's
    /// <c>RunResult.git.branches[0].prUrl</c>. Nullable — only set when the
    /// agent opened (or referenced) a PR. Frontend renders as the
    /// "view PR ↗" chip only when present.
    /// </summary>
    public string? GitPrUrl { get; set; }

    /// <summary>
    /// Artifacts produced by the run as <c>jsonb</c> — the list of files the
    /// agent edited / created / deleted. Shape evolves per Cursor SDK release;
    /// the chat panel just counts entries for the "N files edited" chip and
    /// links each entry into the existing diff viewer.
    /// </summary>
    public string ArtifactsJson { get; set; } = "[]";

    /// <summary>UTC timestamp the result was recorded server-side.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>Owning session. Required navigation.</summary>
    public AgentSession Session { get; set; } = null!;
}
