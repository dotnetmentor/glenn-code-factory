// DestructiveOpGate — Card 9 of daemon-git-ops.
//
// Wraps GitModule from outside to gate destructive operations (Reset,
// ForcePush, BranchDelete, hard cleans, force checkouts, …) behind an explicit
// user approval round-trip:
//
//   1. The orchestrator that wants to run a git invocation calls
//      `DestructiveOpGate.isDestructive(invocation)` BEFORE spawning. If true,
//      it must call `requestApproval(invocation, reason, ctx)` instead of
//      dispatching to the runner directly.
//
//   2. `requestApproval` invokes `RequestDestructiveGitOp` on the hub (Backend
//      Card 2) and parks the call on a pending Map keyed by the server-issued
//      `approvalId`. A 5-minute timer guarantees the promise eventually
//      settles even if the user walks away from the approve/reject UI.
//
//   3. When the user approves, the server calls `ExecuteDestructiveGitOp`
//      (Backend Card 3) on the daemon. The inbound handler (wired in Card 10)
//      forwards to `handleExecuteApproved(opId)`, which looks up the parked
//      entry, executes the invocation via `gitModule.runRaw`, and resolves the
//      original promise with the run result.
//
//   4. Server-initiated `MergeBranch` arrives via the same inbound channel
//      (`handleMergeBranch`). The user has already approved by clicking merge
//      in the UI, so we skip the gate and delegate straight to
//      `gitModule.merge`.
//
//   5. On expiry/shutdown the parked promise resolves with `ok:false` — the
//      orchestrator MUST treat this as an aborted op (no spawn happened).
//
// The gate never spawns git itself; everything goes through GitModule's
// sequential queue so destructive ops still serialise with concurrent
// commits/pushes/merges.
//
// === Why arg-pattern detection in addition to the GitOpType check ===
// The op enum says "this is what I think this invocation is", but the model
// can construct arbitrary argv via raw args (e.g. via a loosely-typed tool
// surface). Defence in depth: also pattern-match the args so a `Push` op with
// `--force` in its args goes through the gate even if the caller forgot to
// label it ForcePush.
//
// === AssistantText carrier ===
// Approval expiry + server-initiated merge failures don't have a dedicated
// hub method yet; we ride on the AssistantText carrier the same way
// PushRetryJob, BootstrapOrchestrator, and ShutdownCoordinator do. Same
// TODO(runtime-bootstrap) marker so a future grep finds every site.

import type { Logger } from 'pino'

import type { SignalRClient } from '../signalr/SignalRClient.js'
import type { EmitEventPayload } from '../signalr/types.js'
import { AgentEventKind } from '../signalr/types.js'
import type { GitInvocation } from './types.js'

const DEFAULT_EXPIRY_MS = 300_000 // 5 minutes

/**
 * Light per-op context the caller threads through. Mirrors GitModule.TurnCtx
 * verbatim — re-declared here to avoid an import cycle through GitModule.
 */
export interface DestructiveOpCtx {
  conversationId?: string
  turnId?: string
}

/**
 * Subset of GitModule the gate needs. Narrow on purpose so tests don't have
 * to fabricate a whole GitModule (which drags in GitRunner + SignalRClient).
 *
 *   - `runRaw` is the passthrough we added to GitModule for executing approved
 *     destructive ops via the same sequential queue.
 *   - `merge` is reused for server-initiated merges (the user already
 *     approved by clicking merge in the UI).
 */
export interface DestructiveOpGitModule {
  runRaw(
    invocation: GitInvocation,
    ctx?: DestructiveOpCtx,
  ): Promise<{ ok: boolean; outputTail: string; exitCode: number | null }>
  merge(branch: string, ctx?: DestructiveOpCtx): Promise<{ ok: boolean }>
}

