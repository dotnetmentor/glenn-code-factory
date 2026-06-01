// GitRunner — Card 5. Pure low-level primitive that runs `/usr/bin/git <args>`
// and returns a structured `GitResult`. No SignalR wiring, no destructive-op
// classification, no SSH key writing — those layer on top in Cards 6/7/9.
//
// The implementation is a near-copy of HookExecutor (the closest sibling),
// adapted for git's argv-based invocation rather than shell strings, and
// extended to:
//
//   - Inject `GIT_SSH_COMMAND` + `GIT_TERMINAL_PROMPT=0` so the child never
//     blocks waiting for an interactive prompt and uses our SSH config + key.
//   - Emit `started` / `completed` audit events with a stable `executionId`,
//     redacting the command line when `invocation.sensitive` is set.
//   - Detect auth failures via a tail-regex heuristic so callers can surface
//     "key denied / re-auth needed" without scraping the output themselves.
//
// Invariants worth surfacing here so they aren't lost in the body:
//
//   - Always go through `/usr/bin/git` with explicit argv. No shell, no PATH
//     lookup. If git isn't installed at that path the spawn fails fast.
//
//   - Both stdout and stderr feed (a) a streaming sha256 over the raw byte
//     stream (computed in arrival order — exactly what the user would see
//     interleaved on a terminal), and (b) a 100-line ring buffer that becomes
//     `outputTail`. Lines are split on `\n`; a trailing partial is held until
//     either a newline arrives or the process exits (then flushed).
//
//   - Timeout default 60s, capped at 5min. On expiry we kill the process
//     group (`-pid`) so children of git (e.g. ssh, ssh-askpass) die too.
//     We send SIGTERM first; if the child is still alive `killEscalationMs`
//     later, SIGKILL. The escalation is delegated to
//     `killProcessGroupWithEscalation` in src/utils/killProcessGroup.ts —
//     same helper HookExecutor uses, so the two modules can't drift on the
//     kill semantics.
//
//   - AbortSignal uses the same kill mechanics as timeout, but the result
//     distinguishes via `timedOut` — only the timeout path sets it true.
//
//   - Already-aborted short-circuit: we don't spawn at all. We still emit
//     started+completed audits so the caller's audit trail is consistent
//     for "we tried to run it" cases.

import { spawn as nodeSpawn } from 'node:child_process'
import { createHash, randomUUID as nodeRandomUUID } from 'node:crypto'
import { homedir } from 'node:os'
import path from 'node:path'
import type { Logger } from 'pino'

import { killProcessGroupWithEscalation } from '../utils/killProcessGroup.js'

import type { GitAuditEvent, GitInvocation, GitResult } from './types.js'

const DEFAULT_TIMEOUT_MS = 60_000
const MAX_TIMEOUT_MS = 5 * 60_000
const DEFAULT_KILL_ESCALATION_MS = 10_000
const TAIL_LINE_LIMIT = 100
const MAX_TAIL_BYTES = 16 * 1024
const MAX_LINE_BYTES = 16 * 1024
const TRUNCATION_SUFFIX = '...[truncated]'

const GIT_BINARY = '/usr/bin/git'

