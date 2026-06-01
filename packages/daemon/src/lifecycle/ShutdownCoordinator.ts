// ShutdownCoordinator — Scene 7 of the daemon-architecture spec.
//
// On SIGTERM/SIGINT supervisord wants the daemon to:
//   1. Stop accepting new turns (so a StartTurn that lands mid-shutdown gets a
//      structured `turn_rejected: daemon_draining` event, not a torn TCP).
//   2. Drain the in-flight turn — bounded by `config.turnTimeoutMs`. If the
//      turn doesn't finish in time, fire its abort signal and give the SDK a
//      short grace period to unwind.
//   3. Stop the background timers (disk, quiet, heartbeat) so they don't
//      ride out into the close phase pinning the event loop or fighting the
//      shutdown event with stale data.
//   4. Tell main API we're leaving voluntarily, so the runtime panel can show
//      "shutting down" instead of "lost contact" for the moments between this
//      event and the SignalR close.
//   5. Close SignalR cleanly.
//   6. process.exit(0).
//
// A second SIGTERM during shutdown means an operator wants out NOW — we exit 1
// without waiting for any of the above.
//
// === Final-shutdown signal: emit-only ===
//
// The brief asked for a "final heartbeat with state: 'shutting_down'", but
// the heartbeat wire format (`HeartbeatPayload` in signalr/types.ts) carries
// only `emittedAt`, `daemonVersion`, `cpuPercent`, `memoryUsedMb` — there is
// no `state` field. Adding one to that DTO is a coupled change across the
// .NET hub and is outside this card's scope.
//
// Instead we mirror BootstrapOrchestrator's stopgap (see its
// #emitRuntimeEvent): emit a runtime-scope `daemon_shutting_down` event via
// `signalr.emitEvent` using `sessionId: ''` and `eventType: 'AssistantText'`
// as a generic carrier, with the real type embedded in `eventData`. When
// runtime-bootstrap lands a proper `EmitRuntimeEvent` hub method we'll swap
// both call sites at once.
//
// We deliberately do NOT send a "final regular heartbeat" right before
// disconnect — it would be indistinguishable on the wire from a normal beat,
// so it gives main API no extra information. The emit-only path carries the
// "leaving voluntarily" semantic; that's what matters for graceful UX.

import type { Logger } from 'pino'

import { DaemonConfig } from '../config/DaemonConfig.js'
import { DiskMonitor } from '../disk/DiskMonitor.js'
import type { DestructiveOpGate } from '../git/DestructiveOpGate.js'
import type { PushRetryJob } from '../git/PushRetryJob.js'
import { HeartbeatModule } from '../heartbeat/HeartbeatModule.js'
import type { LivenessWorker } from '../heartbeat/LivenessWorker.js'
import type { FileChangeWatcher } from '../hooks/FileChangeWatcher.js'
import { SignalRClient } from '../signalr/SignalRClient.js'
import type { EmitEventPayload } from '../signalr/types.js'
import { AgentEventKind } from '../signalr/types.js'
import { QuietModeManager } from '../turn/QuietModeManager.js'
import { TurnRunner } from '../turn/TurnRunner.js'

/**
 * After we've called `cancel('draining')` on a hung turn, give the SDK
 * iterator this much time to surface the AbortError + emit a final event +
 * settle TurnRunner back to `idle`. Bounded so a misbehaving SDK can't keep
 * the daemon process alive forever.
 */
const SETTLE_AFTER_CANCEL_MS = 5_000

