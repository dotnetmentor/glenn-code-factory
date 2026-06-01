// ConnectingStage — first stage of the daemon bootstrap state machine.
//
// Wraps the SignalR-handshake wait so progress streams uniformly through the
// orchestrator's per-stage event channel. main.ts already calls
// `signalr.start()` before the orchestrator runs (so the connection is being
// dialled when this stage executes); this stage's job is to confirm we reached
// `Connected` before proceeding.
//
// Recoverable: if the handshake hasn't completed within `timeoutMs` we return
// a recoverable failure so the orchestrator's retry loop kicks in. The
// underlying `SignalRClient` has its own indefinite reconnect policy too, so a
// transient network blip will resolve on its own — we just need to sit on the
// stage until the wire is up.

import type {
  BootstrapStage,
  BootstrapContext,
  BootstrapStageResult,
} from '../BootstrapOrchestrator.js'
import type { SignalRClient } from '../../signalr/SignalRClient.js'

const DEFAULT_TIMEOUT_MS = 30_000
const POLL_INTERVAL_MS = 250

export interface ConnectingStageDeps {
  signalr: Pick<SignalRClient, 'isConnected'>
  /** Override timeout for tests (default 30s — matches spec). */
  timeoutMs?: number
  /** Override poll cadence for tests (default 250ms). */
  pollIntervalMs?: number
}

export class ConnectingStage implements BootstrapStage {
  readonly name = 'connecting'

  readonly #signalr: Pick<SignalRClient, 'isConnected'>
  readonly #timeoutMs: number
  readonly #pollIntervalMs: number

  constructor(deps: ConnectingStageDeps) {
    this.#signalr = deps.signalr
    this.#timeoutMs = deps.timeoutMs ?? DEFAULT_TIMEOUT_MS
    this.#pollIntervalMs = deps.pollIntervalMs ?? POLL_INTERVAL_MS
  }

  async run(ctx: BootstrapContext): Promise<BootstrapStageResult> {
    if (ctx.signal.aborted) {
      return { ok: false, reason: 'aborted', recoverable: true }
    }

    // Fast path — SignalR already connected by the time the stage runs.
    if (this.#signalr.isConnected()) {
      void emitProgress(ctx, this.name, 'completed')
      return { ok: true }
    }

    void emitProgress(ctx, this.name, 'started', 'waiting for signalr handshake')

    const deadline = Date.now() + this.#timeoutMs
    while (Date.now() < deadline) {
      if (ctx.signal.aborted) {
        return { ok: false, reason: 'aborted', recoverable: true }
      }
      if (this.#signalr.isConnected()) {
        void emitProgress(ctx, this.name, 'completed')
        return { ok: true }
      }
      await sleepWithAbort(this.#pollIntervalMs, ctx.signal)
    }

    void emitProgress(ctx, this.name, 'failed', 'timeout waiting for signalr')
    return {
      ok: false,
      reason: `signalr did not reach Connected within ${this.#timeoutMs}ms`,
      recoverable: true,
    }
  }
}

async function emitProgress(
  ctx: BootstrapContext,
  stage: string,
  status: 'started' | 'progress' | 'completed' | 'failed' | 'skipped',
  detail?: string,
): Promise<void> {
  try {
    await ctx.signalr.reportBootstrapProgress(
      detail !== undefined ? { stage, status, detail } : { stage, status },
    )
  } catch (err) {
    // Best-effort — never fail bootstrap for a side-channel emit.
    ctx.logger.debug({ err, stage, status }, 'reportBootstrapProgress failed')
  }
}

function sleepWithAbort(ms: number, signal: AbortSignal): Promise<void> {
  return new Promise<void>((resolve) => {
    if (signal.aborted) {
      resolve()
      return
    }
    const onAbort = () => {
      clearTimeout(timer)
      resolve()
    }
    const timer = setTimeout(() => {
      signal.removeEventListener('abort', onAbort)
      resolve()
    }, ms)
    signal.addEventListener('abort', onAbort, { once: true })
  })
}
