using Source.Features.Conversations.Models;
using Source.Features.GitOps.Models;
using Source.Features.Hooks.Models;
using Source.Features.RuntimeCuration.Models;
using Source.Features.SignalR.Contracts;
using TypedSignalR.Client;

namespace Source.Features.SignalR.Hubs;

/// <summary>
/// Strongly-typed receiver interface for clients connected to <see cref="AgentHub"/>.
/// Every method here is invoked by the server on the React client; the React side
/// registers handlers for these names. Keeping the contract on a single interface
/// means a backend change that drops a method breaks compile in the generated
/// TypeScript — which is the whole point of using TypedSignalR.
///
/// <para>This card defines only the receiver shape. The server-side methods that
/// invoke these (e.g. domain-event-to-broadcast handlers) ship in subsequent
/// cards of the signalr-architecture spec.</para>
/// </summary>
[Receiver]
public interface IAgentClient
{
    /// <summary>Pushed whenever a runtime moves between lifecycle states.</summary>
    Task RuntimeStateChanged(RuntimeStateChangedNotification payload);

    /// <summary>Pushed during daemon bootstrap to drive the boot-progress UI.</summary>
    Task BootstrapProgress(BootstrapProgressNotification payload);

    /// <summary>
    /// Pushed by the wake-on-connect flow the moment we ask Fly to start a
    /// suspended machine — gives the chat panel a "waking up..." affordance
    /// immediately, before the slower
    /// <see cref="RuntimeStateChangedNotification"/> Suspended → Waking edge
    /// fans out from the state-machine transition.
    /// </summary>
    Task RuntimeWaking(RuntimeWakingNotification payload);

    /// <summary>Generic toast / banner notification surface.</summary>
    Task Notification(NotificationPayload payload);

    /// <summary>
    /// Pushed for every <c>AgentEvent</c> the daemon emits into a session
    /// (assistant text chunks, tool calls, lifecycle ticks, …). Drives the
    /// live conversation feed in the React client. Replay of missed events on
    /// reconnect is handled by a separate request/response flow, not by this
    /// fire-and-forget broadcast.
    /// </summary>
    Task AgentEvent(AgentEventNotification payload);

    /// <summary>
    /// Pushed when a session's terminal Status frame lands and the daemon
    /// included a <see cref="RunResultDto"/> aggregate. Drives the chat
    /// panel's turn footer ("Finished in 14.2s · claude-sonnet-4 · 5 files
    /// edited · view PR ↗") live without a REST round-trip. Fan-out scope
    /// mirrors <see cref="AgentEvent"/> — same branch + workspace groups.
    /// </summary>
    Task RunResult(RunResultNotification payload);

    /// <summary>
    /// Pushed whenever a conversation's title changes — user-initiated rename
    /// or one-shot auto-retitle off the first AssistantText chunk. Drives live
    /// title updates in the conversation list and chat header across all open
    /// tabs of the same project.
    /// </summary>
    Task ConversationRenamed(ConversationRenamedNotification payload);

    /// <summary>Pushed when a hook process starts on a daemon. Drives the
    /// "running…" affordance in the chat panel's hook strip.</summary>
    Task HookStarted(HookStartedPayload payload);

    /// <summary>Pushed for every stdout line of a still-running hook. Not
    /// persisted server-side — pure live UX.</summary>
    Task HookProgress(HookProgressPayload payload);

    /// <summary>Pushed when a hook process exits (any exit code). Carries the
    /// 16 KiB tail and the SHA-256 of the full output for dedupe.</summary>
    Task HookCompleted(HookCompletedPayload payload);

    /// <summary>Pushed when a hook could not run at all (command not found,
    /// malformed config, sandbox refusal, …). Distinct from a hook that ran
    /// and failed.</summary>
    Task HookConfigError(HookConfigErrorPayload payload);

    /// <summary>Relay-only: the daemon has started another turn to recover
    /// from a failing hook. UX hint; no persistence.</summary>
    Task HookSelfHealStarted(HookSelfHealStartedPayload payload);

