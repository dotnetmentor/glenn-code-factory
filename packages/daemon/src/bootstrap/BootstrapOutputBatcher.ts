// BootstrapOutputBatcher — batches stdout/stderr lines from a long-running
// source (install / setup bash, or a supervised service's log files during
// the starting-services window) and emits them as a chunked RuntimeEvent on
// a time- and size-bounded cadence.
//
// === Why ===
//
// During cold-boot the daemon spawns long-running bash for the `install`
// stage (e.g. `mise install dotnet@9 && mise install node@22`) and the
// `running-setup` stage (e.g. `dotnet restore && dotnet build`). Those
// commands can grind for 5–10 minutes. The stages already emit the bookend
// `InstallStarted` / `SetupCommandStarted` + matching `Completed`/`Failed`
// events, but during the long middle the operator sees radio silence on the
// Timeline tab. This batcher fills the gap: it captures every line the
// bash process prints and pushes it up as a chunked RuntimeEvent that the
// Timeline tab can render as an expandable row.
//
// The `starting-services` stage reuses the same machinery (with
// `eventType: 'ServiceOutputChunk'` and `extraPayload: { serviceName }`) to
// stream a supervised service's stdout/stderr during the bootstrap window
// — same cadence, same sanitization, same event-payload shape modulo the
// stage/service identifier.
//
// === Cadence ===
//
//   - Time trigger: flush whenever 2 seconds have passed since the buffer
//     started accumulating (lazy timer — only running while there are lines
//     to flush).
//   - Size trigger: flush immediately when either the stdout or stderr buffer
//     reaches 50 lines. Whichever trigger fires first wins.
//
// One event is emitted per stream per flush — so a single flush typically
// yields one event (the dominant stream) or two (if both buffers have
// content). This keeps the per-event payload bounded (lines × ~80 chars ≈
// 4 KB) and well under the emitter's 16 KB payload cap.
//
// === Lifecycle ===
//
// The owning stage constructs one batcher per source at the start of its
// `run()`, wires `addStdoutLine` / `addStderrLine` into the executor's
// `onStdout` / `onStderr` callbacks (or into a file-tail), and calls
// `dispose()` in a `finally` block. `dispose()` does a final synchronous
// flush so the last partial chunk lands before the stage returns. After
// dispose the batcher is inert — further `add*` calls are no-ops.
//
// === Sanitization ===
//
// Empty lines and pure-whitespace lines are dropped (they're noise in a
// monospaced timeline row). Each line is trimmed of trailing whitespace and
// truncated to `MAX_LINE_CHARS` characters with a `…[truncated]` marker — so
// a misbehaving build that prints a megabyte progress bar can't blow the
// payload cap. The emitter has its own truncation pass as a second safety
// net, but doing it here means individual lines stay readable rather than
// the array getting silently halved.

import { RuntimeEventTypes, type RuntimeEventEmitter } from '../events/RuntimeEventEmitter.js'

export type BootstrapStageName = 'install' | 'setup'
export type BootstrapStreamName = 'stdout' | 'stderr'
export type BootstrapFlushReason = 'interval' | 'size'

/** Cadence — flush every 2 seconds while there are buffered lines. */
const FLUSH_INTERVAL_MS = 2_000
/** Per-stream buffer cap; whichever stream hits this first triggers an early flush. */
const MAX_LINES_PER_BUFFER = 50
/** Hard cap on a single line's length. Longer lines are truncated with a marker. */
const MAX_LINE_CHARS = 4_000
/** Truncation marker appended to over-long lines (counted toward the cap). */
const TRUNCATION_MARKER = '…[truncated]'

export interface BootstrapOutputBatcherDeps {
  /** Structured event emitter; one event per flush per stream. */
  emitter: RuntimeEventEmitter
  /**
   * RuntimeEvent type stamped on every emitted chunk. Defaults to
   * `BootstrapOutputChunk` for the install + setup callsites. The starting-
   * services stage passes `ServiceOutputChunk`.
   */
  eventType?: string
  /**
   * Extra fields merged into every chunk payload alongside
   * `{ stream, lines, batchedAt, lineCount, flushReason }`. The install + setup
   * callsites pass `{ stage: 'install' | 'setup' }`; the starting-services
   * stage passes `{ serviceName }`. Keep small — these get repeated on every
   * flushed event.
   */
  extraPayload?: Record<string, unknown>
  /**
   * Legacy convenience for the install + setup callsites. When provided AND
   * `extraPayload` is not, the batcher emits `{ stage }` into each chunk.
   * Newer callsites should prefer `extraPayload` directly.
   */
  stage?: BootstrapStageName
  /** Override the flush interval (ms). Defaults to {@link FLUSH_INTERVAL_MS}. Tests use this. */
  flushIntervalMs?: number
  /** Override the per-buffer line cap. Defaults to {@link MAX_LINES_PER_BUFFER}. Tests use this. */
  maxLinesPerBuffer?: number
  /** Override the wall clock used for the `batchedAt` ISO timestamp. Defaults to `() => new Date()`. */
  now?: () => Date
}

/**
 * Captures stdout/stderr lines from a long-running source and flushes them
 * upstream as chunked RuntimeEvents on a 2s / 50-line cadence. The default
 * configuration matches the original `BootstrapOutputChunk` behavior; pass
 * `eventType` + `extraPayload` to repurpose for the per-service tail. See
 * module header for full design.
 */
export class BootstrapOutputBatcher {
  readonly #emitter: RuntimeEventEmitter
  readonly #eventType: string
  readonly #extraPayload: Record<string, unknown>
  readonly #flushIntervalMs: number
  readonly #maxLinesPerBuffer: number
  readonly #now: () => Date

