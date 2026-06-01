// GitModule — Card 7 of daemon-git-ops.
//
// Orchestrator on top of GitRunner (Card 5). Owns:
//
//   - Sequential dispatch: every public op enqueues on a `#chain` Promise so
//     two concurrent commits / pushes / merges never interleave a `git add`
//     with somebody else's `git commit`. Same trick HooksModule uses.
//
//   - Audit fan-out: each runner audit (`started` + `completed`) is forwarded
//     over SignalR to RuntimeHub.GitOperationStarted / GitOperationCompleted.
//     We pin the conversation/turn context onto a private field BEFORE
//     dispatching to the runner so the audit callback can stamp it on the
//     wire payload — same dance HookEventEmitter does for the equivalent
//     hook payloads.
//
//   - High-level UI signals: CommitMade after a successful commit,
//     GitPushFailed with a coarse failure reason after a push that didn't
//     succeed, MergeConflict after a merge that produced conflicts. These are
//     in addition to the per-op started/completed pair, never instead of.
//
//   - Hot-swappable auto-commit policy. The flag is read by callers (e.g. the
//     turn lifecycle) — GitModule only stores + exposes it, plus logs each
//     change so audit shows the policy timeline.
//
// What's deliberately NOT in scope:
//
//   - Push-retry queue: GitPushFailed is the signal Card 8's PushRetryJob
//     subscribes to. We don't keep our own retry state.
//
//   - Destructive-op gate (Reset, ForcePush, BranchDelete): Card 9 wraps the
//     module from outside. GitModule executes whatever it's asked to.
//
//   - SshKeyHandler: GitRunner already injects GIT_SSH_COMMAND; we layer no
//     extra ssh handling here.

import type { Logger } from 'pino'

import type { SignalRClient } from '../signalr/SignalRClient.js'
import type { TokenManager } from '../github/TokenManager.js'
import { encodeBasicAuth } from '../github/basicAuth.js'
import {
  getBranchChangedFiles,
  getBranchFileDiff,
  getCommitRange,
  getWorkingTreeChangedFiles,
  getWorkingTreeFileDiff,
  InvalidGitRefError,
  looksLikePlainBranchName,
  type ChangedFilesResult,
  type CommitInfo,
  type FileDiffResult,
} from './DiffQueries.js'
import type { GitRunner } from './GitRunner.js'
import type { GitAuditEvent, GitInvocation, GitOpType, GitResult } from './types.js'

/**
 * Light per-op context the caller threads through public methods so the
 * resulting GitOperationStarted payload can be matched back to a turn /
 * conversation. Both fields are optional — utility ops (e.g. background
 * fetches) carry neither.
 */
export interface TurnCtx {
  conversationId?: string
  turnId?: string
}

export interface CommitResult {
  ok: boolean
  /** Set when there was nothing staged or modified — caller should treat as a no-op success. */
  noChanges?: boolean
  commitSha?: string
  fileCount?: number
  branch?: string
  /** Set on failure. */
  exitCode?: number | null
  /** Set on failure (also on no-changes, but empty). */
  outputTail?: string
}

export interface PushResult {
  ok: boolean
  authError?: boolean
  conflict?: boolean
  outputTail?: string
  /**
   * Coarse failure bucket — same vocabulary as the {@link GitPushFailedWire}
   * payload's `reason` field. Populated on failure only, so callers (e.g. the
   * auto-commit trailer in `main.ts`) can surface the reason in chat history
   * without re-classifying the output tail. Undefined on success.
   */
  failureReason?: GitPushFailureReason
}

export interface MergeResult {
  ok: boolean
  conflict?: boolean
  /** Populated on conflict; parsed from `git merge` output. */
  files?: string[]
  outputTail?: string
}

export interface GitModuleOpts {
  runner: GitRunner
  signalr: SignalRClient
  logger: Logger
  /**
   * Working directory the repo lives in. Threaded into Phase-1 diff queries
   * (`getChangedFiles` / `getFileDiff`) which shell out to `git` directly
   * (bypassing the runner so they don't audit-spam reads). Production wires
   * the same `GIT_CWD = '/data/project/repo'` constant the runner gets.
   */
  cwd: string
  /** Initial auto-commit policy. Defaults to `false`. */
  autoCommit?: boolean
  /**
   * GitHub installation-token manager. When provided together with
   * `getRepoFullName` (and the latter resolves to a non-null value at push
   * time), `push()` mints a fresh token and passes
   *   `git -c http.extraHeader=Authorization: Basic <base64(x-access-token:<token>)>` push …
   * — same auth shape `CloningRepoStage` uses for the initial clone. Without
   * this, a daemon that auto-commits will land the commit locally but fail
   * the push with `fatal: could not read Username for 'https://github.com'`
   * because there's no persistent credential helper installed in the runtime.
   *
   * Optional so test wirings (and the destructive-op gate's raw-execute path)
   * can construct a GitModule without a token surface — those callers either
   * stub out the runner entirely or only run local-only ops.
   */
  tokenManager?: TokenManager
  /**
   * Resolves the `owner/repo` identifier the token manager keys on for the
   * project repo. Returns `null` when the runtime has no repo (the
   * AI-curated / empty-spec onboarding path) or before `BootstrapState` has
   * been populated. Called on every `push()` so a runtime that boots without
   * a repo and is later reconfigured (currently impossible, but cheap to
   * support) would Just Work.
   *
   * Optional alongside `tokenManager` for the same test-wiring reasons.
   */
  getRepoFullName?: () => string | null
  /**
   * Test seam. Reserved for future use (today GitRunner owns its own
   * id/clock injection).
   */
  randomUUID?: () => string
  /** Test seam. Reserved for future use; GitRunner stamps audit timestamps. */
  now?: () => Date
}

/**
 * Wire-payload shape for `GitOperationStarted`. Keys are camelCase — .NET's
 * default binder accepts camelCase against PascalCase records, matching the
 * convention HookEventEmitter uses.
 */