export interface DestructiveOpGateOpts {
  gitModule: DestructiveOpGitModule
  signalr: Pick<SignalRClient, 'invoke' | 'emitEvent'>
  logger: Logger
  /** Test seam — Date.now() by default. */
  now?: () => number
  /** Test seam — global setTimeout by default. */
  setTimeout?: typeof setTimeout
  /** Test seam — global clearTimeout by default. */
  clearTimeout?: typeof clearTimeout
  /** Approval timeout. Default 5 minutes. */
  expiryMs?: number
}

export interface DestructiveOpResult {
  ok: boolean
  outputTail?: string
}

/**
 * Server-initiated merge payload. Mirrors `MergeBranchPayload` in
 * `RuntimeClientPayloads.cs` (Backend Card 3). Open shape so an unstamped
 * runtimeId field doesn't break parsing.
 */
export interface MergeBranchPayload {
  runtimeId?: string
  sourceBranch: string
  targetBranch: string
  requestedBy: string
}

interface PendingRequest {
  approvalId: string
  invocation: GitInvocation
  ctx: DestructiveOpCtx | undefined
  expiresAt: number
  expireTimer: ReturnType<typeof setTimeout>
  resolve: (result: DestructiveOpResult) => void
}

// ============================================================================
// isDestructive predicates — pure args inspection, defence in depth.
// ============================================================================

type ArgsPredicate = (args: readonly string[]) => boolean

const ARGS_PREDICATES: readonly ArgsPredicate[] = [
  // `git reset --hard` / `git reset --keep`
  (args) => {
    const i = args.indexOf('reset')
    if (i < 0) return false
    return args.slice(i + 1).some((a) => a === '--hard' || a === '--keep')
  },
  // `git push --force` / `-f` / `--force-with-lease`
  (args) => {
    const i = args.indexOf('push')
    if (i < 0) return false
    return args
      .slice(i + 1)
      .some((a) => a === '--force' || a === '-f' || a === '--force-with-lease')
  },
  // `git branch -D <name>`  OR  `git branch --delete --force …`
  (args) => {
    const i = args.indexOf('branch')
    if (i < 0) return false
    const tail = args.slice(i + 1)
    if (tail.includes('-D')) return true
    if (tail.includes('--delete') && tail.includes('--force')) return true
    return false
  },
  // `git clean -fd` (one combined flag) OR `git clean -f -d`
  (args) => {
    if (args[0] !== 'clean') return false
    if (args.includes('-fd') || args.includes('-df')) return true
    if (args.includes('-f') && args.includes('-d')) return true
    return false
  },
  // `git checkout … --force`
  (args) => {
    const i = args.indexOf('checkout')
    if (i < 0) return false
    return args.slice(i + 1).includes('--force')
  },
]

export class DestructiveOpGate {
  readonly #gitModule: DestructiveOpGitModule
  readonly #signalr: Pick<SignalRClient, 'invoke' | 'emitEvent'>
  readonly #logger: Logger
  readonly #now: () => number
  readonly #setTimeout: typeof setTimeout
  readonly #clearTimeout: typeof clearTimeout
  readonly #expiryMs: number

  /** Pending approvals keyed by server-issued approvalId. */
  readonly #pending: Map<string, PendingRequest> = new Map()

  constructor(opts: DestructiveOpGateOpts) {
    this.#gitModule = opts.gitModule
    this.#signalr = opts.signalr
    this.#logger = opts.logger.child({ module: 'destructive-op-gate' })
    this.#now = opts.now ?? (() => Date.now())
    this.#setTimeout = opts.setTimeout ?? setTimeout
    this.#clearTimeout = opts.clearTimeout ?? clearTimeout
    this.#expiryMs = opts.expiryMs ?? DEFAULT_EXPIRY_MS
  }

  // ============================================================================
  // Static detection — used by the orchestrator BEFORE dispatching to the
  // runner. Returning true means "you must call `requestApproval`, not
  // `runRaw`/`commit`/`push`/etc directly".
  // ============================================================================

