// Tests for HookExecutor. Real /bin/sh, real spawn — we exercise actual stream
// handling rather than mocking node:child_process. Each test is sub-second by
// using `echo`/`exit N`/`sleep` with small delays.
//
// For abort/timeout paths we use REAL `setTimeout` with 100 ms timeouts. Fake
// timers don't play nicely with real child processes (the kill sequence relies
// on actual elapsed wall time + Node's internal timer queue), and the tests
// stay fast even with real timers.
//
// The SIGTERM → SIGKILL escalation tests at the bottom of this file use a fake
// spawn (EventEmitter-based ChildProcess stand-in) + fake timers so we can
// assert exact escalation timing without relying on a child that ignores
// SIGTERM in the wild — the daemon must escalate even for wedged children.

import { EventEmitter } from 'node:events'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { Logger } from 'pino'

import { HookExecutor, type HookSpec } from './HookExecutor.js'

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

function makeExecutor(logger = makeLogger()) {
  const executor = new HookExecutor({
    cwd: '/tmp',
    env: { ...process.env, PATH: process.env['PATH'] ?? '/usr/bin:/bin' },
    logger: logger as unknown as Logger,
  })
  return { executor, logger }
}

function spec(partial: Partial<HookSpec> & Pick<HookSpec, 'cmd'>): HookSpec {
  return {
    name: partial.name ?? 'test',
    cmd: partial.cmd,
    feedbackMode: partial.feedbackMode ?? 'on-failure',
    ...(partial.timeoutMs !== undefined ? { timeoutMs: partial.timeoutMs } : {}),
  }
}

// ============================================================================
// Tests
// ============================================================================