interface ShutdownCoordinatorDeps {
  turnRunner: TurnRunner
  heartbeat: HeartbeatModule
  /**
   * Optional liveness worker (Fix #1 of the 2026-05-24 runtime-unavailable
   * investigation, `heartbeat/LivenessWorker.ts`). Stopped in step 3 of
   * shutdown alongside the main-thread heartbeat — its worker thread keeps
   * the daemon process alive until `terminate()` resolves, so a missed
   * stop here would leak past process.exit. Optional because the legacy/
   * test bring-up paths don't wire it; production composition root always
   * passes a real instance.
   */
  livenessWorker?: Pick<LivenessWorker, 'stop'>
  signalr: SignalRClient
  diskMonitor: DiskMonitor
  quietMode: QuietModeManager
  /**
   * Optional file watcher (Card 10 of daemon-hooks-runner). Stopped alongside
   * the other observers in step 3 of shutdown. Optional because the existing
   * call sites that build a coordinator without the hook subsystem
   * (legacy/test) shouldn't have to construct a watcher just to satisfy the
   * type.
   */
  fileWatcher?: Pick<FileChangeWatcher, 'stop'>
  /**
   * Optional push-retry job (Card 8 of daemon-git-ops). Stopped alongside the
   * other observers in step 3 of shutdown. Optional for the same reason
   * `fileWatcher` is — call sites that don't have GitModule wired in (tests,
   * legacy bring-up) shouldn't have to fabricate one.
   */
  pushRetryJob?: Pick<PushRetryJob, 'stop'>
  /**
   * Optional destructive-op gate (Card 9 of daemon-git-ops). Shutdown calls
   * its `shutdown()` so any parked approval promises resolve with `ok:false`
   * rather than leaking past process exit. Optional for the same reason
   * `pushRetryJob` is — call sites that don't have GitModule wired in
   * shouldn't have to fabricate one.
   */
  destructiveOpGate?: Pick<DestructiveOpGate, 'shutdown'>
  /**
   * Optional log tailer (runtime-spec-v2 Phase 5). Shutdown calls its
   * `dispose()` so any `tail -F` child processes the daemon spawned for
   * on-demand log streaming are SIGTERM'd before exit — otherwise they'd
   * outlive their parent and dangle as zombies under the runtime
   * container's PID 1. Optional for the same reason `pushRetryJob` is:
   * legacy/test bring-up paths that don't wire the log subsystem
   * shouldn't have to fabricate one.
   */
  logTailer?: { dispose(): void }
  config: DaemonConfig
  logger: Logger
  /**
   * Override `process.exit` for tests so they don't actually terminate the
   * test runner. Production wiring uses `process.exit` directly.
   */
  exit?: (code: number) => void
  /**
   * Override `process.on` / `process.off` for tests so we can drive the
   * registered handlers manually without fighting vitest's own SIGINT
   * trapping. Returns a cleanup-fn the coordinator calls on shutdown
   * completion to detach the handler — lets a test harness re-register.
   */
  onSignal?: (sig: NodeJS.Signals, handler: () => void) => () => void
}

export class ShutdownCoordinator {
  readonly #turnRunner: TurnRunner
  readonly #heartbeat: HeartbeatModule
  readonly #livenessWorker: Pick<LivenessWorker, 'stop'> | null
  readonly #signalr: SignalRClient
  readonly #diskMonitor: DiskMonitor
  readonly #quietMode: QuietModeManager
  readonly #fileWatcher: Pick<FileChangeWatcher, 'stop'> | null
  readonly #pushRetryJob: Pick<PushRetryJob, 'stop'> | null
  readonly #destructiveOpGate: Pick<DestructiveOpGate, 'shutdown'> | null
  readonly #logTailer: { dispose(): void } | null
  readonly #config: DaemonConfig
  readonly #logger: Logger
  readonly #exit: (code: number) => void
  readonly #onSignal: (sig: NodeJS.Signals, handler: () => void) => () => void

  #installed = false
  #shuttingDown = false
  #cleanupListeners: Array<() => void> = []
  // Optional hook so a SIGTERM mid-bootstrap aborts the BootstrapOrchestrator's
  // AbortController. Composition root injects this via `install({...})` after
  // it has built the controller but before it awaits bootstrap.start(). Kept
  // optional so call sites that don't have a bootstrap in flight (tests) can
  // omit it entirely.
  #cancelInFlightBootstrap: (() => void) | null = null

  constructor(deps: ShutdownCoordinatorDeps) {
    this.#turnRunner = deps.turnRunner
    this.#heartbeat = deps.heartbeat
    this.#livenessWorker = deps.livenessWorker ?? null
    this.#signalr = deps.signalr
    this.#diskMonitor = deps.diskMonitor
    this.#quietMode = deps.quietMode
    this.#fileWatcher = deps.fileWatcher ?? null
    this.#pushRetryJob = deps.pushRetryJob ?? null
    this.#destructiveOpGate = deps.destructiveOpGate ?? null
    this.#logTailer = deps.logTailer ?? null
    this.#config = deps.config
    this.#logger = deps.logger.child({ module: 'shutdown' })
    this.#exit = deps.exit ?? ((code) => process.exit(code))
    this.#onSignal =
      deps.onSignal ??
      ((sig, handler) => {
        process.on(sig, handler)
        return () => {
          process.off(sig, handler)
        }
      })
  }

