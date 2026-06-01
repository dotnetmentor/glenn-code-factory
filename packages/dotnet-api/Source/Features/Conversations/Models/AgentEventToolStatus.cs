using Tapper;

namespace Source.Features.Conversations.Models;

/// <summary>
/// Lifecycle status for a Cursor <c>SDKToolUseMessage</c> row (an
/// <see cref="AgentEvent"/> where <see cref="AgentEvent.Kind"/> is
/// <see cref="AgentEventKind.ToolUse"/>). Mirrors Cursor SDK's
/// <c>SDKToolUseMessage.status</c>. Persisted as a string for wire stability
/// across enum reordering — same convention used elsewhere in the codebase
/// (<see cref="AgentEventKind"/>, <see cref="AgentEventRunStatus"/>).
/// </summary>
[TranspilationSource]
public enum AgentEventToolStatus
{
    /// <summary>Tool call dispatched but no result yet — the active row.</summary>
    Running = 0,

    /// <summary>Tool call returned successfully. <see cref="AgentEvent.Result"/> populated.</summary>
    Completed = 1,

    /// <summary>Tool call failed. <see cref="AgentEvent.Result"/> may carry the error payload.</summary>
    Error = 2,
}
