// PushRetryJob — Card 8 of daemon-git-ops.
//
// A periodic background loop that retries `git push` operations that GitModule
// (Card 7) reported as failed. The job owns its own pending state — GitModule
// stays stateless on the retry side and just emits `GitPushFailed` plus
// returns `ok:false` from `push()`. The composition root (Card 10) calls
// `recordFailure(remote, branch)` for each push failure, and this module:
//
//   - de-duplicates by `${remote}:${branch}` so a hot-loop of failed pushes
//     for the same branch never grows the queue;
//   - re-attempts the push on every tick at a fast cadence (default 30s)
//     while the daemon is active, switching to a slow cadence (default 5min)
//     while QuietModeManager reports the daemon as quiet — the user isn't
//     watching, no point keeping the radio loud;
//   - gives up after `maxAttempts` (default 5) and surfaces a runtime-scope
//     event so the UI can prompt the operator to fix the deploy key or
//     network manually;
//   - fast-tracks auth failures: a deploy-key auth error won't be cured by
//     the next git push attempt, so we mark the entry as already-exhausted
//     after the first one-shot retry and surface the exhaustion event
//     immediately rather than wasting four more retries on a guaranteed
//     failure.
//
// Mirrors HeartbeatModule's lifecycle shape (start/stop, idempotent, unref'd
// timer, fake-timer-friendly via injected setInterval/clearInterval). The
// quiet-mode wiring mirrors what HooksModule does — subscribe to QuietMode's
// `sleep`/`wake` events on start(), unsubscribe on stop().
//
// === Why a separate retry queue (not inside GitModule) ===
//
// GitModule is the synchronous orchestrator: callers want immediate
// success/failure on the push they asked for, not a "we'll keep trying"
// promise. The retry semantic is a separate concern with its own clock, its
// own bounded budget, and its own back-off policy. Keeping it out lets
// GitModule stay deterministic per-call and lets this module be unit-tested
// in isolation with fake timers.

import type { Logger } from 'pino'

import type { SignalRClient } from '../signalr/SignalRClient.js'
import type { EmitEventPayload } from '../signalr/types.js'
import { AgentEventKind } from '../signalr/types.js'
import type { QuietModeManager } from '../turn/QuietModeManager.js'

const DEFAULT_INTERVAL_MS = 30_000
const DEFAULT_QUIET_INTERVAL_MS = 300_000
const DEFAULT_MAX_ATTEMPTS = 5

/**
 * Subset of GitModule.push the retry job needs. Narrow on purpose so tests
 * don't have to fabricate a whole GitModule (which drags in GitRunner +
 * SignalRClient).
 */
export interface PushRetryGitModule {
  push(
    remote: string,
    branch: string,
  ): Promise<{
    ok: boolean
    authError?: boolean
    conflict?: boolean
    outputTail?: string
  }>
}

/**
 * Subset of QuietModeManager the retry job needs — just the EventEmitter
 * surface for `sleep`/`wake`. Cast in production wiring; lets tests pass a
 * plain Node EventEmitter.
 */
export interface PushRetryQuietMode {
  on(event: 'sleep' | 'wake', listener: () => void): unknown
  off(event: 'sleep' | 'wake', listener: () => void): unknown
}

export interface PushRetryJobOpts {
  gitModule: PushRetryGitModule
  signalr: Pick<SignalRClient, 'emitEvent'>
  quietMode: PushRetryQuietMode
  logger: Logger
  /** Fast-cadence interval. Default 30_000 ms. */
  intervalMs?: number
  /** Slow-cadence interval (used while QuietMode reports quiet). Default 300_000 ms. */
  quietIntervalMs?: number
  /** Hard cap on per-branch retry attempts before exhaustion. Default 5. */
  maxAttempts?: number
  /** Test seam — Date.now() by default. */
  now?: () => number
  /** Test seam — global setInterval by default. */
  setInterval?: typeof setInterval
  /** Test seam — global clearInterval by default. */
  clearInterval?: typeof clearInterval
}

interface PendingPush {
  remote: string
  branch: string
  attemptCount: number
  firstFailedAt: number
}