  /**
   * Register SIGTERM + SIGINT handlers. Idempotent — calling twice is a
   * silent no-op so a composition-root retry doesn't double-register.
   *
   * `cancelInFlightBootstrap` is an optional escape hatch for the case where
   * SIGTERM arrives while BootstrapOrchestrator is still running: shutdown
   * will fire the callback first so the bootstrap's AbortController unwinds
   * before we drain turns / stop timers / etc. Calls are idempotent on the
   * caller's side; we invoke once.
   */
  install(opts: { cancelInFlightBootstrap?: () => void } = {}): void {
    if (this.#installed) return
    this.#installed = true
    if (opts.cancelInFlightBootstrap !== undefined) {
      this.#cancelInFlightBootstrap = opts.cancelInFlightBootstrap
    }

    const handle = (signal: NodeJS.Signals) => () => {
      if (this.#shuttingDown) {
        // Second signal during shutdown — operator wants out NOW. We don't
        // unregister the first handler before this fires, so a third signal
        // would still run the same handler; that's fine because exit(1)
        // takes the process down before the third can land.
        this.#logger.warn(
          { signal },
          'second signal during shutdown — exiting forcefully',
        )
        this.#exit(1)
        return
      }
      void this.shutdown(signal.toLowerCase())
    }

    this.#cleanupListeners.push(this.#onSignal('SIGTERM', handle('SIGTERM')))
    this.#cleanupListeners.push(this.#onSignal('SIGINT', handle('SIGINT')))
  }

