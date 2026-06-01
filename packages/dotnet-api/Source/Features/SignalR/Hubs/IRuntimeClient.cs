using Source.Features.Diffs;
using Source.Features.GitOps.Models;
using Source.Features.RuntimeCuration.Models;
using Source.Features.SignalR.Contracts;
using TypedSignalR.Client;

namespace Source.Features.SignalR.Hubs;

/// <summary>
/// Strongly-typed receiver interface for daemons connected to <see cref="RuntimeHub"/>.
/// Every method here is invoked by the server on the daemon; the daemon registers
/// handlers for these names. The full payload shapes for these calls ship in
/// dependent specs (daemon-architecture, runtime-bootstrap) — this card only
/// fixes the method names so the typed-hub contract compiles.
/// </summary>
[Receiver]
public interface IRuntimeClient
{
    /// <summary>Server-to-daemon: begin a new agent turn for the supplied session.</summary>
    Task StartTurn(StartTurnPayload payload);

    /// <summary>Server-to-daemon: abort the in-flight turn.</summary>
    Task CancelTurn(CancelTurnPayload payload);

    /// <summary>
    /// Server-to-daemon: install an additive runtime-spec delta after a user
    /// approved (or edited) a <see cref="RuntimeProposal"/>. The daemon shells
    /// out to mise + supervisord idempotently and acks via
    /// <see cref="Hubs.RuntimeHub.RuntimeSpecDeltaApplied"/>; the proposal id
    /// in the payload is the correlation key. Additive only — entries already
    /// present in <c>ProjectRuntime.Spec</c> are stripped server-side.
    /// </summary>
    Task ApplyRuntimeSpecDelta(ApplyRuntimeSpecDeltaPayload payload);

    /// <summary>Server-to-daemon: hot-apply a config refresh (model, thresholds, flags).</summary>
    Task UpdateConfig(ConfigUpdatePayload payload);

    /// <summary>
    /// Server-to-daemon: a user (or admin) approved a destructive git operation
    /// previously requested by the daemon. The daemon looks up its in-memory
    /// approval record by <paramref name="opId"/> and proceeds with the
    /// reset / force-push / branch-delete that was held pending. Fan-out only;
    /// the audit row already exists from the daemon's
    /// <c>RequestDestructiveGitOp</c> roundtrip.
    /// </summary>
    Task ExecuteDestructiveGitOp(Guid opId);

    /// <summary>
    /// Server-to-daemon: a user has asked the runtime to merge a branch. The
    /// daemon runs the merge and reports back through the standard git-op
    /// fan-out (started/completed, plus merge-conflict on conflicts).
    /// </summary>
    Task MergeBranch(MergeBranchPayload payload);

    /// <summary>
    /// Server-to-daemon: restart a single managed service (Phase D Card 2).
    /// Triggered by the heartbeat handler's service-down detector when the
    /// runtime spec lists a service the daemon reports as not running.
    /// Throttled at most 3 per 5 minutes per <c>(runtimeId, serviceName)</c>
    /// — see <see cref="Contracts.RestartServicePayload"/> for the full
    /// trigger / throttle rationale.
    /// </summary>
    Task RestartService(RestartServicePayload payload);

    /// <summary>
    /// Server-to-daemon: wipe local bootstrap state and re-run the bootstrap
    /// flow against a freshly-fetched bundle. Pushed by the admin
    /// force-rebootstrap endpoint when an operator needs to recover a stuck
    /// or partially-bootstrapped runtime without a full Fly machine recreate.
    /// See <see cref="Contracts.ForceRebootstrapPayload"/> for the trigger and
    /// audit-trail rationale.
    /// </summary>
    Task ForceRebootstrap(ForceRebootstrapPayload payload);

    /// <summary>
    /// Server-to-daemon: the user has answered a previously-requested
    /// permission prompt. Correlated by the payload's <c>ToolUseId</c> — the
    /// daemon looks up its in-memory pending <c>canUseTool</c> waiter by that
    /// key and resolves it with the supplied decision (+ optional feedback for
    /// the "deny with feedback" branch). Fan-out is targeted at the single
    /// daemon connection currently bound to the project's active runtime; no
    /// broadcast.
    /// </summary>
    Task PermissionResolved(ResolvePermissionPayload payload);

    /// <summary>
    /// Server-to-daemon: a frontend opened the Logs tab and wants live tail of
    /// a supervised service's stdout/stderr. Pushed by
    /// <c>AgentHub.SubscribeToServiceLogs</c> after it has validated the
    /// service name against the runtime's current spec and added the caller's
    /// connection to the <c>service-logs:{runtimeId}:{serviceName}</c> group.
    ///
    /// <para>The daemon's <c>LogTailer</c> reference-counts subscriptions — a
    /// subsequent call for the same <c>serviceName</c> increments the count
    /// instead of spawning a second <c>tail -F</c>. The daemon emits log lines
    /// back via <c>RuntimeHub.ServiceLogLine</c>.</para>
    /// </summary>
    Task StartLogTail(string serviceName);

    /// <summary>
    /// Server-to-daemon: a frontend closed (or unmounted) the Logs tab. The
    /// daemon decrements its <c>LogTailer</c> ref-count for the service; when
    /// it reaches zero the tail process is SIGTERM'd. Symmetric counterpart to
    /// <see cref="StartLogTail"/>.
    /// </summary>
    Task StopLogTail(string serviceName);