describe('HookExecutor', () => {
  it('captures stdout on zero exit', async () => {
    const { executor } = makeExecutor()
    const result = await executor.run(
      spec({ cmd: 'echo hello && echo world' }),
      new AbortController().signal,
    )

    expect(result.exitCode).toBe(0)
    expect(result.outputTail).toBe('hello\nworld')
    expect(result.onProgressLines).toEqual(['hello', 'world'])
    expect(result.timedOut).toBe(false)
    expect(result.wasConfigError).toBe(false)
    expect(result.outputHash).toMatch(/^[0-9a-f]{64}$/)
  })

  it('captures stderr and non-zero exit', async () => {
    const { executor } = makeExecutor()
    const result = await executor.run(
      spec({ cmd: 'echo bad >&2; exit 7' }),
      new AbortController().signal,
    )

    expect(result.exitCode).toBe(7)
    expect(result.outputTail).toContain('bad')
    expect(result.onProgressLines).toEqual([]) // stderr does not contribute
    expect(result.timedOut).toBe(false)
    expect(result.outputHash).toMatch(/^[0-9a-f]{64}$/)
  })

  it('honours timeout and SIGTERMs the child', async () => {
    const { executor } = makeExecutor()
    const start = Date.now()
    const result = await executor.run(
      spec({ cmd: 'sleep 5', timeoutMs: 100 }),
      new AbortController().signal,
    )
    const elapsed = Date.now() - start

    expect(result.timedOut).toBe(true)
    expect(result.exitCode).toBeNull()
    expect(result.wasConfigError).toBe(false)
    // Generous slack: SIGTERM fires at 100 ms, and `sleep` exits cleanly on
    // SIGTERM. Allow up to 5 s for CI noise but assert non-trivial elapsed.
    expect(elapsed).toBeGreaterThanOrEqual(80)
    expect(elapsed).toBeLessThan(5_000)
  })

  it('honours an external abort signal', async () => {
    const { executor } = makeExecutor()
    const ctrl = new AbortController()
    setTimeout(() => ctrl.abort(), 50)
    const start = Date.now()
    const result = await executor.run(spec({ cmd: 'sleep 5' }), ctrl.signal)
    const elapsed = Date.now() - start

    // Aborted, not timed out — the signal won the race.
    expect(result.timedOut).toBe(false)
    expect(result.exitCode).toBeNull()
    expect(elapsed).toBeLessThan(5_000)
  })

  it('returns immediately when the signal is already aborted', async () => {
    const { executor } = makeExecutor()
    const ctrl = new AbortController()
    ctrl.abort()
    const result = await executor.run(spec({ cmd: 'echo hi' }), ctrl.signal)

    expect(result.exitCode).toBeNull()
    expect(result.timedOut).toBe(false)
    expect(result.outputTail).toBe('')
    expect(result.outputHash).toMatch(/^[0-9a-f]{64}$/)
  })

  it('detects config error from "missing script" output', async () => {
    const { executor } = makeExecutor()
    // Hermetic: simulate npm's exact phrasing without depending on a real
    // package.json or installed npm.
    const result = await executor.run(
      spec({
        cmd: 'echo "npm ERR! missing script: nonexistent" >&2; exit 1',
      }),
      new AbortController().signal,
    )

    expect(result.exitCode).toBe(1)
    expect(result.wasConfigError).toBe(true)
  })

  it('detects config error from "command not found"', async () => {
    const { executor } = makeExecutor()
    const result = await executor.run(
      spec({ cmd: 'this_command_definitely_does_not_exist_xyz_12345' }),
      new AbortController().signal,
    )

    expect(result.exitCode).not.toBe(0)
    expect(result.wasConfigError).toBe(true)
  })

  it('caps tail at 100 lines and progress at 50 stdout lines', async () => {
    const { executor } = makeExecutor()
    const result = await executor.run(
      spec({ cmd: 'for i in $(seq 1 200); do echo "line $i"; done' }),
      new AbortController().signal,
    )

    expect(result.exitCode).toBe(0)
    const tailLines = result.outputTail.split('\n')
    expect(tailLines).toHaveLength(100)
    expect(tailLines[0]).toBe('line 101')
    expect(tailLines[99]).toBe('line 200')

    expect(result.onProgressLines).toHaveLength(50)
    expect(result.onProgressLines[0]).toBe('line 1')
    expect(result.onProgressLines[49]).toBe('line 50')
  })

  it('produces a deterministic hash for identical commands', async () => {
    const { executor } = makeExecutor()
    const a = await executor.run(spec({ cmd: 'echo hello' }), new AbortController().signal)
    const b = await executor.run(spec({ cmd: 'echo hello' }), new AbortController().signal)
    const c = await executor.run(spec({ cmd: 'echo world' }), new AbortController().signal)

    expect(a.outputHash).toBe(b.outputHash)
    expect(a.outputHash).not.toBe(c.outputHash)
  })

  it('returns a 64-char lowercase hex hash', async () => {
    const { executor } = makeExecutor()
    const result = await executor.run(
      spec({ cmd: 'echo deterministic' }),
      new AbortController().signal,
    )

    expect(result.outputHash).toMatch(/^[0-9a-f]{64}$/)
  })

  it('truncates pathologically-long single lines', async () => {
    const { executor } = makeExecutor()
    // 20 KB of zeros on a single line, then newline.
    const result = await executor.run(
      spec({ cmd: 'printf "%020000d" 0; echo' }),
      new AbortController().signal,
    )

    expect(result.exitCode).toBe(0)
    // The line should be truncated to ~16 KB with the suffix appended.
    expect(result.outputTail).toContain('...[truncated]')
    expect(result.outputTail.length).toBeLessThanOrEqual(16 * 1024 + 64)
  })

  it('captures a single-line stdout without trailing newline', async () => {
    const { executor } = makeExecutor()
    const result = await executor.run(
      spec({ cmd: 'printf "no newline"' }),
      new AbortController().signal,
    )

    expect(result.exitCode).toBe(0)
    expect(result.outputTail).toBe('no newline')
    expect(result.onProgressLines).toEqual(['no newline'])
  })

  it('logs start with hook+cmd and completion with metrics', async () => {
    const { executor, logger } = makeExecutor()
    await executor.run(
      spec({ name: 'unit-test', cmd: 'echo logged' }),
      new AbortController().signal,
    )

    expect(logger.info).toHaveBeenCalledWith(
      expect.objectContaining({ hook: 'unit-test', cmd: 'echo logged' }),
      'hook starting',
    )
    expect(logger.debug).toHaveBeenCalledWith(
      expect.objectContaining({
        hook: 'unit-test',
        exitCode: 0,
        timedOut: false,
        wasConfigError: false,
      }),
      'hook completed',
    )
  })

  it('warns + clamps timeout above 30 minutes', async () => {
    const { executor, logger } = makeExecutor()
    await executor.run(
      spec({ cmd: 'echo ok', timeoutMs: 60 * 60 * 1000 }),
      new AbortController().signal,
    )

    expect(logger.warn).toHaveBeenCalledWith(
      expect.objectContaining({ requested: 60 * 60 * 1000, max: 30 * 60 * 1000 }),
      'hook timeout clamped to 30 min',
    )
  })
})

// ============================================================================
// SIGTERM → SIGKILL escalation (Spec 13 Card 10)
// ============================================================================
//
// These tests use a fake spawn so we can verify the escalation timing without
// relying on a real child process's behaviour under signals. The fake child
// is a hand-rolled EventEmitter-based stand-in matching the surface
// HookExecutor consumes (stdout/stderr sub-emitters, pid, kill, close event).

class FakeChild extends EventEmitter {
  stdout = new EventEmitter()
  stderr = new EventEmitter()
  pid = 99001
  killed = false
  exitCode: number | null = null
  kill = vi.fn((_sig?: NodeJS.Signals): boolean => {
    this.killed = true
    return true
  })
}