    /// <summary>Relay-only: the daemon has exhausted the self-heal budget.
    /// UX hint; no persistence.</summary>
    Task HookSelfHealMaxedOut(HookSelfHealMaxedOutPayload payload);

    /// <summary>Pushed when the daemon begins a git operation. Drives the
    /// "running…" affordance in the git activity strip.</summary>
    Task GitOperationStarted(GitOperationStartedPayload payload);

    /// <summary>Pushed when a git operation exits. Carries the exit code,
    /// duration, and a 16 KiB output tail for the UI.</summary>
    Task GitOperationCompleted(GitOperationCompletedPayload payload);

    /// <summary>Pushed when a commit lands. High-level UX signal so the chat
    /// panel and commit history can refresh without parsing every git op.</summary>
    Task CommitMade(CommitMadePayload payload);

    /// <summary>Pushed when a <c>git push</c> fails. Carries a coarse failure
    /// reason for branching + an output tail for operators.</summary>
    Task GitPushFailed(GitPushFailedPayload payload);

    /// <summary>Pushed when a <c>git push</c> succeeds. Positive ack so the UI
    /// can clear an "out of sync with GitHub" banner once the working tree is
    /// back in lockstep with the remote.</summary>
    Task GitPushSucceeded(GitPushSucceededPayload payload);

    /// <summary>Pushed when a <c>git commit</c> attempt failed (e.g. missing
    /// identity, hook rejection, lock contention). Drives the "out of sync
    /// with GitHub" banner alongside <see cref="GitPushFailed"/>.</summary>
    Task CommitFailed(CommitFailedPayload payload);

    /// <summary>Pushed when a merge produced conflicts. Fan-out only; the
    /// working tree is the source of truth.</summary>
    Task MergeConflict(MergeConflictPayload payload);

    /// <summary>Pushed when the daemon requested an approval for a destructive
    /// git op. The UI surfaces the prompt; the eventual approve/reject flow
    /// ships in a follow-up card.</summary>
    Task DestructiveGitOpRequested(Guid approvalId, RequestDestructiveGitOpPayload payload);

    /// <summary>Pushed when the daemon refused to start a turn because another
    /// turn is still in flight on the same runtime (single-turn invariant).
    /// The rejected session has been flipped to
    /// <see cref="AgentSessionStatus.Failed"/> server-side; this fan-out lets
    /// the chat panel surface the refusal without polling.</summary>
    Task TurnRefused(TurnRefusedPayload payload);

    /// <summary>Pushed when the daemon successfully calls
    /// <c>POST /api/runtimes/{id}/proposals</c> (i.e. invokes its
    /// <c>propose_runtime_spec</c> custom tool). Drives the confirmation-card
    /// UI in the chat panel — Approve / Edit / Reject is rendered directly
    /// from this notification.</summary>
    Task RuntimeProposalCreated(RuntimeProposalCreatedPayload payload);

    /// <summary>
    /// Pushed every time a <see cref="RuntimeProposal"/> changes status — user
    /// decision (Approved / Edited / Rejected) and daemon ack (Applied /
    /// Failed). Lets the chat panel morph the pending confirmation card into
    /// its terminal state without a refetch.
    /// </summary>
    Task RuntimeProposalUpdated(RuntimeProposalUpdatedPayload payload);

    /// <summary>
    /// Pushed when a runtime's daemon reports a disk-pressure transition
    /// (Phase D Card 3). One push per transition — the daemon emits only on
    /// level changes, not on every sample, so this is a rare event under
    /// normal operations. Drives the project dashboard's disk-warning banner.
    /// </summary>
    Task RuntimeDiskPressure(RuntimeDiskPressureNotification payload);

    /// <summary>
    /// Pushed when the daemon's <c>canUseTool</c> callback fires and a human
    /// needs to approve (or deny) a single tool invocation. Correlation by
    /// SDK <c>toolUseId</c> on the payload — the eventual
    /// <c>AgentHub.ResolvePermission</c> echoes the same id back so the
    /// daemon can match the decision to its in-flight callback. Drives the
    /// inline approval card the spec describes in <c>ChatCanvas</c>.
    /// </summary>
    Task PermissionRequested(PermissionRequestedPayload payload);