    /// <summary>
    /// runtime-observability-super-admin — server→daemon: a super-admin
    /// opened the runtime drawer's Daemon Logs tab and wants live tail of the
    /// daemon's own stdout/stderr. Pushed by
    /// <c>AgentHub.SubscribeToDaemonLogs</c> after the super-admin gate
    /// passes. The daemon tails
    /// <c>/var/log/supervisor/agent.out.log</c> + <c>agent.err.log</c> via its
    /// <c>LogTailer</c> (with <c>initialLines: 200</c> so the subscriber sees
    /// recent context immediately) and emits lines back via
    /// <c>RuntimeHub.DaemonLogLine</c>. Reference-counted on the daemon side
    /// — subsequent calls just bump the count.
    /// </summary>
    Task StartDaemonLogTail();

    /// <summary>
    /// runtime-observability-super-admin — server→daemon: a super-admin
    /// closed (or unmounted) the runtime drawer's Daemon Logs tab. The
    /// daemon decrements its daemon-log <c>LogTailer</c> ref-count; on zero
    /// the underlying <c>tail -F</c> processes are SIGTERM'd. Symmetric
    /// counterpart to <see cref="StartDaemonLogTail"/>.
    /// </summary>
    Task StopDaemonLogTail();

    /// <summary>
    /// Server→daemon (request/response): list the files that differ in the
    /// requested scope. Phase 1 of the diff-view-tab spec only supports
    /// <c>workingTree</c>; branch / commit / range scopes (with <c>base</c> /
    /// <c>head</c> fields on the request) ship in Phase 3 alongside the picker
    /// UI. The daemon serialises this through <c>GitModule</c>'s queue so it
    /// can't race auto-commit / rebase work in flight, then shells out to
    /// <c>git status --porcelain=v2</c> + <c>git diff --numstat HEAD</c> for
    /// the working-tree case (see <c>DiffQueries.ts</c> on the daemon).
    ///
    /// <para>Caps live on the daemon side: 5000 files max in
    /// <see cref="ChangedFilesResponse.Files"/>; over the cap the array is
    /// truncated and <see cref="ChangedFilesResponse.Reason"/> set to
    /// <c>"too-many"</c>.</para>
    /// </summary>
    Task<ChangedFilesResponse> GetChangedFiles(string runtimeId, ChangedFilesRequest req);

    /// <summary>
    /// Server→daemon (request/response): unified-diff text for a single file
    /// in the requested scope. Tracked working-tree files use
    /// <c>git diff -U3 HEAD -- &lt;path&gt;</c>; untracked files fall back to
    /// <c>git diff -U3 --no-index /dev/null &lt;path&gt;</c>. Always run with
    /// <c>-c diff.renames=true -c core.quotepath=false</c> so we get rename
    /// detection and unicode paths verbatim.
    ///
    /// <para>Body capped at 500 KB; over the cap the head slice is returned
    /// with <see cref="FileDiffResponse.IsTruncated"/> true. Binary files
    /// return with no body, <see cref="FileDiffResponse.IsBinary"/> true,
    /// <see cref="FileDiffResponse.Reason"/> = <c>"binary"</c>.</para>
    /// </summary>
    Task<FileDiffResponse> GetFileDiff(string runtimeId, FileDiffRequest req);

    /// <summary>
    /// Server→daemon (request/response, Phase 3): branch-scope changed-files
    /// query. Compares <paramref name="baseRef"/>..<paramref name="headRef"/>
    /// — typically <c>main..HEAD</c> for the new default UX. The daemon
    /// validates both refs with <c>git rev-parse --verify</c> before running
    /// the diff and surfaces a typed error when either can't be resolved
    /// (the controller turns that into a 400). Same wire shape as
    /// <see cref="GetChangedFiles"/> so the React layer can swap between
    /// scopes without an adapter.
    /// </summary>
    Task<ChangedFilesResponse> GetBranchChangedFiles(string runtimeId, string baseRef, string headRef);

    /// <summary>
    /// Server→daemon (request/response, Phase 3): single-file unified diff
    /// between two refs. Same body-clipping / binary-detection rules as
    /// <see cref="GetFileDiff"/>; same ref-not-found surfacing as
    /// <see cref="GetBranchChangedFiles"/>.
    /// </summary>
    Task<FileDiffResponse> GetBranchFileDiff(string runtimeId, string baseRef, string headRef, string path);

    /// <summary>
    /// Server→daemon (request/response, Phase 3): newest-first list of
    /// commits in <paramref name="baseRef"/>..<paramref name="headRef"/>.
    /// Drives the commit-picker UI. <paramref name="limit"/> caps the
    /// returned row count (default 200 on the daemon; hard cap 1000).
    /// Throws when either ref is invalid.
    /// </summary>
    Task<CommitRangeResponse> GetCommitRange(string runtimeId, string baseRef, string headRef, int limit);

    /// <summary>
    /// chat-file-attachments — server-to-daemon: stage an uploaded attachment
    /// onto the runtime's local FS. Pushed once the browser finishes its
    /// direct-to-R2 upload and the backend stamps
    /// <c>Attachment.UploadedAt</c>. The daemon downloads via the included
    /// presigned GET URL, writes to <see cref="StageAttachmentPayload.LocalPath"/>,
    /// then acks back through <c>RuntimeHub.ReportAttachmentStaged</c>.
    ///
    /// <para>Fire-and-forget. If the runtime is offline the push is silently
    /// dropped — the spec calls out "Upload finishes but runtime is offline"
    /// as a v1-acceptable failure mode.</para>
    /// </summary>
    Task StageAttachment(StageAttachmentPayload payload);
}
