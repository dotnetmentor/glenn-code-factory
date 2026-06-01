// Unit tests for killProcessGroupWithEscalation. We mock `process.kill` via
// vi.spyOn so the test runner's own process tree is never actually signalled,
// and use vitest fake timers to assert escalation timing without sleeping.

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { Logger } from 'pino'

import { killProcessGroupWithEscalation } from './killProcessGroup.js'

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

describe('killProcessGroupWithEscalation', () => {
  beforeEach(() => {
    vi.useFakeTimers()
  })

  afterEach(() => {
    vi.useRealTimers()
    vi.restoreAllMocks()
  })

  it('sends SIGTERM immediately and SIGKILL after escalationMs', async () => {
    const killSpy = vi.spyOn(process, 'kill').mockImplementation(() => true)
    const logger = makeLogger()
    // processClosed never resolves — the escalation timer must fire.
    const processClosed = new Promise<void>(() => {
      /* never resolves */
    })

    killProcessGroupWithEscalation({
      pid: 4242,
      processClosed,
      escalationMs: 10_000,
      logger: logger as unknown as Logger,
      reason: 'timeout',
    })

    // SIGTERM is synchronous on entry.
    expect(killSpy).toHaveBeenCalledTimes(1)
    expect(killSpy).toHaveBeenNthCalledWith(1, -4242, 'SIGTERM')

    // 9s in: still no SIGKILL.
    await vi.advanceTimersByTimeAsync(9_000)
    expect(killSpy).toHaveBeenCalledTimes(1)

    // 10s in: SIGKILL fires.
    await vi.advanceTimersByTimeAsync(1_000)
    expect(killSpy).toHaveBeenCalledTimes(2)
    expect(killSpy).toHaveBeenNthCalledWith(2, -4242, 'SIGKILL')

    // Warn log is structured per spec.
    expect(logger.warn).toHaveBeenCalledWith(
      { pid: 4242, reason: 'timeout', escalationMs: 10_000 },
      'sigterm timeout — escalating to sigkill',
    )
  })

  it('does not escalate to SIGKILL when processClosed resolves first', async () => {
    const killSpy = vi.spyOn(process, 'kill').mockImplementation(() => true)
    const logger = makeLogger()
    let resolveClosed!: () => void
    const processClosed = new Promise<void>((resolve) => {
      resolveClosed = resolve
    })

    killProcessGroupWithEscalation({
      pid: 7777,
      processClosed,
      escalationMs: 10_000,
      logger: logger as unknown as Logger,
      reason: 'abort',
    })

    // SIGTERM fired immediately.
    expect(killSpy).toHaveBeenCalledTimes(1)

    // Close the process well before the escalation deadline.
    resolveClosed()
    await vi.advanceTimersByTimeAsync(5_000)

    // Even past the escalation point, SIGKILL must not fire.
    await vi.advanceTimersByTimeAsync(20_000)
    expect(killSpy).toHaveBeenCalledTimes(1)
    expect(logger.warn).not.toHaveBeenCalled()
  })

  it('is a no-op when pid is undefined', () => {
    const killSpy = vi.spyOn(process, 'kill').mockImplementation(() => true)
    const logger = makeLogger()

    killProcessGroupWithEscalation({
      pid: undefined,
      processClosed: new Promise<void>(() => undefined),
      escalationMs: 10_000,
      logger: logger as unknown as Logger,
      reason: 'timeout',
    })

    expect(killSpy).not.toHaveBeenCalled()
  })

  it('falls back to single-process kill when group-kill throws', () => {
    // First call throws (group-kill ESRCH); second call (single-process) succeeds.
    let invocations = 0
    const killSpy = vi.spyOn(process, 'kill').mockImplementation(((..._args: unknown[]) => {
      invocations++
      if (invocations === 1) {
        const err = new Error('ESRCH') as NodeJS.ErrnoException
        err.code = 'ESRCH'
        throw err
      }
      return true
    }) as unknown as typeof process.kill)

    const logger = makeLogger()
    killProcessGroupWithEscalation({
      pid: 555,
      processClosed: new Promise<void>(() => undefined),
      escalationMs: 10_000,
      logger: logger as unknown as Logger,
      reason: 'timeout',
    })

    expect(killSpy).toHaveBeenCalledTimes(2)
    expect(killSpy).toHaveBeenNthCalledWith(1, -555, 'SIGTERM') // group attempt
    expect(killSpy).toHaveBeenNthCalledWith(2, 555, 'SIGTERM') // single-process fallback
  })

  it('passes the reason through to the warn log', async () => {
    vi.spyOn(process, 'kill').mockImplementation(() => true)
    const logger = makeLogger()

    killProcessGroupWithEscalation({
      pid: 1234,
      processClosed: new Promise<void>(() => undefined),
      escalationMs: 500,
      logger: logger as unknown as Logger,
      reason: 'abort',
    })

    await vi.advanceTimersByTimeAsync(600)
    expect(logger.warn).toHaveBeenCalledWith(
      { pid: 1234, reason: 'abort', escalationMs: 500 },
      'sigterm timeout — escalating to sigkill',
    )
  })
})
