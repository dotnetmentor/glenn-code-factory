using Tapper;

namespace Source.Features.Conversations.Models;

/// <summary>
/// Discriminator for <see cref="AgentEvent"/> — the row's "what kind of Cursor
/// SDK message is this?" tag. Maps 1:1 to Cursor's <c>SDKMessage</c> shapes
/// (see <c>cursor-native-chat-ux</c> spec §5). Persisted as a string so adding
/// a new kind doesn't reshuffle existing rows.
///
/// <para>Each kind activates a different subset of nullable columns on the
/// shared <see cref="AgentEvent"/> table. The shared <c>(SessionId, Sequence)</c>
/// composite PK keeps the monotonic per-session ordering the chat panel
/// depends on — kind matters for rendering, not ordering.</para>
/// </summary>
[TranspilationSource]
public enum AgentEventKind
{
    /// <summary>The user's prompt was received by the daemon. First row of a session at <c>Sequence = 0</c>.</summary>
    PromptReceived = 0,

    /// <summary>
    /// Cursor <c>SDKAssistantTextMessage</c> — a chunk of assistant-visible
    /// text. <see cref="AgentEvent.Text"/> carries the body.
    /// </summary>
    AssistantText = 1,

    /// <summary>
    /// Cursor <c>SDKThinkingMessage</c> — extended-thinking chunk.
    /// <see cref="AgentEvent.Text"/> carries the body;
    /// <see cref="AgentEvent.ThinkingDurationMs"/> is set on terminal frames.
    /// </summary>
    Thinking = 2,

    /// <summary>
    /// Cursor <c>SDKToolUseMessage</c> — a tool invocation. Promoted columns:
    /// <see cref="AgentEvent.CallId"/>, <see cref="AgentEvent.ToolName"/>,
    /// <see cref="AgentEvent.ToolStatus"/>, <see cref="AgentEvent.Args"/>,
    /// <see cref="AgentEvent.Result"/>, <see cref="AgentEvent.ArgsTruncated"/>,
    /// <see cref="AgentEvent.ResultTruncated"/>.
    /// </summary>
    ToolUse = 3,

    /// <summary>
    /// Cursor <c>SDKStatusMessage</c> — run-level state transition. Promoted
    /// columns: <see cref="AgentEvent.RunStatus"/>,
    /// <see cref="AgentEvent.StatusMessage"/>.
    /// </summary>
    Status = 4,

    /// <summary>
    /// Cursor <c>SDKTaskMessage</c> — milestone divider. Promoted columns:
    /// <see cref="AgentEvent.TaskId"/>, <see cref="AgentEvent.TaskTitle"/>.
    /// </summary>
    Task = 5,
}