  static isDestructive(invocation: GitInvocation): boolean {
    if (
      invocation.op === 'Reset' ||
      invocation.op === 'ForcePush' ||
      invocation.op === 'BranchDelete'
    ) {
      return true
    }
    for (const predicate of ARGS_PREDICATES) {
      if (predicate(invocation.args)) return true
    }
    return false
  }

  // ============================================================================
  // Public API — approval request + inbound handlers
  // ============================================================================

  /**
   * Ask the server for approval to run a destructive op. Resolves when
   * `handleExecuteApproved` runs the op (whether it succeeded or failed), or
   * when the 5min expiry fires.
   *
   * Never throws — even a SignalR disconnection resolves the returned promise
   * with `{ok:false, outputTail:'destructive op approval failed: <err>'}`.
   * That contract lets the caller treat "the user said no" and "we couldn't
   * even ask the user" identically: the spawn does not happen.
   */
  async requestApproval(
    invocation: GitInvocation,
    reason: string,
    ctx?: DestructiveOpCtx,
  ): Promise<DestructiveOpResult> {
    const opType = invocation.op
    const argsStr = invocation.args.join(' ')

    let approvalId: string
    try {
      const response = await this.#signalr.invoke<{ approvalId: string }>(
        'RequestDestructiveGitOp',
        { opType, args: argsStr, reason },
      )
      approvalId = response.approvalId
    } catch (err) {
      this.#logger.warn(
        { err, opType, args: argsStr },
        'destructive op approval invoke failed',
      )
      return {
        ok: false,
        outputTail: `destructive op approval failed: ${stringifyErr(err)}`,
      }
    }

    return new Promise<DestructiveOpResult>((resolve) => {
      const expireTimer = this.#setTimeout(() => {
        this.#expire(approvalId)
      }, this.#expiryMs)
      // unref so the timer doesn't keep the event loop alive on its own —
      // SignalR + signal handlers do that. Mirrors HeartbeatModule /
      // PushRetryJob.
      ;(expireTimer as { unref?: () => void }).unref?.()

      const entry: PendingRequest = {
        approvalId,
        invocation,
        ctx,
        expiresAt: this.#now() + this.#expiryMs,
        expireTimer,
        resolve,
      }
      this.#pending.set(approvalId, entry)
      this.#logger.info(
        { approvalId, opType, args: argsStr },
        'destructive op approval requested',
      )
    })
  }

  /**
   * Inbound: the server has approved a previously-requested op. Look up the
   * entry by approvalId, run the parked invocation via GitModule.runRaw, and
   * resolve the original `requestApproval` promise with the run result.
   *
   * Unknown approvalIds are logged at warn and silently dropped — that path
   * is reachable on a duplicate approval after expiry already fired, or on a
   * server bug. Either way crashing the daemon is the wrong move.
   */
  async handleExecuteApproved(opId: string): Promise<void> {
    const entry = this.#pending.get(opId)
    if (entry === undefined) {
      this.#logger.warn(
        { approvalId: opId },
        'execute-destructive-git-op for unknown approvalId; ignoring',
      )
      return
    }
    this.#pending.delete(opId)
    this.#clearTimeout(entry.expireTimer)

    this.#logger.info(
      { approvalId: opId, opType: entry.invocation.op },
      'destructive op approved; executing',
    )

    try {
      const result = await this.#gitModule.runRaw(entry.invocation, entry.ctx)
      const out: DestructiveOpResult = { ok: result.ok }
      if (result.outputTail !== undefined) out.outputTail = result.outputTail
      entry.resolve(out)
    } catch (err) {
      this.#logger.error(
        { err, approvalId: opId },
        'destructive op execution threw',
      )
      entry.resolve({ ok: false, outputTail: stringifyErr(err) })
    }
  }

  /**
   * Inbound: the server is asking us to merge `sourceBranch` into the current
   * branch (which it expects to be `targetBranch`). The user already approved
   * by clicking merge in the UI, so no extra gate.
   *
   * v1 simplification: trust the pre-state. The daemon doesn't switch
   * branches before merging — it just calls `gitModule.merge(sourceBranch)`.
   * If the daemon happens to be on a different branch, the merge will land
   * on the wrong target; treating that as a known limitation rather than
   * blocking the merge until we add a branch-switch primitive.
   *
   * TODO(runtime-bootstrap): cross-check `currentBranch() === targetBranch`
   * before merging and surface a typed error event when they diverge.
   */
  async handleMergeBranch(payload: MergeBranchPayload): Promise<void> {
    this.#logger.info(
      {
        sourceBranch: payload.sourceBranch,
        targetBranch: payload.targetBranch,
        requestedBy: payload.requestedBy,
      },
      'server-initiated merge',
    )

    try {
      await this.#gitModule.merge(payload.sourceBranch)
    } catch (err) {
      this.#logger.error(
        { err, sourceBranch: payload.sourceBranch, targetBranch: payload.targetBranch },
        'server-initiated merge failed',
      )
      this.#emitMergeFailedToUser(payload, err)
    }
  }

  /**
   * Tear down: cancel all pending approval timers and resolve every parked
   * promise with `ok:false`. Idempotent — a second call is a no-op.
   */
  shutdown(): void {
    for (const entry of this.#pending.values()) {
      this.#clearTimeout(entry.expireTimer)
      entry.resolve({ ok: false, outputTail: 'daemon shutting down' })
    }
    this.#pending.clear()
  }

  // ============================================================================
  // Private helpers
  // ============================================================================

  #expire(approvalId: string): void {
    const entry = this.#pending.get(approvalId)
    if (entry === undefined) return
    this.#pending.delete(approvalId)
    // Timer already fired, no need to clear it.

    const opLabel = entry.invocation.op
    const text = `Destructive operation '${opLabel}' was not approved within ${
      this.#expiryMs / 60_000
    } minutes; canceled.`

    this.#logger.warn(
      { approvalId, opType: opLabel, args: entry.invocation.args.join(' ') },
      'destructive op approval expired',
    )

    this.#emitAssistantText({
      type: 'destructive_op_expired',
      approvalId,
      opType: opLabel,
      args: entry.invocation.args.join(' '),
      text,
    })

    entry.resolve({ ok: false, outputTail: 'approval timeout' })
  }

  #emitMergeFailedToUser(payload: MergeBranchPayload, err: unknown): void {
    const text = `Merge of '${payload.sourceBranch}' into '${payload.targetBranch}' failed: ${stringifyErr(err)}`
    this.#emitAssistantText({
      type: 'server_merge_failed',
      sourceBranch: payload.sourceBranch,
      targetBranch: payload.targetBranch,
      requestedBy: payload.requestedBy,
      error: stringifyErr(err),
      text,
    })
  }

  /**
   * Best-effort runtime-scope event emit via the Status carrier shape. Same
   * pattern as PushRetryJob / BootstrapOrchestrator / ShutdownCoordinator —
   * sessionId='' (hub routes by runtimeId from the connection), the real
   * subtype embedded in eventData JSON. The audit row preserves the body;
   * the chat panel ignores Status frames with no runStatus column.
   *
   * TODO(runtime-bootstrap): replace once a real runtime-scope hub method
   * (`EmitRuntimeEvent`) ships.
   */
  #emitAssistantText(body: Record<string, unknown>): void {
    const payload: EmitEventPayload = {
      sessionId: '',
      kind: AgentEventKind.Status,
      eventData: JSON.stringify(body),
      emittedAt: new Date().toISOString(),
    }
    this.#signalr.emitEvent(payload).catch((err: unknown) => {
      this.#logger.error(
        { err },
        'failed to emit destructive-op carrier event',
      )
    })
  }
}

function stringifyErr(err: unknown): string {
  if (err instanceof Error) return err.message
  return String(err)
}
