// BootRetryCounter — tracks how many times the daemon has tried to bootstrap
// before successfully reaching ReportReadyStage.
//
// === Spec context (runtime-observability-super-admin) ===
//
// The super-admin runtime drawer wants to know when a daemon has been
// crash-loop bootstrapping. The counter persists at `/data/.daemon/boot-retry-count`
// on the runtime's persistent volume so a respawn (supervisord, Fly machine
// restart) sees the previous value rather than starting fresh every cold boot.
//
// Lifecycle:
//   - On daemon start, `loadAndIncrement()` reads the file (treating missing /
//     malformed as 0), increments by one, persists, and returns the new value.
//     This value is the `bootAttemptNumber` stamped on every `BootstrapStage*`
//     event payload.
//   - On `ReportReadyStage` success, `reset()` writes `0` so the next cold boot
//     starts at attempt 1 again.
//
// === Fault model ===
//
// Best-effort persistence: a write failure (ENOSPC, ENOENT on the parent
// directory, …) is logged at warn and the in-memory counter still advances.
// The worst outcome is one missed reset where a freshly-booted daemon reports
// an inflated attempt number — far better than crashing the boot path.
//
// We intentionally do NOT use atomic rename — the file is one integer and a
// torn write would just produce a malformed string which `loadAndIncrement`
// treats as 0 (i.e. "start fresh"). That's the desired behaviour: a corrupt
// counter shouldn't pin a runtime at retry 999 forever.

import { mkdir, readFile, writeFile } from 'node:fs/promises'
import { dirname } from 'node:path'
import type { Logger } from 'pino'

/** Default file path inside the runtime container. */
const DEFAULT_PATH = '/data/.daemon/boot-retry-count'

export interface BootRetryCounterOptions {
  logger: Logger
  /** Override the persisted file path. Tests pass a tmpdir. */
  path?: string
}

/**
 * Persistent boot-attempt counter. One instance per daemon process; the
 * composition root creates it in `runMain` before bootstrap starts and threads
 * the current value into the orchestrator + emitter.
 */
export class BootRetryCounter {
  readonly #path: string
  readonly #logger: Logger
  /** In-memory snapshot of the on-disk value. Hydrated by `loadAndIncrement`. */
  #current = 0

  constructor(opts: BootRetryCounterOptions) {
    this.#path = opts.path ?? DEFAULT_PATH
    this.#logger = opts.logger.child({ module: 'boot-retry-counter' })
  }

  /** Latest in-memory value (post-load). */
  get current(): number {
    return this.#current
  }

  /**
   * Read the persisted counter (missing/malformed → 0), increment, persist,
   * and return the new value. Call once at daemon startup, before bootstrap.
   *
   * Returns the new attempt number (>= 1). Persistence failures are logged
   * but never thrown — the daemon keeps the in-memory value either way.
   */
  async loadAndIncrement(): Promise<number> {
    let prev = 0
    try {
      const raw = await readFile(this.#path, 'utf8')
      const parsed = Number.parseInt(raw.trim(), 10)
      if (Number.isFinite(parsed) && parsed >= 0) {
        prev = parsed
      } else {
        this.#logger.debug({ raw }, 'malformed boot-retry-count file; treating as 0')
      }
    } catch (err) {
      // ENOENT on cold boot is the normal path — debug.
      this.#logger.debug({ err }, 'boot-retry-count missing or unreadable; treating as 0')
    }

    this.#current = prev + 1
    await this.#persist(this.#current)
    return this.#current
  }

  /**
   * Persist `0` to disk and zero the in-memory snapshot. Called from
   * `ReportReadyStage` on success so the next cold boot starts at attempt 1.
   * Persistence failure logs at warn but never throws.
   */
  async reset(): Promise<void> {
    this.#current = 0
    await this.#persist(0)
  }

  async #persist(value: number): Promise<void> {
    try {
      await mkdir(dirname(this.#path), { recursive: true })
      await writeFile(this.#path, String(value), 'utf8')
    } catch (err) {
      this.#logger.warn({ err, path: this.#path, value }, 'failed to persist boot-retry-count')
    }
  }
}