  /**
   * Run the shutdown sequence. Idempotent — a second concurrent call (e.g.
   * from a programmatic path that races with a signal) returns immediately.
   */
  async shutdown(reason: string): Promise<void> {
    if (this.#shuttingDown) return
    this.#shuttingDown = true
    this.#logger.info({ reason }, 'shutdown starting')

    // 0. If bootstrap is still in flight, abort it BEFORE we touch the rest
    //    of the runtime. The bootstrap's AbortSignal then unwinds it from
    //    inside, leaving us free to drain whatever residual state remains.
    if (this.#cancelInFlightBootstrap !== null) {
      try {
        this.#cancelInFlightBootstrap()
      } catch (err) {
        this.#logger.warn({ err }, 'cancelInFlightBootstrap threw (continuing)')
      }
    }

    // 1. Refuse new turns. A StartTurn that arrives between here and the
    //    SignalR close gets a structured `turn_rejected: daemon_draining`
    //    via TurnRunner's existing rejection path.
    this.#turnRunner.setAcceptingNewTurns(false)

    // 2. Drain the in-flight turn (bounded).
    await this.#drainTurn()

    // 3. Stop background timers — order matters: disk + quiet + fileWatcher +
    //    pushRetryJob are observers and don't talk to the wire (well,
    //    pushRetryJob does on retry attempts, but we stop it BEFORE the
    //    shutdown emit so its `git push` invocations don't race the close),
    //    but heartbeat does, and we want the `daemon_shutting_down` event
    //    below to be the LAST thing main API sees from us. So heartbeat stops
    //    before the emit.
    this.#diskMonitor.stop()
    this.#quietMode.stop()
    if (this.#fileWatcher !== null) {
      try {
        await this.#fileWatcher.stop()
      } catch (err) {
        this.#logger.warn({ err }, 'fileWatcher stop threw (continuing)')
      }
    }
    if (this.#pushRetryJob !== null) {
      try {
        this.#pushRetryJob.stop()
      } catch (err) {
        this.#logger.warn({ err }, 'pushRetryJob stop threw (continuing)')
      }
    }
    if (this.#destructiveOpGate !== null) {
      try {
        this.#destructiveOpGate.shutdown()
      } catch (err) {
        this.#logger.warn({ err }, 'destructiveOpGate shutdown threw (continuing)')
      }
    }
    if (this.#logTailer !== null) {
      try {
        this.#logTailer.dispose()
      } catch (err) {
        this.#logger.warn({ err }, 'logTailer dispose threw (continuing)')
      }
    }
    this.#heartbeat.stop()
    if (this.#livenessWorker !== null) {
      // Fix #1: stop the liveness worker alongside heartbeat — both feed the
      // same `LastHeartbeatAt` column, so they go silent together. terminate()
      // is best-effort; failures get logged but never block the rest of
      // shutdown (the watchdog SIGKILLs us on a true wedge anyway).
      try {
        await this.#livenessWorker.stop()
      } catch (err) {
        this.#logger.warn({ err }, 'livenessWorker stop threw (continuing)')
      }
    }

    // 4. Best-effort runtime-scope shutdown event. Failure does NOT block
    //    the rest of shutdown.
    await this.#emitShutdownEvent(reason)

    // 5. Close SignalR. A throw here is non-fatal; we still need to exit.
    try {
      await this.#signalr.stop()
    } catch (err) {
      this.#logger.warn({ err }, 'signalr stop threw (continuing)')
    }

    // 6. Detach signal handlers — leaves no danglers if something installs
    //    a fresh coordinator inside the same process (typically a test).
    for (const cleanup of this.#cleanupListeners) cleanup()
    this.#cleanupListeners = []

    this.#logger.info('shutdown complete')
    this.#exit(0)
  }

  // ============================================================================
  // Internal
  // ============================================================================

  async #drainTurn(): Promise<void> {
    if (this.#turnRunner.state().kind === 'idle') return

    this.#logger.info(
      { state: this.#turnRunner.state().kind },
      'draining in-flight turn',
    )

    // Listener installed once; both phases (pre-cancel timeout race and
    // post-cancel settle race) reuse the same resolved promise so an `idle`
    // event fired during phase 1 is observable in phase 2.
    const idlePromise = this.#waitForIdle()

    const drainTimeout = this.#config.turnTimeoutMs
    const phase1 = await Promise.race([
      idlePromise.then(() => 'drained' as const),
      timeoutSignal(drainTimeout),
    ])
    if (phase1 === 'drained') {
      this.#logger.info('turn drained cleanly')
      return
    }

    this.#logger.warn({ drainTimeout }, 'drain timeout — cancelling turn')
    await this.#turnRunner.cancel('draining')

    // Give the SDK iterator + cancel path a bounded grace window to settle.
    // We don't error on miss — at worst we exit with a still-running task
    // that Node will kill on process.exit anyway.
    await Promise.race([idlePromise, timeoutSignal(SETTLE_AFTER_CANCEL_MS)])
  }

  /**
   * Resolves the first time TurnRunner emits `idle`. Listener is registered
   * via `once` so it auto-detaches; suitable for use across both drain
   * phases.
   */
  #waitForIdle(): Promise<void> {
    return new Promise<void>((resolve) => {
      this.#turnRunner.once('idle', () => {
        resolve()
      })
    })
  }

  async #emitShutdownEvent(reason: string): Promise<void> {
    const payload: EmitEventPayload = {
      sessionId: '',
      // Runtime-scope event — no session, no run. Projected as a Status frame
      // with the body in `eventData` JSON. The chat panel ignores Status
      // frames without a runStatus column; this carries operational telemetry
      // for the audit row only.
      kind: AgentEventKind.Status,
      eventData: JSON.stringify({
        type: 'daemon_shutting_down',
        reason,
        runtimeId: this.#config.runtimeId,
        daemonVersion: this.#config.daemonVersion,
      }),
      emittedAt: new Date().toISOString(),
    }
    try {
      await this.#signalr.emitEvent(payload)
    } catch (err) {
      // Best-effort — main API will still notice the connection drop in a
      // couple of seconds, just without the "I left voluntarily" hint.
      this.#logger.warn(
        { err },
        'failed to emit daemon_shutting_down (non-fatal)',
      )
    }
  }
}

/**
 * Resolves to `'timeout'` after `ms`. Wrapped here (rather than inlined) so
 * the timer's `unref()` is consistently applied — we don't want the drain
 * race to be the thing keeping the event loop alive on its own.
 *
 * Uses the global `setTimeout` (not `node:timers/promises`) because vitest's
 * fake timers patch the global by default but do not patch
 * `node:timers/promises.setTimeout` — keeping this deterministically testable.
 */
function timeoutSignal(ms: number): Promise<'timeout'> {
  return new Promise<'timeout'>((resolve) => {
    const t = setTimeout(() => resolve('timeout'), ms)
    t.unref?.()
  })
}
