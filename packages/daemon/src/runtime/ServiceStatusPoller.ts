// ServiceStatusPoller — long-lived poller that emits ServiceCrashed /
// ServiceRestarted / ServiceFailedToStart events when supervisord state
// transitions cross meaningful boundaries, and pushes a live supervisord
// snapshot up to the backend on every tick.
//
// === Spec context (runtime-spec-v2 "Event taxonomy" + runtime-observability-super-admin B1/B2/B3/B4) ===
//
// Once a service is up, the user wants to know if it crashes (and how long it
// was up before the crash), and if supervisord restarts it (and how many times
// it's restarted in this runtime's lifetime). Neither is observable from the
// existing `*Starting` / `*Running` events emitted at boot — those fire once
// per service per bootstrap. The poller fills the gap by polling supervisord
// on a configurable interval and emitting the matching runtime event whenever
// a transition is detected. Per the observability super-admin spec it also:
//
//   - Polls supervisord's XML-RPC interface (Section B1) rather than the
//     `supervisorctl status` text path. The XML-RPC response carries the
//     fields the text-parse can't: `exitstatus`, `spawnerr`, `pid`,
//     `stdout_logfile`, `stderr_logfile`.
//   - Emits `ServiceFailedToStart` on STARTING|BACKOFF → FATAL when the
//     service has never reached RUNNING (Section B2).
//   - Populates `exitCode` on `ServiceCrashed` from `exitstatus` (Section B3).
//   - Attaches the last 50 stderr lines to FATAL / crash events as
//     `stderrTailLines` (Section B4).
//   - Pushes a fresh `LiveSupervisordSnapshot` upstream every tick so the
//     drawer's Services tab renders FATAL / BACKOFF / STOPPED states the
//     event stream can't carry alone.
//
// === Why a separate poller (not bolted onto StartingServicesStage) ===
//
// StartingServicesStage runs once during bootstrap and exits when every
// service reaches RUNNING. The crash/restart events need a long-lived watcher
// that runs for the daemon's entire lifetime. Putting it in StartingServicesStage
// would mean a never-exiting bootstrap stage; making the poller a peer in
// `main.ts` is the cleaner factoring.
//
// === Transition logic ===
//
// `detectTransitions(prev, current)` is a PURE function exported below.
// Given the previous tracker snapshot and the current statename it returns
// the set of side-effects to apply (events to emit + tracker mutations).
// Pure-function form so the rule set is unit-testable in isolation; the
// poller's `#applyTransitions` step folds the result into the live tracker
// map and emits each event with whatever XML-RPC fields it needs.
//
//   - RUNNING → BACKOFF / EXITED / FATAL          → ServiceCrashed
//   - STARTING / BACKOFF → FATAL (never ran)      → ServiceFailedToStart
//   - BACKOFF / STARTING → RUNNING (after RUNNING)→ ServiceRestarted
//
// We keep a snapshot of `name → tracker` from the previous tick and compare.
// The first tick has no prior snapshot so no transitions fire (this avoids a
// spurious ServiceRestarted on daemon boot when supervisord already has every
// service in RUNNING from StartingServicesStage). We also track:
//
//   - lastRunningStartMs per service — for uptimeMs on ServiceCrashed and
//     to gate ServiceFailedToStart (which only fires when the service has
//     NEVER reached RUNNING — a service that ran once then later goes FATAL
//     is a Crashed event, not a FailedToStart).
//   - restartCount per service — monotonic counter for ServiceRestarted.
//   - attemptCount per service — startTransitions observed since seed; folds
//     into the FailedToStart payload so the drawer can show "tried 3 times".
//
// === Lifecycle ===
//
// `start()` kicks off a setInterval loop. `stop()` clears the interval and
// any in-flight poll resolves naturally (we don't cancel mid-call because
// XML-RPC is fast and the polled state isn't going anywhere).

import type { Logger } from 'pino'

import type { RuntimeEventEmitter } from '../events/RuntimeEventEmitter.js'
import { RuntimeEventTypes } from '../events/RuntimeEventTypes.js'
import { readLogTail } from '../logs/readLogTail.js'
import type {
  SupervisordProcessInfo,
  SupervisordXmlRpcClient,
} from '../supervisord/SupervisordXmlRpcClient.js'
import type { SignalRClient } from '../signalr/SignalRClient.js'

const DEFAULT_INTERVAL_MS = 10_000

