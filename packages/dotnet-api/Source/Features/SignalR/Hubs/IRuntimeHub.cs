using Source.Features.AgentPermissions.Models;
using Source.Features.Conversations.Models;
using Source.Features.GitOps.Models;
using Source.Features.Hooks.Models;
using Source.Features.RuntimeBootstrap.Contracts;
using Source.Features.RuntimeCuration.Models;
using Source.Features.SignalR.Contracts;
using TypedSignalR.Client;

namespace Source.Features.SignalR.Hubs;

/// <summary>
/// Strongly-typed server method surface for <see cref="RuntimeHub"/> — the methods
/// the daemon (TypedSignalR-generated TS client) is allowed to invoke. Carrying
/// the contract on a single <c>[Hub]</c>-decorated interface drives the
/// TypedSignalR.Client.TypeScript analyzer to emit a non-empty
/// <c>HubProxyFactoryProvider</c>; without it the generator ships a stub and the
/// daemon side has nothing to call.
///
/// <para>The matching <see cref="IRuntimeClient"/> receiver interface lives next
/// door — that is the server-to-daemon push channel. Two interfaces, two
/// directions, one hub.</para>
///
/// <para>Lifecycle hooks (<c>OnConnectedAsync</c>, <c>OnDisconnectedAsync</c>) are
/// SignalR-framework concerns and intentionally not declared here.</para>
/// </summary>
[Hub]
public interface IRuntimeHub
{
    Task Heartbeat(HeartbeatPayload payload);

    Task<BootstrapPayloadV2> GetBootstrap();

    /// <summary>
    /// Daemon-invoked: fetch the per-prompt Anthropic credentials for the
    /// project this connection's runtime is pinned to. The fall-back chain
    /// is implemented server-side; daemon just consumes the result.
    /// </summary>
    Task<AgentSecretsDto> GetSecrets();

    /// <summary>
    /// Daemon-invoked: fetch the effective permission config for the project this
    /// connection's runtime is pinned to. Resolution is "project override or system
    /// defaults" (no merging) — see <see cref="Source.Features.AgentPermissions.Services.IAgentPermissionsResolver"/>. The
    /// daemon caches the result for the duration of a single turn so mid-turn
    /// config changes don't create inconsistent decisions inside one tool sequence.
    /// </summary>
    Task<AgentPermissionsConfig> GetAgentPermissions();

    /// <summary>
    /// Daemon-invoked: mint a fresh, repository-scoped GitHub-App installation
    /// token for cloning the project's repo over HTTPS. Replaces the legacy
    /// deploy-key+SSH path. The daemon must pass the <c>owner/name</c> of the
    /// repo it intends to clone; the server cross-checks this against the
    /// connection's pinned project and rejects mismatches (a compromised
    /// daemon cannot mint tokens for arbitrary repos in the same installation).
    /// </summary>
    Task<RepoAccessToken> GetRepoAccessToken(string repoFullName);

    Task RuntimeReady();

    Task ReportError(ErrorReportPayload payload);

    Task ReportDiskPressure(DiskPressurePayload payload);

    Task EmitEvent(EmitEventPayload payload);

    /// <summary>
    /// Daemon-to-server per-message cost report (legacy Claude path). Fires
    /// from the daemon's <c>onRawMessage</c> hook once per assistant message
    /// that carries a <c>usage</c> block. The hub accumulates the
    /// per-message tokens + derived USD cost onto the addressed
    /// <see cref="AgentSession"/>, so the running total at session end is
    /// the sum across every assistant message in that turn.
    ///
    /// <para>The first two positional arguments (<c>containerId</c>,
    /// <c>sessionId</c>) match the wire shape the deployed
    /// <c>glenn-code</c> daemon already invokes — adding the hub method
    /// alone makes its existing emissions land instead of failing silently
    /// in the daemon's <c>.catch(console.error)</c>. The <c>containerId</c>
    /// is observability-only; auth + routing run off the connection-bound
    /// <c>RuntimeId</c> claim like every other daemon push method.</para>
    /// </summary>
    Task ReportSessionCost(string containerId, Guid sessionId, ReportSessionCostPayload payload);

    Task TurnRefused(TurnRefusedPayload payload);

    Task RuntimeSpecDeltaApplied(RuntimeSpecDeltaApplyResultPayload payload);

    Task HookStarted(HookStartedPayload payload);

    Task HookProgress(HookProgressPayload payload);

    Task HookCompleted(HookCompletedPayload payload);

    Task HookConfigError(HookConfigErrorPayload payload);

    Task HookSelfHealStarted(HookSelfHealStartedPayload payload);

    Task HookSelfHealMaxedOut(HookSelfHealMaxedOutPayload payload);

    Task<RequestSelfHealContinuationResponse> RequestSelfHealContinuation(
        RequestSelfHealContinuationPayload payload);

    Task GitOperationStarted(GitOperationStartedPayload payload);

    Task GitOperationCompleted(GitOperationCompletedPayload payload);

    Task CommitMade(CommitMadePayload payload);

    Task GitPushFailed(GitPushFailedPayload payload);

    Task GitPushSucceeded(GitPushSucceededPayload payload);

    Task CommitFailed(CommitFailedPayload payload);

    Task MergeConflict(MergeConflictPayload payload);

    Task<RequestDestructiveGitOpResponse> RequestDestructiveGitOp(
        RequestDestructiveGitOpPayload payload);

