// SelfHealCoordinator — Card 9 of the daemon-hooks-runner spec.
//
// Given a set of hook failures, build a templated feedback prompt and ask the
// .NET RuntimeHub to start a continuation turn. The hub answers with either
// `{ accepted: true, newTurnId }` or `{ accepted: false, rejectionReason }`.
// On acceptance we emit `HookSelfHealStarted` (relay-only — the server has
// already created the new turn) and let the caller decide what to do next.
//
// Design notes:
//
//   - Synchronous Promise. No EventEmitter. Caller (Card 10 wire-up) decides
//     how to react to the outcome.
//
//   - Two budget caps in series: this class checks `iteration < maxIterations`
//     before bothering the hub, and the server (backend Card 3) caps at 3
//     independently. Daemon-side check just avoids one redundant round trip
//     per dead turn.
//
//   - `maxedOut` from the server is informational here — backend Card 3 has
//     already emitted `HookSelfHealMaxedOut` to the project group, so the UI
//     sees it without our help. We do NOT re-emit.
//
//   - Prompt assembly is byte-budgeted. A pathological hook output (10 KB
//     stack trace) must not blow `maxPromptBytes` (default 8 KiB). We slice
//     per-failure and then belt-and-suspenders the whole prompt at the end.

import type { Logger } from 'pino'

import type { SignalRClient } from '../signalr/SignalRClient.js'
import type { HookEventEmitter } from './HookEventEmitter.js'
import type { HookSelfHealStartedPayload } from './types.js'

/**
 * Default daemon-side budget. Mirrors the server-side cap in backend Card 3.
 */
export const DEFAULT_MAX_ITERATIONS = 3

/**
 * Default upper bound on the assembled feedback prompt in UTF-8 bytes.
 */
export const DEFAULT_MAX_PROMPT_BYTES = 8192

export interface SelfHealCoordinatorOptions {
  signalr: SignalRClient
  emitter: HookEventEmitter
  runtimeId: string
  logger: Logger
  /** Default {@link DEFAULT_MAX_ITERATIONS} (3). Mirrors the server budget. */
  maxIterations?: number
  /** Default {@link DEFAULT_MAX_PROMPT_BYTES} (8192). */
  maxPromptBytes?: number
}

export interface RequestContinuationArgs {
  conversationId: string
  turnId: string
  agentId: string
  failures: { hookName: string; outputTail: string }[]
  /** Daemon-tracked count of self-heals already done for this turn. */
  iteration: number
}

export type SelfHealRejectionReason =
  | 'maxedOut'
  | 'turnNotRunning'
  | 'runtimeMismatch'
  | 'budgetExhausted'
  | 'invokeFailed'

export interface RequestContinuationResult {
  accepted: boolean
  newTurnId?: string
  rejectionReason?: SelfHealRejectionReason
}

/**
 * Wire shape mirroring `Source.Features.Hooks.Models.RequestSelfHealContinuationPayload`.
 * Field order is documentation only — JSON has no positional ordering.
 */
interface RequestSelfHealContinuationPayload {
  runtimeId: string
  conversationId: string
  turnId: string
  agentId: string
  hookName: string
  feedbackPrompt: string
  iteration: number
}

/**
 * Wire shape mirroring `Source.Features.Hooks.Models.RequestSelfHealContinuationResponse`.
 */
interface RequestSelfHealContinuationResponse {
  accepted: boolean
  newTurnId: string | null
  rejectionReason:
    | 'maxedOut'
    | 'turnNotRunning'
    | 'runtimeMismatch'
    | null
    | string
}

const PROMPT_BLOCK_SEPARATOR = '\n\n---\n\n'
const TRUNCATED_MARKER = '\n[truncated]\n'

export class SelfHealCoordinator {
  readonly #signalr: SignalRClient
  readonly #emitter: HookEventEmitter
  readonly #runtimeId: string
  readonly #logger: Logger
  readonly #maxIterations: number
  readonly #maxPromptBytes: number

