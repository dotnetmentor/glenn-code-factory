// HookExecutor — pure, low-level primitive that runs a hook spec via /bin/sh and
// returns a structured result. No SignalR, no module wiring, no event emission;
// just spawn → capture → return. Card 6 (HooksModule) layers orchestration on
// top, Card 8 (HookEventEmitter) layers event delivery.
//
// Implementation invariants worth surfacing here so they aren't lost in the
// body of `run()`:
//
//   - The shell is the contract. We always go through `/bin/sh -c <cmd>`. The
//     user's `cmd` is a string; no array-form, no `execFile`. This matches the
//     spec's brief and means hooks like `npm run build && echo ok` Just Work.
//
//   - Both stdout and stderr feed (a) a streaming sha256 over the raw byte
//     stream (computed in arrival order — exactly what the user would see if
//     they ran the command), and (b) a 100-line circular buffer (the
//     `outputTail`). Lines are split on `\n`; partial trailing bytes are held
//     until either a newline arrives or the process exits, at which point the
//     final partial line is flushed.
//
//   - `onProgressLines` only sees the FIRST 50 stdout lines, never stderr.
//     Once we've recorded 50 we stop appending. This is for upstream live
//     emission; the executor itself doesn't push anywhere.
//
//   - Timeout default 5 min, capped at 30 min (clamped + warned). On expiry
//     we SIGTERM, then `killEscalationMs` later SIGKILL if the child hasn't
//     closed. The escalation is delegated to `killProcessGroupWithEscalation`
//     in src/utils/killProcessGroup.ts — same helper GitRunner uses, so the
//     two modules can't drift on the kill semantics.
//
//   - AbortSignal is treated identically to a timeout for the kill mechanics,
//     but the result distinguishes via the `timedOut` flag — only timeout sets
//     it.
//
//   - Config-error heuristic runs ONLY on non-zero exit (not on timeout — that's
//     a different failure mode). Patterns: `npm ERR! missing script`,
//     `command not found`, `No such file or directory`. Last 4 KB of combined
//     output is sufficient to match.

import { spawn as nodeSpawn } from 'node:child_process'
import { createHash } from 'node:crypto'
import type { Logger } from 'pino'

import { killProcessGroupWithEscalation } from '../utils/killProcessGroup.js'

export interface HookSpec {
  name: string
  cmd: string
  /** Default 5 min (300_000 ms). Clamped to 30 min (1_800_000 ms) max. */
  timeoutMs?: number
  feedbackMode: 'on-failure' | 'always' | 'silent'
}

export interface HookResult {
  /** null if the child was killed (timeout or abort). */
  exitCode: number | null
  durationMs: number
  /** Last N lines (default 100), '\n'-joined. May be empty. */
  outputTail: string
  /** sha256 hex (lower-case, 64 chars) of the combined stdout+stderr byte stream. */
  outputHash: string
  timedOut: boolean
  /** Heuristic: missing-script / command-not-found / no-such-file-or-directory. */
  wasConfigError: boolean
  /** First 50 stdout lines for upstream live emission. */
  onProgressLines: string[]
}

export interface HookExecutorOptions {
  cwd: string
  env: NodeJS.ProcessEnv
  logger: Logger
  /**
   * ms between SIGTERM dispatch and SIGKILL escalation when a hook is killed
   * via timeout or abort. Defaults to {@link DEFAULT_KILL_ESCALATION_MS}; the
   * production wiring threads {@link DaemonConfig.processKillEscalationMs}.
   */
  killEscalationMs?: number
  /** Test seam for injecting a fake spawn. Defaults to node:child_process spawn. */
  spawn?: typeof nodeSpawn
  /** Test seam for clock. Defaults to Date.now. */
  now?: () => number
}

const DEFAULT_TIMEOUT_MS = 5 * 60 * 1000
const MAX_TIMEOUT_MS = 30 * 60 * 1000
const DEFAULT_KILL_ESCALATION_MS = 10_000
const TAIL_LINE_LIMIT = 100
const PROGRESS_LINE_LIMIT = 50
const MAX_LINE_BYTES = 16 * 1024
const TRUNCATION_SUFFIX = '...[truncated]'
const CONFIG_ERROR_TAIL_BYTES = 4 * 1024

