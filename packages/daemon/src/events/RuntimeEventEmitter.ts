// RuntimeEventEmitter — daemon-side fan-out of structured runtime events.
//
// === Spec context (runtime-spec-v2 "Event taxonomy" + "Timing — first-class concern") ===
//
// Every meaningful thing that happens inside a runtime (bootstrap stage, install
// snippet, service lifecycle transition, spec delta apply) is emitted as a
// `RuntimeEvent` envelope and pushed up to the backend via the SignalR hub
// method `RecordRuntimeEvent`. The backend persists each event to the
// `RuntimeEvents` table (capped at 5000 per runtime) so the Timeline tab in
// the runtime drawer can show the user what happened, when, and how long it
// took.
//
// === Design notes ===
//
//   - Best-effort observability. `emit()` returns synchronously and never
//     throws — a transport blip must NEVER fail the calling stage or service
//     handler. The actual hub invoke is fired in the background; failures are
//     logged at `warn` and dropped.
//
//   - Buffering when disconnected. SignalR may be down at boot (the orchestrator
//     waits in ConnectingStage) or mid-flight (reconnect window). We buffer up
//     to 200 events in a FIFO queue; on `onConnected` we drain the queue best-
//     effort. Past 200 we drop the oldest event and warn once per minute (so
//     spam doesn't fill the log). 200 is plenty for the longest bootstrap path
//     (16 distinct event types × a handful of stages) and small enough that an
//     unbounded daemon backlog can't OOM us.
//
//   - Severity defaults baked into the type-name suffix. `*Started` /
//     `*Completed` / `*Skipped` → Info; `*Failed` → Error. `startTimer` derives
//     the matching end-event severity automatically so call sites don't have to
//     remember which strings map to which severity.
//
//   - Timing as a first-class concern. `startTimer()` records `Date.now()` and
//     stamps `startedAt` on the started event's payload. The returned
//     `EventTimer.complete / fail / skip` auto-fills `durationMs` on the end
//     event. This is the only path call sites should use for paired events —
//     manual `emit()` is reserved for one-shot events (ServiceStarting,
//     ServiceRunning, ServiceCrashed, …) where the duration is computed across
//     async boundaries by the caller.

import type { Logger } from 'pino'

import type { SignalRClient } from '../signalr/SignalRClient.js'
import { RuntimeEventTypes } from './RuntimeEventTypes.js'

export type Severity = 'Info' | 'Warn' | 'Error'

/**
 * Wire envelope for the `RecordRuntimeEvent` hub method. The backend persists
 * this verbatim (after JSON-stringifying the payload) into the `RuntimeEvents`
 * table. RuntimeId is derived from the connection's auth on the backend — we
 * deliberately do NOT include it here.
 */
export interface RuntimeEventEnvelope {
  type: string
  severity: Severity
  /** ISO8601 timestamp; always emitted from the daemon's clock. */
  timestamp: string
  /** Optional duration in ms (for `*Completed` / `*Failed` / `*Skipped`). */
  durationMs?: number
  /** Free-form structured payload. Backend stores as jsonb. */
  payload: Record<string, unknown>
}

export interface EventTimer {
  /** Finish with a `*Completed`-style event (severity defaults to Info). */
  complete(endType: string, endPayload?: Record<string, unknown>): void
  /** Finish with a `*Failed`-style event (severity defaults to Error). */
  fail(endType: string, endPayload?: Record<string, unknown>): void
  /** Finish with a `*Skipped`-style event (severity defaults to Info). */
  skip(endType: string, endPayload?: Record<string, unknown>): void
}

export interface RuntimeEventEmitter {
  emit(
    type: string,
    severity: Severity,
    payload?: Record<string, unknown>,
  ): void
  startTimer(
    startType: string,
    startPayload?: Record<string, unknown>,
  ): EventTimer
}

// --------- Internal constants ---------

const DEFAULT_BUFFER_CAP = 200
/** How often the "buffer full, dropping oldest" warning may log. */
const BUFFER_WARN_INTERVAL_MS = 60_000
/** Hub method name on the .NET side (P3.2 in-flight). */
const HUB_METHOD = 'RecordRuntimeEvent'

/**
 * Per-event payload byte cap. The backend persists payload as JSONB on
 * `RuntimeEvents` (a 5000-row capped table per runtime). A single rogue event
 * carrying a megabyte of stderr would bloat the row, fail the SignalR
 * `MaximumReceiveMessageSize` (default 32KB), or both. We pre-serialise the
 * payload here and, if it exceeds this cap, truncate the longest array
 * property (the typical bloat vector — log tails, file lists) and stamp a
 * `_truncated: true` marker so the consumer can render "[…N more]" in the UI.
 */