  constructor(opts: SelfHealCoordinatorOptions) {
    this.#signalr = opts.signalr
    this.#emitter = opts.emitter
    this.#runtimeId = opts.runtimeId
    this.#logger = opts.logger.child({ module: 'self-heal-coordinator' })
    this.#maxIterations = opts.maxIterations ?? DEFAULT_MAX_ITERATIONS
    this.#maxPromptBytes = opts.maxPromptBytes ?? DEFAULT_MAX_PROMPT_BYTES
  }

  async requestContinuation(
    args: RequestContinuationArgs,
  ): Promise<RequestContinuationResult> {
    this.#logger.info(
      {
        conversationId: args.conversationId,
        turnId: args.turnId,
        iteration: args.iteration,
        failureCount: args.failures.length,
      },
      'self-heal continuation requested',
    )

    // Daemon-side budget pre-check. Server has the same cap, but skipping the
    // round trip for an obviously-dead turn is cheap.
    if (args.iteration >= this.#maxIterations) {
      this.#logger.info(
        { iteration: args.iteration, maxIterations: this.#maxIterations },
        'self-heal budget exhausted',
      )
      return { accepted: false, rejectionReason: 'budgetExhausted' }
    }

    const feedbackPrompt = this.#buildPrompt(args.failures)

    // First failure determines the hookName label; the prompt covers all.
    const firstHookName = args.failures[0]?.hookName ?? ''

    const payload: RequestSelfHealContinuationPayload = {
      runtimeId: this.#runtimeId,
      conversationId: args.conversationId,
      turnId: args.turnId,
      agentId: args.agentId,
      hookName: firstHookName,
      feedbackPrompt,
      iteration: args.iteration,
    }

    let response: RequestSelfHealContinuationResponse
    try {
      response = await this.#signalr.invoke<RequestSelfHealContinuationResponse>(
        'RequestSelfHealContinuation',
        payload,
      )
    } catch (err) {
      this.#logger.warn({ err }, 'self-heal continuation request failed')
      return { accepted: false, rejectionReason: 'invokeFailed' }
    }

    if (response.accepted) {
      const newTurnId = response.newTurnId ?? ''
      const nextIteration = args.iteration + 1
      const eventPayload: HookSelfHealStartedPayload = {
        runtimeId: this.#runtimeId,
        conversationId: args.conversationId,
        previousTurnId: args.turnId,
        newTurnId,
        iteration: nextIteration,
      }
      this.#emitter.emitSelfHealStarted(eventPayload)
      this.#logger.info(
        { newTurnId, iteration: nextIteration },
        'self-heal continuation accepted',
      )
      return { accepted: true, newTurnId }
    }

    const reason = response.rejectionReason
    if (reason === 'maxedOut') {
      // Server has already emitted HookSelfHealMaxedOut to the project group.
      // We don't re-emit; we just log + report back to the caller.
      this.#logger.info(
        { reason },
        'self-heal continuation rejected: server budget exhausted',
      )
      return { accepted: false, rejectionReason: 'maxedOut' }
    }
    if (reason === 'turnNotRunning') {
      this.#logger.info(
        { reason },
        'self-heal continuation rejected: turn not running',
      )
      return { accepted: false, rejectionReason: 'turnNotRunning' }
    }
    if (reason === 'runtimeMismatch') {
      this.#logger.warn(
        { reason },
        'self-heal continuation rejected: runtime mismatch (misconfiguration)',
      )
      return { accepted: false, rejectionReason: 'runtimeMismatch' }
    }
    this.#logger.warn(
      { reason },
      'self-heal continuation rejected with unknown reason',
    )
    return {
      accepted: false,
      rejectionReason: (reason ?? 'invokeFailed') as SelfHealRejectionReason,
    }
  }

  // ============================================================================
  // Prompt assembly
  // ============================================================================

  /**
   * Build the templated feedback prompt for a list of failures, byte-budgeted
   * to `#maxPromptBytes`. See class header for the policy.
   */
  #buildPrompt(failures: { hookName: string; outputTail: string }[]): string {
    if (failures.length === 0) {
      return ''
    }

    // Fast path: assemble untruncated, return if it fits.
    const fullBlocks = failures.map((f) =>
      renderBlock(f.hookName, f.outputTail, false),
    )
    const fullPrompt = fullBlocks.join(PROMPT_BLOCK_SEPARATOR)
    if (Buffer.byteLength(fullPrompt, 'utf8') <= this.#maxPromptBytes) {
      return fullPrompt
    }

    // Slow path: divide the byte budget evenly across failures, truncate each
    // block's `outputTail` to fit, then assemble. This is intentionally simple;
    // the per-block math can drift a bit due to quoting overhead, hence the
    // belt-and-suspenders final clamp below.
    const perFailureQuota = Math.floor(this.#maxPromptBytes / failures.length)
    const truncatedBlocks = failures.map((f) =>
      renderBlockWithBudget(f.hookName, f.outputTail, perFailureQuota),
    )
    const assembled = truncatedBlocks.join(PROMPT_BLOCK_SEPARATOR)

    if (Buffer.byteLength(assembled, 'utf8') <= this.#maxPromptBytes) {
      return assembled
    }

    // Final pass: assembled is still over (rare — quoting overhead drift).
    // Hard-cut the whole thing and append a marker.
    const cap = Math.max(0, this.#maxPromptBytes - 32)
    const buf = Buffer.from(assembled, 'utf8')
    let cut = Math.min(cap, buf.length)
    // Walk back if mid-codepoint.
    while (cut > 0 && (buf[cut] !== undefined && (buf[cut]! & 0xc0) === 0x80)) {
      cut--
    }
    return buf.subarray(0, cut).toString('utf8') + TRUNCATED_MARKER
  }
}

