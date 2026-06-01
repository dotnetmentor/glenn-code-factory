using Tapper;

namespace Source.Features.GitOps.Models;

/// <summary>
/// Why a git push failed, classified by the daemon. The set is deliberately
/// small — fine-grained taxonomy lives in the <c>OutputTail</c> for operators;
/// this enum is only what the UI needs to branch on (auth banner vs. retry vs.
/// "open a conflict resolution flow").
///
/// <para>Persisted as a string on the wire (<c>JsonStringEnumConverter</c>
/// without a naming policy → PascalCase tokens like <c>"Auth"</c>), so adding
/// a new variant is forward-compatible for UIs that switch on the string.</para>
/// </summary>
[TranspilationSource]
public enum GitPushFailureReason
{
    Auth = 0,
    Network = 1,
    Conflict = 2,
    Unknown = 3,
}

/// <summary>
/// Daemon-to-server: a git operation has begun. Inserted as a fresh
/// <see cref="GitOperation"/> row with the lifecycle / completion fields left
/// blank — the matching <see cref="GitOperationCompletedPayload"/> fills them
/// in once the process exits.
///
/// <para>Direction mirrors <c>HookStartedPayload</c>: lives under the
/// <c>GitOps</c> slice, transports over <see cref="SignalR.Hubs.RuntimeHub"/>.</para>
/// </summary>
[TranspilationSource]
public record GitOperationStartedPayload(
    Guid OperationId,
    GitOpType OpType,
    string CommandLine,
    Guid? ConversationId,
    Guid? TurnId);

/// <summary>
/// Daemon-to-server: the git process exited (any exit code; runner ran to
/// completion). Closes the matching <see cref="GitOperation"/> row; the row is
/// then immutable.
/// </summary>
[TranspilationSource]
public record GitOperationCompletedPayload(
    Guid OperationId,
    int ExitCode,
    int DurationMs,
    string OutputTail,
    string OutputHash);

/// <summary>
/// Daemon-to-server: a commit landed. High-level signal for the UI to refresh
/// the commit history strip — the underlying <see cref="GitOperation"/> row
/// (with <see cref="GitOpType.Commit"/>) is the actual audit. <i>Not persisted
/// as its own entity</i> — pure fan-out hint.
/// </summary>
[TranspilationSource]
public record CommitMadePayload(
    Guid? OperationId,
    string CommitSha,
    string Message,
    int FileCount,
    string Branch,
    Guid? ConversationId,
    Guid? TurnId);

/// <summary>
/// Daemon-to-server: a <c>git push</c> failed. Carries a coarse reason for the
/// UI to branch on plus a tail of the daemon's output for operators. Fan-out
/// only — the underlying <see cref="GitOperation"/> row already records the
/// failure exit code + full output tail.
/// </summary>
[TranspilationSource]
public record GitPushFailedPayload(
    Guid? OperationId,
    GitPushFailureReason Reason,
    string LastOutputTail,
    string Branch);

/// <summary>
/// Daemon-to-server: a <c>git push</c> succeeded. Positive ack so the UI can
/// clear an "out-of-sync with GitHub" banner once the working tree is back in
/// lockstep with the remote. Fan-out only — the matching
/// <see cref="GitOperation"/> row with <see cref="GitOpType.Push"/> and exit
/// code 0 is the durable record.
/// </summary>
[TranspilationSource]
public record GitPushSucceededPayload(
    Guid? OperationId,
    string Branch,
    Guid? ConversationId,
    Guid? TurnId);

