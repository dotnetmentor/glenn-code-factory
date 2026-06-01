using Tapper;

namespace Source.Features.Hooks.Models;

/// <summary>
/// Daemon-to-server: a hook process has begun. Inserted as a fresh
/// <see cref="HookExecution"/> row with the lifecycle / completion fields left
/// blank — the matching <see cref="HookCompletedPayload"/> (or
/// <see cref="HookConfigErrorPayload"/>) fills them in once the process exits.
///
/// <para><b>Direction.</b> Lives alongside the other daemon-to-server
/// (<c>RuntimeServerPayloads</c>) shapes in spirit, but is colocated under the
/// <c>Hooks</c> slice rather than under <c>SignalR/Contracts</c> — a hook
/// belongs to the hooks feature first, the SignalR transport second.</para>
/// </summary>
[TranspilationSource]
public record HookStartedPayload(
    Guid ExecutionId,
    Guid RuntimeId,
    Guid? ConversationId,
    Guid? TurnId,
    HookPoint HookPoint,
    string HookName,
    string Cmd,
    HookFeedbackMode FeedbackMode,
    DateTime StartedAt);

/// <summary>
/// Daemon-to-server: a single newline-terminated stdout line streamed live
/// while the hook process is still running. <i>Not persisted</i> — only
/// fanned out to the project's UI clients so the chat panel can show live
/// hook output. Compaction into the 16 KiB <c>OutputTail</c> happens on the
/// daemon side and arrives via <see cref="HookCompletedPayload"/>.
/// </summary>
[TranspilationSource]
public record HookProgressPayload(
    Guid ExecutionId,
    Guid RuntimeId,
    string StdoutLine,
    int LineIndex);

/// <summary>
/// Daemon-to-server: the hook process exited normally (zero or non-zero, but
/// the runner itself ran to completion). Fills in the end-of-run fields on
/// the matching <see cref="HookExecution"/>; the row is then immutable.
/// </summary>
[TranspilationSource]
public record HookCompletedPayload(
    Guid ExecutionId,
    Guid RuntimeId,
    int ExitCode,
    int DurationMs,
    string OutputTail,
    string OutputHash,
    bool TimedOut,
    DateTime EndedAt);

/// <summary>
/// Daemon-to-server: the hook could not run at all — e.g. the command was not
/// found, the config was malformed, or a sandbox policy refused it.
/// Distinguished from <see cref="HookCompletedPayload"/> with a non-zero exit
/// code so operators can filter "ran and failed" vs "couldn't run".
/// </summary>
[TranspilationSource]
public record HookConfigErrorPayload(
    Guid ExecutionId,
    Guid RuntimeId,
    string Reason,
    string OutputTail,
    DateTime EndedAt);

/// <summary>
/// Daemon-to-server: relay-only signal that the daemon is starting another
/// agent turn to "self-heal" after a hook reported a problem. <i>Not persisted</i>
/// — purely a UX hint for the chat panel so the user sees the loop continue.
/// The actual continuation flow ships in a follow-up card.
/// </summary>
[TranspilationSource]
public record HookSelfHealStartedPayload(
    Guid RuntimeId,
    Guid ConversationId,
    Guid PreviousTurnId,
    Guid NewTurnId,
    int Iteration);

/// <summary>
/// Daemon-to-server: relay-only signal that the self-heal retry budget has
/// been exhausted and the daemon is giving up on automatic recovery. The
/// chat panel surfaces this so the user knows to intervene.
/// </summary>
[TranspilationSource]
public record HookSelfHealMaxedOutPayload(
    Guid RuntimeId,
    Guid ConversationId,
    Guid TurnId,
    int Iteration);

/// <summary>
/// Daemon-to-server request: an <c>afterPrompt</c> hook reported a failure
/// that the daemon believes can be self-healed by re-invoking Claude with the
/// hook's feedback prompt. The server is the budget authority — it owns the
/// per-turn cap (3 attempts) and the database, so it must approve the retry,
/// dispatch a fresh <c>StartTurn</c> against the same Cursor SDK agent id
/// (so context is retained), and report the outcome synchronously to the
/// daemon.
///
/// <list type="bullet">
///   <item><see cref="RuntimeId"/> must match the connection's claimed
///         runtime — otherwise the request is rejected with
///         <c>runtimeMismatch</c>. Belt-and-braces against a daemon claiming
///         a peer's runtime.</item>
///   <item><see cref="ConversationId"/> is the conversation the new turn will
///         attach to. Inherited unchanged from the failing turn — self-heal
///         never crosses conversations.</item>
///   <item><see cref="TurnId"/> is the failing <c>AgentSession.Id</c>. The
///         hub looks the row up to read+bump <c>SelfHealAttempts</c> and to
///         enforce that the session is still <c>Running</c>.</item>
///   <item><see cref="ClaudeSessionId"/> is the SDK session token captured by
///         the daemon during the failing turn. The new dispatch reuses it so
///         Claude has full context of what it just did.</item>
///   <item><see cref="HookName"/> + <see cref="FeedbackPrompt"/> are the hook
///         that fired and the prompt the daemon wants to send. <c>HookName</c>
///         is for telemetry / UX; <c>FeedbackPrompt</c> is the literal text
///         that lands on the new <c>AgentSession.Prompt</c>.</item>
///   <item><see cref="Iteration"/> is the daemon's view of how many self-heal
///         loops have run on this turn. Used purely for the UI relay — the
///         server's authoritative budget lives on
///         <c>AgentSession.SelfHealAttempts</c>.</item>
/// </list>
/// </summary>
[TranspilationSource]
public record RequestSelfHealContinuationPayload(
    Guid RuntimeId,
    Guid ConversationId,
    Guid TurnId,
    string AgentId,
    string HookName,
    string FeedbackPrompt,
    int Iteration);

/// <summary>
/// Synchronous response to <see cref="RequestSelfHealContinuationPayload"/>.
///
/// <list type="bullet">
///   <item><see cref="Accepted"/> is <c>true</c> only when the budget had
///         room AND the session is still in <c>Running</c> AND the runtime
///         claim matched. On <c>true</c>, <see cref="NewTurnId"/> is the
///         freshly-allocated <c>AgentSession.Id</c> the daemon should track
///         for follow-up events.</item>
///   <item><see cref="RejectionReason"/> is one of the small fixed set of
///         strings (<c>maxedOut</c>, <c>turnNotRunning</c>,
///         <c>runtimeMismatch</c>) when accepted is false; null otherwise.
///         The daemon switches on it for telemetry / UX hints.</item>
/// </list>
/// </summary>
[TranspilationSource]
public record RequestSelfHealContinuationResponse(
    bool Accepted,
    Guid? NewTurnId,
    string? RejectionReason);
