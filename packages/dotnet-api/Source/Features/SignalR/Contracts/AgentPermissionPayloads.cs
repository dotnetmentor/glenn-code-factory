using Tapper;

namespace Source.Features.SignalR.Contracts;

/// <summary>
/// Daemon-to-server-to-React broadcast: the agent SDK's <c>canUseTool</c> callback
/// fired and the daemon is asking a human to approve a single tool invocation.
/// One correlation key per outstanding request — the SDK-supplied
/// <see cref="ToolUseId"/> — which the eventual
/// <see cref="ResolvePermissionPayload"/> echoes back so the daemon can match the
/// decision to its in-flight callback.
///
/// <para><b>Direction.</b> Daemon invokes <c>RuntimeHub.PermissionRequested</c>
/// with this payload; the hub relays it onto every React tab in the
/// <c>project-{projectId}</c> group via
/// <see cref="Hubs.IAgentClient.PermissionRequested"/>. Wire surface only — the
/// resolver service, project override entity, and frontend approval card are
/// owned by separate cards and explicitly out of scope here.</para>
///
/// <list type="bullet">
///   <item><see cref="ToolUseId"/> is the SDK-emitted id (per spec scene 2/4 —
///         the correlation key that matches a request to its resolution). String
///         shape, not Guid, because the wire format is owned by the agent backend.</item>
///   <item><see cref="ToolName"/> is the tool the agent is trying to call —
///         e.g. <c>Bash</c>, <c>Write</c>, <c>Edit</c>. Plain string so the
///         frontend can render "Claude wants to run X" without a lookup table.</item>
///   <item><see cref="ToolInput"/> is the raw SDK tool-input blob, JSON-serialized
///         as a string for wire stability — shape varies per tool, and Tapper's
///         TS codegen can't transpile <c>JsonElement</c> directly. The daemon
///         <c>JSON.stringify</c>s the SDK's <c>tool_use.input</c> before
///         emitting; the frontend <c>JSON.parse</c>s on the way in and renders
///         the result on the approval card. No server-side semantic
///         interpretation today.</item>
///   <item><see cref="ConversationId"/> / <see cref="TurnId"/> are optional
///         correlation hints so the frontend can render the approval card inline
///         in the chat panel even if the runtime spec didn't pin them. Either
///         can be <c>null</c> when the SDK callback fired outside of a tracked
///         turn (defence in depth — should not happen on the happy path).</item>
/// </list>
/// </summary>
[TranspilationSource]
public record PermissionRequestedPayload(
    string ToolUseId,
    string ToolName,
    string ToolInput,
    Guid? ConversationId,
    Guid? TurnId);

/// <summary>
/// React-to-server-to-daemon resolution: the user clicked one of the four
/// approval-card actions and the daemon's pending <c>canUseTool</c> callback
/// needs the answer to unblock. Echoes <see cref="ToolUseId"/> verbatim from
/// the originating <see cref="PermissionRequestedPayload"/> so the daemon can
/// look up its in-memory waiter by the same correlation key.
///
/// <para><b>Direction.</b> The React client invokes
/// <c>AgentHub.ResolvePermission</c> with this payload; the hub resolves
/// project → active runtime → daemon connection and forwards via
/// <see cref="Hubs.IRuntimeClient.PermissionResolved"/> to that one daemon.
/// No persistence — this is pure session-scoped wire — and no policy logic
/// (resolver service is a separate card).</para>
///
/// <list type="bullet">
///   <item><see cref="Decision"/> is one of the four card actions, plain
///         string for wire stability (the frontend uses a TS string-literal
///         union; the daemon parses + maps to the SDK return shape):
///         <c>approve</c>, <c>approveAlwaysSession</c>, <c>deny</c>,
///         <c>denyWithFeedback</c>. Per-spec we deliberately do NOT model an
///         enum so future actions can be added without a breaking wire change.</item>
///   <item><see cref="Feedback"/> is populated when the user picked "Deny with
///         feedback…" — the agent uses this as next-step context per the
///         user-story "I want the agent to receive my reason as part of its
///         next-step context". <c>null</c> for the other three decisions.</item>
///   <item><see cref="ConversationId"/> / <see cref="TurnId"/> echo the
///         originating request's correlation hints so the daemon can sanity-check
///         the answer is for the turn it's still waiting on. Optional — daemon
///         tolerates <c>null</c>.</item>
/// </list>
/// </summary>
[TranspilationSource]
public record ResolvePermissionPayload(
    string ToolUseId,
    string Decision,
    string? Feedback,
    Guid? ConversationId,
    Guid? TurnId);