const MAX_PAYLOAD_BYTES = 16 * 1024
/** Minimum length we'll truncate an array to. Smaller and the array becomes useless. */
const MIN_ARRAY_TRUNCATE_LENGTH = 2

/**
 * Default severity inference from the type-name suffix. Spec says:
 *   - `*Started` / `*Completed` / `*Skipped` → Info
 *   - `*Failed` → Error
 * Everything else falls back to Info so a new constant added to RuntimeEventTypes
 * doesn't crash the emitter — callers should pass an explicit severity when
 * the suffix-based inference is wrong.
 */
export function inferSeverity(type: string): Severity {
  if (type.endsWith('Failed') || type.endsWith('Crashed')) return 'Error'
  // Started / Completed / Skipped / Starting / Running / Restarted / Applied / …
  return 'Info'
}

/**
 * Cap the byte size of an event payload before it goes over the wire.
 *
 * Algorithm:
 *   1. Serialize the payload to JSON. If the byte length ≤ `maxBytes`, return
 *      as-is (no truncation, no marker).
 *   2. Find the longest array property (by JSON-stringified length of each
 *      array). This is the typical bloat vector — `stderrTailLines`,
 *      `outputTailLines`, batched item lists.
 *   3. Halve its length, never below `MIN_ARRAY_TRUNCATE_LENGTH`. Loop back
 *      to step 1.
 *   4. If no array is found and we're still over budget, stop trying — set
 *      `_truncated: true` and let the wire send what's there. The server's
 *      `MaximumReceiveMessageSize` will reject if catastrophically large,
 *      surfaced as the normal emit-failed warn.
 *
 * Exported for unit-testability — pure function, no side effects beyond an
 * optional debug log.
 */
export function capPayloadBytes(
  payload: Record<string, unknown>,
  maxBytes: number,
  logger?: Pick<Logger, 'debug'>,
  eventType?: string,
): Record<string, unknown> {
  const initialBytes = byteLength(JSON.stringify(payload))
  if (initialBytes <= maxBytes) return payload

  // Shallow clone — we mutate ONLY top-level array properties. Nested arrays
  // inside objects are left alone; the spec calls for "longest array" which
  // we interpret as longest top-level array property. That covers the
  // documented bloat vectors (stderrTailLines, outputTailLines, etc.).
  const out: Record<string, unknown> = { ...payload }
  let truncated = false
  let iterations = 0
  const MAX_ITERATIONS = 12 // halving any array bottoms out at MIN_ARRAY_TRUNCATE_LENGTH long before this
  while (byteLength(JSON.stringify(out)) > maxBytes) {
    iterations += 1
    if (iterations > MAX_ITERATIONS) break

    let longestKey: string | undefined
    let longestArrayBytes = 0
    for (const [key, value] of Object.entries(out)) {
      if (!Array.isArray(value)) continue
      if (value.length <= MIN_ARRAY_TRUNCATE_LENGTH) continue
      const bytes = byteLength(JSON.stringify(value))
      if (bytes > longestArrayBytes) {
        longestArrayBytes = bytes
        longestKey = key
      }
    }
    if (longestKey === undefined) break // nothing left to trim

    const arr = out[longestKey] as unknown[]
    const newLength = Math.max(MIN_ARRAY_TRUNCATE_LENGTH, Math.floor(arr.length / 2))
    out[longestKey] = arr.slice(0, newLength)
    truncated = true
  }

  if (truncated) {
    out['_truncated'] = true
    logger?.debug(
      {
        type: eventType,
        initialBytes,
        finalBytes: byteLength(JSON.stringify(out)),
        maxBytes,
      },
      'runtime event payload truncated to fit byte cap',
    )
  }
  return out
}

/**
 * Byte length of a string under UTF-8 encoding. Uses `Buffer.byteLength` so
 * multi-byte characters (a stderr line containing a `…` truncation marker, or
 * non-ASCII output from an i18n'd npm package) count correctly.
 */
function byteLength(s: string): number {
  return Buffer.byteLength(s, 'utf8')
}

export interface SignalRInvoker {
  invoke(method: string, payload: unknown): Promise<unknown>
  isConnected(): boolean
  onConnected(cb: () => void): void
  onDisconnected(cb: (err?: Error) => void): void
}