function makeFakeSpawn(): { child: FakeChild; spawn: ReturnType<typeof vi.fn> } {
  const child = new FakeChild()
  const spawn = vi.fn(() => child)
  return { child, spawn }
}

function makeFakeExecutor(opts: {
  spawn: ReturnType<typeof vi.fn>
  killEscalationMs?: number
}) {
  const logger = makeLogger()
  const executor = new HookExecutor({
    cwd: '/tmp',
    env: { ...process.env, PATH: process.env['PATH'] ?? '/usr/bin:/bin' },
    logger: logger as unknown as Logger,
    spawn: opts.spawn as never,
    ...(opts.killEscalationMs !== undefined ? { killEscalationMs: opts.killEscalationMs } : {}),
  })
  return { executor, logger }
}

describe('HookExecutor SIGTERM → SIGKILL escalation', () => {
  beforeEach(() => {
    vi.useFakeTimers()
  })

  afterEach(() => {
    vi.useRealTimers()
    vi.restoreAllMocks()
  })

  it('escalates to SIGKILL 10s after abort if child does not close', async () => {
    const { child, spawn } = makeFakeSpawn()
    const killSpy = vi.spyOn(process, 'kill').mockImplementation(() => true)
    const { executor } = makeFakeExecutor({ spawn, killEscalationMs: 10_000 })

    const ctrl = new AbortController()
    const promise = executor.run(spec({ cmd: 'sleep 999' }), ctrl.signal)

    // Abort while the (fake) child is "running".
    ctrl.abort()

    // 9s after abort: SIGTERM has been sent, SIGKILL has not.
    await vi.advanceTimersByTimeAsync(9_000)
    expect(killSpy).toHaveBeenCalledTimes(1)
    expect(killSpy).toHaveBeenNthCalledWith(1, -99001, 'SIGTERM')

    // Cross the 10s threshold: SIGKILL fires.
    await vi.advanceTimersByTimeAsync(1_000)
    expect(killSpy).toHaveBeenCalledTimes(2)
    expect(killSpy).toHaveBeenNthCalledWith(2, -99001, 'SIGKILL')

    // Now let the child "finally close" so run() resolves.
    child.exitCode = null
    child.emit('close', null)
    const result = await promise

    expect(result.timedOut).toBe(false) // abort path, not timeout
    expect(result.exitCode).toBeNull()
  })

  it('escalates to SIGKILL 10s after timeout if child does not close', async () => {
    const { child, spawn } = makeFakeSpawn()
    const killSpy = vi.spyOn(process, 'kill').mockImplementation(() => true)
    const { executor } = makeFakeExecutor({ spawn, killEscalationMs: 10_000 })

    const promise = executor.run(
      spec({ cmd: 'sleep 999', timeoutMs: 1_000 }),
      new AbortController().signal,
    )

    // Cross the timeout: SIGTERM dispatched.
    await vi.advanceTimersByTimeAsync(1_001)
    expect(killSpy).toHaveBeenCalledTimes(1)
    expect(killSpy).toHaveBeenNthCalledWith(1, -99001, 'SIGTERM')

    // 10s past SIGTERM: SIGKILL escalation.
    await vi.advanceTimersByTimeAsync(10_000)
    expect(killSpy).toHaveBeenCalledTimes(2)
    expect(killSpy).toHaveBeenNthCalledWith(2, -99001, 'SIGKILL')

    child.exitCode = null
    child.emit('close', null)
    const result = await promise
    expect(result.timedOut).toBe(true)
  })

  it('does not SIGKILL when child closes before escalation deadline', async () => {
    const { child, spawn } = makeFakeSpawn()
    const killSpy = vi.spyOn(process, 'kill').mockImplementation(() => true)
    const { executor, logger } = makeFakeExecutor({ spawn, killEscalationMs: 10_000 })

    const ctrl = new AbortController()
    const promise = executor.run(spec({ cmd: 'sleep 999' }), ctrl.signal)
    ctrl.abort()

    // Child closes 5s after the abort — well before the 10s escalation.
    await vi.advanceTimersByTimeAsync(5_000)
    child.exitCode = null
    child.emit('close', null)
    await promise

    // Drive past where the escalation timer would have fired.
    await vi.advanceTimersByTimeAsync(20_000)

    // Only the initial SIGTERM was sent — no SIGKILL.
    expect(killSpy).toHaveBeenCalledTimes(1)
    expect(killSpy).toHaveBeenNthCalledWith(1, -99001, 'SIGTERM')
    expect(logger.warn).not.toHaveBeenCalledWith(
      expect.anything(),
      'sigterm timeout — escalating to sigkill',
    )
  })
})
