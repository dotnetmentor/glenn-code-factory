// LivenessWorker — heartbeat-in-worker_thread (Fix #1 of the 2026-05-24
// runtime-unavailable investigation).
//
// === Why this exists ===
//
// HeartbeatModule lives on the main event loop. It pings the master via
// SignalR every `config.heartbeatIntervalMs` (5s). During heavy Cursor SDK
// turns the main loop is starved (large stdout frames + slow JSON serialise
// + the SDK's `for await` blocking microtasks). The `setInterval` timer
// can't fire on schedule, so heartbeats drop and the master's
// HeartbeatWatcherJob flags us as Crashed (now 30s threshold, see Fix #5).
//
// SelfWatchdog (Fix #3) is the floor — if main wedges hard, the watchdog
// SIGKILL's the process so entrypoint.sh respawns us before the master
// notices. But killing the process loses any in-flight turn state. We'd
// prefer to KEEP THE PROCESS ALIVE through the busy window and just make
// sure the master never flags us in the first place.
//
// That's what THIS module does: spawn a worker_thread that fetches the
// new `POST /api/runtimes/{id}/heartbeat-tick` endpoint on its own
// `setInterval`. The worker has its own OS thread + its own event loop;
// it's completely insulated from whatever the main thread is doing. As
// long as the worker can reach the master, the runtime stays "alive" in
// the master's view, even if the main thread is wedged on a 20s SDK
// flush.
//
// === HTTP, not SignalR ===
//
// Considered dual-SignalR (one connection on main for full traffic,
// another on the worker for just heartbeats). Dropped because:
//
//   - Two connections doubles the master-side connection count (and
//     RuntimeHub's per-connection bookkeeping).
//   - Token rotation across threads is awkward (the worker can't share
//     `config.runtimeToken` directly — DaemonConfig is on main; we'd need
//     a postMessage protocol anyway, see below).
//   - HTTP is simpler — one POST per beat, no negotiate/keepalive
//     state, no reconnect machinery, no protocol layer on the worker.
//
// The endpoint side does exactly the same DB write as
// `RuntimeHub.Heartbeat` (stamps `ProjectRuntimes.LastHeartbeatAt`).
// HeartbeatWatcherJob reads the same column, so both paths feed the
// same liveness signal.
//
// === Token rotation ===
//
// The runtime JWT can rotate at any time (UpdateConfig handler in
// main.ts calls `config.rotateToken`). The worker holds its own copy of
// the token, so we postMessage `UPDATE_TOKEN` to it whenever main
// rotates. The worker's Bearer header is read from a local variable
// updated by the message handler — no shared memory needed for the
// token (the SAB pattern from SelfWatchdog doesn't fit; strings have
// no fixed size and Atomics on string buffers is over-engineering for
// a value that changes maybe once an hour).
//
// On startup the master pushes a fresh token via UpdateConfig within
// seconds of connect; until then the worker uses the initial token
// passed at construction.
//
// === Why inline-bundle the worker code? ===
//
// Same reason as SelfWatchdog: esbuild produces one `dist/main.js`. A
// second entryPoint complicates the build config and `__dirname`
// resolution. The worker is small (~30 lines) and imports nothing from
// the rest of the codebase, so the `{ eval: true }` pattern is the
// simplest robust option.
//
// === Failure modes ===
//
// - Master down / network out: fetch throws → worker catches, logs to
//   stderr, posts a `BEAT_FAILED` message back to main (best-effort —
//   queues if main is wedged), retries next interval. Does NOT crash.
// - Master returns 401/403: token expired or runtime not found.
//   Logged, posted back to main; we'll get a fresh token via
//   UpdateConfig and the next beat lands. Does NOT crash.
// - Master returns 5xx: transient — log, retry next tick.
// - Main thread wedges before sending UPDATE_TOKEN: worker keeps using
//   the previous token. If it's not yet expired, beats continue.
//   If it IS expired, beats fail → SelfWatchdog catches the wedge
//   anyway → process respawns → fresh token on boot.
//
// === Test surface ===
//
// `LivenessWorker.test.ts` exercises:
//   - The main-thread half via the `workerFactory` test seam (we never
//     spawn a real worker_threads worker in unit tests).
//   - `start()` idempotency, `stop()` cleanup, `rotateToken()` posts
//     UPDATE_TOKEN with the new value.
//   - Message handlers — BEAT_OK / BEAT_FAILED are logged at the right
//     levels; non-message worker errors don't crash.