export interface RuntimeEventEmitterDeps {
  signalr: SignalRInvoker | SignalRClient
  logger: Logger
  /** Override buffer cap. Default 200. */
  bufferCap?: number
  /**
   * Monotonic clock for timer accounting + warning throttling. Defaulted to
   * `Date.now`; tests override for deterministic timing.
   */
  now?: () => number
}

/**
 * Concrete RuntimeEventEmitter. Wires onto a SignalRClient's lifecycle
 * listeners to detect (re)connect and drain its in-memory buffer.
 */
export class DefaultRuntimeEventEmitter implements RuntimeEventEmitter {
  readonly #signalr: SignalRInvoker
  readonly #logger: Logger
  readonly #bufferCap: number
  readonly #now: () => number

  /** FIFO queue of envelopes captured while disconnected. */
  readonly #buffer: RuntimeEventEnvelope[] = []
  /** Number of drops since last warning — reset when we log. */
  #droppedSinceLastWarn = 0
  /** Timestamp of last "buffer full" warning, for once-per-minute throttling. */
  #lastBufferWarnAt = 0

  constructor(deps: RuntimeEventEmitterDeps) {
    this.#signalr = deps.signalr as SignalRInvoker
    this.#logger = deps.logger.child({ module: 'runtime-event-emitter' })
    this.#bufferCap = deps.bufferCap ?? DEFAULT_BUFFER_CAP
    this.#now = deps.now ?? Date.now

    // Hook onto reconnect so we can drain the buffer best-effort. We do NOT
    // register a disconnected handler — emit() polls `isConnected()` per call,
    // which is both simpler and correct under race (a disconnected event might
    // arrive after the connection has already been re-established).
    try {
      this.#signalr.onConnected(() => {
        this.#drain()
      })
    } catch (err) {
      // Some test stubs don't implement onConnected; tolerate it.
      this.#logger.debug({ err }, 'signalr.onConnected wiring failed (test stub?)')
    }
  }

  emit(
    type: string,
    severity: Severity,
    payload: Record<string, unknown> = {},
  ): void {
    const envelope: RuntimeEventEnvelope = {
      type,
      severity,
      timestamp: new Date().toISOString(),
      payload,
    }
    this.#dispatch(envelope)
  }

  startTimer(
    startType: string,
    startPayload: Record<string, unknown> = {},
  ): EventTimer {
    const startedAtMs = this.#now()
    const startedAtIso = new Date().toISOString()
    // Stamp `startedAt` on the start payload so the backend (and any consumer
    // reading the raw payload jsonb) can correlate without joining rows.
    const payload = { ...startPayload, startedAt: startedAtIso }
    this.#dispatch({
      type: startType,
      severity: inferSeverity(startType),
      timestamp: startedAtIso,
      payload,
    })

    const finish = (
      endType: string,
      endPayload: Record<string, unknown> | undefined,
      severityOverride: Severity | undefined,
    ): void => {
      const durationMs = Math.max(0, this.#now() - startedAtMs)
      this.#dispatch({
        type: endType,
        severity: severityOverride ?? inferSeverity(endType),
        timestamp: new Date().toISOString(),
        durationMs,
        payload: endPayload ?? {},
      })
    }

    return {
      complete: (endType, endPayload) => finish(endType, endPayload, 'Info'),
      fail: (endType, endPayload) => finish(endType, endPayload, 'Error'),
      skip: (endType, endPayload) => finish(endType, endPayload, 'Info'),
    }
  }

  // ============================================================================
  // Internals
  // ============================================================================

  #dispatch(envelope: RuntimeEventEnvelope): void {
    if (this.#signalr.isConnected()) {
      // Best-effort: drop the promise on the floor with a `.catch` so an
      // unhandled rejection from a brief race (disconnected between the check
      // and the invoke) doesn't leak.
      this.#sendNow(envelope)
      return
    }
    this.#enqueue(envelope)
  }

  #sendNow(envelope: RuntimeEventEnvelope): void {
    // Server's `RuntimeEventPayloadDto.Payload` is a `string` (the persisted
    // column is jsonb but the wire shape is pre-serialized JSON — the
    // command handler stores it verbatim). If we pass `payload` as an object
    // here, SignalR's argument binder sees JsonElement-vs-string mismatch and
    // rejects the whole invoke with `InvalidDataException: Error binding
    // arguments` — every emit fails on every call. Stringify at the wire
    // boundary so the type the server signature expects is exactly what
    // arrives. The local TS shape stays as `Record<string, unknown>` for
    // ergonomic in-process use (tests assert on the structured object).
    //
    // Payload byte cap: serialize first, check size, and if too large iterate
    // truncating the longest array property until we fit (or there's no
    // array left to trim — at which point we send what we have and accept
    // the wire-side risk). See `capPayloadBytes` for the algorithm.
    const cappedPayload = capPayloadBytes(
      envelope.payload ?? {},
      MAX_PAYLOAD_BYTES,
      this.#logger,
      envelope.type,
    )
    const wire = {
      type: envelope.type,
      severity: envelope.severity,
      timestamp: envelope.timestamp,
      durationMs: envelope.durationMs,
      payload: JSON.stringify(cappedPayload),
    }
    this.#signalr.invoke(HUB_METHOD, wire).catch((err: unknown) => {
      this.#logger.warn(
        { err, type: envelope.type },
        'failed to emit runtime event',
      )
    })
  }

  #enqueue(envelope: RuntimeEventEnvelope): void {
    if (this.#buffer.length >= this.#bufferCap) {
      // Drop oldest. FIFO: shift the head, push the new tail.
      this.#buffer.shift()
      this.#droppedSinceLastWarn += 1
      this.#maybeWarnDropped()
    }
    this.#buffer.push(envelope)
  }

  #drain(): void {
    if (this.#buffer.length === 0) return
    this.#logger.debug(
      { bufferedEvents: this.#buffer.length },
      'draining buffered runtime events on (re)connect',
    )
    // Snapshot then clear — if a `sendNow` somehow re-buffers (it shouldn't
    // because we just got onConnected), we won't enter an infinite loop.
    const pending = this.#buffer.splice(0, this.#buffer.length)
    for (const envelope of pending) {
      this.#sendNow(envelope)
    }
  }

  #maybeWarnDropped(): void {
    const now = this.#now()
    if (now - this.#lastBufferWarnAt < BUFFER_WARN_INTERVAL_MS) return
    this.#logger.warn(
      {
        droppedEvents: this.#droppedSinceLastWarn,
        bufferCap: this.#bufferCap,
      },
      'runtime event buffer full; dropped oldest events',
    )
    this.#lastBufferWarnAt = now
    this.#droppedSinceLastWarn = 0
  }
}