// Auth-error heuristic. We scan the captured tail (already byte-clamped to
// ~16 KB) for any of these. The patterns mirror what OpenSSH / git print on
// the unhappy path; matching any one is enough to set `authError=true`.
const AUTH_ERROR_PATTERNS: readonly RegExp[] = [
  /Permission denied \(publickey/i,
  /Authentication failed/i,
  /Could not read from remote/i,
]

/**
 * Default `GIT_SSH_COMMAND` when the caller doesn't supply one. Resolves `~`
 * to `os.homedir()` at construction time — git itself doesn't expand `~` in
 * `GIT_SSH_COMMAND` because the env var is passed verbatim to the shell that
 * runs ssh, and that shell isn't always interactive.
 */
function defaultSshCommand(): string {
  const home = homedir()
  const config = path.join(home, '.ssh', 'config')
  const key = path.join(home, '.ssh', 'id_ed25519')
  return `ssh -F ${config} -o StrictHostKeyChecking=accept-new -o IdentitiesOnly=yes -i ${key}`
}

export interface GitRunnerOpts {
  cwd: string
  /** Override the SSH wrapper. When undefined, {@link defaultSshCommand} is used. */
  sshCommand?: string
  logger: Logger
  /**
   * Audit sink — fired once with `kind:'started'` before spawn, once with
   * `kind:'completed'` after the child exits (or after we decide it's not
   * worth spawning). `executionId` is stable across the pair.
   */
  onAudit: (e: GitAuditEvent) => void
  /**
   * ms between SIGTERM dispatch and SIGKILL escalation when a git invocation
   * is killed via timeout or abort. Defaults to {@link DEFAULT_KILL_ESCALATION_MS};
   * the production wiring threads {@link DaemonConfig.processKillEscalationMs}.
   */
  killEscalationMs?: number
  /** Test seam for injecting a fake spawn. Defaults to `node:child_process` spawn. */
  spawn?: typeof nodeSpawn
  /** Test seam for executionId. Defaults to `crypto.randomUUID`. */
  randomUUID?: () => string
  /** Test seam for clock. Defaults to `() => new Date()`. */
  now?: () => Date
}

export class GitRunner {
  readonly #cwd: string
  readonly #sshCommand: string
  readonly #logger: Logger
  readonly #onAudit: (e: GitAuditEvent) => void
  readonly #spawn: typeof nodeSpawn
  readonly #randomUUID: () => string
  readonly #now: () => Date
  readonly #killEscalationMs: number

  constructor(opts: GitRunnerOpts) {
    this.#cwd = opts.cwd
    this.#sshCommand = opts.sshCommand ?? defaultSshCommand()
    this.#logger = opts.logger.child({ module: 'git-runner' })
    this.#onAudit = opts.onAudit
    this.#spawn = opts.spawn ?? nodeSpawn
    this.#randomUUID = opts.randomUUID ?? (() => nodeRandomUUID())
    this.#now = opts.now ?? ((): Date => new Date())
    this.#killEscalationMs = opts.killEscalationMs ?? DEFAULT_KILL_ESCALATION_MS
  }

  async run(invocation: GitInvocation, signal: AbortSignal): Promise<GitResult> {
    // Clamp timeout. Default 60s, cap at 5min; warn on clamp so misconfigured
    // callers surface in logs without failing.
    let timeoutMs = invocation.timeoutMs ?? DEFAULT_TIMEOUT_MS
    if (timeoutMs > MAX_TIMEOUT_MS) {
      this.#logger.warn(
        { op: invocation.op, requested: timeoutMs, max: MAX_TIMEOUT_MS },
        'git timeout clamped to 5 min',
      )
      timeoutMs = MAX_TIMEOUT_MS
    }

    const executionId = this.#randomUUID()
    const startedAt = this.#now()
    // Two layers of audit redaction:
    //
    //   1. `sensitive: true` — full opt-in: the entire arg list is replaced
    //      with `[redacted]`. Used when the args themselves are private
    //      (e.g. a clone URL with a baked-in token).
    //
    //   2. `Authorization: …` value scrub — applies always. GitModule and
    //      CloningRepoStage both pass `-c http.extraHeader=Authorization:
    //      Basic <base64(x-access-token:<token>)>` to authenticate against
    //      GitHub's git HTTP backend (see basicAuth.ts). The base64 value
    //      is the installation token in plaintext; it MUST NOT land in the
    //      `GitOperations.CommandLine` audit row, the runtime logs, or any
    //      structured-log destination. The replacement keeps the header
    //      *name* (so an operator skim of the command line still shows
    //      "this push was authenticated") but strips the value.
    const commandLine = invocation.sensitive
      ? `git ${invocation.op} [redacted]`
      : redactAuthHeaders(['git', ...invocation.args].join(' '))

    this.#logger.info({ op: invocation.op, commandLine }, 'git starting')

    // Audit the started event before we do anything that can fail/short-circuit
    // — even the "already aborted" path emits both started + completed.
    this.#onAudit({
      kind: 'started',
      executionId,
      op: invocation.op,
      commandLine,
      startedAt,
    })

    // Already-aborted short-circuit. We don't spawn at all; return a synthetic
    // "killed before start" result. Empty hash = sha256 of zero bytes.
    if (signal.aborted) {
      const endedAt = this.#now()
      const result: GitResult = {
        exitCode: null,
        durationMs: endedAt.getTime() - startedAt.getTime(),
        outputTail: '',
        outputHash: createHash('sha256').digest('hex'),
        timedOut: false,
        authError: false,
      }
      this.#onAudit({
        kind: 'completed',
        executionId,
        op: invocation.op,
        commandLine,
        startedAt,
        endedAt,
        exitCode: result.exitCode,
        durationMs: result.durationMs,
        outputTail: result.outputTail,
        outputHash: result.outputHash,
        timedOut: result.timedOut,
        authError: result.authError,
      })
      this.#logger.debug(
        { op: invocation.op, executionId },
        'git completed (aborted before start)',
      )
      return result
    }

    const env: NodeJS.ProcessEnv = {
      ...process.env,
      GIT_SSH_COMMAND: this.#sshCommand,
      GIT_TERMINAL_PROMPT: '0',
    }

    // `detached: true` puts the child in its own process group, so we can
    // `process.kill(-pid, sig)` the whole group on timeout/abort. Without
    // this, ssh subprocesses keep stdio pipes open and delay 'close' past
    // our timeout. See `tryKill` for the group-kill mechanics.
    const child = this.#spawn(GIT_BINARY, invocation.args, {
      cwd: this.#cwd,
      env,
      stdio: ['ignore', 'pipe', 'pipe'],
      detached: true,
    })

    const hash = createHash('sha256')
    const tailLines: string[] = []

    let stdoutPartial = ''
    let stderrPartial = ''

    const ingestLine = (line: string): void => {
      // Bound the worst-case payload at TAIL_LINE_LIMIT * MAX_LINE_BYTES.
      // The hash sees the original bytes; only the tail buffer truncates.
      let stored = line
      if (Buffer.byteLength(stored, 'utf8') > MAX_LINE_BYTES) {
        const allowed = MAX_LINE_BYTES - TRUNCATION_SUFFIX.length
        stored = stored.slice(0, allowed) + TRUNCATION_SUFFIX
      }
      if (tailLines.length === TAIL_LINE_LIMIT) {
        tailLines.shift()
      }
      tailLines.push(stored)
    }

    const consumeChunk = (chunk: Buffer, fromStdout: boolean): void => {
      hash.update(chunk)

      const text = chunk.toString('utf8')
      const carry = fromStdout ? stdoutPartial : stderrPartial
      const combined = carry + text
      const parts = combined.split('\n')
      // The last element is the new partial (everything after the final '\n').
      const newPartial = parts.pop() ?? ''
      for (const part of parts) {
        ingestLine(part)
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
    // cancelled the moment the child actually exits.
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

    // Wait for natural exit. We listen on 'close' rather than 'exit' so we
    // know the stdio streams have drained before we compute the final tail.
    const exitInfo = await new Promise<{ code: number | null }>((resolve) => {
      child.once('close', (code) => {
        resolveClosed()
        resolve({ code })
      })
      child.once('error', (err) => {
        // spawn-level error (e.g. /usr/bin/git missing). Surface as a
        // kill-without-code so the caller distinguishes from a clean failure.
        this.#logger.error({ err, op: invocation.op }, 'git spawn error')
        resolveClosed()
        resolve({ code: null })
      })
    })

    if (sigtermTimer !== null) clearTimeout(sigtermTimer)
    signal.removeEventListener('abort', onAbort)

    // Flush any trailing partial lines so the final tail accounts for output
    // that didn't end in a newline.
    if (stdoutPartial.length > 0) {
      ingestLine(stdoutPartial)
      stdoutPartial = ''
    }
    if (stderrPartial.length > 0) {
      ingestLine(stderrPartial)
      stderrPartial = ''
    }

    const endedAt = this.#now()
    const durationMs = endedAt.getTime() - startedAt.getTime()
    const exitCode = timedOut ? null : exitInfo.code
    const outputHash = hash.digest('hex')
    const outputTail = clampTailBytes(tailLines.join('\n'), MAX_TAIL_BYTES)
    const authError = AUTH_ERROR_PATTERNS.some((re) => re.test(outputTail))

    const result: GitResult = {
      exitCode,
      durationMs,
      outputTail,
      outputHash,
      timedOut,
      authError,
    }

    this.#onAudit({
      kind: 'completed',
      executionId,
      op: invocation.op,
      commandLine,
      startedAt,
      endedAt,
      exitCode: result.exitCode,
      durationMs: result.durationMs,
      outputTail: result.outputTail,
      outputHash: result.outputHash,
      timedOut: result.timedOut,
      authError: result.authError,
    })

    this.#logger.debug(
      {
        op: invocation.op,
        executionId,
        exitCode: result.exitCode,
        durationMs: result.durationMs,
        timedOut: result.timedOut,
        authError: result.authError,
      },
      'git completed',
    )

    return result
  }
}