export class PushRetryJob {
  readonly #gitModule: PushRetryGitModule
  readonly #signalr: Pick<SignalRClient, 'emitEvent'>
  readonly #quietMode: PushRetryQuietMode
  readonly #logger: Logger
  readonly #intervalMs: number
  readonly #quietIntervalMs: number
  readonly #maxAttempts: number
  readonly #now: () => number
  readonly #setInterval: typeof setInterval
  readonly #clearInterval: typeof clearInterval

  /** Per-branch pending state. Single entry per `${remote}:${branch}`. */
  readonly #pending: Map<string, PendingPush> = new Map()

  #timer: ReturnType<typeof setInterval> | null = null
  #started = false
  /** Tracks the cadence the current timer is running at, so we don't restart needlessly on a duplicate event. */
  #currentInterval = DEFAULT_INTERVAL_MS

  // Stable listener references so removeListener can identity-match on stop().
  // Arrow-property fields capture `this` and avoid the bind-on-every-call
  // anti-pattern that would defeat removeListener.
  readonly #onSleep = (): void => {
    this.#switchInterval(this.#quietIntervalMs)
  }
  readonly #onWake = (): void => {
    this.#switchInterval(this.#intervalMs)
  }

  constructor(opts: PushRetryJobOpts) {
    this.#gitModule = opts.gitModule
    this.#signalr = opts.signalr
    this.#quietMode = opts.quietMode
    this.#logger = opts.logger.child({ module: 'push-retry' })
    this.#intervalMs = opts.intervalMs ?? DEFAULT_INTERVAL_MS
    this.#quietIntervalMs = opts.quietIntervalMs ?? DEFAULT_QUIET_INTERVAL_MS
    this.#maxAttempts = opts.maxAttempts ?? DEFAULT_MAX_ATTEMPTS
    this.#now = opts.now ?? (() => Date.now())
    this.#setInterval = opts.setInterval ?? setInterval
    this.#clearInterval = opts.clearInterval ?? clearInterval
  }

  // ============================================================================
  // Public API
  // ============================================================================

  /**
   * Record that a `git push <remote> <branch>` failed. Idempotent per branch:
   * if the branch already has a pending entry we increment `attemptCount`,
   * otherwise we insert a fresh entry with `attemptCount = 1`.
   *
   * Single entry per branch — even if the caller floods us with failures for
   * the same branch we never accumulate. The next tick treats it as one
   * outstanding push.
   */
  recordFailure(remote: string, branch: string): void {
    const key = makeKey(remote, branch)
    const existing = this.#pending.get(key)
    if (existing !== undefined) {
      existing.attemptCount += 1
      this.#logger.debug(
        { remote, branch, attemptCount: existing.attemptCount },
        'push failure recorded (existing entry)',
      )
      return
    }
    this.#pending.set(key, {
      remote,
      branch,
      attemptCount: 1,
      firstFailedAt: this.#now(),
    })
    this.#logger.debug({ remote, branch }, 'push failure recorded (new entry)')
  }

  /**
   * Subscribe to QuietMode events and start the retry interval. Idempotent —
   * a second call is a no-op. Starts at fast cadence (`intervalMs`); the
   * quiet/wake handlers swap cadence when QuietMode transitions.
   */
  start(): void {
    if (this.#started) return
    this.#started = true

    this.#quietMode.on('sleep', this.#onSleep)
    this.#quietMode.on('wake', this.#onWake)

    this.#currentInterval = this.#intervalMs
    this.#timer = this.#setInterval(() => {
      void this.#tick()
    }, this.#intervalMs)
    // unref so the retry timer doesn't keep the event loop alive on its own —
    // SignalR + signal handlers do that. Mirrors HeartbeatModule.
    this.#timer.unref?.()
  }

  /**
   * Tear down: clear the interval, unsubscribe from QuietMode. Idempotent.
   * After stop() no further ticks fire and recordFailure still records into
   * the map (that's harmless — the entries simply never get retried), but
   * since this is called during shutdown the daemon process exits shortly
   * after anyway.
   */
  stop(): void {
    if (!this.#started) return
    this.#started = false

    if (this.#timer !== null) {
      this.#clearInterval(this.#timer)
      this.#timer = null
    }

    this.#quietMode.off('sleep', this.#onSleep)
    this.#quietMode.off('wake', this.#onWake)
  }

  // ============================================================================
  // Internal
  // ============================================================================

  /**
   * One pass over the pending map: snapshot to an array (so we don't mutate
   * the map while iterating it), then for each entry attempt the push and
   * apply the result to the entry.
   */
  async #tick(): Promise<void> {
    if (this.#pending.size === 0) return

    // Snapshot keys so deletions inside the loop don't disturb iteration.
    const entries = Array.from(this.#pending.values())
    for (const entry of entries) {
      await this.#retryOne(entry)
    }
  }

  async #retryOne(entry: PendingPush): Promise<void> {
    const key = makeKey(entry.remote, entry.branch)

    let result: Awaited<ReturnType<PushRetryGitModule['push']>>
    try {
      result = await this.#gitModule.push(entry.remote, entry.branch)
    } catch (err) {
      // GitModule.push shouldn't throw (it returns a Result), but if it does
      // we treat it as a failed attempt rather than crashing the loop.
      this.#logger.warn(
        { err, remote: entry.remote, branch: entry.branch },
        'push retry threw (treating as failed attempt)',
      )
      result = { ok: false }
    }

    if (result.ok) {
      this.#pending.delete(key)
      this.#logger.info(
        { remote: entry.remote, branch: entry.branch, attemptCount: entry.attemptCount },
        'push retry succeeded',
      )
      return
    }

    if (result.authError === true) {
      // Auth failures don't get better with more retries — the deploy key is
      // the underlying problem. Fast-track to exhaustion so the user sees the
      // surface message immediately rather than after N attempts.
      this.#logger.warn(
        { remote: entry.remote, branch: entry.branch },
        'push retry hit auth error — fast-tracking to exhaustion',
      )
      entry.attemptCount = this.#maxAttempts
    } else {
      entry.attemptCount += 1
    }

    if (entry.attemptCount >= this.#maxAttempts) {
      this.#emitExhausted(entry)
      this.#pending.delete(key)
      return
    }

    this.#logger.debug(
      {
        remote: entry.remote,
        branch: entry.branch,
        attemptCount: entry.attemptCount,
        outputTail: result.outputTail,
      },
      'push retry failed',
    )
  }

  #emitExhausted(entry: PendingPush): void {
    const text = `Pushing to ${entry.remote}/${entry.branch} failed after ${entry.attemptCount} attempts. Check the deploy key or network and retry manually.`

    // TODO(runtime-bootstrap): replace AssistantText carrier with dedicated
    // GitPushExhausted hub method. Same TODO appears at every `sessionId: ''`
    // emit site so a single grep finds them all.
    //
    // No active session/turn means no carrier target — surfacing this via the
    // session-scoped event would attach noise to the wrong session, so we log
    // and skip rather than crash. When the runtime-scope hub method ships
    // this branch goes away.
    const payload: EmitEventPayload = {
      sessionId: '',
      kind: AgentEventKind.Status,
      eventData: JSON.stringify({
        type: 'git_push_exhausted',
        remote: entry.remote,
        branch: entry.branch,
        attempts: entry.attemptCount,
        firstFailedAt: new Date(entry.firstFailedAt).toISOString(),
        text,
      }),
      emittedAt: new Date().toISOString(),
    }

    this.#logger.warn(
      {
        remote: entry.remote,
        branch: entry.branch,
        attempts: entry.attemptCount,
      },
      'push retry exhausted',
    )

    this.#signalr.emitEvent(payload).catch((err: unknown) => {
      this.#logger.error(
        { err, remote: entry.remote, branch: entry.branch },
        'failed to emit git_push_exhausted (no carrier target available)',
      )
    })
  }

  /**
   * Swap the active interval to a different cadence. Cheap restart: clear the
   * existing timer, schedule a fresh one. If start() hasn't been called yet
   * (or stop() has run), this is a no-op — the cadence we'd swap to gets
   * picked up the next time start() runs.
   */
  #switchInterval(nextMs: number): void {
    if (!this.#started || this.#timer === null) return
    if (this.#currentInterval === nextMs) return

    this.#clearInterval(this.#timer)
    this.#currentInterval = nextMs
    this.#timer = this.#setInterval(() => {
      void this.#tick()
    }, nextMs)
    this.#timer.unref?.()
  }
}

function makeKey(remote: string, branch: string): string {
  return `${remote}:${branch}`
}