// Patterns from the spec brief, plus dash's variant ("…: not found") which is
// what /bin/sh prints on Debian/Ubuntu when bash isn't the shell (bash prints
// "command not found"; dash drops the "command" prefix). Both shapes mean the
// same thing — the user's hook references something that isn't installed.
const CONFIG_ERROR_PATTERNS: readonly RegExp[] = [
  /npm ERR!\s+missing script/i,
  /command not found/i,
  /No such file or directory/i,
  /:\s+not found(?:\s|$)/im,
]

export class HookExecutor {
  readonly #cwd: string
  readonly #env: NodeJS.ProcessEnv
  readonly #logger: Logger
  readonly #spawn: typeof nodeSpawn
  readonly #now: () => number
  readonly #killEscalationMs: number

  constructor(opts: HookExecutorOptions) {
    this.#cwd = opts.cwd
    this.#env = opts.env
    this.#logger = opts.logger.child({ module: 'hook-executor' })
    this.#spawn = opts.spawn ?? nodeSpawn
    this.#now = opts.now ?? Date.now
    this.#killEscalationMs = opts.killEscalationMs ?? DEFAULT_KILL_ESCALATION_MS
  }

  async run(spec: HookSpec, signal: AbortSignal): Promise<HookResult> {
    // Clamp timeout. Default 5 min, cap at 30 min; warn on clamp so misconfigured
    // specs surface in logs without failing.
    let timeoutMs = spec.timeoutMs ?? DEFAULT_TIMEOUT_MS
    if (timeoutMs > MAX_TIMEOUT_MS) {
      this.#logger.warn(
        { hook: spec.name, requested: timeoutMs, max: MAX_TIMEOUT_MS },
        'hook timeout clamped to 30 min',
      )
      timeoutMs = MAX_TIMEOUT_MS
    }

    this.#logger.info({ hook: spec.name, cmd: spec.cmd }, 'hook starting')
    const startedAt = this.#now()