    /// <summary>
    /// Daemon-to-server: the agent SDK's <c>canUseTool</c> callback fired and
    /// the daemon wants the user to make the call. Relay-only — the hub fans
    /// the payload out to every React tab in the <c>project-{projectId}</c>
    /// group via <see cref="IAgentClient.PermissionRequested"/>. The eventual
    /// <c>AgentHub.ResolvePermission</c> ships the decision back through
    /// <see cref="IRuntimeClient.PermissionResolved"/>, correlated by the
    /// payload's <c>ToolUseId</c>.
    /// </summary>
    Task PermissionRequested(PermissionRequestedPayload payload);

    /// <summary>
    /// Daemon-to-server: append a structured event to this runtime's event
    /// store and broadcast it to subscribed frontends. The owning runtime
    /// is resolved server-side from the connection's signed <c>rt_runtime</c>
    /// claim — the daemon does not (and cannot) supply a <c>RuntimeId</c> on
    /// the wire. After persistence the hub fans the event out to the
    /// <c>runtime-events:{runtimeId}</c> group via
    /// <see cref="IAgentClient.RuntimeEventReceived"/> so open drawer
    /// Timeline tabs see it in real time.
    /// </summary>
    Task RecordRuntimeEvent(RuntimeEventPayloadDto payload);

    /// <summary>
    /// Daemon-to-server: report the health of this runtime's spec application
    /// (self-healing-runtime-specs, card B1). The owning runtime is resolved
    /// server-side from the connection's signed <c>rt_runtime</c> claim — the
    /// daemon does not (and cannot) supply a <c>RuntimeId</c> on the wire. The
    /// hub maps <c>payload.Health</c> ("Healthy"/"Degraded"/"Unknown") to the
    /// <c>RuntimeSpecHealth</c> enum and persists it to
    /// <c>ProjectRuntime.SpecHealth</c>. Best-effort: the hub never throws and a
    /// persistence failure only logs — boot-issue <i>details</i> ride in
    /// separately-emitted <c>RuntimeEvents</c> (<c>SpecDegraded</c>), not on the
    /// runtime row.
    /// </summary>
    Task ReportSpecHealth(ReportSpecHealthPayload payload);

    /// <summary>
    /// Daemon-to-server: a single line of stdout/stderr from a service the
    /// daemon's <c>LogTailer</c> is currently tailing. The hub resolves the
    /// owning <c>RuntimeId</c> from the connection's signed <c>rt_runtime</c>
    /// claim, then broadcasts to the
    /// <c>service-logs:{runtimeId}:{serviceName}</c> group via
    /// <see cref="IAgentClient.ServiceLogLine"/>. Lines are NOT persisted —
    /// the supervisord-rotated file on disk is the only durable copy.
    /// </summary>
    Task ServiceLogLine(ServiceLogLineDto payload);

    /// <summary>
    /// runtime-observability-super-admin — daemon→server: a single line of
    /// stdout/stderr from the daemon's own log files
    /// (<c>/var/log/supervisor/agent.out.log</c>,
    /// <c>/var/log/supervisor/agent.err.log</c>). The hub resolves the owning
    /// <c>RuntimeId</c> from the connection's signed claim, then broadcasts to
    /// the <c>daemon-logs:{runtimeId}</c> group via
    /// <see cref="IAgentClient.DaemonLogLineReceived"/>. Lines are NOT
    /// persisted — the supervisord-rotated file is the durable source.
    /// </summary>
    Task DaemonLogLine(DaemonLogLineDto payload);

    /// <summary>
    /// Daemon-to-server live supervisord snapshot push. Pushed by the daemon's
    /// <c>ServiceStatusPoller</c> on every poll tick (default 10s). The hub
    /// resolves the owning <c>RuntimeId</c> from the connection's signed
    /// <c>rt_runtime</c> claim — the daemon does not supply it on the wire —
    /// then fans the payload out to the <c>runtime-events:{runtimeId}</c>
    /// group via <see cref="IAgentClient.LiveSupervisordSnapshotReceived"/>.
    ///
    /// <para><b>Not persisted.</b> Pure push-through — consumers cache the
    /// latest snapshot keyed by runtimeId.</para>
    /// </summary>
    Task PushLiveSupervisordSnapshot(LiveSupervisordSnapshotPayload payload);

    /// <summary>
    /// chat-file-attachments — daemon-to-server: ack that an attachment push
    /// (<see cref="IRuntimeClient.StageAttachment"/>) finished. On
    /// <paramref name="success"/> the hub idempotently stamps
    /// <c>Attachment.StagedAt</c>; on failure it leaves the row unstaged and
    /// logs <paramref name="error"/>. Either way it fans an
    /// <c>AttachmentStateChanged</c> notification out to the conversation's
    /// branch group so the composer chip flips its UI state.
    ///
    /// <para><b>Auth.</b> Connection-level <c>RuntimeId</c> claim, same
    /// contract as <c>ReportSessionCost</c>. The hub cross-checks the
    /// attachment's parent conversation's branch resolves to a runtime owned
    /// by the calling daemon — a daemon claiming a peer's attachment is a hard
    /// <see cref="Microsoft.AspNetCore.SignalR.HubException"/>.</para>
    /// </summary>
    Task ReportAttachmentStaged(Guid attachmentId, bool success, string? error);
}
