// QuietModeManager — Scene 6 of the daemon-architecture spec.
//
// Observes TurnRunner's `idle` / `activity` stream and, after
// `config.quietTimeoutMs` of continuous idle, marks the daemon as quiet.
// The next `activity` event wakes it back up.
//
// === Why a separate module ===
// TurnRunner already owns the turn state machine, SDK plumbing, and SignalR
// inbound/outbound wire. Quiet mode is a memory-and-status optimisation that
// observes turn lifecycle without participating in it. Splitting it out:
//   - keeps TurnRunner's surface focused;
//   - lets this clock be unit-tested in isolation with fake timers;
//   - mirrors the sibling pattern (DiskMonitor) of "passive observer that
//     emits transition events for the composition root to wire wherever".
//
// === On the "drop the SDK from memory" semantic ===
// The spec brief talks about dropping the Cursor SDK module on quiet entry
// and lazy-loading on the next StartTurn. The concrete daemon already routes
// every turn through `buildCursorFactory`, which does
// `await import('@cursor/sdk')` inside its async iterator — so the SDK is
// lazy on first use already. The catch: once Node has imported a native ESM
// module it stays in the module cache forever; there is no portable
// equivalent of `delete require.cache[id]` for ESM. So the "drop" is
// structural, not literal:
//
//   - we emit `sleep` so the composition root can release any per-session
//     state it owns (HTTP clients, MCP server handles, …);
//   - we expose `isQuiet()` so HeartbeatModule's gather() can report quiet
//     status to main API;
//   - we do NOT touch require.cache or the module registry.
//
// The wake target in the spec (≤500 ms) is therefore trivially met: the
// `activity` event fires synchronously inside TurnRunner's StartTurn handler,
// and our `wake` listener also runs synchronously — there is no I/O on the
// path between them, so the `wake` event is observable before the next
// microtask. The actual SDK re-import is whatever `import()` costs from the
// already-warm module cache, which is sub-millisecond.

import { EventEmitter } from 'node:events'
import type { Logger } from 'pino'

import type { DaemonConfig } from '../config/DaemonConfig.js'
import type { TurnRunner } from './TurnRunner.js'

type QuietModeEvents = {
  sleep: []
  wake: []
}

interface QuietModeManagerDeps {
  turnRunner: TurnRunner
  config: DaemonConfig
  logger: Logger
}

export class QuietModeManager extends EventEmitter<QuietModeEvents> {
  readonly #turnRunner: TurnRunner
  readonly #timeoutMs: number
  readonly #logger: Logger

  #timer: NodeJS.Timeout | null = null
  #quiet = false
  #started = false

  // Listener references kept stable so we can deregister them on stop().
  // Arrow-property fields capture `this` and avoid the bind-on-every-call
  // anti-pattern that would defeat removeListener identity matching.
  readonly #onIdle = (): void => this.#scheduleSleep()
  readonly #onActivity = (): void => this.#cancelSleep()

  constructor(deps: QuietModeManagerDeps) {
    super()
    this.#turnRunner = deps.turnRunner
    this.#timeoutMs = deps.config.quietTimeoutMs
    this.#logger = deps.logger.child({ module: 'quiet-mode' })
  }

  /**
   * Subscribe to the runner's idle/activity stream. If the runner is already
   * idle we start the sleep timer immediately — that's the normal post-boot
   * state. If it's running, we're already in active mode and just wait for
   * the next `idle`.
   *
   * Idempotent: a second call is a no-op (no double-registered listeners).
   */
  start(): void {
    if (this.#started) return
    this.#started = true

    this.#turnRunner.on('idle', this.#onIdle)
    this.#turnRunner.on('activity', this.#onActivity)

    if (this.#turnRunner.state().kind === 'idle') {
      this.#scheduleSleep()
    }
  }

  /**
   * Detach listeners and clear the pending timer. Idempotent. After stop()
   * the manager will not emit further `sleep`/`wake` events.
   */
  stop(): void {
    if (!this.#started) return
    this.#started = false

    this.#turnRunner.off('idle', this.#onIdle)
    this.#turnRunner.off('activity', this.#onActivity)
    this.#cancelSleep({ silent: true })
  }

  isQuiet(): boolean {
    return this.#quiet
  }

  #scheduleSleep(): void {
    if (this.#timer) clearTimeout(this.#timer)
    this.#timer = setTimeout(() => {
      this.#timer = null
      if (this.#quiet) return // already quiet — defensive, shouldn't happen
      this.#quiet = true
      this.#logger.info('entering quiet mode')
      this.emit('sleep')
    }, this.#timeoutMs)
    // Don't pin the event loop just to wait for an idle deadline.
    this.#timer.unref?.()
  }

  /**
   * Cancel the pending sleep (if any) and, if we're already quiet, transition
   * back to awake and emit `wake`. The wake emit is synchronous: there is no
   * await on this path, so an `activity` event from TurnRunner triggers our
   * `wake` listeners before the next microtask.
   *
   * `silent` is for stop(): we want to clear the timer without announcing a
   * wake (we're tearing down, not waking).
   */
  #cancelSleep(opts: { silent?: boolean } = {}): void {
    if (this.#timer) {
      clearTimeout(this.#timer)
      this.#timer = null
    }
    if (this.#quiet && !opts.silent) {
      this.#quiet = false
      this.#logger.info('waking from quiet mode')
      this.emit('wake')
    } else if (this.#quiet && opts.silent) {
      // stop() while quiet: clear the flag without announcing.
      this.#quiet = false
    }
  }
}
