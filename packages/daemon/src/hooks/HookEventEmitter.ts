// HookEventEmitter — Card 8 of the daemon-hooks-runner spec.
//
// A thin one-direction adapter. Events flow IN as `HookLifecycleEvent`s from
// HooksModule (Card 6) plus self-heal events from SelfHealCoordinator (Card 9);
// they flow OUT as typed SignalR `invoke` calls against the .NET RuntimeHub.
//
// Design notes:
//
//   - Pure adapter, no state beyond the constructor inputs. No EventEmitter on
//     this class — the contract is `emitLifecycle()` in, `signalr.invoke()` out.
//
//   - Best-effort delivery. A failed `invoke` (disconnected hub, server error,
//     unknown method) is swallowed and logged at `warn`. We never throw out of
//     `emitLifecycle` because the caller is HooksModule's run loop, and a
//     transport blip must not abort a hook run.
//
//   - Fire-and-forget. `emitLifecycle()` is synchronous (returns void). The
//     underlying `invoke` Promise is dropped on the floor with a `.catch()`
//     to satisfy the warn-on-failure contract. This decouples hub latency
//     from hook progress.
//
//   - Output-tail truncation lives here, not in HooksModule. The .NET column
//     `OutputTail` is `nvarchar(16384)` (per backend Card 1); sending more is
//     a server-side error. We clamp by *byte length* (utf8) since one
//     pathological multi-byte char could otherwise blow the budget.
//
//   - Enum mapping: `HooksModule` uses camelCase string discriminants
//     ('beforePrompt', 'on-failure', …). The wire wants the .NET enum member
//     names verbatim ('BeforePrompt', 'OnFailure', …) because the server
//     registers `JsonStringEnumConverter()` with no naming policy. See
//     types.ts for the full evidence trail.

import type { Logger } from 'pino'

import type { SignalRClient } from '../signalr/SignalRClient.js'
import type { HookSpec } from './HookExecutor.js'
import type { HookLifecycleEvent, HookPoint } from './HooksModule.js'
import type {
  HookCompletedPayload,
  HookConfigErrorPayload,
  HookFeedbackModeWire,
  HookPointWire,
  HookProgressPayload,
  HookSelfHealMaxedOutPayload,
  HookSelfHealStartedPayload,
  HookStartedPayload,
} from './types.js'

/**
 * Default cap on `outputTail` byte length before truncation. Matches the
 * .NET column `OutputTail` which is `nvarchar(16384)` — sending more than
 * 16 KiB would be a server-side error.
 */
export const DEFAULT_MAX_TAIL_BYTES = 16 * 1024

const HOOK_POINT_TO_WIRE: Record<HookPoint, HookPointWire> = {
  beforePrompt: 'BeforePrompt',
  afterPrompt: 'AfterPrompt',
  onFileChange: 'OnFileChange',
  beforeCommit: 'BeforeCommit',
}

const HOOK_FEEDBACK_MODE_TO_WIRE: Record<
  HookSpec['feedbackMode'],
  HookFeedbackModeWire
> = {
  'on-failure': 'OnFailure',
  always: 'Always',
  silent: 'Silent',
}

export interface HookEventEmitterOptions {
  signalr: SignalRClient
  runtimeId: string
  logger: Logger
  /** Override the per-payload tail clamp. Default is {@link DEFAULT_MAX_TAIL_BYTES} (16 KiB). */
  maxTailBytes?: number
}

export interface HookLifecycleCtx {
  /** Conversation id to stamp on `started` payloads. Optional. */
  conversationId?: string
  /** Turn id to stamp on `started` payloads. Optional. */
  turnId?: string
}

export class HookEventEmitter {
  readonly #signalr: SignalRClient
  readonly #runtimeId: string
  readonly #logger: Logger
  readonly #maxTailBytes: number

  constructor(opts: HookEventEmitterOptions) {
    this.#signalr = opts.signalr
    this.#runtimeId = opts.runtimeId
    this.#logger = opts.logger.child({ module: 'hook-event-emitter' })
    this.#maxTailBytes = opts.maxTailBytes ?? DEFAULT_MAX_TAIL_BYTES
  }

  /**
   * Bridge from a HooksModule lifecycle event to the matching hub method.
   * Best-effort: a transport failure is logged at warn and swallowed. Returns
   * synchronously — the underlying `invoke` is fire-and-forget.
   */
  emitLifecycle(event: HookLifecycleEvent, ctx: HookLifecycleCtx = {}): void {
    switch (event.type) {
      case 'started': {
        const payload: HookStartedPayload = {
          executionId: event.executionId,
          runtimeId: this.#runtimeId,
          conversationId: ctx.conversationId ?? null,
          turnId: ctx.turnId ?? null,
          hookPoint: HOOK_POINT_TO_WIRE[event.point],
          hookName: event.spec.name,
          cmd: event.spec.cmd,
          feedbackMode: HOOK_FEEDBACK_MODE_TO_WIRE[event.spec.feedbackMode],
          startedAt: event.startedAt.toISOString(),
        }
        this.#fireAndForget('HookStarted', payload, { executionId: payload.executionId })
        return
      }

      case 'progress': {
        const payload: HookProgressPayload = {
          executionId: event.executionId,
          runtimeId: this.#runtimeId,
          stdoutLine: event.line,
          lineIndex: event.lineIndex,
        }
        this.#fireAndForget('HookProgress', payload, {
          executionId: payload.executionId,
          lineIndex: payload.lineIndex,
        })
        return
      }

