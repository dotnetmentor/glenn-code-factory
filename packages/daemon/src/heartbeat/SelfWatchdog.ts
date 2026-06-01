// SelfWatchdog — last-resort liveness guard for the daemon's main thread.
//
// === Why this exists ===
//
// The Node daemon is single-threaded. The Cursor SDK stream iterator
// (`for await (const event of stream)`) shares the event loop with our
// `setInterval`-based heartbeat (HeartbeatModule). During a heavy "scan the
// repo" turn the SDK emits large frames faster than we can JSON.stringify +
// SignalR-invoke them; on a 1-shared-CPU / 2-GB-RAM Fly machine that pushes
// the event loop into 10–20 second starvation windows. Two things happen:
//
//   1. The heartbeat `setInterval` callback can't fire (timers are queued
//      behind every other microtask + I/O), so the master's HeartbeatWatcher
//      flags the runtime as Crashed (~60 s now, see HeartbeatWatcherJob.cs).
//   2. By the time the master decides we're dead, the in-flight turn has
//      already burned 30 s of an SDK call that may itself be wedged on a
//      backpressured stdout pipe, leaving the daemon in a degraded "alive
//      but unresponsive" state where SIGTERM may not arrive cleanly either.
//
// Heartbeat-in-worker (Fix #1) addresses (1) — beats keep flowing because
// they're on a separate OS thread. Backpressure on the emit pipeline
// (Fix #2, BoundedAsyncQueue) addresses the root cause of the starvation.
// THIS file (Fix #3) is the safety net: if those two ever fail to catch a
// real wedge — JS bug, infinite sync loop, native binding deadlock — the
// watchdog notices, signals the main thread once, and force-kills the
// process so the entrypoint.sh `while true; do node dist/main.js; done`
// loop respawns us in milliseconds. We prefer a fast self-respawn over the
// master noticing silence and Fly-destroying the machine (slower, costlier).
//
// === Threshold ===
//
// 50 s. Comfortably under the 60 s HeartbeatWatcher threshold (~10 s gap)
// so we self-respawn via entrypoint.sh before the master flags us as
// Crashed and Fly-destroys the machine. Above the SDK's natural quiet
// windows (large file scans peak around 10–15 s) so we don't false-positive.
// Above the typical GC pause budget (sub-second) so a stop-the-world
// doesn't kill us either.
//
// Raised from 25 s → 50 s in lockstep with the master watcher going
// 30 s → 60 s. If you change one side, change the other — the ~10 s gap
// is the load-bearing invariant.
//
// === Mechanism ===
//
// 1. Main thread allocates an 8-byte SharedArrayBuffer and writes the
//    current `Date.now()` into it every 500 ms via `Atomics.store` on a
//    `BigInt64Array` view.
// 2. A worker_threads worker (inline-bundled here as a string, see WORKER_SRC)
//    reads the same buffer every 1 s via `Atomics.load`.
// 3. If `now - lastTimestamp > thresholdMs`, the worker:
//      a. Posts a `STALL` message back to main (best-effort — main may be
//         too wedged to receive it, but we try).
//      b. Schedules a 2 s grace `setTimeout` then calls
//         `process.kill(process.pid, 'SIGKILL')`. SIGKILL from inside a
//         worker_threads worker targets the shared OS process so the
//         entire daemon dies — that's exactly what we want.
//
// The grace window gives the main thread (if it's NOT truly wedged, only
// briefly behind) a chance to call `process.exit(70)` itself, which gives
// pino's stdio queue a millisecond to flush. The SIGKILL is the floor.
//
// === Why inline-bundle the worker code? ===
//
// esbuild produces a single `dist/main.js`. A second entryPoint would work
// but doubles the bundle config surface and complicates `__dirname` /
// `import.meta.url` resolution at runtime (paths shift between dev `tsx`
// and prod `dist/main.js`). The watchdog worker is small (< 30 lines of
// runtime logic) — passing it as a string with `{ eval: true }` is the
// simplest robust option, and the worker imports nothing from the rest of
// the codebase so the inlining cost is zero.
//
// === Test surface ===
//
// `SelfWatchdog.test.ts` exercises:
//   - The main-thread side (timer updates the buffer, `start()` is
//     idempotent, `stop()` clears the timer + terminates the worker).
//   - The worker logic — extracted as a pure function `runWatcherTick`
//     below so we can unit-test the stall-detection decision without
//     spinning up a real worker_threads worker.

import { Worker } from 'node:worker_threads'
import type { Logger } from 'pino'

/** Default time between main-thread timestamp updates. */
export const DEFAULT_UPDATE_INTERVAL_MS = 500

/** Default time between worker liveness checks. */
export const DEFAULT_CHECK_INTERVAL_MS = 1_000

/** Default stall threshold — under HeartbeatWatcherJob's 60 s by ~10 s. */
export const DEFAULT_THRESHOLD_MS = 50_000

