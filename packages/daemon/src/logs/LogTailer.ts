// LogTailer — on-demand `tail -F` per supervisord service, streaming each line
// back through the SignalR client (runtime-spec-v2 Phase 5).
//
// === Why "on-demand" ===
//
// Service stdout/stderr is already captured to disk by supervisord at
// `/var/log/supervisor/<name>.log` (see SupervisordController.renderServiceBlock).
// Streaming every line back to the hub at all times would burn bandwidth and
// CPU for logs nobody is watching. Instead, the .NET hub asks the daemon to
// start a tail when a user opens the Logs tab (`StartLogTail`) and to stop it
// when they navigate away or the last subscriber drops (`StopLogTail`).
//
// === Ref-counted tails ===
//
// Multiple operators can open the same service's logs simultaneously. Each
// `StartLogTail` arrives independently, but spawning a `tail -F` per
// subscriber would multiply a single line into N redundant deliveries (the
// hub already fans-out to all subscribers — we send once, hub broadcasts).
// We ref-count: first `startTail` for a service spawns the child; subsequent
// `startTail`s just increment. Each `stopTail` decrements; on zero we tear
// the child down. This mirrors the contract described on
// `SignalRClient.onStartLogTail` / `onStopLogTail`.
//
// === Why `tail -F` (capital F) ===
//
//   - `-f` keeps the file descriptor open. If supervisord rotates the log
//     (`stdout_logfile_backups=3` triggers rotation at 10 MB, see
//     SupervisordController), `-f` keeps reading the old, now-renamed file
//     and never sees the new one.
//   - `-F` re-opens by name on rotation/truncation, which is what we want.
//
// === Lifecycle ===
//
//   - `startTail(name)`  — ref++; spawn `tail -F` if first.
//   - `stopTail(name)`   — ref--; SIGTERM the child if zero.
//   - `dispose()`        — SIGTERM every live tail. Called by ShutdownCoordinator
//                          so children don't outlive the daemon as zombies under
//                          the runtime container's PID 1 reaper.
//
// === Failure handling ===
//
// `tail -F` is robust by design — it survives missing files (it logs a
// warning and waits) and rotations. If `tail` itself dies (binary missing,
// killed externally), we log a warn and clear the entry; subsequent
// `startTail`s will attempt to respawn. We do NOT auto-respawn on death —
// that risks a tight loop if `tail` is permanently broken on the host. The
// next `StartLogTail` from the hub will heal it organically.
//
// === Backpressure ===
//
// stdout is read into a small line buffer; lines are forwarded synchronously
// to `onLine` (which awaits the SignalR send under the hood). If the hub is
// slow, the OS pipe buffer fills, `tail` blocks on write, and the kernel
// applies backpressure for free. We don't need internal queueing.
//
// === Path safety ===
//
// Service names come from the runtime spec, which is server-validated, but
// we still defence-in-depth: only allow `[A-Za-z0-9._-]+` in the filename
// segment. Anything else is rejected with a logged error so a malformed
// `StartLogTail` can't be coerced into reading `/etc/passwd` via a `..`
// segment. The hub already rejects bad names — this is belt-and-braces.

import { spawn as nodeSpawn, type ChildProcessByStdio } from 'node:child_process'
import type { Readable } from 'node:stream'

import type { Logger } from 'pino'

/**
 * Concrete child-process type for our `tail` spawn: stdin ignored, stdout +
 * stderr piped. Node infers this when `stdio: ['ignore', 'pipe', 'pipe']` is
 * passed; declaring it explicitly keeps the rest of the file readable.
 */
type TailChild = ChildProcessByStdio<null, Readable, Readable>

/** Where supervisord writes per-service logs. Mirrors SupervisordController. */
const LOG_DIR = '/var/log/supervisor'

/** Service-name allowlist — letters, digits, dot, underscore, dash. */
const SAFE_NAME_RE = /^[A-Za-z0-9._-]+$/

/** Time between SIGTERM and SIGKILL when tearing down a tail. */
const DEFAULT_KILL_ESCALATION_MS = 2_000