/// <summary>
/// Why a <c>git commit</c> attempt failed, classified by the daemon. Same
/// small-set / forward-compatible philosophy as <see cref="GitPushFailureReason"/>
/// — fine-grained diagnosis lives in the <c>LastOutputTail</c> for operators;
/// this enum is only what the UI needs to branch on.
///
/// <list type="bullet">
///   <item><see cref="Identity"/>: <c>user.name</c> / <c>user.email</c> is not
///         configured. Surfaces the "Please tell me who you are" error from
///         git. The runtime image bakes a system-wide identity, so this is
///         the unrecoverable case if it ever fires in production.</item>
///   <item><see cref="Hook"/>: a <c>pre-commit</c> / <c>commit-msg</c> hook
///         rejected the change. Project-controlled — the user owns the fix.</item>
///   <item><see cref="Lock"/>: <c>.git/index.lock</c> contention from another
///         git process. Usually transient.</item>
///   <item><see cref="Timeout"/>: the runner killed the child process
///         (audit wire <c>exitCode == -1</c>). Network glitch, OOM, or a hook
///         that hung forever.</item>
///   <item><see cref="Unknown"/>: anything we don't recognise. The tail is
///         the operator's drill-down path.</item>
/// </list>
/// </summary>
[TranspilationSource]
public enum GitCommitFailureReason
{
    Identity = 0,
    Hook = 1,
    Lock = 2,
    Timeout = 3,
    Unknown = 4,
}

/// <summary>
/// Daemon-to-server: a <c>git commit</c> attempt failed. Carries a coarse reason
/// for the UI plus the captured output tail for operators. Fan-out only — the
/// underlying <see cref="GitOperation"/> row already records the failure exit
/// code + full output tail.
///
/// <para>Emitted alongside the matching <see cref="GitOperationCompletedPayload"/>
/// pair so the UI gets a typed signal ("out of sync with GitHub") without
/// having to re-parse audit rows.</para>
/// </summary>
[TranspilationSource]
public record CommitFailedPayload(
    Guid? OperationId,
    GitCommitFailureReason Reason,
    string LastOutputTail,
    string Branch,
    Guid? ConversationId,
    Guid? TurnId);

/// <summary>
/// Daemon-to-server: a merge produced conflicts. Fan-out only. The conflict
/// list is the daemon's parse of the <c>git merge</c> output; the source of
/// truth is still the working tree.
/// </summary>
[TranspilationSource]
public record MergeConflictPayload(
    Guid? OperationId,
    string[] Files,
    string Summary,
    string SourceBranch,
    string TargetBranch);

/// <summary>
/// Daemon-to-server <i>request</i>: the daemon wants to run a destructive git
/// op (reset, force-push, branch-delete) and is asking the server to allocate
/// an approval id and surface the request to the UI. The hub inserts a stub
/// <see cref="GitOperation"/> row tagged with the new approval id (so the
/// audit trail captures the intent regardless of whether the user approves)
/// and returns the id synchronously.
///
/// <para>The actual approve/reject decision flow ships in a follow-up card —
/// this card just mints the id and persists the intent.</para>
/// </summary>
[TranspilationSource]
public record RequestDestructiveGitOpPayload(
    GitOpType OpType,
    string Args,
    string Reason);

/// <summary>
/// Synchronous response to <see cref="RequestDestructiveGitOpPayload"/>. The
/// daemon stores <see cref="ApprovalId"/> and reports it on the eventual
/// <see cref="GitOperationStartedPayload"/> / <see cref="GitOperationCompletedPayload"/>
/// pair so the audit row can be matched back to the approval flow.
/// </summary>
[TranspilationSource]
public record RequestDestructiveGitOpResponse(
    Guid ApprovalId);

/// <summary>
/// Server-to-daemon: a user has asked the runtime to merge <see cref="SourceBranch"/>
/// into <see cref="TargetBranch"/>. The daemon executes the merge and emits the
/// resulting <see cref="GitOperationStartedPayload"/> / <see cref="GitOperationCompletedPayload"/>
/// pair (and, on conflicts, a <see cref="MergeConflictPayload"/>) so the UI can
/// follow along through the standard git-op fan-out.
///
/// <para><see cref="RequestedBy"/> is the authenticated user's id stamped at the
/// edge so the audit trail attributes the merge to a real principal even though
/// the actual <c>git merge</c> command runs as the runtime's daemon process.</para>
/// </summary>
[TranspilationSource]
public record MergeBranchPayload(
    string SourceBranch,
    string TargetBranch,
    string RequestedBy);