/**
 * Grace period the worker waits after posting STALL before SIGKILL'ing
 * the process. Lets pino's async logger and a graceful `process.exit(70)`
 * from main race the SIGKILL; whichever wins, the process dies.
 */
export const DEFAULT_GRACE_MS = 2_000

/**
 * Pure decision function shared between worker runtime and unit tests.
 * Returns `true` iff the last main-thread heartbeat is older than
 * `thresholdMs`. A zero `lastMs` (buffer never written) is treated as
 * "not yet started" — never a stall — so an outright worker-before-main
 * race doesn't trigger a spurious kill at boot.
 */
export function isStalled(nowMs: number, lastMs: number, thresholdMs: number): boolean {
  if (lastMs <= 0) return false
  return nowMs - lastMs > thresholdMs
}

/**
 * Message sent worker → main when a stall is detected. The worker also
 * schedules SIGKILL on its own; the main-thread handler is purely advisory
 * (gives the parent a chance to log + flush + exit cleanly before SIGKILL).
 */
export interface StallMessage {
  type: 'STALL'
  silentMs: number
  thresholdMs: number
}

interface SelfWatchdogDeps {
  logger: Logger
  thresholdMs?: number
  updateIntervalMs?: number
  checkIntervalMs?: number
  graceMs?: number
  /**
   * Test seam: replace the worker factory. Production passes nothing and
   * we spawn a real worker_threads worker with WORKER_SRC. Tests pass a
   * stub that records constructor args + lets us hand-fire onmessage.
   */
  workerFactory?: (sab: SharedArrayBuffer, opts: WorkerOpts) => WorkerLike
  /**
   * Test seam: replace the on-stall response. Defaults to calling
   * `process.exit(70)`. Tests pass a spy so we don't actually kill the
   * vitest worker.
   */
  onStall?: (msg: StallMessage) => void
}

export interface WorkerOpts {
  thresholdMs: number
  checkIntervalMs: number
  graceMs: number
}

/**
 * Minimal subset of `node:worker_threads`.Worker that the watchdog uses.
 * Lets tests substitute a plain EventEmitter-shaped object.
 */
export interface WorkerLike {
  on(event: 'message', listener: (msg: StallMessage) => void): void
  on(event: 'error', listener: (err: Error) => void): void
  on(event: 'exit', listener: (code: number) => void): void
  terminate(): Promise<number>
}

/**
 * Inline worker source. Kept as a string so esbuild bundles it into the
 * single-file daemon without needing a second entryPoint. The worker
 * intentionally imports nothing — just node built-ins via require —
 * so the {eval:true} Worker doesn't need a module loader at all.
 *
 * Keep this source TINY and side-effect-free except for the timer + kill.
 * Every line here runs on the parallel OS thread; complexity defeats the
 * "this is the floor that catches the other floor failing" purpose.
 */
const WORKER_SRC = `
const { workerData, parentPort } = require('node:worker_threads')
const view = new BigInt64Array(workerData.buffer)
const thresholdMs = workerData.thresholdMs
const checkIntervalMs = workerData.checkIntervalMs
const graceMs = workerData.graceMs

let fired = false
function tick() {
  if (fired) return
  const last = Number(Atomics.load(view, 0))
  const now = Date.now()
  if (last > 0 && now - last > thresholdMs) {
    fired = true
    const silentMs = now - last
    try {
      process.stderr.write(
        '[SelfWatchdog] STALL DETECTED silent_ms=' + silentMs +
        ' threshold_ms=' + thresholdMs +
        ' — SIGKILL in ' + graceMs + 'ms\\n'
      )
    } catch (_) {}
    try {
      parentPort && parentPort.postMessage({
        type: 'STALL',
        silentMs,
        thresholdMs,
      })
    } catch (_) {}
    setTimeout(() => {
      try {
        process.kill(process.pid, 'SIGKILL')
      } catch (_) {
        // last-ditch: synchronous abort. abort() ignores try/catch.
        process.abort()
      }
    }, graceMs)
  }
}

const timer = setInterval(tick, checkIntervalMs)
if (typeof timer.unref === 'function') timer.unref()
`

export class SelfWatchdog {
  readonly #logger: Logger
  readonly #thresholdMs: number
  readonly #updateIntervalMs: number
  readonly #checkIntervalMs: number
  readonly #graceMs: number
  readonly #workerFactory: (sab: SharedArrayBuffer, opts: WorkerOpts) => WorkerLike
  readonly #onStall: (msg: StallMessage) => void

  #sab: SharedArrayBuffer | null = null
  #view: BigInt64Array | null = null
  #timer: NodeJS.Timeout | null = null
  #worker: WorkerLike | null = null