/** Single emitted log line, flowing toward the SignalR sink. */
export interface LogLineEvent {
  readonly serviceName: string
  readonly line: string
  /** ISO-8601 string. The composition root re-hydrates to a `Date` for the C# DTO. */
  readonly timestamp: string
}

/** Sink invoked once per tailed line. Wired to `signalr.sendServiceLogLine` in main.ts. */
export type LogLineSink = (event: LogLineEvent) => void | Promise<void>

export interface LogTailerOptions {
  /** Where each tailed line goes. Composition root binds to the SignalR client. */
  onLine: LogLineSink
  logger: Logger
  /**
   * Override the spawn function for tests. Defaults to `node:child_process.spawn`.
   * Tests pass a fake that returns a controllable child stub.
   */
  spawn?: typeof nodeSpawn
  /**
   * Override the log directory. Defaults to `/var/log/supervisor`. Tests
   * point this at a temp dir.
   */
  logDir?: string
  /**
   * ms between SIGTERM dispatch and SIGKILL escalation. Defaults to 2s — `tail`
   * is well-behaved and exits on SIGTERM essentially immediately, but the
   * escalation guards against pathological hangs.
   */
  killEscalationMs?: number
  /** Override the clock for deterministic tests. Defaults to `() => new Date().toISOString()`. */
  now?: () => string
  /**
   * Number of historical lines to dump from the end of the file when the tail
   * attaches. Defaults to 0 — service-log tails are "live from now" because
   * server-side history is fetched separately and replaying noisy startup
   * output would mislead the operator.
   *
   * The daemon-log tail (runtime-observability-super-admin) sets this to 200
   * so a super-admin opening the runtime drawer immediately sees the recent
   * daemon output without having to wait for the next line.
   */
  initialLines?: number
}

/** Per-service tail state. */
interface TailEntry {
  /** Number of outstanding `startTail` calls for this service. Decremented by `stopTail`. */
  refCount: number
  /** The spawned `tail -F` child, or null if the spawn failed. */
  child: TailChild | null
  /** Carry-over from a previous chunk that didn't end on a newline. */
  stdoutPartial: string
  /** Same for stderr. `tail -F` writes warnings (e.g. "file truncated") here. */
  stderrPartial: string
  /** True once we've issued SIGTERM — guards the SIGKILL escalation timer. */
  terminating: boolean
}

export class LogTailer {
  readonly #onLine: LogLineSink
  readonly #logger: Logger
  readonly #spawn: typeof nodeSpawn
  readonly #logDir: string
  readonly #killEscalationMs: number
  readonly #now: () => string
  readonly #initialLines: number

  /** name → tail entry. Absent ⇒ no active tail for that service. */
  readonly #tails = new Map<string, TailEntry>()

  /** Set on `dispose()`. Subsequent `startTail` calls become no-ops. */
  #disposed = false

  constructor(opts: LogTailerOptions) {
    this.#onLine = opts.onLine
    this.#logger = opts.logger.child({ module: 'log-tailer' })
    this.#spawn = opts.spawn ?? nodeSpawn
    this.#logDir = opts.logDir ?? LOG_DIR
    this.#killEscalationMs = opts.killEscalationMs ?? DEFAULT_KILL_ESCALATION_MS
    this.#now = opts.now ?? (() => new Date().toISOString())
    this.#initialLines = opts.initialLines ?? 0
  }

  /**
   * Begin tailing `<logDir>/<serviceName>.log`. Idempotent per call site —
   * each `startTail` increments the ref-count; only the first spawns the
   * underlying `tail -F`. Safe to call before the file exists; `tail -F` will
   * wait for it.
   */
  startTail(serviceName: string): void {
    if (this.#disposed) {
      this.#logger.debug({ serviceName }, 'startTail ignored (tailer disposed)')
      return
    }
    if (!SAFE_NAME_RE.test(serviceName)) {
      this.#logger.error({ serviceName }, 'startTail rejected: unsafe service name')
      return
    }

    const existing = this.#tails.get(serviceName)
    if (existing !== undefined) {
      existing.refCount += 1
      this.#logger.debug(
        { serviceName, refCount: existing.refCount },
        'startTail incremented existing tail',
      )
      return
    }