/** Per-service tracker state. */
export interface ServiceTracker {
  /** Last observed state name from supervisord (RUNNING / STARTING / …). */
  lastState: string
  /** Monotonic clock when the service most recently entered RUNNING (used for uptime on crash, and as the "ever ran" flag). */
  lastRunningStartMs: number | null
  /** How many times this service has been restarted (RUNNING after non-RUNNING-after-RUNNING). */
  restartCount: number
  /** Number of start attempts (STARTING transitions) observed for this service. */
  attemptCount: number
}

/**
 * A single transition discovered by {@link detectTransitions}. The poller
 * folds these into its live tracker map and emits the matching runtime
 * event. Keeping the rule set side-effect-free makes the transition logic
 * testable without spinning up a SignalR connection.
 */
export type ServiceTransition =
  | {
      kind: 'crashed'
      serviceName: string
      previousState: string
      newState: string
      uptimeMs: number | null
    }
  | {
      kind: 'failed-to-start'
      serviceName: string
      previousState: string
      attemptCount: number
    }
  | {
      kind: 'restarted'
      serviceName: string
      previousState: string
      restartCount: number
    }

const CRASH_STATES = new Set(['BACKOFF', 'EXITED', 'FATAL'])

/**
 * PURE — compute the transition (if any) implied by a `prev → current` state
 * change for a single service. Returns the transition descriptor plus the
 * mutations to apply to the tracker. The caller is responsible for applying
 * the mutations and (separately) emitting the event.
 *
 * Splitting the rule set out as a pure function lets the unit test cover
 * every edge case without the indirection of a fake emitter — exactly the
 * shape the observability spec calls for ("extract a pure
 * `detectTransitions(prev, current)` first, zero behaviour change").
 */
export function detectTransitions(
  prev: ServiceTracker,
  currentState: string,
  nowMs: number,
  serviceName: string,
): {
  transition: ServiceTransition | null
  /**
   * Patch to merge into the tracker AFTER returning from this function. The
   * caller applies it; we return rather than mutate so the function stays
   * referentially transparent for the unit tests.
   */
  trackerPatch: Partial<ServiceTracker>
} {
  // Snapshot the tracker fields we'll need below — the caller's `prev`
  // reference is the live tracker, so reading first / patching last is the
  // safe order.
  const lastState = prev.lastState
  const lastRunningStartMs = prev.lastRunningStartMs
  const restartCount = prev.restartCount
  const attemptCount = prev.attemptCount

  // 1. RUNNING → bad state == crash.
  if (lastState === 'RUNNING' && CRASH_STATES.has(currentState)) {
    const uptimeMs =
      lastRunningStartMs !== null ? Math.max(0, nowMs - lastRunningStartMs) : null
    return {
      transition: {
        kind: 'crashed',
        serviceName,
        previousState: lastState,
        newState: currentState,
        uptimeMs,
      },
      trackerPatch: {
        lastState: currentState,
        // Crash invalidates the running-start reference.
        lastRunningStartMs: null,
      },
    }
  }

  // 2. STARTING / BACKOFF → FATAL on a service that has never reached
  // RUNNING. This is the "service is wedged" case — supervisord exhausted
  // its startretries and gave up. Only fire when `lastRunningStartMs` has
  // never been set (the "ever ran" flag).
  if (
    currentState === 'FATAL' &&
    (lastState === 'STARTING' || lastState === 'BACKOFF') &&
    lastRunningStartMs === null
  ) {
    return {
      transition: {
        kind: 'failed-to-start',
        serviceName,
        previousState: lastState,
        attemptCount,
      },
      trackerPatch: {
        lastState: currentState,
      },
    }
  }

  // 3. bad state → RUNNING (post-RUNNING-once) == restart.
  if (
    currentState === 'RUNNING' &&
    lastState !== 'RUNNING' &&
    lastRunningStartMs === null &&
    restartCount === 0 &&
    CRASH_STATES.has(lastState)
  ) {
    // First-ever restart path (RUNNING → crashed → RUNNING).
    return {
      transition: {
        kind: 'restarted',
        serviceName,
        previousState: lastState,
        restartCount: restartCount + 1,
      },
      trackerPatch: {
        lastState: currentState,
        lastRunningStartMs: nowMs,
        restartCount: restartCount + 1,
      },
    }
  }

  if (
    currentState === 'RUNNING' &&
    lastState !== 'RUNNING' &&
    lastRunningStartMs === null
  ) {
    // Subsequent restarts (or first restart from STARTING that we caught
    // before a crash state). Treat any transition into RUNNING from a
    // non-RUNNING-no-uptime state as a restart so the user sees the bounce.
    return {
      transition: {
        kind: 'restarted',
        serviceName,
        previousState: lastState,
        restartCount: restartCount + 1,
      },
      trackerPatch: {
        lastState: currentState,
        lastRunningStartMs: nowMs,
        restartCount: restartCount + 1,
      },
    }
  }

  // 4. STARTING bookkeeping — bump the attempt counter so a future
  // FailedToStart event can report it. We also bump on prev !== STARTING
  // → STARTING transitions; staying in STARTING across ticks doesn't count
  // a fresh attempt (it's the same attempt still warming up).
  let attemptPatch: Partial<ServiceTracker> = {}
  if (currentState === 'STARTING' && lastState !== 'STARTING') {
    attemptPatch = { attemptCount: attemptCount + 1 }
  }

  // Default — no transition, just record the new state (and the optional
  // attempt-count bump above).
  return {
    transition: null,
    trackerPatch: { lastState: currentState, ...attemptPatch },
  }
}

