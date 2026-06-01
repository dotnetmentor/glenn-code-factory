// Tests for GitRunner. Hand-rolled spawn fake (an EventEmitter with stdout +
// stderr sub-emitters and a `kill()` method) — we never invoke a real `git`
// binary so the suite is sub-second under fake timers. The fake mirrors the
// surface that `node:child_process.spawn` returns in `stdio: ['ignore','pipe','pipe']`
// mode that GitRunner uses; nothing more.
//
// We use vitest's fake timers to drive the timeout path without sleeping. The
// rest of the suite runs synchronously: emit data → emit 'close' → await run().

import { EventEmitter } from 'node:events'
import { createHash } from 'node:crypto'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { Logger } from 'pino'

import { GitRunner } from './GitRunner.js'
import type { GitAuditEvent, GitInvocation } from './types.js'

// ============================================================================
// Test helpers
// ============================================================================

function makeLogger() {
  const log = {
    trace: vi.fn(),
    debug: vi.fn(),
    info: vi.fn(),
    warn: vi.fn(),
    error: vi.fn(),
    fatal: vi.fn(),
    child: vi.fn(() => log),
  }
  return log
}

/**
 * Minimal child_process.ChildProcess stand-in. EventEmitter for `close` /
 * `error`; sub-EventEmitters for stdout/stderr to receive `data` events.
 * `kill(sig)` records the call and (best-effort) emits `close` so the runner
 * can resolve. `pid` is a fixed value so the runner's `process.kill(-pid)`
 * path doesn't blow up — we override `process.kill` for those tests.
 */
class FakeChild extends EventEmitter {
  stdout = new EventEmitter()
  stderr = new EventEmitter()
  pid = 12345
  killed = false
  exitCode: number | null = null
  kill = vi.fn((_sig?: NodeJS.Signals): boolean => {
    this.killed = true
    return true
  })
}

interface FakeSpawnContext {
  child: FakeChild
  spawn: ReturnType<typeof vi.fn>
}

function makeFakeSpawn(): FakeSpawnContext {
  const child = new FakeChild()
  const spawn = vi.fn(() => child)
  return { child, spawn }
}

interface MakeRunnerOpts {
  spawn?: ReturnType<typeof vi.fn>
  randomUUID?: () => string
  now?: () => Date
  sshCommand?: string
  killEscalationMs?: number
}

function makeRunner(opts: MakeRunnerOpts = {}) {
  const audits: GitAuditEvent[] = []
  const onAudit = vi.fn((e: GitAuditEvent) => {
    audits.push(e)
  })
  const logger = makeLogger()
  const runner = new GitRunner({
    cwd: '/tmp/repo',
    logger: logger as unknown as Logger,
    onAudit,
    ...(opts.spawn !== undefined ? { spawn: opts.spawn as never } : {}),
    ...(opts.randomUUID !== undefined ? { randomUUID: opts.randomUUID } : {}),
    ...(opts.now !== undefined ? { now: opts.now } : {}),
    ...(opts.sshCommand !== undefined ? { sshCommand: opts.sshCommand } : {}),
    ...(opts.killEscalationMs !== undefined ? { killEscalationMs: opts.killEscalationMs } : {}),
  })
  return { runner, onAudit, audits, logger }
}

function inv(partial: Partial<GitInvocation> & Pick<GitInvocation, 'op'>): GitInvocation {
  return {
    op: partial.op,
    args: partial.args ?? [partial.op.toLowerCase()],
    ...(partial.sensitive !== undefined ? { sensitive: partial.sensitive } : {}),
    ...(partial.timeoutMs !== undefined ? { timeoutMs: partial.timeoutMs } : {}),
  }
}

// ============================================================================
// Tests
// ============================================================================