interface GitOperationStartedWire {
  operationId: string
  opType: GitOpType
  commandLine: string
  conversationId: string | null
  turnId: string | null
}

interface GitOperationCompletedWire {
  operationId: string
  exitCode: number
  durationMs: number
  outputTail: string
  outputHash: string
}

interface CommitMadeWire {
  operationId: string | null
  commitSha: string
  message: string
  fileCount: number
  branch: string
  conversationId: string | null
  turnId: string | null
}

export type GitPushFailureReason = 'Auth' | 'Network' | 'Conflict' | 'Unknown'

interface GitPushFailedWire {
  operationId: string | null
  reason: GitPushFailureReason
  lastOutputTail: string
  branch: string
  /**
   * Turn correlation hints — same shape as {@link GitPushSucceededWire} so the
   * server's CommitMade-trailer pipeline (P3.1 follow-up) can attribute the
   * failed push to the originating turn the same way it does for the success
   * path. `null` when the push happened outside an in-flight turn (e.g. from
   * `pushRetryJob`'s background sweep).
   */
  conversationId: string | null
  turnId: string | null
}

/**
 * Coarse bucket for what went wrong on a commit attempt. Surfaced to the
 * frontend so the UX can render a readable hint ("Missing git identity",
 * "Pre-commit hook rejected the change", etc.) without re-parsing the tail.
 */
type GitCommitFailureReason =
  | 'Identity'   // user.name / user.email not configured (exit 128)
  | 'Hook'       // a pre-commit / commit-msg hook rejected the commit
  | 'Lock'       // .git/index.lock contention from a concurrent git process
  | 'Timeout'    // runner killed the child (exitCode === null → wire -1)
  | 'Unknown'

interface CommitFailedWire {
  operationId: string | null
  reason: GitCommitFailureReason
  lastOutputTail: string
  /** Best-effort. Pulled from `currentBranch()` if we can resolve it; '' otherwise. */
  branch: string
  conversationId: string | null
  turnId: string | null
}

interface GitPushSucceededWire {
  operationId: string | null
  branch: string
  conversationId: string | null
  turnId: string | null
}

interface MergeConflictWire {
  operationId: string | null
  files: string[]
  summary: string
  sourceBranch: string
  targetBranch: string
}

/**
 * Output-tail patterns the classifier matches against for commit failures.
 * Order matters in #classifyCommitFailure — identity first because its
 * symptoms ("Please tell me who you are") are unambiguous; hook second because
 * its output usually contains the substring "hook"; lock last because it's
 * the most generic.
 */
const COMMIT_IDENTITY_PATTERNS: readonly string[] = [
  'Please tell me who you are',
  'empty ident name',
  'unable to auto-detect email address',
  'committer ident',
  'author identity unknown',
]

const COMMIT_HOOK_PATTERNS: readonly string[] = [
  'pre-commit hook',
  'commit-msg hook',
  'hook declined',
  'hook failed',
]

const COMMIT_LOCK_PATTERNS: readonly string[] = [
  'index.lock',
  'Unable to create',
]

const COMMIT_SHA_REGEX = /\[(?:[\w./-]+)\s+([0-9a-f]{7,40})\]/
const COMMIT_BRANCH_REGEX = /\[([\w./-]+)\s+[0-9a-f]{7,40}\]/
const FILES_CHANGED_REGEX = /(\d+)\s+files?\s+changed/
const MERGE_CONFLICT_FILE_REGEX = /CONFLICT\s+\([^)]+\):\s+Merge conflict in (.+)/g

const NETWORK_ERROR_PATTERNS: readonly string[] = [
  'Connection timed out',
  'Could not resolve host',
  'Network is unreachable',
]

const PUSH_CONFLICT_PATTERNS: readonly string[] = [
  'rejected',
  'non-fast-forward',
]

/**
 * Patterns that identify a non-fast-forward rejection — i.e. the remote moved
 * ahead and our local push was refused. This is the ONLY push failure mode we
 * try to reconcile automatically (fetch + rebase + retry). Other failures
 * (auth, network, unknown) bubble out unchanged.
 *
 * We require BOTH the `! [rejected]` line and one of the recognised "remote
 * is ahead" hints. Plain "rejected" alone is too loose — git also says
 * "rejected" for pre-receive hooks and forced-push protection, neither of
 * which a rebase would cure.
 */
const PUSH_REJECT_FETCH_FIRST_PATTERNS: readonly string[] = [
  'fetch first',
  'non-fast-forward',
]

export class GitModule {
  readonly #runner: GitRunner
  readonly #signalr: SignalRClient
  readonly #logger: Logger
  readonly #tokenManager: TokenManager | null
  readonly #getRepoFullName: (() => string | null) | null
  readonly #cwd: string

  #autoCommit: boolean

  /** Sequential queue: every public op tail-chains here so they can't interleave. */
  #chain: Promise<unknown> = Promise.resolve()

  /**
   * Context to stamp on the next runner audit. Set inside each public op
   * BEFORE calling `runner.run()`, cleared after. Read by the audit callback
   * passed at construction time. Same pattern as HookEventEmitter.
   */
  #currentCtx: TurnCtx | null = null

  /**
   * Captured executionId of the most recent runner invocation. Used by
   * `commit()` to stamp `CommitMade.operationId` and by `push()` /
   * `merge()` to stamp the matching failure-side payloads. Reset to null
   * before each runner call so a stale id can never leak into a follow-up
   * fan-out for an op that emitted no audit (which never happens today, but
   * the invariant is cheap to keep).
   */
  #lastExecutionId: string | null = null

  constructor(opts: GitModuleOpts) {
    this.#runner = opts.runner
    this.#signalr = opts.signalr
    this.#logger = opts.logger.child({ module: 'git' })
    this.#autoCommit = opts.autoCommit ?? false
    this.#tokenManager = opts.tokenManager ?? null
    this.#getRepoFullName = opts.getRepoFullName ?? null
    this.#cwd = opts.cwd
    // `opts.randomUUID` and `opts.now` are accepted for future use; GitRunner
    // currently owns id + clock seams.
    void opts.randomUUID
    void opts.now
  }