/**
 * Wire-shape for the live supervisord snapshot pushed up to the backend on
 * every poll tick. Mirrors the C# `LiveSupervisordSnapshotPayload` record.
 * NOT persisted — the backend forwards through the `runtime-events:{id}`
 * group; consumers cache the latest snapshot and replace it on every push.
 */
export interface LiveSupervisordSnapshotProcess {
  name: string
  state: string
  pid: number
  uptimeMs: number | null
  exitStatus: number | null
  spawnErr: string | null
  startedAt: string | null
}

export interface LiveSupervisordSnapshotPayload {
  sampledAt: string
  processes: LiveSupervisordSnapshotProcess[]
}

export interface ServiceStatusPollerDeps {
  /** Supervisord XML-RPC client — replaces the legacy text-parse path. */
  supervisord: SupervisordXmlRpcClient
  emitter: RuntimeEventEmitter
  logger: Logger
  /**
   * SignalR client for pushing the live snapshot upstream. Optional so older
   * tests / call sites that don't wire signalr don't break — when absent the
   * snapshot push is silently skipped (events still emit).
   */
  signalr?: Pick<SignalRClient, 'invoke' | 'isConnected'>
  /** Override poll interval (default 10s). Tests use shorter intervals. */
  intervalMs?: number
  /** Monotonic clock — `Date.now` in production, deterministic in tests. */
  now?: () => number
  /** `setInterval` shim — tests can inject a controllable scheduler. */
  setInterval?: (cb: () => void, ms: number) => unknown
  /** `clearInterval` paired with the shim above. */
  clearInterval?: (handle: unknown) => void
  /** Tail-reader override for tests; defaults to `readLogTail`. */
  readLogTail?: typeof readLogTail
}

export class ServiceStatusPoller {
  readonly #supervisord: SupervisordXmlRpcClient
  readonly #emitter: RuntimeEventEmitter
  readonly #signalr: Pick<SignalRClient, 'invoke' | 'isConnected'> | undefined
  readonly #logger: Logger
  readonly #intervalMs: number
  readonly #now: () => number
  readonly #setInterval: (cb: () => void, ms: number) => unknown
  readonly #clearInterval: (handle: unknown) => void
  readonly #readLogTail: typeof readLogTail

  /** name → tracker. Populated lazily on first observation of each service. */
  readonly #trackers = new Map<string, ServiceTracker>()
  /** name → latest XML-RPC info (for snapshot push + payload enrichment). */
  readonly #latestInfo = new Map<string, SupervisordProcessInfo>()
  /** Set once `start()` has been called so a `stop()` mid-poll is a clean no-op. */
  #handle: unknown = null
  /** True after the first poll has populated the initial snapshot (no transitions on the very first tick). */
  #seededInitial = false
  /** Re-entrancy guard so a slow XML-RPC call doesn't have two pollers stomping each other. */
  #polling = false

  constructor(deps: ServiceStatusPollerDeps) {
    this.#supervisord = deps.supervisord
    this.#emitter = deps.emitter
    this.#signalr = deps.signalr
    this.#logger = deps.logger.child({ module: 'service-status-poller' })
    this.#intervalMs = deps.intervalMs ?? DEFAULT_INTERVAL_MS
    this.#now = deps.now ?? Date.now
    this.#setInterval =
      deps.setInterval ?? ((cb, ms) => setInterval(cb, ms) as unknown)
    this.#clearInterval =
      deps.clearInterval ??
      ((handle: unknown) => clearInterval(handle as Parameters<typeof clearInterval>[0]))
    this.#readLogTail = deps.readLogTail ?? readLogTail
  }