      case 'completed': {
        const payload: HookCompletedPayload = {
          executionId: event.executionId,
          runtimeId: this.#runtimeId,
          // The .NET payload field is non-nullable `int`. The result's exitCode
          // is `number | null` (null when killed mid-run). Surface as -1 so the
          // server still has a clean numeric value; the `timedOut` flag and the
          // tail are sufficient for the UI to disambiguate.
          exitCode: event.result.exitCode ?? -1,
          durationMs: event.result.durationMs,
          outputTail: this.#clampTail(event.result.outputTail),
          outputHash: event.result.outputHash,
          timedOut: event.result.timedOut,
          endedAt: event.endedAt.toISOString(),
        }
        this.#fireAndForget('HookCompleted', payload, { executionId: payload.executionId })
        return
      }

      case 'configError': {
        const payload: HookConfigErrorPayload = {
          executionId: event.executionId,
          runtimeId: this.#runtimeId,
          reason: event.reason,
          outputTail: this.#clampTail(event.outputTail),
          endedAt: event.endedAt.toISOString(),
        }
        this.#fireAndForget('HookConfigError', payload, { executionId: payload.executionId })
        return
      }
    }
  }

  /**
   * Self-heal lifecycle is emitted by SelfHealCoordinator (Card 9), not
   * HooksModule, so it gets its own entry points. Same best-effort semantics.
   */
  emitSelfHealStarted(payload: HookSelfHealStartedPayload): void {
    this.#fireAndForget('HookSelfHealStarted', payload, {
      conversationId: payload.conversationId,
      newTurnId: payload.newTurnId,
    })
  }

  emitSelfHealMaxedOut(payload: HookSelfHealMaxedOutPayload): void {
    this.#fireAndForget('HookSelfHealMaxedOut', payload, {
      conversationId: payload.conversationId,
      turnId: payload.turnId,
    })
  }

  // ============================================================================
  // Private helpers
  // ============================================================================

  /**
   * Clamp a string to `#maxTailBytes` UTF-8 bytes. If under cap, return as-is.
   * If over, slice and append a `\n[truncated N bytes]\n` marker where N is
   * the count of dropped source bytes. The returned string is guaranteed to
   * be ≤ `#maxTailBytes` bytes.
   */
  #clampTail(tail: string): string {
    const sourceBytes = Buffer.byteLength(tail, 'utf8')
    if (sourceBytes <= this.#maxTailBytes) {
      return tail
    }

    // Build the marker first so we know how many bytes we have left for content.
    // The marker uses the byte count of dropped *source* bytes, which we can
    // compute exactly from the source length minus the kept portion.
    //
    // Two-pass byte-aware truncation: slice by bytes (not chars), then decode.
    // A naive `s.slice(0, n)` would slice by code units and could overshoot
    // the byte budget for multi-byte chars.
    const sourceBuf = Buffer.from(tail, 'utf8')

    // Reserve enough space for the worst-case marker. We don't know N until
    // we know the kept-prefix length, but the marker length is bounded by
    // `\n[truncated <digits> bytes]\n` ≤ 32 chars (digits ≤ ~10 for any int).
    // Compute the actual marker once we know N.
    const computeMarker = (droppedBytes: number): string =>
      `\n[truncated ${droppedBytes} bytes]\n`

    // Upper bound on marker byte length (digits = 20 covers up to 2^64).
    const maxMarkerBytes = Buffer.byteLength(computeMarker(Number.MAX_SAFE_INTEGER), 'utf8')
    const keepBytes = Math.max(0, this.#maxTailBytes - maxMarkerBytes)

    // Slice on a UTF-8 boundary. Buffer.subarray is byte-indexed; decoding may
    // drop a trailing partial char (replaced by a replacement char). Walk back
    // a few bytes if we landed mid-codepoint to keep the output clean. This is
    // belt-and-braces — for ASCII (the overwhelming majority of hook output)
    // it's a no-op.
    let cut = keepBytes
    while (cut > 0 && (sourceBuf[cut] !== undefined && (sourceBuf[cut]! & 0xc0) === 0x80)) {
      cut--
    }

    const kept = sourceBuf.subarray(0, cut).toString('utf8')
    const droppedBytes = sourceBytes - Buffer.byteLength(kept, 'utf8')
    const marker = computeMarker(droppedBytes)
    const result = kept + marker

    // Final sanity clamp: if the actual marker came in shorter than our
    // worst-case reservation (it usually will — small N → short digits), we
    // could repack to use the freed budget. We deliberately don't — the
    // simpler invariant ("≤ maxTailBytes, marker is exact") is more valuable
    // than squeezing a few hundred extra source bytes through.
    return result
  }

  /**
   * Invoke a hub method best-effort: drop the Promise, log on rejection.
   * `logCtx` is merged into the warn log so test assertions can pin down which
   * call failed without parsing the payload.
   */
  #fireAndForget(method: string, payload: unknown, logCtx: Record<string, unknown>): void {
    this.#signalr.invoke(method, payload).catch((err: unknown) => {
      this.#logger.warn(
        { err, method, ...logCtx },
        'hook event emit failed',
      )
    })
  }
}