// ============================================================================
// Block rendering helpers (module-scoped, pure)
// ============================================================================

/**
 * Render a single failure block. If `truncated` is true, append the truncated
 * marker before the closing fence (within the fenced section) — callers using
 * the budget-aware path pass `truncated=true` once they've sliced the tail.
 */
function renderBlock(
  hookName: string,
  outputTail: string,
  truncated: boolean,
): string {
  const tailWithMarker = truncated ? `${outputTail}${TRUNCATED_MARKER}` : outputTail
  return [
    `After your changes, hook \`${hookName}\` failed. Fix this:`,
    '',
    '```',
    tailWithMarker,
    '```',
    '',
    'Do not address other issues.',
  ].join('\n')
}

/**
 * Render a block whose total byte length is ≤ `quotaBytes`. If the full block
 * already fits, return as-is. Otherwise, truncate `outputTail` from the FRONT
 * (preserving the END — most recent error context) and append `[truncated]`
 * before the closing fence.
 */
function renderBlockWithBudget(
  hookName: string,
  outputTail: string,
  quotaBytes: number,
): string {
  const full = renderBlock(hookName, outputTail, false)
  if (Buffer.byteLength(full, 'utf8') <= quotaBytes) {
    return full
  }

  // Frame overhead = bytes of the rendered block when outputTail is empty.
  const empty = renderBlock(hookName, '', true)
  const frameBytes = Buffer.byteLength(empty, 'utf8')
  const tailBudget = Math.max(0, quotaBytes - frameBytes)

  // Preserve the END of the tail — that's where the most relevant error sits.
  const buf = Buffer.from(outputTail, 'utf8')
  let start = Math.max(0, buf.length - tailBudget)
  // Walk forward to the next code-point boundary to avoid mid-codepoint cuts.
  while (
    start < buf.length &&
    buf[start] !== undefined &&
    (buf[start]! & 0xc0) === 0x80
  ) {
    start++
  }
  const truncatedTail = buf.subarray(start).toString('utf8')

  return renderBlock(hookName, truncatedTail, true)
}