    const entry: TailEntry = {
      refCount: 1,
      child: null,
      stdoutPartial: '',
      stderrPartial: '',
      terminating: false,
    }
    this.#tails.set(serviceName, entry)

    const path = `${this.#logDir}/${serviceName}.log`
    let child: TailChild
    try {
      // `-n <initialLines>` — by default 0 ("live from now") for service-log
      // tails so we don't replay the last 10 lines (tail's default) with
      // misleading `Date.now()` timestamps. The daemon-log tail overrides to
      // 200 so a super-admin opening the runtime drawer immediately sees
      // recent daemon output instead of waiting for the next line.
      child = this.#spawn('tail', ['-n', String(this.#initialLines), '-F', path], {
        stdio: ['ignore', 'pipe', 'pipe'],
      }) as TailChild
    } catch (err) {
      this.#logger.error({ err, serviceName, path }, 'failed to spawn tail')
      this.#tails.delete(serviceName)
      return
    }

    entry.child = child
    this.#wireChild(serviceName, entry, child)
    this.#logger.info({ serviceName, path, pid: child.pid }, 'tail started')
  }

  /**
   * Decrement the ref-count for `serviceName`. When it hits zero, SIGTERM the
   * underlying `tail -F` (escalating to SIGKILL after `killEscalationMs`).
   * No-op if no tail is active for that service — the hub may issue a stale
   * StopLogTail after a daemon restart.
   */
  stopTail(serviceName: string): void {
    const entry = this.#tails.get(serviceName)
    if (entry === undefined) {
      this.#logger.debug({ serviceName }, 'stopTail ignored (no active tail)')
      return
    }
    entry.refCount -= 1
    if (entry.refCount > 0) {
      this.#logger.debug(
        { serviceName, refCount: entry.refCount },
        'stopTail decremented (still subscribers)',
      )
      return
    }
    this.#terminate(serviceName, entry)
  }

  /**
   * Tear down every active tail. Called by ShutdownCoordinator on SIGTERM/SIGINT
   * so child `tail` processes don't outlive the daemon. Idempotent.
   */
  dispose(): void {
    if (this.#disposed) return
    this.#disposed = true
    for (const [name, entry] of this.#tails) {
      this.#terminate(name, entry)
    }
    this.#logger.info({ count: this.#tails.size }, 'log tailer disposed')
  }

  /**
   * Wire stdout/stderr/exit handlers on a freshly-spawned tail child. Splits
   * incoming chunks into newline-terminated lines and forwards each through
   * `onLine`. Carries the trailing partial across chunks so a line that
   * straddles a read boundary still arrives intact.
   */
  #wireChild(
    serviceName: string,
    entry: TailEntry,
    child: TailChild,
  ): void {
    child.stdout.setEncoding('utf8')
    child.stderr.setEncoding('utf8')

    child.stdout.on('data', (chunk: string) => {
      entry.stdoutPartial = this.#processChunk(serviceName, entry.stdoutPartial, chunk)
    })

    child.stderr.on('data', (chunk: string) => {
      // `tail -F` writes informational notices to stderr, e.g.
      //   tail: '/var/log/supervisor/foo.log' has appeared; following new file
      // We log them at debug — the operator doesn't need to see them in the
      // Logs tab, and most are noise. Forwarding stderr lines as if they
      // were stdout would corrupt the live-log view.
      entry.stderrPartial = this.#drainStderr(serviceName, entry.stderrPartial, chunk)
    })

    child.on('error', (err) => {
      this.#logger.warn({ err, serviceName }, 'tail child errored')
    })

    child.on('close', (code, signal) => {
      // Flush any final partial line before forgetting the entry.
      if (entry.stdoutPartial.length > 0) {
        this.#emit(serviceName, entry.stdoutPartial)
        entry.stdoutPartial = ''
      }
      // Only delete if this entry is still the current one for the service —
      // a rapid stop+start sequence could have already replaced it. Compare by
      // identity to avoid clobbering the new entry.
      const current = this.#tails.get(serviceName)
      if (current === entry) {
        this.#tails.delete(serviceName)
      }
      const level = entry.terminating ? 'debug' : 'warn'
      this.#logger[level](
        { serviceName, code, signal, terminating: entry.terminating },
        'tail child exited',
      )
    })
  }

  /**
   * Append `chunk` to `partial`, split on newline, emit complete lines, and
   * return the new trailing partial (empty if `chunk` ended on a newline).
   */
  #processChunk(serviceName: string, partial: string, chunk: string): string {
    const combined = partial + chunk
    const lines = combined.split('\n')
    // Last element is the partial (everything after the final newline). If
    // `combined` ended on a newline, this slot is the empty string and we
    // emit nothing trailing — that's correct.
    const trailing = lines.pop() ?? ''
    for (const line of lines) {
      this.#emit(serviceName, line)
    }
    return trailing
  }

  /**
   * Same line-splitting as `#processChunk` but log-only — stderr from `tail`
   * is operational noise, not user-facing log content.
   */
  #drainStderr(serviceName: string, partial: string, chunk: string): string {
    const combined = partial + chunk
    const lines = combined.split('\n')
    const trailing = lines.pop() ?? ''
    for (const line of lines) {
      if (line.length > 0) {
        this.#logger.debug({ serviceName, line }, 'tail stderr')
      }
    }
    return trailing
  }

  /**
   * Push one line to the configured sink. Errors thrown from `onLine` are
   * caught and logged so a transient SignalR failure doesn't crash the
   * tailer — losing one log line is preferable to dropping the whole stream.
   */
  #emit(serviceName: string, line: string): void {
    const event: LogLineEvent = {
      serviceName,
      line,
      timestamp: this.#now(),
    }
    try {
      const result = this.#onLine(event)
      if (result instanceof Promise) {
        result.catch((err: unknown) => {
          this.#logger.warn({ err, serviceName }, 'onLine rejected (line dropped)')
        })
      }
    } catch (err) {
      this.#logger.warn({ err, serviceName }, 'onLine threw (line dropped)')
    }
  }

  /**
   * SIGTERM the child, then SIGKILL after `killEscalationMs` if it hasn't
   * exited. Marks the entry `terminating` so the `close` handler knows the
   * exit was intentional. Removes the entry from the map immediately so a
   * subsequent `startTail` for the same service spawns a fresh child rather
   * than ref-counting onto a dying one.
   */
  #terminate(serviceName: string, entry: TailEntry): void {
    if (entry.terminating) return
    entry.terminating = true
    // Remove from the map up-front. The `close` handler's identity check
    // handles the case where this race actually fires.
    this.#tails.delete(serviceName)

    const child = entry.child
    if (child === null) {
      this.#logger.debug({ serviceName }, 'terminate skipped (no child)')
      return
    }

    try {
      child.kill('SIGTERM')
    } catch (err) {
      this.#logger.warn({ err, serviceName }, 'SIGTERM failed')
    }

    const escalation = setTimeout(() => {
      try {
        // exitCode === null AND signalCode === null ⇒ still alive.
        if (child.exitCode === null && child.signalCode === null) {
          this.#logger.warn({ serviceName, pid: child.pid }, 'tail did not exit; SIGKILL')
          child.kill('SIGKILL')
        }
      } catch (err) {
        this.#logger.warn({ err, serviceName }, 'SIGKILL failed')
      }
    }, this.#killEscalationMs)
    // Don't keep the event loop alive solely for this timer — the daemon may
    // be exiting the moment we set it.
    if (typeof escalation.unref === 'function') escalation.unref()

    // Clear the timer once the child actually exits, so we don't hold onto
    // it longer than necessary.
    child.once('close', () => clearTimeout(escalation))

    this.#logger.info({ serviceName, pid: child.pid }, 'tail terminating')
  }
}
