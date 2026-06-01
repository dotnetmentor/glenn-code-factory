import { statfs } from 'node:fs/promises'
import { EventEmitter } from 'node:events'
import type { Logger } from 'pino'

export type DiskSample = {
  usedBytes: number
  totalBytes: number
  sampledAt: Date
}

export type DiskPressureLevel = 'ok' | 'warn' | 'critical'

export type DiskMonitorOptions = {
  /** Path to monitor — '/data' inside a runtime Machine. */
  path: string
  /** Sample interval in milliseconds. Default 30_000 (30s). */
  intervalMs?: number
  /** Used-fraction at which to escalate from `ok` to `warn`. Default 0.80. */
  warnThreshold?: number
  /** Used-fraction at which to escalate from `warn` to `critical`. Default 0.95. */
  criticalThreshold?: number
  /** Logger; if absent the module is silent. */
  logger?: Logger
}

type DiskMonitorEvents = {
  pressure: [level: DiskPressureLevel, sample: DiskSample]
}

/**
 * Periodically samples a filesystem path's usage. Emits a `pressure` event ONLY
 * on level transitions (ok→warn, warn→critical, etc.) — not on every sample.
 *
 * The composition root wires the heartbeat gather() to call `latest()` for the
 * most recent sample so HeartbeatPayload can carry usage numbers. The transition
 * events are exposed for higher-level wiring (e.g. emitting a runtime-scope
 * event up to main API) but the daemon doesn't have to consume them — passive
 * use via `latest()` is sufficient for v1.
 */
export class DiskMonitor extends EventEmitter<DiskMonitorEvents> {
  readonly #path: string
  readonly #intervalMs: number
  readonly #warnThreshold: number
  readonly #criticalThreshold: number
  readonly #logger: Logger | null
  #timer: NodeJS.Timeout | null = null
  #latest: DiskSample | null = null
  #level: DiskPressureLevel = 'ok'

  constructor(opts: DiskMonitorOptions) {
    super()
    this.#path = opts.path
    this.#intervalMs = opts.intervalMs ?? 30_000
    this.#warnThreshold = opts.warnThreshold ?? 0.8
    this.#criticalThreshold = opts.criticalThreshold ?? 0.95
    this.#logger = opts.logger?.child({ module: 'disk-monitor' }) ?? null

    if (this.#warnThreshold <= 0 || this.#warnThreshold >= 1) {
      throw new RangeError('warnThreshold must be in (0,1)')
    }
    if (this.#criticalThreshold <= this.#warnThreshold || this.#criticalThreshold >= 1) {
      throw new RangeError('criticalThreshold must be > warnThreshold and < 1')
    }
    if (this.#intervalMs < 1_000) {
      throw new RangeError('intervalMs must be >= 1000')
    }
  }

  start(): void {
    if (this.#timer) return // idempotent
    // Fire once immediately so latest() / level() are populated quickly after
    // boot rather than waiting a full interval.
    void this.#tick()
    this.#timer = setInterval(() => void this.#tick(), this.#intervalMs)
    // unref so the timer doesn't keep the event loop alive on its own — SIGTERM
    // handlers and the SignalR connection are responsible for liveness.
    this.#timer.unref?.()
  }

  stop(): void {
    if (!this.#timer) return // idempotent
    clearInterval(this.#timer)
    this.#timer = null
  }

  latest(): DiskSample | null {
    return this.#latest
  }

  level(): DiskPressureLevel {
    return this.#level
  }

  async #tick(): Promise<void> {
    let sample: DiskSample
    try {
      sample = await this.#sample()
    } catch (err) {
      // statfs can fail on transient FS errors. Log and try again next tick —
      // never crash the ticker.
      this.#logger?.error({ err, path: this.#path }, 'failed to sample disk usage')
      return
    }

    this.#latest = sample
    const fraction = sample.totalBytes > 0 ? sample.usedBytes / sample.totalBytes : 0
    const newLevel: DiskPressureLevel =
      fraction >= this.#criticalThreshold
        ? 'critical'
        : fraction >= this.#warnThreshold
          ? 'warn'
          : 'ok'

    if (newLevel !== this.#level) {
      const previous = this.#level
      this.#level = newLevel
      this.#logger?.info(
        {
          previous,
          newLevel,
          usedBytes: sample.usedBytes,
          totalBytes: sample.totalBytes,
          fraction,
        },
        'disk pressure transition',
      )
      this.emit('pressure', newLevel, sample)
    }
  }

  async #sample(): Promise<DiskSample> {
    const stat = await statfs(this.#path)
    // statfs returns BigInt-typed bsize/bavail/blocks on some Node 20.x versions
    // and Number on others. Normalise to Number — disk usage at runtime is always
    // well under 2^53, so the precision concession is fine.
    const bsize = Number(stat.bsize)
    const blocks = Number(stat.blocks)
    const bavail = Number(stat.bavail)
    const totalBytes = blocks * bsize
    const usedBytes = (blocks - bavail) * bsize
    return {
      usedBytes,
      totalBytes,
      sampledAt: new Date(),
    }
  }
}