  constructor(deps: SelfWatchdogDeps) {
    this.#logger = deps.logger.child({ module: 'self-watchdog' })
    this.#thresholdMs = deps.thresholdMs ?? DEFAULT_THRESHOLD_MS
    this.#updateIntervalMs = deps.updateIntervalMs ?? DEFAULT_UPDATE_INTERVAL_MS
    this.#checkIntervalMs = deps.checkIntervalMs ?? DEFAULT_CHECK_INTERVAL_MS
    this.#graceMs = deps.graceMs ?? DEFAULT_GRACE_MS
    this.#workerFactory = deps.workerFactory ?? defaultWorkerFactory
    this.#onStall =
      deps.onStall ??
      ((msg) => {
        // Default: log + try to exit cleanly. The worker is racing us with
        // SIGKILL in graceMs; whichever wins, the process dies and the
        // entrypoint.sh restart loop respawns. Code 70 = EX_SOFTWARE
        // (BSD sysexits) — distinguishes from clean exit / SIGTERM in
        // operator dashboards.
        try {
          process.exit(70)
        } catch {
          // Fallthrough — worker SIGKILL will arrive in graceMs.
        }
      })
  }

  /**
   * Start the watchdog. Idempotent. Allocates the SharedArrayBuffer,
   * starts the main-thread updater timer (also fires once immediately so
   * the buffer is never zero by the time the worker's first check fires),
   * then spawns the worker.
   */
  start(): void {
    if (this.#timer) return // idempotent

    this.#sab = new SharedArrayBuffer(8)
    this.#view = new BigInt64Array(this.#sab)
    // Seed the buffer NOW so the worker's first tick — which can fire
    // anywhere from immediately to `checkIntervalMs` later — sees a fresh
    // timestamp and doesn't trip on a 0 → still-zero "stall" race. The
    // `isStalled` helper treats 0 as "not started" defensively, but seeding
    // is the belt-and-braces fix.
    Atomics.store(this.#view, 0, BigInt(Date.now()))

    this.#timer = setInterval(() => {
      // Re-narrow inside the closure — TS can't prove the field is still
      // non-null across the timer callback (a concurrent stop() could
      // null it out before the queued callback fires).
      const view = this.#view
      if (view) Atomics.store(view, 0, BigInt(Date.now()))
    }, this.#updateIntervalMs)
    this.#timer.unref?.()

    this.#worker = this.#workerFactory(this.#sab, {
      thresholdMs: this.#thresholdMs,
      checkIntervalMs: this.#checkIntervalMs,
      graceMs: this.#graceMs,
    })

    this.#worker.on('message', (msg) => {
      if (msg && msg.type === 'STALL') {
        this.#logger.error(
          { silentMs: msg.silentMs, thresholdMs: msg.thresholdMs },
          'self-watchdog STALL detected — exiting',
        )
        this.#onStall(msg)
      }
    })

    this.#worker.on('error', (err) => {
      // Watcher worker died — log loudly, but DON'T crash the daemon. The
      // worker dying just means we lose the safety net; the heartbeat
      // watcher in master will still flag us if main truly wedges. Better
      // to keep serving traffic than to take ourselves down because the
      // floor cracked.
      this.#logger.error({ err }, 'self-watchdog worker errored; safety net is down')
    })

    this.#worker.on('exit', (code) => {
      // Unexpected exit (non-zero, and we didn't call .terminate). Log
      // and forget; same reasoning as the `error` handler.
      if (code !== 0) {
        this.#logger.warn({ code }, 'self-watchdog worker exited unexpectedly')
      }
    })

    this.#logger.info(
      {
        thresholdMs: this.#thresholdMs,
        updateIntervalMs: this.#updateIntervalMs,
        checkIntervalMs: this.#checkIntervalMs,
        graceMs: this.#graceMs,
      },
      'self-watchdog started',
    )
  }

  /**
   * Stop the watchdog. Idempotent. Clears the updater timer and
   * terminates the worker. Used by ShutdownCoordinator + tests.
   */
  async stop(): Promise<void> {
    if (this.#timer) {
      clearInterval(this.#timer)
      this.#timer = null
    }
    if (this.#worker) {
      try {
        await this.#worker.terminate()
      } catch (err) {
        this.#logger.debug({ err }, 'self-watchdog worker.terminate() threw; ignoring')
      }
      this.#worker = null
    }
    this.#sab = null
    this.#view = null
  }

  /** Test-only: peek the current stored timestamp. Returns 0 if not started. */
  _lastWriteMs(): number {
    return this.#view ? Number(Atomics.load(this.#view, 0)) : 0
  }
}

/**
 * Real worker factory used in production. Spawns the inline WORKER_SRC
 * with `{ eval: true }` so esbuild doesn't need to bundle a second
 * entryPoint — the worker source is a plain string literal at build time.
 */
function defaultWorkerFactory(
  sab: SharedArrayBuffer,
  opts: WorkerOpts,
): WorkerLike {
  return new Worker(WORKER_SRC, {
    eval: true,
    workerData: {
      buffer: sab,
      thresholdMs: opts.thresholdMs,
      checkIntervalMs: opts.checkIntervalMs,
      graceMs: opts.graceMs,
    },
  })
}