/**
 * Replace any `Authorization: <scheme> <value>` header value in `s` with
 * the literal string `[redacted]`. Used to scrub installation tokens out of
 * the audit `commandLine` we ship over SignalR + persist in the
 * `GitOperations` table.
 *
 * The pattern matches both case-insensitive (`authorization:` /
 * `AUTHORIZATION:`) and the typical scheme/value pair we use (`Basic
 * <base64>` from CloningRepoStage / GitModule). It deliberately does NOT
 * try to be a general HTTP-header parser — it's tuned for the exact shape
 * `git -c http.extraHeader=Authorization: Basic <base64>` produces when
 * argv is joined with spaces. The match runs to whitespace so a chained
 * `-c something-else=...` later on the line is not consumed.
 */
function redactAuthHeaders(s: string): string {
  return s.replace(/Authorization:\s*([A-Za-z]+)\s+\S+/gi, 'Authorization: $1 [redacted]')
}

/**
 * Clamp a string to `maxBytes` UTF-8 bytes. If under cap, return as-is.
 * Otherwise slice on a UTF-8 boundary (walking back over continuation
 * bytes) and return the prefix. Belt-and-braces — for ASCII output (the
 * overwhelming majority) it's a no-op.
 */
function clampTailBytes(tail: string, maxBytes: number): string {
  const bytes = Buffer.byteLength(tail, 'utf8')
  if (bytes <= maxBytes) return tail
  const buf = Buffer.from(tail, 'utf8')
  let cut = maxBytes
  while (cut > 0 && buf[cut] !== undefined && (buf[cut]! & 0xc0) === 0x80) {
    cut--
  }
  return buf.subarray(0, cut).toString('utf8')
}