// Re-export the canonical type-name constants so call sites can do a single
// import: `import { RuntimeEventTypes, type RuntimeEventEmitter } from '../events/RuntimeEventEmitter.js'`
export { RuntimeEventTypes } from './RuntimeEventTypes.js'

// ============================================================================
// Test helper — a recording emitter for assertion-based tests in stages /
// the applier. Lives next to the production class so consumers can import it
// from the same module under test without dragging in a separate helper file.
// ============================================================================

/**
 * Test double that records every emitted event into an array. Identical
 * surface to the production emitter; `events` is exposed for assertions.
 *
 * Usage:
 *   const recorder = new TestRuntimeEventEmitter()
 *   await stage.run({ ..., emitter: recorder })
 *   expect(recorder.events.map(e => e.type)).toEqual([
 *     'InstallStarted', 'InstallCompleted',
 *   ])
 */
export class TestRuntimeEventEmitter implements RuntimeEventEmitter {
  /** Every dispatched envelope, in order. */
  readonly events: RuntimeEventEnvelope[] = []

  emit(
    type: string,
    severity: Severity,
    payload: Record<string, unknown> = {},
  ): void {
    this.events.push({
      type,
      severity,
      timestamp: new Date().toISOString(),
      payload,
    })
  }

  startTimer(
    startType: string,
    startPayload: Record<string, unknown> = {},
  ): EventTimer {
    const startedAtMs = Date.now()
    const startedAtIso = new Date().toISOString()
    this.events.push({
      type: startType,
      severity: inferSeverity(startType),
      timestamp: startedAtIso,
      payload: { ...startPayload, startedAt: startedAtIso },
    })

    const finish = (
      endType: string,
      endPayload: Record<string, unknown> | undefined,
      severityOverride: Severity,
    ): void => {
      this.events.push({
        type: endType,
        severity: severityOverride,
        timestamp: new Date().toISOString(),
        durationMs: Math.max(0, Date.now() - startedAtMs),
        payload: endPayload ?? {},
      })
    }

    return {
      complete: (endType, endPayload) => finish(endType, endPayload, 'Info'),
      fail: (endType, endPayload) => finish(endType, endPayload, 'Error'),
      skip: (endType, endPayload) => finish(endType, endPayload, 'Info'),
    }
  }

  /** Clear recorded events between test phases. */
  reset(): void {
    this.events.length = 0
  }
}