import { Worker } from 'node:worker_threads'
import type { Logger } from 'pino'

/**
 * Default beat interval. Matches `DaemonConfig.heartbeatIntervalMs` default
 * (5s); the constructor accepts an override so DaemonConfig wiring stays
 * the single source of truth.
 */
export const DEFAULT_BEAT_INTERVAL_MS = 5_000

/**
 * Default per-request timeout. Aborts the fetch if the master doesn't
 * respond within this window so a hung TCP socket doesn't pile up
 * outstanding requests across many intervals. 4s leaves ~1s slack inside
 * a 5s beat interval before the next tick fires.
 */
export const DEFAULT_REQUEST_TIMEOUT_MS = 4_000

// ============================================================================
// Worker → main message protocol
// ============================================================================

/** Successful beat — logged at debug for normal volume. */
export interface BeatOkMessage {
  type: 'BEAT_OK'
  /** HTTP status code returned by the master (always 204 in the happy path). */
  status: number
  /** Wall-clock ms the fetch took, round-tripped for SLI visibility. */
  durationMs: number
}

/**
 * Beat failed — fetch threw, timed out, or the master returned non-2xx.
 * Logged at warn so it surfaces in operator dashboards without spamming
 * during a busy turn that drops one beat.
 */
export interface BeatFailedMessage {
  type: 'BEAT_FAILED'
  /** HTTP status code if we got a response; null on fetch reject / timeout. */
  status: number | null
  /** Error message — present on fetch reject; null on non-2xx. */
  error: string | null
}

export type WorkerOutboundMessage = BeatOkMessage | BeatFailedMessage

// ============================================================================
// Main → worker message protocol
// ============================================================================

/** Token rotation — main posts on `DaemonConfig.rotateToken`. */
export interface UpdateTokenMessage {
  type: 'UPDATE_TOKEN'
  token: string
}

export type WorkerInboundMessage = UpdateTokenMessage

// ============================================================================
// Test seam types
// ============================================================================

/** Minimum subset of `node:worker_threads.Worker` LivenessWorker uses. */
export interface WorkerLike {
  on(event: 'message', listener: (msg: WorkerOutboundMessage) => void): void
  on(event: 'error', listener: (err: Error) => void): void
  on(event: 'exit', listener: (code: number) => void): void
  postMessage(msg: WorkerInboundMessage): void
  terminate(): Promise<number>
}

/** Initial workerData payload — everything the worker needs to start beating. */
export interface WorkerInit {
  beatUrl: string
  initialToken: string
  intervalMs: number
  requestTimeoutMs: number
}

export interface LivenessWorkerDeps {
  /** Pino logger — child-prefixed with `module: 'liveness-worker'`. */
  logger: Logger
  /** Master base URL (no trailing slash). Combined with `/api/runtimes/{id}/heartbeat-tick`. */
  masterUrl: string
  /** This daemon's runtime id (UUID string) — path segment in the beat URL. */
  runtimeId: string
  /** Initial JWT bearer token. Rotated via `rotateToken()`. */
  initialToken: string
  /** Beat interval; defaults to `DEFAULT_BEAT_INTERVAL_MS`. */
  intervalMs?: number
  /** Per-request fetch timeout; defaults to `DEFAULT_REQUEST_TIMEOUT_MS`. */
  requestTimeoutMs?: number
  /**
   * Test seam: replace the worker factory. Production passes nothing and
   * we spawn the real inline WORKER_SRC. Tests pass a stub that records
   * construction args + lets us hand-fire messages.
   */
  workerFactory?: (init: WorkerInit) => WorkerLike
}