  #stdoutBuffer: string[] = []
  #stderrBuffer: string[] = []
  #timer: ReturnType<typeof setInterval> | undefined
  #disposed = false

  constructor(deps: BootstrapOutputBatcherDeps) {
    this.#emitter = deps.emitter
    this.#eventType = deps.eventType ?? RuntimeEventTypes.BootstrapOutputChunk
    // Honour the legacy `stage` shortcut so we don't have to churn the
    // install + setup callsites. Explicit `extraPayload` wins if both are
    // provided — that's the supervisord/service-tail callsite shape.
    if (deps.extraPayload !== undefined) {
      this.#extraPayload = deps.extraPayload
    } else if (deps.stage !== undefined) {
      this.#extraPayload = { stage: deps.stage }
    } else {
      this.#extraPayload = {}
    }
    this.#flushIntervalMs = deps.flushIntervalMs ?? FLUSH_INTERVAL_MS
    this.#maxLinesPerBuffer = deps.maxLinesPerBuffer ?? MAX_LINES_PER_BUFFER
    this.#now = deps.now ?? (() => new Date())
  }

  /**
   * Accept one stdout line. Empty / whitespace-only lines are dropped on the
   * floor. Long lines get truncated. If the stdout buffer hits the cap we
   * flush stdout immediately (stderr is untouched and waits for its own
   * trigger).
   */
  addStdoutLine(line: string): void {
    this.#addLine('stdout', line)
  }

  /**
   * Accept one stderr line. Empty / whitespace-only lines are dropped on the
   * floor. Long lines get truncated. If the stderr buffer hits the cap we
   * flush stderr immediately.
   */
  addStderrLine(line: string): void {
    this.#addLine('stderr', line)
  }

  /**
   * Final flush + tear down the interval timer. Idempotent. After this
   * returns, further `add*` calls become no-ops so a late-arriving stdout
   * event (e.g. from a child process that was still draining) can't resurrect
   * the batcher. Call this from a `finally` block in the owning stage so it
   * runs on both success and failure paths.
   */
  dispose(): void {
    if (this.#disposed) return
    this.#disposed = true
    this.#stopTimer()
    this.#flush('interval')
  }

  // ============================================================================
  // Internals
  // ============================================================================

  #addLine(stream: BootstrapStreamName, raw: string): void {
    if (this.#disposed) return
    const sanitized = this.#sanitize(raw)
    if (sanitized === null) return

    const buf = stream === 'stdout' ? this.#stdoutBuffer : this.#stderrBuffer
    buf.push(sanitized)
    this.#ensureTimer()

    if (buf.length >= this.#maxLinesPerBuffer) {
      this.#flushStream(stream, 'size')
    }
  }

  #sanitize(raw: string): string | null {
    if (typeof raw !== 'string') return null
    // Trim only trailing whitespace — leading indentation is meaningful in
    // tool output (npm install nests by package). Drop blank lines entirely;
    // they're noise in the monospaced timeline row.
    const trimmed = raw.replace(/\s+$/u, '')
    if (trimmed.length === 0) return null
    if (trimmed.length <= MAX_LINE_CHARS) return trimmed
    // Truncate from the head (we want the start of the line — that's where
    // the meaningful prefix lives for most tools). Reserve room for the
    // marker so the total stays at MAX_LINE_CHARS.
    const keep = MAX_LINE_CHARS - TRUNCATION_MARKER.length
    return trimmed.slice(0, keep) + TRUNCATION_MARKER
  }

  #ensureTimer(): void {
    if (this.#disposed) return
    if (this.#timer !== undefined) return
    // Lazy start: only run the interval while we have lines to flush. The
    // interval clears itself the first time it runs over empty buffers (see
    // #onIntervalTick).
    this.#timer = setInterval(() => {
      this.#onIntervalTick()
    }, this.#flushIntervalMs)
    // Don't keep the daemon's event loop alive just for this timer — if the
    // process is otherwise idle we want it to be able to exit. `unref` is
    // a no-op on environments that don't support it.
    if (typeof this.#timer === 'object' && this.#timer !== null &&
        'unref' in this.#timer && typeof this.#timer.unref === 'function') {
      this.#timer.unref()
    }
  }

  #stopTimer(): void {
    if (this.#timer === undefined) return
    clearInterval(this.#timer)
    this.#timer = undefined
  }

  #onIntervalTick(): void {
    // If both buffers are empty there's nothing to flush AND nothing to wait
    // for — stop the timer so we're not paying for a wake-up every 2s during
    // long quiescent stretches. The next `addLine` call will restart it.
    if (this.#stdoutBuffer.length === 0 && this.#stderrBuffer.length === 0) {
      this.#stopTimer()
      return
    }
    this.#flush('interval')
  }

  #flush(reason: BootstrapFlushReason): void {
    if (this.#stdoutBuffer.length > 0) this.#flushStream('stdout', reason)
    if (this.#stderrBuffer.length > 0) this.#flushStream('stderr', reason)
  }

  #flushStream(stream: BootstrapStreamName, reason: BootstrapFlushReason): void {
    const buf = stream === 'stdout' ? this.#stdoutBuffer : this.#stderrBuffer
    if (buf.length === 0) return
    // Snapshot then clear synchronously so a re-entrant `addLine` from inside
    // emit (extremely unlikely — emit is fire-and-forget — but defensive)
    // can't see the same lines twice.
    const lines = buf.splice(0, buf.length)
    const batchedAt = this.#now().toISOString()
    this.#emitter.emit(this.#eventType, 'Info', {
      ...this.#extraPayload,
      stream,
      lines,
      batchedAt,
      lineCount: lines.length,
      flushReason: reason,
    })
  }
}
