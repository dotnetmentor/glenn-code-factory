// OutputTailBuffer — bounded ring-buffer for capturing the tail of an in-flight
// child process's stdout+stderr stream.
//
// === Use case ===
//
// Setup-command failure events (`SetupCommandFailed`) and success events
// (`SetupCommandCompleted`) ride a short combined stdout+stderr tail on the
// event payload so the super-admin Timeline shows what happened without
// requiring the user to open the Logs tab. Unlike `readLogTail` (which reads
// from a file post-hoc), this one captures live output as it arrives via the
// executor's `onStdout`/`onStderr` callbacks.
//
// === Algorithm ===
//
// 1. Each `pushStdout(chunk)` / `pushStderr(chunk)` splits the chunk on `\n`
//    and appends each line to a FIFO buffer with a `[stdout]`/`[stderr]`
//    prefix so the combined tail preserves the interleave.
// 2. Trailing fragments without a newline are held in a per-stream pending
//    buffer; the next chunk for that stream prepends them. On `take()` any
//    pending fragments are flushed as their own lines.
// 3. After every push we evict from the front until BOTH the line count
//    AND the total byte count are within their caps. Byte cap evicts more
//    aggressively than the line cap when individual lines are large.
// 4. `take()` returns the captured lines in arrival order (already
//    prefixed) and resets the buffer so it can be reused.
//
// === Defaults ===
//
// 100 lines, 24KB total bytes. Originally 30/8KB to match an earlier spec; the
// V8 bootstrap work showed that 30 lines often isn't enough to capture a real
// stack trace (a .NET startup crash routinely consumes 40-60 lines). 24KB still
// sits under the SignalR transport's 32KB MaximumReceiveMessageSize once the
// event envelope (type+severity+timestamp+rest of payload) is JSON-stringified,
// and the per-event byte cap in RuntimeEventEmitter will truncate the array
// further if needed. Tests can override.
//
// === Per-line cap ===
//
// A single pathological log line (e.g. a stack trace dump from a misbehaving
// `npm install`) shouldn't be able to consume the entire byte budget by
// itself. Lines longer than `maxLineBytes` (default 500) are truncated with
// a `… [truncated]` suffix mirroring `readLogTail`'s behaviour. The
// `[stdout]`/`[stderr]` prefix counts against the line's stored length.

const DEFAULT_LINE_COUNT = 100
const DEFAULT_MAX_TOTAL_BYTES = 24 * 1024
const DEFAULT_MAX_LINE_BYTES = 500

export interface OutputTailBufferOptions {
  /** Max number of lines retained. Default 30. */
  lineCount?: number
  /** Total byte cap across all retained lines (excluding `\n` separators). Default 8192. */
  maxTotalBytes?: number
  /** Per-line byte cap; longer lines get truncated + marked. Default 500. */
  maxLineBytes?: number
}

/**
 * Bounded in-memory tail buffer fed by `onStdout`/`onStderr` callbacks of an
 * in-flight child. Call `take()` to drain on completion / failure.
 */
export class OutputTailBuffer {
  readonly #lineCount: number
  readonly #maxTotalBytes: number
  readonly #maxLineBytes: number

  // FIFO of finalised lines (with `[stdout]`/`[stderr]` prefix already
  // applied). We track per-line byte costs in parallel so we don't have to
  // re-`.length` everything on every evict.
  readonly #lines: string[] = []
  readonly #lineBytes: number[] = []
  #totalBytes = 0

  // Per-stream pending fragment (no terminating newline yet).
  #pendingStdout = ''
  #pendingStderr = ''

  constructor(options: OutputTailBufferOptions = {}) {
    this.#lineCount = options.lineCount ?? DEFAULT_LINE_COUNT
    this.#maxTotalBytes = options.maxTotalBytes ?? DEFAULT_MAX_TOTAL_BYTES
    this.#maxLineBytes = options.maxLineBytes ?? DEFAULT_MAX_LINE_BYTES
  }

  pushStdout(chunk: string): void {
    this.#push(chunk, 'stdout')
  }

  pushStderr(chunk: string): void {
    this.#push(chunk, 'stderr')
  }

  /**
   * Drain the captured tail. Flushes any per-stream pending fragments first
   * so a child that exits mid-line still contributes its final partial line.
   * Resets internal state — calling `take()` twice yields `[]` the second
   * time.
   */
  take(): string[] {
    this.#flushPending('stdout')
    this.#flushPending('stderr')
    const out = this.#lines.slice()
    this.#lines.length = 0
    this.#lineBytes.length = 0
    this.#totalBytes = 0
    return out
  }

  // -------- internals --------

  #push(chunk: string, stream: 'stdout' | 'stderr'): void {
    if (chunk.length === 0) return
    // Combine with any pending fragment from a prior chunk on this stream.
    const combined =
      stream === 'stdout' ? this.#pendingStdout + chunk : this.#pendingStderr + chunk
    const parts = combined.split('\n')
    // Last part is the new pending fragment (empty if chunk ended in `\n`).
    const pending = parts.pop() ?? ''
    if (stream === 'stdout') this.#pendingStdout = pending
    else this.#pendingStderr = pending

    for (const raw of parts) {
      if (raw.length === 0) continue
      this.#enqueue(`[${stream}] ${this.#clampLine(raw)}`)
    }
  }

  #flushPending(stream: 'stdout' | 'stderr'): void {
    const pending = stream === 'stdout' ? this.#pendingStdout : this.#pendingStderr
    if (pending.length === 0) return
    this.#enqueue(`[${stream}] ${this.#clampLine(pending)}`)
    if (stream === 'stdout') this.#pendingStdout = ''
    else this.#pendingStderr = ''
  }

  #enqueue(line: string): void {
    const cost = line.length
    this.#lines.push(line)
    this.#lineBytes.push(cost)
    this.#totalBytes += cost
    this.#evict()
  }

  #evict(): void {
    // Evict from the front (oldest) until both caps are satisfied.
    while (
      this.#lines.length > this.#lineCount ||
      this.#totalBytes > this.#maxTotalBytes
    ) {
      const dropped = this.#lineBytes.shift() ?? 0
      this.#lines.shift()
      this.#totalBytes -= dropped
      // Defensive: don't let rounding drift `#totalBytes` below zero.
      if (this.#totalBytes < 0) this.#totalBytes = 0
      if (this.#lines.length === 0) break
    }
  }

  #clampLine(line: string): string {
    if (line.length <= this.#maxLineBytes) return line
    return line.slice(0, this.#maxLineBytes) + '… [truncated]'
  }
}
