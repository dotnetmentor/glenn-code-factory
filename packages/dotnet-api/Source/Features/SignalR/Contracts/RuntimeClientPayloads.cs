using Source.Features.ProjectSecrets.Models;
using Tapper;

namespace Source.Features.SignalR.Contracts;

/// <summary>
/// Server-to-daemon command instructing the daemon to begin a new agent turn.
///
/// <para><b>Multi-backend fields (claude-agent-backend Phase 3).</b> The first
/// seven fields are the original Cursor-only contract and are preserved
/// byte-for-byte. The trailing three are additive and defaulted so the Cursor
/// path is unaffected and older daemons that ignore them keep working:
/// <list type="bullet">
///   <item><see cref="Backend"/> — the per-turn backend discriminator
///         (<c>"cursor"</c> | <c>"claude"</c>). The dispatcher resolves it from
///         the turn → conversation → cursor chain, so it is always populated on
///         the wire; the daemon's <c>TurnRunner</c> uses it to pick the agent
///         factory, falling back to its configured default when absent. Default
///         <c>"cursor"</c> keeps the wire shape stable for non-Claude callers.</item>
///   <item><see cref="ReasoningEffort"/> — the Claude SDK <c>effort</c> level
///         (<c>"low"</c> | <c>"medium"</c> | <c>"high"</c> | <c>"xhigh"</c> |
///         <c>"max"</c>). Populated only on the Claude path; <c>null</c> for
///         Cursor turns and when no effort override applies (the daemon /
///         model default then wins).</item>
///   <item><see cref="ClaudeResumeId"/> — the Claude Agent SDK <c>session_id</c>
///         captured on a prior turn, used to resume the conversation. The
///         Cursor resume id continues to ride on <see cref="AgentId"/>; this
///         is the Claude-only parallel so the two backends never share a resume
///         slot. <c>null</c> on the first Claude turn of a conversation and on
///         every Cursor turn.</item>
/// </list></para>
/// </summary>
[TranspilationSource]
public record StartTurnPayload(
    Guid SessionId,
    Guid ConversationId,
    string Prompt,
    string? Model = null,
    string? AgentId = null,
    bool Yolo = false,
    bool PullBeforeStart = false,
    string Backend = "cursor",
    string? ReasoningEffort = null,
    string? ClaudeResumeId = null);

/// <summary>
/// Server-to-daemon command instructing the daemon to abort the in-flight turn.
/// </summary>
[TranspilationSource]
public record CancelTurnPayload(
    Guid SessionId,
    string Reason);

/// <summary>
/// Server-to-daemon command pushing a config refresh.
/// </summary>
[TranspilationSource]
public record ConfigUpdatePayload(
    Guid RuntimeId,
    string Version,
    string? RuntimeToken = null,
    string? HooksJson = null,
    bool? AutoCommit = null,
    string? DeployKey = null,
    List<EnvVarDelta>? EnvVarsDelta = null);

/// <summary>
/// Server-to-daemon command instructing the daemon to restart a single managed service.
/// </summary>
[TranspilationSource]
public record RestartServicePayload(
    Guid RuntimeId,
    string ServiceName,
    string Reason,
    Guid RequestId);

/// <summary>
/// Server-to-daemon command instructing the daemon to wipe bootstrap and re-fetch.
/// </summary>
[TranspilationSource]
public record ForceRebootstrapPayload(
    string Reason,
    DateTime InitiatedAt);

/// <summary>
/// chat-file-attachments — server-to-daemon command pushed when the browser
/// finishes its direct-to-R2 upload (<c>POST /api/attachments/{id}/complete</c>)
/// and the backend has stamped <c>Attachment.UploadedAt</c>. The daemon downloads
/// the bytes via the included presigned GET URL and writes them to the local
/// FS at <see cref="LocalPath"/>, then acks back through
/// <c>RuntimeHub.ReportAttachmentStaged</c>.
///
/// <list type="bullet">
///   <item><see cref="AttachmentId"/> — primary key of the Attachment row. The
///         daemon echoes this back on its ack so the hub can stamp
///         <c>StagedAt</c> on the right row.</item>
///   <item><see cref="ConversationId"/> — parent conversation, for the daemon's
///         audit logs and to group local files without an extra round-trip.</item>
///   <item><see cref="FileName"/> — original filename the user uploaded.</item>
///   <item><see cref="DownloadUrl"/> — short-lived presigned GET URL against R2.
///         The daemon must finish the download before the URL expires.</item>
///   <item><see cref="LocalPath"/> — the absolute path on the daemon's FS the
///         file must land at. Computed server-side via
///         <c>PromptPrefixBuilder.LocalPathFor</c> so the daemon and the
///         prompt-prefix renderer agree on exactly one path per attachment.</item>
/// </list>
///
/// <para><b>Idempotency.</b> The hub only pushes this payload when
/// <c>Attachment.StagedAt</c> is still null. A re-push after a daemon restart is
/// fine — the daemon should overwrite the local file and re-ack.</para>
/// </summary>
[TranspilationSource]
public record StageAttachmentPayload(
    Guid AttachmentId,
    Guid ConversationId,
    string FileName,
    string DownloadUrl,
    string LocalPath);