/**
 * Inline worker source. Same `{ eval: true }` pattern as SelfWatchdog so
 * esbuild bundles into a single `dist/main.js` without a second
 * entryPoint. The worker uses only Node built-ins (`worker_threads`,
 * global `fetch`, `AbortController`, `setInterval`) — no module loader,
 * no extra files at runtime.
 *
 * KEEP THIS TINY. Every line runs on a parallel OS thread; complexity
 * defeats the "this is the floor that catches the main thread starving"
 * purpose. If you find yourself adding logic, ask whether it belongs on
 * the main-thread side via a postMessage instead.
 */
const WORKER_SRC = `
const { workerData, parentPort } = require('node:worker_threads')

const beatUrl = workerData.beatUrl
const intervalMs = workerData.intervalMs
const requestTimeoutMs = workerData.requestTimeoutMs
let token = workerData.initialToken

// Token rotation from main thread. The .NET runtime token rotates via the
// UpdateConfig SignalR push; main.ts forwards the new token to us as
// soon as it arrives. Until that lands we use whatever the constructor
// handed us (which is the same token the SignalR connection currently
// uses, so it should always be valid on boot).
parentPort && parentPort.on('message', (msg) => {
  if (msg && msg.type === 'UPDATE_TOKEN' && typeof msg.token === 'string' && msg.token.length > 0) {
    token = msg.token
  }
})

let inFlight = false
async function beat() {
  // Skip if a previous beat is still in flight — the master is being slow,
  // adding another request just makes the backlog worse. The next interval
  // tick will try again. (We don't await beat() from setInterval anyway,
  // so this is purely a self-throttle.)
  if (inFlight) return
  inFlight = true

  const startedMs = Date.now()
  const ac = new AbortController()
  const timer = setTimeout(() => ac.abort(), requestTimeoutMs)

  try {
    const resp = await fetch(beatUrl, {
      method: 'POST',
      headers: { Authorization: 'Bearer ' + token },
      signal: ac.signal,
    })
    const durationMs = Date.now() - startedMs
    if (resp.ok) {
      try {
        parentPort && parentPort.postMessage({
          type: 'BEAT_OK',
          status: resp.status,
          durationMs,
        })
      } catch (_) {}
    } else {
      try {
        parentPort && parentPort.postMessage({
          type: 'BEAT_FAILED',
          status: resp.status,
          error: null,
        })
      } catch (_) {}
    }
  } catch (err) {
    try {
      parentPort && parentPort.postMessage({
        type: 'BEAT_FAILED',
        status: null,
        error: err && err.message ? String(err.message) : String(err),
      })
    } catch (_) {}
  } finally {
    clearTimeout(timer)
    inFlight = false
  }
}

// Fire once immediately so the master doesn't have to wait \`intervalMs\` for
// the first liveness signal from this thread (the main-thread SignalR
// Heartbeat also fires immediately, but this thread might be the one that
// wins the race during a busy boot).
beat()
const tick = setInterval(beat, intervalMs)
if (typeof tick.unref === 'function') tick.unref()
`

export class LivenessWorker {
  readonly #logger: Logger
  readonly #beatUrl: string
  readonly #initialToken: string
  readonly #intervalMs: number
  readonly #requestTimeoutMs: number
  readonly #workerFactory: (init: WorkerInit) => WorkerLike

  #worker: WorkerLike | null = null
  /**
   * Local mirror of the current token. Kept so `rotateToken` calls before
   * `start()` are still honoured: the latest value flows into `WorkerInit`
   * at spawn time.
   */
  #currentToken: string

  constructor(deps: LivenessWorkerDeps) {
    this.#logger = deps.logger.child({ module: 'liveness-worker' })
    // Strip a single trailing slash off masterUrl to avoid `//api/...`. We
    // accept either form for caller convenience.
    const base = deps.masterUrl.endsWith('/') ? deps.masterUrl.slice(0, -1) : deps.masterUrl
    this.#beatUrl = `${base}/api/runtimes/${deps.runtimeId}/heartbeat-tick`
    this.#initialToken = deps.initialToken
    this.#currentToken = deps.initialToken
    this.#intervalMs = deps.intervalMs ?? DEFAULT_BEAT_INTERVAL_MS
    this.#requestTimeoutMs = deps.requestTimeoutMs ?? DEFAULT_REQUEST_TIMEOUT_MS
    this.#workerFactory = deps.workerFactory ?? defaultWorkerFactory
  }