    // Already-aborted short-circuit. We don't spawn at all; return a synthetic
    // "killed before start" result.
    if (signal.aborted) {
      const result: HookResult = {
        exitCode: null,
        durationMs: 0,
        outputTail: '',
        outputHash: createHash('sha256').digest('hex'),
        timedOut: false,
        wasConfigError: false,
        onProgressLines: [],
      }
      this.#logger.debug(
        {
          exitCode: result.exitCode,
          durationMs: result.durationMs,
          timedOut: result.timedOut,
          wasConfigError: result.wasConfigError,
        },
        'hook completed (aborted before start)',
      )
      return result
    }

    // `detached: true` puts the child in its own process group. This lets us
    // kill the WHOLE group (`-pid`) on timeout/abort — without it, /bin/sh's
    // grandchildren (e.g. `sleep` under `npm run …`) keep stdio pipes open and
    // delay the 'close' event by their own runtime, defeating the timeout. See
    // tryKill below for the group-kill mechanics.
    const child = this.#spawn('/bin/sh', ['-c', spec.cmd], {
      cwd: this.#cwd,
      env: this.#env,
      stdio: ['ignore', 'pipe', 'pipe'],
      detached: true,
    })

    const hash = createHash('sha256')
    const tailLines: string[] = []
    const progressLines: string[] = []
    // Last 4 KB of combined output, retained as a Buffer ring for the
    // config-error heuristic. Avoids holding the full stream in memory.
    let configTail = Buffer.alloc(0)

    let stdoutPartial = ''
    let stderrPartial = ''

    const ingestLine = (line: string, fromStdout: boolean): void => {
      // Truncate single pathologically-long lines so we bound the worst-case
      // payload at TAIL_LINE_LIMIT * MAX_LINE_BYTES (~1.6 MB). The hash sees
      // the original bytes; only the tail buffer truncates.
      let stored = line
      if (Buffer.byteLength(stored, 'utf8') > MAX_LINE_BYTES) {
        const allowed = MAX_LINE_BYTES - TRUNCATION_SUFFIX.length
        // Safe to slice by code unit here — we're truncating display copy, not
        // touching the hash. The trailing replacement char risk is acceptable
        // for the truncation use case.
        stored = stored.slice(0, allowed) + TRUNCATION_SUFFIX
      }
      if (tailLines.length === TAIL_LINE_LIMIT) {
        tailLines.shift()
      }
      tailLines.push(stored)

      if (fromStdout && progressLines.length < PROGRESS_LINE_LIMIT) {
        progressLines.push(stored)
      }
    }

    const consumeChunk = (chunk: Buffer, fromStdout: boolean): void => {
      hash.update(chunk)

      // Refresh the rolling 4 KB tail used for config-error matching.
      const merged = Buffer.concat([configTail, chunk])
      configTail =
        merged.length > CONFIG_ERROR_TAIL_BYTES
          ? merged.subarray(merged.length - CONFIG_ERROR_TAIL_BYTES)
          : merged

      const text = chunk.toString('utf8')
      const carry = fromStdout ? stdoutPartial : stderrPartial
      const combined = carry + text
      const parts = combined.split('\n')
      // The last element is the new partial (everything after the final '\n').
      const newPartial = parts.pop() ?? ''
      for (const part of parts) {
        ingestLine(part, fromStdout)
      }
      if (fromStdout) {
        stdoutPartial = newPartial
      } else {
        stderrPartial = newPartial
      }
    }

    child.stdout?.on('data', (chunk: Buffer) => consumeChunk(chunk, true))
    child.stderr?.on('data', (chunk: Buffer) => consumeChunk(chunk, false))

    let timedOut = false
    let sigtermTimer: NodeJS.Timeout | null = null

    // `processClosed` resolves when the child emits 'close' (or 'error').
    // The escalation helper races against this so the SIGKILL timer is
    // cancelled the moment the child actually exits — no spurious escalation
    // log for fast-cleanup processes.
    let resolveClosed!: () => void
    const processClosed = new Promise<void>((resolve) => {
      resolveClosed = resolve
    })

    const armKillSequence = (markTimeout: boolean, reason: 'timeout' | 'abort'): void => {
      if (markTimeout) timedOut = true
      killProcessGroupWithEscalation({
        pid: child.pid,
        processClosed,
        escalationMs: this.#killEscalationMs,
        logger: this.#logger,
        reason,
      })
    }

    sigtermTimer = setTimeout(() => {
      armKillSequence(true, 'timeout')
    }, timeoutMs)
    sigtermTimer.unref?.()

    const onAbort = (): void => {
      armKillSequence(false, 'abort')
    }
    signal.addEventListener('abort', onAbort, { once: true })

    // Wait for natural exit. We listen on 'close' rather than 'exit' so we know
    // the stdio streams have drained before we compute the final tail.
    const exitInfo = await new Promise<{ code: number | null }>((resolve) => {
      child.once('close', (code) => {
        resolveClosed()
        resolve({ code })
      })
      child.once('error', (err) => {
        // spawn-level error (e.g. /bin/sh missing). Surface as a kill-without-code.
        this.#logger.error({ err, hook: spec.name }, 'spawn error')
        resolveClosed()
        resolve({ code: null })
      })
    })

    // Cleanup the SIGTERM timer + abort listener. The SIGKILL escalation
    // timer is owned by killProcessGroupWithEscalation and cancels itself
    // via processClosed; we don't need to track it here.
    if (sigtermTimer !== null) clearTimeout(sigtermTimer)
    signal.removeEventListener('abort', onAbort)

    // Flush any trailing partial lines so the final tail accounts for output
    // that didn't end in a newline (e.g. `printf 'no newline'`).
    if (stdoutPartial.length > 0) {
      ingestLine(stdoutPartial, true)
      stdoutPartial = ''
    }
    if (stderrPartial.length > 0) {
      ingestLine(stderrPartial, false)
      stderrPartial = ''
    }

    const durationMs = this.#now() - startedAt
    const exitCode = timedOut ? null : exitInfo.code
    const outputHash = hash.digest('hex')
    const outputTail = tailLines.join('\n')

    let wasConfigError = false
    if (!timedOut && exitCode !== 0) {
      const tailText = configTail.toString('utf8')
      wasConfigError = CONFIG_ERROR_PATTERNS.some((re) => re.test(tailText))
    }

    const result: HookResult = {
      exitCode,
      durationMs,
      outputTail,
      outputHash,
      timedOut,
      wasConfigError,
      onProgressLines: progressLines,
    }

    this.#logger.debug(
      {
        hook: spec.name,
        exitCode: result.exitCode,
        durationMs: result.durationMs,
        timedOut: result.timedOut,
        wasConfigError: result.wasConfigError,
      },
      'hook completed',
    )

    return result
  }
}
