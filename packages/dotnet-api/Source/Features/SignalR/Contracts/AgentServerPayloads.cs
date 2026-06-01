using Tapper;

namespace Source.Features.SignalR.Contracts;

/// <summary>
/// React-to-server invocation: the user typed a prompt and the client wants the
/// platform to start an agent turn. Carried by <c>AgentHub.SubmitPrompt</c>.
///
/// <list type="bullet">
///   <item><see cref="ProjectId"/> identifies which project's runtime should
///         execute the turn. The hub looks up the <c>ProjectRuntime</c> row by
///         this id.</item>
///   <item><see cref="ConversationId"/> is <c>null</c> on the very first prompt
///         of a new conversation; the hub creates the conversation, derives a
///         title from the prompt, and returns the new id. Non-null means
///         "append to this existing conversation" — the hub validates that the
///         conversation belongs to the same <see cref="ProjectId"/>.</item>
///   <item><see cref="BranchId"/> is the project branch the conversation is
///         scoped to. Now a real FK to <c>ProjectBranch</c> (promoted from a
///         free-form string in the e2e-smoketest spec) — the client must pass
///         the id of an existing branch row, typically the project's default.</item>
///   <item><see cref="Text"/> is the raw user prompt. Hub enforces basic
///         sanity (non-empty, ≤50_000 chars) and stores it on the
///         <c>AgentSession.Prompt</c> column verbatim.</item>
///   <item><see cref="ModelId"/> is the optional per-session Cursor model
///         override. <c>null</c> means "use the project default".</item>
///   <item><see cref="Yolo"/> is the per-turn "skip permission prompts"
///         override. When <c>true</c>, the dispatcher overrides the resolved
///         <c>AgentPermissionsConfig</c> for THIS TURN ONLY to
///         <c>PermissionMode = "bypassPermissions"</c> /
///         <c>AllowDangerouslySkipPermissions = true</c>. The project's stored
///         permissions row is NOT modified. Default <c>false</c> preserves
///         normal permission-gating behaviour.</item>
/// </list>
///
/// <para>Direction matters: this lives in <c>AgentServerPayloads</c> — sent
/// FROM the React client TO the hub — alongside any future client-to-server
/// invocations (cancel, replay request). Server-to-client payloads belong in
/// <c>AgentClientPayloads</c>.</para>
/// </summary>
[TranspilationSource]
public record SubmitPromptPayload(
    Guid ProjectId,
    Guid? ConversationId,
    Guid BranchId,
    string Text,
    Guid? ModelId = null,
    bool Yolo = false);

/// <summary>
/// Response returned synchronously to the calling client from
/// <c>AgentHub.SubmitPrompt</c>. The client uses these ids to navigate to the
/// freshly-created or updated conversation and to associate inbound
/// <c>AgentEvent</c> broadcasts with the right session row before the first
/// daemon-emitted event arrives.
///
/// <list type="bullet">
///   <item><see cref="Queued"/> is <c>true</c> when another session was already
///         running on this runtime and the new session was queued behind it
///         instead of being dispatched immediately. The session sits in
///         <see cref="Source.Features.Conversations.Models.AgentSessionStatus.Pending"/>
///         until the runtime frees up; the chat panel uses this to render a
///         "queued" affordance instead of "in flight".</item>
///   <item><see cref="QueuePosition"/> mirrors
///         <see cref="Source.Features.Conversations.Models.AgentSession.QueuePosition"/>
///         on the new row — <c>null</c> when <see cref="Queued"/> is false,
///         a 1-based position when queued (1 = next to dispatch).</item>
/// </list>
/// </summary>
[TranspilationSource]
public record SubmitPromptResponse(
    Guid ConversationId,
    Guid SessionId,
    bool Queued,
    int? QueuePosition);

/// <summary>
/// React-to-server invocation: the user clicked the "stop" affordance on an
/// in-flight session. Carried by <c>AgentHub.CancelTurn</c>.
///
/// <para>Distinct from the server-to-daemon <see cref="CancelTurnPayload"/> —
/// this one travels FROM the React client TO the hub. The hub looks up the
/// session, resolves the project's runtime, and dispatches a
/// <see cref="CancelTurnPayload"/> to the daemon group. Status transitions
/// only happen when the daemon emits its <c>TurnCanceled</c> event back via
/// <c>RuntimeHub.EmitEvent</c>; the hub never mutates session status itself.</para>
///
/// <para>No <c>Reason</c> field on the request — the hub fills in a default
/// when constructing the outbound payload. The user-facing UI doesn't have
/// granular reason taxonomy yet.</para>
/// </summary>
[TranspilationSource]
public record CancelTurnRequest(Guid SessionId);

/// <summary>
/// React-to-server invocation: the client (re)connected and wants the platform
/// to re-deliver any <c>AgentEvent</c> rows it missed since the last sequence
/// it observed. Carried by <c>AgentHub.RequestEventReplay</c>.
///
/// <list type="bullet">
///   <item><see cref="SessionId"/> identifies the <c>AgentSession</c> whose
///         event stream should be replayed.</item>
///   <item><see cref="SinceSequence"/> is exclusive — the response contains
///         every event with <c>Sequence &gt; SinceSequence</c>. Pass <c>-1</c>
///         to receive the full stream from the start (fresh tab load).</item>
/// </list>
///
/// <para>The hub returns a <c>List&lt;AgentEventNotification&gt;</c> — the same
/// record type the broadcast handler emits — so the JS client uses one event
/// shape for live and replayed events. Hard-capped at 1000 rows; if the client
/// genuinely needs more it should reload the conversation via the REST API.</para>
///
/// <para>Replay is best-effort: an unknown <see cref="SessionId"/> resolves to
/// an empty list rather than an error. The client uses other state to decide
/// whether the session still exists.</para>
/// </summary>
[TranspilationSource]
public record EventReplayRequest(
    Guid SessionId,
    long SinceSequence);