    /// <summary>
    /// Pushed when the daemon emits a structured runtime event (bootstrap
    /// stage, install / setup step, supervised service lifecycle, spec delta
    /// apply / fail — see <c>RuntimeEventTypes</c>) and the backend has
    /// successfully appended it to the event store. Frontends opt in to this
    /// channel by calling <c>AgentHub.SubscribeToRuntimeEvents(runtimeId)</c>
    /// on drawer mount; the broadcast lands in the
    /// <c>runtime-events:{runtimeId}</c> group. Drives the live Timeline tab.
    /// </summary>
    Task RuntimeEventReceived(RuntimeEventNotification payload);

    /// <summary>
    /// Pushed for every line the daemon tails out of a supervised service's
    /// log file. Drives the live "Logs" tab in the runtime drawer. Subscribers
    /// opt in by calling <c>AgentHub.SubscribeToServiceLogs(runtimeId, serviceName)</c>
    /// — the broadcast lands in the
    /// <c>service-logs:{runtimeId}:{serviceName}</c> group. Not persisted
    /// server-side; the daemon tails from disk on demand.
    /// </summary>
    Task ServiceLogLine(ServiceLogLineNotification payload);

    /// <summary>
    /// Pushed every time the daemon's <c>ServiceStatusPoller</c> samples a
    /// fresh supervisord process list (default once every 10s). Drives the
    /// live "Services" tab in the super-admin runtime drawer so transient
    /// states (FATAL / BACKOFF / STOPPED) the event-driven Timeline can't
    /// represent end-to-end are always visible. Subscribers are the same
    /// <c>runtime-events:{runtimeId}</c> group that already receives
    /// <see cref="RuntimeEventReceived"/>.
    /// </summary>
    Task LiveSupervisordSnapshotReceived(LiveSupervisordSnapshotNotification payload);

    /// <summary>
    /// runtime-observability-super-admin — pushed for every line the daemon
    /// tails out of <c>/var/log/supervisor/agent.out.log</c> +
    /// <c>agent.err.log</c>. Drives the super-admin runtime drawer's
    /// "Daemon Logs" tab. Subscribers opt in by calling
    /// <c>AgentHub.SubscribeToDaemonLogs(runtimeId)</c> — the broadcast lands
    /// in the <c>daemon-logs:{runtimeId}</c> group. Not persisted server-side;
    /// the daemon tails from disk on demand.
    /// </summary>
    Task DaemonLogLineReceived(DaemonLogLineNotification payload);

    /// <summary>
    /// Pushed whenever a project's preview port is hot-swapped through
    /// <c>PATCH /api/projects/{projectId}/preview-port</c>. Fans out to every
    /// <c>branch-{branchId}</c> group of the project AND the parent
    /// <c>workspace-{workspaceId}</c> group so settings dialogs / sidebars /
    /// runtime drawers can re-render the port live. The Cloudflare tunnels
    /// have already been re-pointed at the new port before this broadcast
    /// fires — receivers can trust the value is live at the edge.
    /// </summary>
    Task PreviewPortChanged(PreviewPortChangedNotification payload);

    /// <summary>
    /// chat-file-attachments — pushed to the <c>branch-{branchId}</c> group
    /// when an attachment's daemon-staging state changes. The composer chip
    /// flips between "uploading", "Ready" (the daemon staged the file onto the
    /// runtime FS), and "Failed" (with Retry/Remove). Fan-out scope is
    /// <c>branch-{branchId}</c> so sibling-branch tabs don't see each other's
    /// composer events; <see cref="AttachmentStateChangedPayload.BranchId"/>
    /// is also embedded on the payload so a client tracking multiple branches
    /// can correlate without re-deriving from the attachment row.
    /// </summary>
    Task AttachmentStateChanged(AttachmentStateChangedPayload payload);
}