describe('GitRunner', () => {
  beforeEach(() => {
    vi.useFakeTimers()
  })

  afterEach(() => {
    vi.useRealTimers()
    vi.restoreAllMocks()
  })

  it('returns exitCode 0 with deterministic hash on clean exit', async () => {
    const { child, spawn } = makeFakeSpawn()
    const { runner } = makeRunner({ spawn })

    const promise = runner.run(
      inv({ op: 'BranchList', args: ['branch', '--list'] }),
      new AbortController().signal,
    )

    // Drive the child: emit some output, then close.
    child.stdout.emit('data', Buffer.from('main\n'))
    child.stdout.emit('data', Buffer.from('feature/foo\n'))
    child.exitCode = 0
    child.emit('close', 0)

    const result = await promise

    expect(result.exitCode).toBe(0)
    expect(result.timedOut).toBe(false)
    expect(result.authError).toBe(false)
    expect(result.outputTail).toBe('main\nfeature/foo')
    expect(result.outputHash).toMatch(/^[0-9a-f]{64}$/)
    expect(result.durationMs).toBeGreaterThanOrEqual(0)

    // Hash matches a hand-computed sha256 over the exact bytes streamed.
    const expected = createHash('sha256')
      .update('main\n')
      .update('feature/foo\n')
      .digest('hex')
    expect(result.outputHash).toBe(expected)

    // Spawn invocation surface.
    expect(spawn).toHaveBeenCalledTimes(1)
    const callArgs = spawn.mock.calls[0]!
    expect(callArgs[0]).toBe('/usr/bin/git')
    expect(callArgs[1]).toEqual(['branch', '--list'])
    const opts = callArgs[2] as {
      cwd: string
      detached: boolean
      env: NodeJS.ProcessEnv
    }
    expect(opts.cwd).toBe('/tmp/repo')
    expect(opts.detached).toBe(true)
    expect(opts.env['GIT_TERMINAL_PROMPT']).toBe('0')
    expect(opts.env['GIT_SSH_COMMAND']).toContain('ssh')
    expect(opts.env['GIT_SSH_COMMAND']).toContain('IdentitiesOnly=yes')
  })

  it('honours custom sshCommand override', async () => {
    const { child, spawn } = makeFakeSpawn()
    const { runner } = makeRunner({ spawn, sshCommand: 'ssh -i /custom/key' })

    const promise = runner.run(inv({ op: 'Fetch' }), new AbortController().signal)
    child.exitCode = 0
    child.emit('close', 0)
    await promise

    const opts = spawn.mock.calls[0]![2] as { env: NodeJS.ProcessEnv }
    expect(opts.env['GIT_SSH_COMMAND']).toBe('ssh -i /custom/key')
  })

  it('returns non-zero exit without auth error', async () => {
    const { child, spawn } = makeFakeSpawn()
    const { runner } = makeRunner({ spawn })

    const promise = runner.run(inv({ op: 'Commit' }), new AbortController().signal)
    child.stderr.emit('data', Buffer.from('error: nothing to commit\n'))
    child.exitCode = 1
    child.emit('close', 1)
    const result = await promise

    expect(result.exitCode).toBe(1)
    expect(result.authError).toBe(false)
    expect(result.timedOut).toBe(false)
    expect(result.outputTail).toContain('error: nothing to commit')
  })

  it.each([
    ['Permission denied (publickey).\n', 'publickey'],
    ['fatal: Authentication failed for repo\n', 'auth-failed'],
    ['Could not read from remote repository.\n', 'remote-read'],
  ])('detects auth error for %s', async (line, _label) => {
    const { child, spawn } = makeFakeSpawn()
    const { runner } = makeRunner({ spawn })

    const promise = runner.run(inv({ op: 'Push' }), new AbortController().signal)
    child.stderr.emit('data', Buffer.from(line))
    child.exitCode = 128
    child.emit('close', 128)
    const result = await promise

    expect(result.authError).toBe(true)
  })

  it('does NOT mark non-matching error as authError', async () => {
    const { child, spawn } = makeFakeSpawn()
    const { runner } = makeRunner({ spawn })

    const promise = runner.run(inv({ op: 'Push' }), new AbortController().signal)
    child.stderr.emit('data', Buffer.from('fatal: refusing to merge unrelated histories\n'))
    child.exitCode = 128
    child.emit('close', 128)
    const result = await promise

    expect(result.authError).toBe(false)
  })

  it('kills process and sets timedOut=true on timeout', async () => {
    const { child, spawn } = makeFakeSpawn()
    // Override process.kill so the runner's group-kill path is a no-op rather
    // than killing this test runner's own process group.
    const killSpy = vi.spyOn(process, 'kill').mockImplementation(() => true)
    const { runner } = makeRunner({ spawn })

    const promise = runner.run(
      inv({ op: 'Fetch', timeoutMs: 1_000 }),
      new AbortController().signal,
    )

    // Advance past the timeout. The runner SIGTERMs the group; our fake kill
    // doesn't actually signal anything, so we close manually to let the
    // promise resolve (mirrors what the kernel would do once SIGTERM lands).
    await vi.advanceTimersByTimeAsync(1_001)
    child.exitCode = null
    child.emit('close', null)

    const result = await promise

    expect(result.timedOut).toBe(true)
    expect(result.exitCode).toBeNull()
    // process.kill called with -pid, SIGTERM.
    expect(killSpy).toHaveBeenCalledWith(-12345, 'SIGTERM')
  })

  it('clamps timeout above 5 minutes and warns', async () => {
    const { child, spawn } = makeFakeSpawn()
    const { runner, logger } = makeRunner({ spawn })

    const promise = runner.run(
      inv({ op: 'Fetch', timeoutMs: 60 * 60 * 1000 }),
      new AbortController().signal,
    )
    child.exitCode = 0
    child.emit('close', 0)
    await promise

    expect(logger.warn).toHaveBeenCalledWith(
      expect.objectContaining({ requested: 60 * 60 * 1000, max: 5 * 60_000 }),
      'git timeout clamped to 5 min',
    )
  })

  it('returns immediately when signal is already aborted (no spawn)', async () => {
    const { spawn } = makeFakeSpawn()
    const { runner, audits } = makeRunner({ spawn })

    const ctrl = new AbortController()
    ctrl.abort()
    const result = await runner.run(inv({ op: 'Fetch' }), ctrl.signal)

    expect(result.exitCode).toBeNull()
    expect(result.timedOut).toBe(false)
    expect(result.outputTail).toBe('')
    expect(result.outputHash).toMatch(/^[0-9a-f]{64}$/)
    // Empty-stream sha256 is a known constant.
    expect(result.outputHash).toBe(createHash('sha256').digest('hex'))
    expect(spawn).not.toHaveBeenCalled()

    // Audit pair still emitted.
    expect(audits).toHaveLength(2)
    expect(audits[0]!.kind).toBe('started')
    expect(audits[1]!.kind).toBe('completed')
  })

  it('kills the process group when aborted mid-run', async () => {
    const { child, spawn } = makeFakeSpawn()
    const killSpy = vi.spyOn(process, 'kill').mockImplementation(() => true)
    const { runner } = makeRunner({ spawn })
    const ctrl = new AbortController()

    const promise = runner.run(inv({ op: 'Fetch' }), ctrl.signal)

    // Abort mid-run, then resolve via close.
    ctrl.abort()
    child.exitCode = null
    child.emit('close', null)

    const result = await promise

    expect(result.timedOut).toBe(false)
    expect(result.exitCode).toBeNull()
    expect(killSpy).toHaveBeenCalledWith(-12345, 'SIGTERM')
  })

  it('caps tail at 100 lines but hash covers all output', async () => {
    const { child, spawn } = makeFakeSpawn()
    const { runner } = makeRunner({ spawn })

    const promise = runner.run(inv({ op: 'BranchList' }), new AbortController().signal)

    // Emit 200 distinct lines.
    const lines: string[] = []
    for (let i = 1; i <= 200; i++) {
      lines.push(`line ${i}`)
    }
    const fullText = lines.join('\n') + '\n'
    child.stdout.emit('data', Buffer.from(fullText))
    child.exitCode = 0
    child.emit('close', 0)

    const result = await promise

    const tailLines = result.outputTail.split('\n')
    expect(tailLines).toHaveLength(100)
    expect(tailLines[0]).toBe('line 101')
    expect(tailLines[99]).toBe('line 200')

    // Hash is over the full streamed bytes, not just the tail.
    const expected = createHash('sha256').update(fullText).digest('hex')
    expect(result.outputHash).toBe(expected)
  })

  it('produces deterministic hash for identical input', async () => {
    const run = async () => {
      const { child, spawn } = makeFakeSpawn()
      const { runner } = makeRunner({ spawn })
      const promise = runner.run(inv({ op: 'Fetch' }), new AbortController().signal)
      child.stdout.emit('data', Buffer.from('hello world\n'))
      child.exitCode = 0
      child.emit('close', 0)
      return promise
    }
    const a = await run()
    const b = await run()
    expect(a.outputHash).toBe(b.outputHash)
  })

  it('emits started + completed audit with stable executionId', async () => {
    const { child, spawn } = makeFakeSpawn()
    const fixedId = 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee'
    const fixedNow = new Date('2026-05-08T12:00:00Z')
    const { runner, audits } = makeRunner({
      spawn,
      randomUUID: () => fixedId,
      now: () => fixedNow,
    })

    const promise = runner.run(
      inv({ op: 'Commit', args: ['commit', '-m', 'msg'] }),
      new AbortController().signal,
    )
    child.stdout.emit('data', Buffer.from('ok\n'))
    child.exitCode = 0
    child.emit('close', 0)
    await promise

    expect(audits).toHaveLength(2)
    const started = audits[0]!
    const completed = audits[1]!
    expect(started.kind).toBe('started')
    expect(completed.kind).toBe('completed')
    expect(started.executionId).toBe(fixedId)
    expect(completed.executionId).toBe(fixedId)
    expect(started.op).toBe('Commit')
    expect(completed.op).toBe('Commit')
    expect(started.commandLine).toBe('git commit -m msg')
    expect(completed.commandLine).toBe('git commit -m msg')
    expect(started.startedAt).toBe(fixedNow)
    expect(completed.startedAt).toBe(fixedNow)
    expect(completed.endedAt).toBe(fixedNow)
    expect(completed.exitCode).toBe(0)
    expect(completed.outputHash).toMatch(/^[0-9a-f]{64}$/)
    expect(completed.timedOut).toBe(false)
    expect(completed.authError).toBe(false)
  })

  it('redacts commandLine in audit when invocation.sensitive=true', async () => {
    const { child, spawn } = makeFakeSpawn()
    const { runner, audits } = makeRunner({ spawn })

    const promise = runner.run(
      inv({
        op: 'Clone',
        args: ['clone', 'https://user:secret@github.com/o/r.git', 'r'],
        sensitive: true,
      }),
      new AbortController().signal,
    )
    child.exitCode = 0
    child.emit('close', 0)
    await promise

    expect(audits[0]!.commandLine).toBe('git Clone [redacted]')
    expect(audits[1]!.commandLine).toBe('git Clone [redacted]')
    // Sanity: the secret is NOT in the audit anywhere.
    for (const a of audits) {
      expect(JSON.stringify(a)).not.toContain('secret')
    }
  })

  it('scrubs Authorization header values from commandLine even without sensitive flag', async () => {
    // GitModule.push and CloningRepoStage both pass
    //   `-c http.extraHeader=Authorization: Basic <base64(x-access-token:<token>)>` …
    // The base64 value is the installation token in plaintext — it MUST NOT
    // land in the audit `commandLine` (which is persisted to GitOperations
    // and shipped over SignalR). The runner scrubs the value to `[redacted]`
    // unconditionally; the rest of the args (op kind, remote, branch) stays
    // visible so an operator can still tell what the push was doing.
    const { child, spawn } = makeFakeSpawn()
    const { runner, audits } = makeRunner({ spawn })

    const fakeBase64 = 'eC1hY2Nlc3MtdG9rZW46Z2hzX0ZBS0VfVE9LRU4='
    const promise = runner.run(
      inv({
        op: 'Push',
        args: [
          '-c',
          `http.extraHeader=Authorization: Basic ${fakeBase64}`,
          'push',
          'origin',
          'main',
        ],
      }),
      new AbortController().signal,
    )
    child.exitCode = 0
    child.emit('close', 0)
    await promise

    // Header value gone, but the structure / scheme / surrounding args stay.
    for (const a of audits) {
      expect(a.commandLine).toContain('Authorization: Basic [redacted]')
      expect(a.commandLine).toContain('push origin main')
      expect(a.commandLine).not.toContain(fakeBase64)
    }
    // Defence in depth — the encoded token bytes are nowhere in the audit
    // payload (logger, payloads, anywhere we'd structurally serialise).
    for (const a of audits) {
      expect(JSON.stringify(a)).not.toContain(fakeBase64)
    }
  })

  it('flushes trailing partial line on close', async () => {
    const { child, spawn } = makeFakeSpawn()
    const { runner } = makeRunner({ spawn })

    const promise = runner.run(inv({ op: 'Fetch' }), new AbortController().signal)
    child.stdout.emit('data', Buffer.from('no newline'))
    child.exitCode = 0
    child.emit('close', 0)
    const result = await promise

    expect(result.outputTail).toBe('no newline')
  })

  it('interleaves stdout + stderr into a single ordered tail', async () => {
    const { child, spawn } = makeFakeSpawn()
    const { runner } = makeRunner({ spawn })

    const promise = runner.run(inv({ op: 'Push' }), new AbortController().signal)
    child.stdout.emit('data', Buffer.from('stdout-line\n'))
    child.stderr.emit('data', Buffer.from('stderr-line\n'))
    child.exitCode = 0
    child.emit('close', 0)
    const result = await promise

    const lines = result.outputTail.split('\n')
    expect(lines).toContain('stdout-line')
    expect(lines).toContain('stderr-line')
  })

  // ============================================================================
  // SIGTERM → SIGKILL escalation (Spec 13 Card 10)
  // ============================================================================
  //
  // The runner's child (via /usr/bin/git) might ignore SIGTERM in pathological
  // cases — wedged ssh handshake, kernel-blocked TCP connect, and so on. The
  // daemon must escalate to SIGKILL so a stuck git invocation can't hold up
  // the shutdown drain. These tests use the existing fake spawn + fake timers
  // pattern to assert escalation timing without touching real git.

  describe('escalation', () => {
    it('escalates to SIGKILL 10s after timeout if child does not close', async () => {
      const { child, spawn } = makeFakeSpawn()
      const killSpy = vi.spyOn(process, 'kill').mockImplementation(() => true)
      const { runner } = makeRunner({ spawn, killEscalationMs: 10_000 })

      const promise = runner.run(
        inv({ op: 'Fetch', timeoutMs: 1_000 }),
        new AbortController().signal,
      )

      // Timeout fires → SIGTERM dispatched.
      await vi.advanceTimersByTimeAsync(1_001)
      expect(killSpy).toHaveBeenCalledTimes(1)
      expect(killSpy).toHaveBeenNthCalledWith(1, -12345, 'SIGTERM')

      // 9s after SIGTERM: still no escalation.
      await vi.advanceTimersByTimeAsync(9_000)
      expect(killSpy).toHaveBeenCalledTimes(1)

      // 10s after SIGTERM: SIGKILL escalation.
      await vi.advanceTimersByTimeAsync(1_000)
      expect(killSpy).toHaveBeenCalledTimes(2)
      expect(killSpy).toHaveBeenNthCalledWith(2, -12345, 'SIGKILL')

      // Let the child finally close so run() resolves.
      child.exitCode = null
      child.emit('close', null)
      const result = await promise

      expect(result.timedOut).toBe(true)
      expect(result.exitCode).toBeNull()
    })

    it('escalates to SIGKILL 10s after abort if child does not close', async () => {
      const { child, spawn } = makeFakeSpawn()
      const killSpy = vi.spyOn(process, 'kill').mockImplementation(() => true)
      const { runner } = makeRunner({ spawn, killEscalationMs: 10_000 })

      const ctrl = new AbortController()
      const promise = runner.run(inv({ op: 'Push' }), ctrl.signal)

      ctrl.abort()
      // Drain microtask queue so the abort listener runs synchronously.
      await Promise.resolve()
      expect(killSpy).toHaveBeenNthCalledWith(1, -12345, 'SIGTERM')

      // 9s in: no SIGKILL yet.
      await vi.advanceTimersByTimeAsync(9_000)
      expect(killSpy).toHaveBeenCalledTimes(1)

      // 10s in: SIGKILL.
      await vi.advanceTimersByTimeAsync(1_000)
      expect(killSpy).toHaveBeenCalledTimes(2)
      expect(killSpy).toHaveBeenNthCalledWith(2, -12345, 'SIGKILL')

      child.exitCode = null
      child.emit('close', null)
      const result = await promise
      expect(result.timedOut).toBe(false) // abort, not timeout
    })

    it('does not SIGKILL when child closes before escalation deadline', async () => {
      const { child, spawn } = makeFakeSpawn()
      const killSpy = vi.spyOn(process, 'kill').mockImplementation(() => true)
      const { runner, logger } = makeRunner({ spawn, killEscalationMs: 10_000 })

      const ctrl = new AbortController()
      const promise = runner.run(inv({ op: 'Push' }), ctrl.signal)
      ctrl.abort()
      await Promise.resolve()

      // Child closes 5s after abort — before the 10s escalation deadline.
      await vi.advanceTimersByTimeAsync(5_000)
      child.exitCode = null
      child.emit('close', null)
      await promise

      // Drive past where the escalation timer would have fired.
      await vi.advanceTimersByTimeAsync(20_000)

      // Only SIGTERM, never SIGKILL.
      expect(killSpy).toHaveBeenCalledTimes(1)
      expect(killSpy).toHaveBeenNthCalledWith(1, -12345, 'SIGTERM')
      expect(logger.warn).not.toHaveBeenCalledWith(
        expect.anything(),
        'sigterm timeout — escalating to sigkill',
      )
    })
  })
})