  // ============================================================================
  // Auto-commit policy
  // ============================================================================

  setAutoCommit(enabled: boolean): void {
    this.#autoCommit = enabled
    this.#logger.info({ enabled }, 'auto-commit policy updated')
  }

  isAutoCommit(): boolean {
    return this.#autoCommit
  }

  // ============================================================================
  // Audit handler (passed to GitRunner via constructor wiring at the call site)
  //
  // The runner's onAudit fires synchronously; we forward to SignalR best-effort.
  // Failures are logged at warn and swallowed — a transport blip must not
  // abort an in-flight git op.
  // ============================================================================

  /**
   * Forward a runner audit to the backend. Public so the runner can be
   * constructed with `onAudit: e => gitModule.handleRunnerAudit(e)` from the
   * wiring point (Card 10). Test code uses this seam too.
   */
  handleRunnerAudit(e: GitAuditEvent): void {
    if (e.kind === 'started') {
      this.#lastExecutionId = e.executionId
      const payload: GitOperationStartedWire = {
        operationId: e.executionId,
        opType: e.op,
        commandLine: e.commandLine,
        conversationId: this.#currentCtx?.conversationId ?? null,
        turnId: this.#currentCtx?.turnId ?? null,
      }
      this.#fireAndForget('GitOperationStarted', payload, {
        operationId: payload.operationId,
        opType: payload.opType,
      })
      return
    }

    // completed
    const payload: GitOperationCompletedWire = {
      operationId: e.executionId,
      // Server expects non-null int; runner returns null when the child was
      // killed (timeout/abort). Surface as -1 so the wire stays clean and the
      // server can disambiguate via `timedOut` / outputTail if needed. Same
      // contract HookEventEmitter uses for the equivalent HookCompleted field.
      exitCode: e.exitCode ?? -1,
      durationMs: e.durationMs ?? 0,
      outputTail: e.outputTail ?? '',
      outputHash: e.outputHash ?? '',
    }
    this.#fireAndForget('GitOperationCompleted', payload, {
      operationId: payload.operationId,
    })
  }

  // ============================================================================
  // Public ops — every method enqueues on `#chain` so two concurrent calls
  // serialise. The fast-path utility `currentBranch()` deliberately bypasses
  // the queue (it's a pure read used internally by other ops).
  // ============================================================================

  async commit(message: string, ctx: TurnCtx = {}): Promise<CommitResult> {
    return this.#enqueue(() => this.#commitImpl(message, ctx))
  }

  async push(remote = 'origin', branch?: string, ctx: TurnCtx = {}): Promise<PushResult> {
    return this.#enqueue(() => this.#pushImpl(remote, branch, ctx))
  }

  async fetch(remote = 'origin', ctx: TurnCtx = {}): Promise<GitResult> {
    return this.#enqueue(() => this.#runWithCtx(
      { op: 'Fetch', args: ['fetch', remote] },
      ctx,
    ))
  }

  async merge(branch: string, ctx: TurnCtx = {}): Promise<MergeResult> {
    return this.#enqueue(() => this.#mergeImpl(branch, ctx))
  }

  /**
   * Fast-forward-only pull for the daemon-git-sync-redesign spec (Scene 1 /
   * Scene 5). Runs `git fetch origin <branch>` followed by
   * `git merge --ff-only origin/<branch>` — explicit two-step rather than
   * `git pull --ff-only` so it mirrors the rest of GitModule's primitives
   * (which never spawn `pull` directly) and surfaces a clean exit code for
   * each leg.
   *
   * Uses the same HTTPS basic-auth path as {@link push} (GitHub App
   * installation token via {@link TokenManager}), with the same single 401
   * retry on auth failure. A non-zero exit on EITHER fetch or merge throws
   * a descriptive {@link Error} so the TurnRunner's catch path can serialise
   * the message into the `branch_divergent` eventData; a clean fetch followed
   * by a clean ff-merge resolves the promise.
   *
   * Throws (does not return a result object) because the spec calls for a
   * binary success/failure decision at the TurnRunner level — the runner
   * catches and emits TurnFailed. A "result object with .ok" would force the
   * caller to re-classify, which is what GitModule's other ops do because
   * THEY need to fan-out CommitMade / GitPushFailed / MergeConflict events
   * to the hub. `pullFastForward` doesn't fan-out — the audit-started /
   * audit-completed pair via `#runWithCtx` is the only signal we emit, and
   * those go automatically through the runner's onAudit callback.
   */
  async pullFastForward(branch: string, ctx: TurnCtx = {}): Promise<void> {
    return this.#enqueue(() => this.#pullFastForwardImpl(branch, ctx))
  }

  async createBranch(name: string, ctx: TurnCtx = {}): Promise<GitResult> {
    return this.#enqueue(() => this.#runWithCtx(
      { op: 'BranchCreate', args: ['checkout', '-b', name] },
      ctx,
    ))
  }

  /**
   * Thin passthrough — execute a raw `GitInvocation` through the same
   * sequential queue + audit forwarding the high-level ops use, without the
   * commit/push/merge wrapping. The DestructiveOpGate (Card 9) calls this to
   * run approved destructive ops; it's the only sanctioned way to execute an
   * arbitrary invocation while still serialising with concurrent commits and
   * pushes. Audit started/completed are emitted automatically via the
   * existing onAudit forwarding — no extra fan-out here.
   */
  async runRaw(
    invocation: GitInvocation,
    ctx: TurnCtx = {},
  ): Promise<{ ok: boolean; outputTail: string; exitCode: number | null }> {
    return this.#enqueue(async () => {
      const result = await this.#runWithCtx(invocation, ctx)
      return {
        ok: result.exitCode === 0,
        outputTail: result.outputTail,
        exitCode: result.exitCode,
      }
    })
  }

  // ============================================================================
  // Read-side diff queries (Phase 1 of diff-view-tab spec).
  //
  // Both methods route through the same `#enqueue` queue so a Changes-tab
  // click can't race an in-flight `commit()` / `merge()` and read a
  // half-staged tree. We deliberately DO NOT emit started/completed audits
  // for these — diff queries are pure reads (the user is just clicking
  // around a tab); auditing them would inflate the GitOperations table by
  // 10-100x with no value. See `DiffQueries.ts` header for the full
  // rationale.
  // ============================================================================

  async getChangedFiles(): Promise<ChangedFilesResult> {
    return this.#enqueue(() => getWorkingTreeChangedFiles(this.#cwd))
  }

  async getFileDiff(filePath: string): Promise<FileDiffResult> {
    return this.#enqueue(() => getWorkingTreeFileDiff(this.#cwd, filePath))
  }

  /**
   * Branch-scope changed-files query (Phase 3 of diff-view-tab). Mirrors
   * {@link getChangedFiles} but compares two arbitrary refs instead of HEAD
   * vs the working tree. Threads through the same queue so a branch-scope
   * read can't race auto-commit / rebase. Throws
   * {@link import('./DiffQueries.js').InvalidGitRefError} when a ref can't
   * be resolved — the controller surfaces that as a 400.
   *
   * On a {@link InvalidGitRefError} for a plain branch name, we attempt a
   * lazy `git fetch --depth=1 origin +refs/heads/<name>:refs/remotes/origin/<name>`
   * (with HTTPS-basic auth from {@link TokenManager}) and retry once. Runtime
   * clones are `--single-branch`, so the default UX of "compare against main"
   * would otherwise always 400 on a feature-branch runtime where `main`
   * exists only on the remote.
   */
  async getBranchChangedFiles(
    baseRef: string,
    headRef: string = 'HEAD',
  ): Promise<ChangedFilesResult> {
    return this.#enqueue(() =>
      this.#withLazyFetchRetry(baseRef, headRef, () =>
        getBranchChangedFiles(this.#cwd, baseRef, headRef),
      ),
    )
  }

  /**
   * Single-file unified diff for a `baseRef..headRef` range. Same body
   * caps / binary detection as {@link getFileDiff}; same ref-not-found
   * error surfacing as {@link getBranchChangedFiles}.
   */
  async getBranchFileDiff(
    baseRef: string,
    headRef: string,
    filePath: string,
  ): Promise<FileDiffResult> {
    return this.#enqueue(() =>
      this.#withLazyFetchRetry(baseRef, headRef, () =>
        getBranchFileDiff(this.#cwd, baseRef, headRef, filePath),
      ),
    )
  }

  /**
   * Newest-first list of commits in `baseRef..headRef`. Drives the
   * commit-picker UI on the frontend.
   */
  async getCommitRange(
    baseRef: string,
    headRef: string = 'HEAD',
    limit: number = 200,
  ): Promise<CommitInfo[]> {
    return this.#enqueue(() =>
      this.#withLazyFetchRetry(baseRef, headRef, () =>
        getCommitRange(this.#cwd, baseRef, headRef, limit),
      ),
    )
  }

  /**
   * Utility — `git rev-parse --abbrev-ref HEAD`. Bypasses the sequential
   * queue because it's a pure read used internally by other ops while the
   * queue slot is held. Throws on non-zero exit so callers can `try/catch`
   * the rare detached-HEAD / corrupt-repo cases without re-checking.
   */
  async currentBranch(): Promise<string> {
    const result = await this.#runner.run(
      { op: 'BranchList', args: ['rev-parse', '--abbrev-ref', 'HEAD'] },
      new AbortController().signal,
    )
    if (result.exitCode !== 0) {
      throw new Error(
        `git rev-parse --abbrev-ref HEAD failed (exit ${result.exitCode ?? 'null'}): ${result.outputTail}`,
      )
    }
    // Trim, then take the first line — defensive against trailing newlines or
    // unexpected stderr noise mixed in by the runner's interleaved tail.
    const firstLine = result.outputTail.split('\n')[0] ?? ''
    return firstLine.trim()
  }

  // ============================================================================
  // Implementation: each `#xImpl` runs INSIDE the queue slot. They call
  // `#runWithCtx` (private helper) which sets `#currentCtx` for the audit
  // callback and clears it after.
  // ============================================================================

  async #commitImpl(message: string, ctx: TurnCtx): Promise<CommitResult> {
    // 1. Status. If working tree is clean, short-circuit.
    const status = await this.#runWithCtx(
      { op: 'BranchList', args: ['status', '--porcelain'] },
      ctx,
    )
    if (status.exitCode !== 0) {
      const statusOpId = this.#lastExecutionId
      await this.#emitCommitFailed(statusOpId, status.exitCode, status.outputTail, ctx)
      return { ok: false, exitCode: status.exitCode, outputTail: status.outputTail }
    }
    if (status.outputTail.trim().length === 0) {
      return { ok: true, noChanges: true }
    }

    // 2. Stage everything.
    const add = await this.#runWithCtx(
      { op: 'Add', args: ['add', '-A'] },
      ctx,
    )
    if (add.exitCode !== 0) {
      const addOpId = this.#lastExecutionId
      await this.#emitCommitFailed(addOpId, add.exitCode, add.outputTail, ctx)
      return { ok: false, exitCode: add.exitCode, outputTail: add.outputTail }
    }

    // 3. Commit.
    const commit = await this.#runWithCtx(
      { op: 'Commit', args: ['commit', '-m', message] },
      ctx,
    )
    const commitOpId = this.#lastExecutionId
    if (commit.exitCode !== 0) {
      await this.#emitCommitFailed(commitOpId, commit.exitCode, commit.outputTail, ctx)
      return { ok: false, exitCode: commit.exitCode, outputTail: commit.outputTail }
    }

    // 4. Parse commit metadata. Best-effort — git's first-line summary is
    //    `[<branch> <sha>] <subject>`. If git's output ever changes shape we
    //    return undefined for the missing fields rather than failing the call.
    const shaMatch = commit.outputTail.match(COMMIT_SHA_REGEX)
    const branchMatch = commit.outputTail.match(COMMIT_BRANCH_REGEX)
    const filesMatch = commit.outputTail.match(FILES_CHANGED_REGEX)
    const commitSha = shaMatch?.[1]
    const branch = branchMatch?.[1]
    const fileCount = filesMatch?.[1] !== undefined ? Number.parseInt(filesMatch[1], 10) : 0

    // 5. Fan out CommitMade. Best-effort — failure to deliver this hint must
    //    not turn a successful commit into a reported failure.
    if (commitSha !== undefined && branch !== undefined) {
      const payload: CommitMadeWire = {
        operationId: commitOpId,
        commitSha,
        message,
        fileCount,
        branch,
        conversationId: ctx.conversationId ?? null,
        turnId: ctx.turnId ?? null,
      }
      this.#fireAndForget('CommitMade', payload, { commitSha, branch })
    }

    const result: CommitResult = { ok: true }
    if (commitSha !== undefined) result.commitSha = commitSha
    if (branch !== undefined) result.branch = branch
    result.fileCount = fileCount
    return result
  }

  async #pushImpl(
    remote: string,
    explicitBranch: string | undefined,
    ctx: TurnCtx,
  ): Promise<PushResult> {
    const branch = explicitBranch ?? (await this.currentBranch())

    // Resolve the project's owner/repo so we can mint a GitHub App
    // installation token for HTTPS auth. `null` here is the legitimate
    // "no repo" case (the AI-curated empty-spec onboarding path) — we
    // still attempt the push (it will fail loud with the same "could not
    // read Username" error today, but we don't want to silently swallow
    // it; and a future runtime that has its remote pre-configured for
    // some other reason should still go through this code path).
    const repoFullName = this.#getRepoFullName?.() ?? null

    // === Auth-aware push path ===
    //
    // CloningRepoStage's pattern: `git -c http.extraHeader=Authorization: Basic <base64(x-access-token:<token>)> …`.
    // GitHub's git HTTP backend speaks Basic auth ONLY (not Bearer); the
    // header value is `x-access-token:<installation-token>` base64-encoded.
    //
    // On 401 we invalidate the cached token and retry ONCE with a
    // force-refresh, mirroring CloningRepoStage.#gitWithAuthRetry. Any
    // further failure propagates as a normal `GitPushFailed`. We cap at one
    // retry so a hung-token / wrong-installation misconfig can't loop.
    let result = await this.#runPush(remote, branch, repoFullName, ctx, { forceRefresh: false })
    let pushOpId = this.#lastExecutionId

    if (
      result.exitCode !== 0 &&
      result.authError &&
      this.#tokenManager !== null &&
      repoFullName !== null
    ) {
      this.#logger.warn(
        { remote, branch, repoFullName },
        'push auth failure — invalidating token and retrying once',
      )
      this.#tokenManager.invalidate(repoFullName)
      result = await this.#runPush(remote, branch, repoFullName, ctx, { forceRefresh: true })
      pushOpId = this.#lastExecutionId
    }

    if (result.exitCode === 0) {
      // Positive ack so the UI can clear an "out-of-sync" banner. Same
      // best-effort fan-out shape as CommitMade.
      const successPayload: GitPushSucceededWire = {
        operationId: pushOpId,
        branch,
        conversationId: ctx.conversationId ?? null,
        turnId: ctx.turnId ?? null,
      }
      this.#fireAndForget('GitPushSucceeded', successPayload, { branch })
      return { ok: true }
    }

    // Classify reason from the runner's already-captured outputTail.
    let reason: GitPushFailureReason = 'Unknown'
    let conflict = false
    if (result.authError) {
      reason = 'Auth'
    } else if (NETWORK_ERROR_PATTERNS.some((p) => result.outputTail.includes(p))) {
      reason = 'Network'
    } else if (PUSH_CONFLICT_PATTERNS.some((p) => result.outputTail.includes(p))) {
      reason = 'Conflict'
      conflict = true
    }

    const payload: GitPushFailedWire = {
      operationId: pushOpId,
      reason,
      lastOutputTail: result.outputTail,
      branch,
      conversationId: ctx.conversationId ?? null,
      turnId: ctx.turnId ?? null,
    }
    this.#fireAndForget('GitPushFailed', payload, { reason, branch })

    const out: PushResult = { ok: false, outputTail: result.outputTail, failureReason: reason }
    if (result.authError) out.authError = true
    if (conflict) out.conflict = true
    return out
  }

  /**
   * Build the argv for a push and execute it. Handles the optional auth
   * prefix uniformly so the caller deals with one shape regardless of
   * whether tokens are wired. Token is fetched from the manager on each
   * call (cache-hit is cheap; near-expiry triggers a refresh inside
   * TokenManager); `forceRefresh:true` is only used by the 401 retry path.
   *
   * Args order matters for git: `-c <key>=<value>` flags go BEFORE the
   * subcommand. We construct the auth-prefixed argv here and never let it
   * leak into a public method signature.
   */
  async #runPush(
    remote: string,
    branch: string,
    repoFullName: string | null,
    ctx: TurnCtx,
    opts: { forceRefresh: boolean },
  ): Promise<GitResult> {
    let prefixArgs: string[] = []
    if (this.#tokenManager !== null && repoFullName !== null) {
      const token = await this.#tokenManager.getToken(
        repoFullName,
        opts.forceRefresh ? { forceRefresh: true } : undefined,
      )
      // The header value (base64(x-access-token:<token>)) is the
      // installation token in plaintext — GitRunner scrubs it from the
      // audit `commandLine` via redactAuthHeaders before fan-out, so the
      // token never touches the GitOperations table or the structured logs.
      prefixArgs = ['-c', `http.extraHeader=Authorization: Basic ${encodeBasicAuth(token)}`]
    }

    return await this.#runWithCtx(
      { op: 'Push', args: [...prefixArgs, 'push', remote, branch] },
      ctx,
    )
  }

  /**
   * Execute the `fetch + merge --ff-only` sequence for {@link pullFastForward}.
   * Mirrors {@link #pushImpl}'s auth strategy: build args with a fresh
   * installation token, retry ONCE on auth failure with a forced token
   * refresh. Either leg failing throws an Error with the captured outputTail
   * so the caller can include it in the wire payload.
   */
  async #pullFastForwardImpl(branch: string, ctx: TurnCtx): Promise<void> {
    const repoFullName = this.#getRepoFullName?.() ?? null

    // === Fetch leg ===
    let fetchResult = await this.#runFetchForFf(branch, repoFullName, ctx, {
      forceRefresh: false,
    })

    if (
      fetchResult.exitCode !== 0 &&
      fetchResult.authError &&
      this.#tokenManager !== null &&
      repoFullName !== null
    ) {
      this.#logger.warn(
        { branch, repoFullName },
        'pull-ff fetch auth failure — invalidating token and retrying once',
      )
      this.#tokenManager.invalidate(repoFullName)
      fetchResult = await this.#runFetchForFf(branch, repoFullName, ctx, {
        forceRefresh: true,
      })
    }

    if (fetchResult.exitCode !== 0) {
      throw new Error(
        `git fetch origin ${branch} failed (exit ${fetchResult.exitCode ?? 'null'}): ${fetchResult.outputTail}`,
      )
    }

    // === Merge leg (ff-only) ===
    //
    // `--ff-only` causes git to fail with non-zero exit if the histories
    // can't be fast-forwarded (i.e. divergent). This is the signal we want:
    // any non-zero here means the local branch has commits origin doesn't,
    // or the trees genuinely diverged. The TurnRunner translates that into
    // `branch_divergent`.
    const mergeResult = await this.#runWithCtx(
      { op: 'Merge', args: ['merge', '--ff-only', `origin/${branch}`] },
      ctx,
    )
    if (mergeResult.exitCode !== 0) {
      throw new Error(
        `git merge --ff-only origin/${branch} failed (exit ${mergeResult.exitCode ?? 'null'}): ${mergeResult.outputTail}`,
      )
    }
  }

  /**
   * Build the auth-prefixed argv for a pull-ff fetch and execute it via the
   * standard {@link #runWithCtx} path. Same shape `#runPush` uses for push
   * auth — kept separate so each op's argv stays explicit in the audit log.
   */
  async #runFetchForFf(
    branch: string,
    repoFullName: string | null,
    ctx: TurnCtx,
    opts: { forceRefresh: boolean },
  ): Promise<GitResult> {
    let prefixArgs: string[] = []
    if (this.#tokenManager !== null && repoFullName !== null) {
      const token = await this.#tokenManager.getToken(
        repoFullName,
        opts.forceRefresh ? { forceRefresh: true } : undefined,
      )
      prefixArgs = [
        '-c',
        `http.extraHeader=Authorization: Basic ${encodeBasicAuth(token)}`,
      ]
    }

    // CRITICAL: use an EXPLICIT refspec `+refs/heads/<branch>:refs/remotes/origin/<branch>`
    // instead of bare `fetch origin <branch>`. Runtimes are created via
    // `git clone --single-branch --branch=<initialBranch>`, which leaves the
    // remote's configured fetch refspec restricted to that single branch
    // (e.g. `+refs/heads/lab:refs/remotes/origin/lab`). The bare form would
    // then download the requested branch's commits but NOT update
    // `refs/remotes/origin/<branch>` — leaving the local cached tracking ref
    // stale. The downstream `git merge --ff-only origin/<branch>` would
    // silently merge against the stale ref and report "Already up to date"
    // even when origin had genuinely diverged. The leading `+` allows non-FF
    // updates to the local tracking ref (force-push tolerant), which is
    // exactly what divergence detection NEEDS — we want the local ref to
    // mirror the actual remote tip so the subsequent ff-only merge can see
    // the true divergence and refuse.
    const explicitRefspec = `+refs/heads/${branch}:refs/remotes/origin/${branch}`
    return await this.#runWithCtx(
      { op: 'Fetch', args: [...prefixArgs, 'fetch', 'origin', explicitRefspec] },
      ctx,
    )
  }

  async #mergeImpl(branch: string, ctx: TurnCtx): Promise<MergeResult> {
    let result = await this.#runWithCtx(
      { op: 'Merge', args: ['merge', '--no-ff', branch] },
      ctx,
    )

    // Partial-clone retry: on `clone --single-branch --branch=<feature>`
    // runtimes, common merge targets like `main` exist only as
    // `refs/remotes/origin/main`. `git merge main` then fails with
    // "pathspec did not match" or "not something we can merge". Detect
    // exactly that failure shape, lazy-fetch the branch from origin via
    // GitHub App auth (same path the diff handler uses), and retry once
    // against `origin/<branch>`. Anything that doesn't look like a missing
    // ref (e.g. a real conflict, working-tree dirty, plain invalid arg)
    // skips the retry and falls through to the normal conflict/error path.
    if (result.exitCode !== 0 && looksLikePlainBranchName(branch)) {
      if (this.#mergeFailedOnMissingRef(result.outputTail, branch)) {
        const refetched = await this.#tryLazyFetchForMerge(branch)
        if (refetched) {
          this.#logger.info(
            { branch },
            'merge target missing locally — lazy fetch landed, retrying as origin/<branch>',
          )
          result = await this.#runWithCtx(
            { op: 'Merge', args: ['merge', '--no-ff', `origin/${branch}`] },
            ctx,
          )
        }
      }
    }
    const mergeOpId = this.#lastExecutionId

    if (result.exitCode === 0) {
      return { ok: true }
    }

    const tail = result.outputTail
    const isConflict =
      tail.includes('CONFLICT') || tail.includes('Automatic merge failed')

    if (!isConflict) {
      return { ok: false, outputTail: tail }
    }

    // Parse conflicting files from the merge output. Best-effort — if the
    // regex misses (e.g. `--abort` previously left an exotic line), we
    // surface the empty list rather than failing.
    const files: string[] = []
    for (const m of tail.matchAll(MERGE_CONFLICT_FILE_REGEX)) {
      const captured = m[1]
      if (captured !== undefined) files.push(captured.trim())
    }

    // Resolve the target branch BEFORE aborting — once the abort completes
    // the working tree is back at the branch we started from, but we want
    // the name we were on when the merge was attempted.
    let targetBranch = ''
    try {
      targetBranch = await this.currentBranch()
    } catch {
      // currentBranch can throw on detached HEAD or transient issues;
      // surface an empty string rather than failing the whole merge call.
    }

    // Abort to leave the working tree clean. This is a separate audit emitted
    // as op=Merge with --abort args; treated as ordinary fan-out.
    await this.#runWithCtx(
      { op: 'Merge', args: ['merge', '--abort'] },
      ctx,
    )

    const payload: MergeConflictWire = {
      operationId: mergeOpId,
      files,
      summary: tail.slice(0, 500),
      sourceBranch: branch,
      targetBranch,
    }
    this.#fireAndForget('MergeConflict', payload, {
      sourceBranch: branch,
      fileCount: files.length,
    })

    return { ok: false, conflict: true, files, outputTail: tail }
  }

  // ============================================================================
  // Private helpers
  // ============================================================================

  /**
   * Tail-chain `task` onto `#chain`. The returned promise resolves with the
   * task's value but the chain never rejects (a thrown task does NOT poison
   * follow-ups) — we swallow rejections on the chain side and re-surface
   * them on the caller's promise.
   */
  #enqueue<T>(task: () => Promise<T>): Promise<T> {
    const next = this.#chain.then(task, task)
    // Swallow the rejection on the chain itself so a failed op doesn't
    // poison every subsequent op. The caller still sees the rejection via
    // the returned `next` promise.
    this.#chain = next.catch(() => undefined)
    return next
  }

  /**
   * Run a single git invocation with the given ctx pinned for audit
   * forwarding. Always clears `#currentCtx` on the way out, including on
   * thrown errors.
   */
  async #runWithCtx(invocation: GitInvocation, ctx: TurnCtx): Promise<GitResult> {
    this.#currentCtx = ctx
    this.#lastExecutionId = null
    try {
      return await this.#runner.run(invocation, new AbortController().signal)
    } finally {
      this.#currentCtx = null
    }
  }

  /**
   * Best-effort SignalR invoke. Same contract as HookEventEmitter — drop the
   * Promise, log on rejection. Never throws.
   */
  #fireAndForget(method: string, payload: unknown, logCtx: Record<string, unknown>): void {
    this.#signalr.invoke(method, payload).catch((err: unknown) => {
      this.#logger.warn({ err, method, ...logCtx }, 'git event emit failed')
    })
  }

  /**
   * Classify a non-zero `git` exit + its captured outputTail into a coarse
   * bucket the frontend can render readably. Best-effort — anything we don't
   * recognise falls through to `Unknown` and the raw tail is still on the
   * wire for operator drill-down.
   *
   * `exitCode === null` means the runner killed the child (timeout/abort),
   * which on our audit wire serialises as -1. Either way we surface `Timeout`.
   */
  #classifyCommitFailure(exitCode: number | null, tail: string): GitCommitFailureReason {
    if (exitCode === null || exitCode === -1) return 'Timeout'
    if (COMMIT_IDENTITY_PATTERNS.some((p) => tail.includes(p))) return 'Identity'
    if (COMMIT_HOOK_PATTERNS.some((p) => tail.includes(p))) return 'Hook'
    if (COMMIT_LOCK_PATTERNS.some((p) => tail.includes(p))) return 'Lock'
    return 'Unknown'
  }

  /**
   * Fan out a `CommitFailed` event. Branch is resolved best-effort via
   * `currentBranch()` — we may be on a detached HEAD or in a corrupt repo, in
   * which case the call throws and we send an empty string rather than fail
   * the whole emission. The actual commit failure has already happened by the
   * time we're called, so we never throw.
   *
   * Marked async only so callers can `await` it inside the queue slot before
   * returning — the underlying invoke is still fire-and-forget. Avoiding an
   * `await` here would let the caller's `return` race the branch lookup.
   */
  async #emitCommitFailed(
    operationId: string | null,
    exitCode: number | null,
    outputTail: string,
    ctx: TurnCtx,
  ): Promise<void> {
    const reason = this.#classifyCommitFailure(exitCode, outputTail)

    let branch = ''
    try {
      branch = await this.currentBranch()
    } catch {
      // detached HEAD / fresh clone / corrupt repo — surface empty string.
    }

    const payload: CommitFailedWire = {
      operationId,
      reason,
      lastOutputTail: outputTail,
      branch,
      conversationId: ctx.conversationId ?? null,
      turnId: ctx.turnId ?? null,
    }
    this.#fireAndForget('CommitFailed', payload, { reason, branch })
  }

  // ============================================================================
  // Lazy branch-ref fetch (runs from inside the diff-query path on missing-ref)
  //
  // Runtimes clone the repo with `--single-branch --depth=1 --branch=<branch>`
  // (see CloningRepoStage), so a feature-branch runtime literally does not
  // have `refs/remotes/origin/main` on disk — the default "Compare against
  // main" UX in the Changes tab would always 400.
  //
  // The fix: a depth=1 fetch of just the missing branch, written into
  // `refs/remotes/origin/<name>`. That's
  // enough for diff/log between two refs (numstat + name-status + log don't
  // need full history past the merge base, and a depth=1 main is enough for
  // "what changed on lab since main"). For older commits the user picks via
  // the commit-picker, those SHAs are enumerated from the LOCAL feature
  // branch so they always resolve without an extra fetch.
  //
  // Concurrency: the public methods (getBranchChangedFiles / …) call this via
  // `#enqueue`, so the fetch serialises against in-flight commits / pushes
  // just like everything else in this module.
  // ============================================================================

  /**
   * Run `task`. If it throws {@link InvalidGitRefError} for a plain branch
   * name, attempt a depth=1 fetch of that branch from origin and retry once.
   * Any error from the fetch itself is swallowed (logged at warn) so the
   * original "git ref not found" error surfaces unchanged — that's the right
   * UX for a genuinely-bad ref name (e.g. typo), since the fetch can't
   * conjure a non-existent branch into being.
   *
   * `headRef` is accepted only so the audit log can correlate the lazy fetch
   * with the originating query — we never lazy-fetch `headRef` since the
   * runtime is always checked out on the head branch (otherwise the daemon
   * couldn't be running there).
   */
  async #withLazyFetchRetry<T>(
    baseRef: string,
    headRef: string,
    task: () => Promise<T>,
  ): Promise<T> {
    try {
      return await task()
    } catch (err) {
      if (!(err instanceof InvalidGitRefError)) throw err
      const missingRef = err.ref
      if (!looksLikePlainBranchName(missingRef)) throw err
      // Only fetch if we have credentials. Without a token / repo we can't
      // talk to the remote — let the original error propagate.
      if (this.#tokenManager === null || this.#getRepoFullName === null) throw err
      const repoFullName = this.#getRepoFullName()
      if (repoFullName === null) throw err

      this.#logger.info(
        { missingRef, baseRef, headRef, repoFullName },
        'diff query missing branch ref — attempting lazy fetch',
      )
      const fetched = await this.#lazyFetchBranchRef(repoFullName, missingRef)
      if (!fetched) throw err

      this.#logger.info(
        { missingRef, baseRef, headRef },
        'diff query missing branch ref — lazy fetch landed, retrying query',
      )
      return await task()
    }
  }

  /**
   * Run `git fetch --depth=1 origin +refs/heads/<branch>:refs/remotes/origin/<branch>`
   * with HTTPS basic auth from {@link TokenManager}. On 401/403 (per
   * `GitRunner.authError`), invalidate the cached token and retry once. Both
   * attempts go through the runner so they show up in the GitOperations
   * audit table — a lazy fetch is a real network op, not a pure read.
   *
   * Returns `true` when the resulting `refs/remotes/origin/<branch>` is
   * resolvable locally afterwards. Returns `false` (with a warn log) if
   * either git attempt failed, leaving the caller to surface the original
   * "ref not found" error verbatim.
   */
  async #lazyFetchBranchRef(repoFullName: string, branch: string): Promise<boolean> {
    if (this.#tokenManager === null) return false

    const buildArgs = async (forceRefresh: boolean): Promise<string[]> => {
      const token = await this.#tokenManager!.getToken(
        repoFullName,
        forceRefresh ? { forceRefresh: true } : undefined,
      )
      return [
        '-c',
        `http.extraHeader=Authorization: Basic ${encodeBasicAuth(token)}`,
        'fetch',
        '--depth=1',
        '--no-tags',
        'origin',
        `+refs/heads/${branch}:refs/remotes/origin/${branch}`,
      ]
    }

    try {
      const first = await this.#runWithCtx(
        { op: 'Fetch', args: await buildArgs(false) },
        {},
      )
      if (first.exitCode === 0) return true
      if (first.authError) {
        this.#logger.warn(
          { branch, repoFullName },
          'lazy fetch auth failure — invalidating token and retrying once',
        )
        this.#tokenManager.invalidate(repoFullName)
        const retry = await this.#runWithCtx(
          { op: 'Fetch', args: await buildArgs(true) },
          {},
        )
        return retry.exitCode === 0
      }
      this.#logger.warn(
        { branch, repoFullName, exitCode: first.exitCode, outputTail: first.outputTail },
        'lazy fetch failed (non-auth)',
      )
      return false
    } catch (err) {
      this.#logger.warn({ err, branch, repoFullName }, 'lazy fetch threw')
      return false
    }
  }

  /**
   * True when `git merge`'s output matches the canonical "branch name didn't
   * resolve to any object" failure shape — i.e. the local repo doesn't have
   * the ref, so the merge was rejected before even attempting a merge.
   *
   * Conservative on purpose: we only retry the partial-clone lazy-fetch
   * path for outputs that scream "missing ref". Working-tree-dirty, true
   * merge conflicts, and other failure modes are left alone.
   */
  #mergeFailedOnMissingRef(outputTail: string, branch: string): boolean {
    return (
      outputTail.includes('did not match any file(s) known to git') ||
      outputTail.includes('not something we can merge') ||
      outputTail.includes('Not a valid object name') ||
      outputTail.includes(`merge: ${branch} - not something we can merge`)
    )
  }

  /**
   * Lazy-fetch wrapper for the merge retry path. Bails out (returns false)
   * if we don't have credentials or a repo identity — the caller then
   * surfaces the original merge failure unchanged. Logs at info on the
   * happy path so operators can trace partial-clone retries.
   */
  async #tryLazyFetchForMerge(branch: string): Promise<boolean> {
    if (this.#tokenManager === null || this.#getRepoFullName === null) return false
    const repoFullName = this.#getRepoFullName()
    if (repoFullName === null) return false

    this.#logger.info(
      { branch, repoFullName },
      'merge failed with missing-ref shape — attempting lazy fetch',
    )
    return await this.#lazyFetchBranchRef(repoFullName, branch)
  }
}