  /**
   * Begin the polling loop. Subsequent calls are a no-op while the poller is
   * running. Safe to call after `stop()` — restarts a fresh loop.
   */
  start(): void {
    if (this.#handle !== null) return
    this.#handle = this.#setInterval(() => {
      void this.#pollOnce().catch((err: unknown) => {
        // Defence-in-depth — #pollOnce already catches everything internally
        // but a future refactor might let one through.
        this.#logger.warn({ err }, 'service status poll failed')
      })
    }, this.#intervalMs)
    this.#logger.info({ intervalMs: this.#intervalMs }, 'service status poller started')
  }

  /**
   * Stop the polling loop. Subsequent `start()` calls reset the loop.
   * Idempotent.
   */
  stop(): void {
    if (this.#handle === null) return
    this.#clearInterval(this.#handle)
    this.#handle = null
    this.#logger.info('service status poller stopped')
  }

  /**
   * Run one poll cycle. Exposed for tests so they can drive the poller
   * deterministically without setInterval timing flakes.
   */
  async pollOnce(): Promise<void> {
    await this.#pollOnce()
  }

  async #pollOnce(): Promise<void> {
    if (this.#polling) {
      // Skip overlapping ticks. We'll catch up on the next interval.
      this.#logger.debug('service status poll skipped (previous poll still in flight)')
      return
    }
    this.#polling = true
    try {
      const infos = await this.#readProcessInfos()
      const nowMs = this.#now()

      // Refresh latest-info map. Useful for the snapshot push below and for
      // pulling `stderr_logfile` / `exitstatus` / `spawnerr` at emit time.
      this.#latestInfo.clear()
      for (const info of infos) this.#latestInfo.set(info.name, info)

      // Bootstrap the trackers on the first observation: snapshot only, no
      // transitions emitted. This prevents a false "service restarted" on
      // daemon boot when supervisord already has everything in RUNNING from
      // StartingServicesStage.
      if (!this.#seededInitial) {
        for (const info of infos) {
          this.#trackers.set(info.name, {
            lastState: info.statename,
            // If the service is already RUNNING when we seed, we don't know
            // its actual start time. Use the current poll moment as the
            // reference point so a subsequent crash has *some* uptime to
            // report (lower-bound but honest).
            lastRunningStartMs: info.statename === 'RUNNING' ? nowMs : null,
            restartCount: 0,
            attemptCount: 0,
          })
        }
        this.#seededInitial = true
        await this.#pushSnapshot(infos, nowMs)
        return
      }

      for (const info of infos) {
        const prev = this.#trackers.get(info.name)
        if (prev === undefined) {
          // New service appeared mid-runtime (probably a freshly-applied
          // delta). Seed it without emitting transitions — the
          // RuntimeSpecApplier already emitted the SpecDeltaApplied + the
          // StartingServicesStage equivalent isn't running for live deltas.
          this.#trackers.set(info.name, {
            lastState: info.statename,
            lastRunningStartMs: info.statename === 'RUNNING' ? nowMs : null,
            restartCount: 0,
            attemptCount: 0,
          })
          continue
        }

        const { transition, trackerPatch } = detectTransitions(
          prev,
          info.statename,
          nowMs,
          info.name,
        )
        // Apply the tracker patch first so a re-entrant emit can't see a
        // stale `lastState`.
        if (trackerPatch.lastState !== undefined) prev.lastState = trackerPatch.lastState
        if (trackerPatch.lastRunningStartMs !== undefined)
          prev.lastRunningStartMs = trackerPatch.lastRunningStartMs
        if (trackerPatch.restartCount !== undefined)
          prev.restartCount = trackerPatch.restartCount
        if (trackerPatch.attemptCount !== undefined)
          prev.attemptCount = trackerPatch.attemptCount

        if (transition !== null) {
          await this.#emitTransition(transition, info)
        }
      }

      // Push the live snapshot upstream every tick. Even with no transitions
      // the snapshot is fresh data the drawer needs to render FATAL /
      // BACKOFF / STOPPED states the event stream alone can't carry.
      await this.#pushSnapshot(infos, nowMs)

      // We deliberately do NOT prune trackers when a service disappears from
      // supervisord output — Phase-1 policy is "removed services keep their
      // supervisord program running" (see RuntimeSpecApplier comment). If the
      // service genuinely vanishes (e.g. someone hand-deletes the conf), we
      // keep the tracker so a subsequent re-add picks up its history rather
      // than resetting to zero restarts. Memory cost is bounded by the number
      // of services in this runtime's lifetime, which is small.
    } finally {
      this.#polling = false
    }
  }

  async #emitTransition(
    transition: ServiceTransition,
    info: SupervisordProcessInfo,
  ): Promise<void> {
    switch (transition.kind) {
      case 'crashed': {
        // Try to attach a stderr tail — best-effort, the helper returns
        // empty on any read failure so we never block the emit.
        const stderrTailLines =
          info.stderr_logfile.length > 0 ? await this.#readLogTail(info.stderr_logfile) : []
        const payload: Record<string, unknown> = {
          serviceName: transition.serviceName,
          previousState: transition.previousState,
          newState: transition.newState,
          // Populate exitCode from XML-RPC's exitstatus. `0` is meaningful
          // (clean exit) so we DO NOT coerce zero to null. Supervisord
          // returns 0 for "never exited"; we still pass it through — the
          // pairing with a non-RUNNING newState makes the value meaningful.
          exitCode: info.exitstatus,
          ...(transition.uptimeMs !== null ? { uptimeMs: transition.uptimeMs } : {}),
        }
        if (info.spawnerr.length > 0) payload['spawnErr'] = info.spawnerr
        if (stderrTailLines.length > 0) payload['stderrTailLines'] = stderrTailLines
        this.#emitter.emit(RuntimeEventTypes.ServiceCrashed, 'Error', payload)
        return
      }
      case 'failed-to-start': {
        const stderrTailLines =
          info.stderr_logfile.length > 0 ? await this.#readLogTail(info.stderr_logfile) : []
        const payload: Record<string, unknown> = {
          serviceName: transition.serviceName,
          attemptCount: transition.attemptCount,
          finalState: 'FATAL',
          exitStatus: info.exitstatus,
          spawnErr: info.spawnerr.length > 0 ? info.spawnerr : null,
          stderrTailLines,
        }
        this.#emitter.emit(RuntimeEventTypes.ServiceFailedToStart, 'Error', payload)
        return
      }
      case 'restarted': {
        this.#emitter.emit(RuntimeEventTypes.ServiceRestarted, 'Info', {
          serviceName: transition.serviceName,
          previousState: transition.previousState,
          restartCount: transition.restartCount,
        })
        return
      }
    }
  }

  async #pushSnapshot(infos: SupervisordProcessInfo[], nowMs: number): Promise<void> {
    if (this.#signalr === undefined) return
    if (!this.#signalr.isConnected()) return
    const sampledAtIso = new Date(nowMs).toISOString()
    const processes: LiveSupervisordSnapshotProcess[] = infos.map((info) => ({
      name: info.name,
      state: info.statename,
      pid: info.pid,
      // uptimeMs: derived from XML-RPC's start/now epoch seconds. Both are
      // 0 when the process has never started; treat that as null so the
      // consumer can render "—" instead of a nonsense duration.
      uptimeMs:
        info.start > 0 && info.now >= info.start
          ? Math.max(0, (info.now - info.start) * 1000)
          : null,
      exitStatus: info.exitstatus,
      spawnErr: info.spawnerr.length > 0 ? info.spawnerr : null,
      startedAt: info.start > 0 ? new Date(info.start * 1000).toISOString() : null,
    }))
    const payload: LiveSupervisordSnapshotPayload = {
      sampledAt: sampledAtIso,
      processes,
    }
    try {
      // Hub method name (camelCase server signature). The backend resolves
      // the runtime id from the connection's signed claim so we deliberately
      // do not include it on the wire.
      await this.#signalr.invoke('PushLiveSupervisordSnapshot', payload)
    } catch (err) {
      // Live snapshot is best-effort observability — a transport hiccup is
      // benign because the next tick will push a fresher snapshot anyway.
      this.#logger.debug({ err }, 'PushLiveSupervisordSnapshot invoke failed (ignored)')
    }
  }

  /**
   * Issue `getAllProcessInfo`. Returns the array of structs, or an empty
   * array on any transport / parse failure (logged at debug so the poll
   * loop stays quiet during expected supervisord-not-ready windows).
   */
  async #readProcessInfos(): Promise<SupervisordProcessInfo[]> {
    try {
      return await this.#supervisord.getAllProcessInfo()
    } catch (err) {
      this.#logger.debug({ err }, 'supervisord getAllProcessInfo failed; treating as no-info')
      return []
    }
  }
}
