import type { Logger } from 'pino'
import { DaemonConfig } from '../config/DaemonConfig.js'
import { SignalRClient } from '../signalr/SignalRClient.js'
import type { HeartbeatPayload } from '../signalr/types.js'

/**
 * Reports liveness to main API every `config.heartbeatIntervalMs`.
 *
 * The module is intentionally dumb: it knows how to tick and how to send. It does
 * NOT know about TurnRunner, DiskMonitor, or QuietMode. Those modules are surfaced
 * through the `gather` callback that the composition root supplies — this keeps
 * concerns separated and lets each contributor module evolve without touching
 * Heartbeat.
 */
export type GatherHeartbeat = () => HeartbeatPayload

export class HeartbeatModule {
  readonly #signalr: SignalRClient
  readonly #config: DaemonConfig
  readonly #gather: GatherHeartbeat
  readonly #logger: Logger
  #timer: NodeJS.Timeout | null = null

  constructor(deps: {
    signalr: SignalRClient
    config: DaemonConfig
    gather: GatherHeartbeat
    logger: Logger
  }) {
    this.#signalr = deps.signalr
    this.#config = deps.config
    this.#gather = deps.gather
    this.#logger = deps.logger.child({ module: 'heartbeat' })
  }

  start(): void {
    if (this.#timer) return // idempotent
    // Fire once immediately so main API doesn't have to wait `intervalMs` for
    // first signal of life from a freshly-booted daemon.
    void this.#tick()
    this.#timer = setInterval(() => void this.#tick(), this.#config.heartbeatIntervalMs)
    // unref so the timer doesn't keep the event loop alive on its own — the
    // SignalR connection and SIGTERM handlers do that. This makes graceful
    // shutdown cleaner.
    this.#timer.unref?.()
  }

  stop(): void {
    if (!this.#timer) return // idempotent
    clearInterval(this.#timer)
    this.#timer = null
  }

  async #tick(): Promise<void> {
    let payload: HeartbeatPayload
    try {
      payload = this.#gather()
    } catch (err) {
      // gather() must not throw; if it does, log and skip — never crash the ticker.
      this.#logger.error({ err }, 'gather() threw; skipping this heartbeat')
      return
    }

    try {
      await this.#signalr.sendHeartbeat(payload)
    } catch (err) {
      // Connection probably down; reconnect logic in SignalRClient handles it.
      // Heartbeat misses are expected during reconnect windows — debug-level.
      this.#logger.debug({ err }, 'heartbeat invoke failed (likely disconnected)')
    }
  }
}