  /**
   * Start the worker. Idempotent. Spawns the inline WORKER_SRC with the
   * initial token + beat config; from this point the worker fetches
   * `/heartbeat-tick` on its own thread independent of main.
   */
  start(): void {
    if (this.#worker) return // idempotent

    this.#worker = this.#workerFactory({
      beatUrl: this.#beatUrl,
      initialToken: this.#currentToken,
      intervalMs: this.#intervalMs,
      requestTimeoutMs: this.#requestTimeoutMs,
    })

    this.#worker.on('message', (msg) => {
      if (!msg) return
      if (msg.type === 'BEAT_OK') {
        this.#logger.debug(
          { status: msg.status, durationMs: msg.durationMs },
          'liveness beat ok',
        )
        return
      }
      if (msg.type === 'BEAT_FAILED') {
        // warn (not error) — single beat failures during reconnect / network
        // hiccups are routine; the master flags Crashed only after 30s of
        // silence. Operators care about sustained failures, not one-offs.
        this.#logger.warn(
          { status: msg.status, error: msg.error },
          'liveness beat failed',
        )
        return
      }
    })

    this.#worker.on('error', (err) => {
      // Worker died — log loudly but DON'T crash the daemon. Same reasoning
      // as SelfWatchdog: better to keep serving traffic with the main-thread
      // SignalR Heartbeat as the only liveness path than to take ourselves
      // down because the secondary path failed.
      this.#logger.error({ err }, 'liveness worker errored; thread-independent path down')
    })

    this.#worker.on('exit', (code) => {
      if (code !== 0) {
        this.#logger.warn({ code }, 'liveness worker exited unexpectedly')
      }
    })

    this.#logger.info(
      {
        beatUrl: this.#beatUrl,
        intervalMs: this.#intervalMs,
        requestTimeoutMs: this.#requestTimeoutMs,
      },
      'liveness worker started',
    )
  }

  /**
   * Update the bearer token used by the worker. Called from the
   * UpdateConfig handler in main.ts whenever the runtime JWT rotates.
   *
   * If called BEFORE `start()`, the new value replaces the initial token
   * and the worker spawns with it. If called AFTER `start()`, we
   * postMessage the new token; the worker's local cache picks it up
   * before the next fetch.
   */
  rotateToken(newToken: string): void {
    if (typeof newToken !== 'string' || newToken.length === 0) {
      this.#logger.warn({}, 'rotateToken called with empty token; ignoring')
      return
    }
    this.#currentToken = newToken
    if (!this.#worker) return // pre-start — start() will pick up the new value
    try {
      this.#worker.postMessage({ type: 'UPDATE_TOKEN', token: newToken })
    } catch (err) {
      // postMessage can throw if the worker has already terminated. Swallow
      // — start() will re-spawn with the latest #currentToken if called
      // again, and the SelfWatchdog catches a truly dead daemon.
      this.#logger.warn({ err }, 'liveness worker postMessage failed (worker may be down)')
    }
  }

  /**
   * Stop the worker. Idempotent. Used by ShutdownCoordinator + tests.
   */
  async stop(): Promise<void> {
    if (!this.#worker) return
    try {
      await this.#worker.terminate()
    } catch (err) {
      this.#logger.debug({ err }, 'liveness worker.terminate() threw; ignoring')
    }
    this.#worker = null
  }

  /** Test-only: peek the current cached token. */
  _currentToken(): string {
    return this.#currentToken
  }

  /** Test-only: peek the resolved beat URL. */
  _beatUrl(): string {
    return this.#beatUrl
  }

  /** Test-only: peek the initial token (never mutated). */
  _initialToken(): string {
    return this.#initialToken
  }
}

/**
 * Real worker factory used in production. Spawns the inline WORKER_SRC
 * with `{ eval: true }` so esbuild doesn't need a second entryPoint.
 */
function defaultWorkerFactory(init: WorkerInit): WorkerLike {
  return new Worker(WORKER_SRC, {
    eval: true,
    workerData: {
      beatUrl: init.beatUrl,
      initialToken: init.initialToken,
      intervalMs: init.intervalMs,
      requestTimeoutMs: init.requestTimeoutMs,
    },
  })
}
