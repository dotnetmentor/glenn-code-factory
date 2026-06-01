// Tests for ConnectingStage. Hand-rolled SignalR fake (just `isConnected` +
// `reportBootstrapProgress`), vitest fake timers to deterministically drive
// the poll loop.

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { Logger } from 'pino'

import type { BootstrapContext } from '../BootstrapOrchestrator.js'
import type { DaemonConfig } from '../../config/DaemonConfig.js'
import type { SignalRClient } from '../../signalr/SignalRClient.js'

import { ConnectingStage } from './ConnectingStage.js'

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

function makeContext(
  opts: { signal?: AbortSignal; signalr?: Partial<SignalRClient> } = {},
): BootstrapContext {
  return {
    config: {} as DaemonConfig,
    signalr: (opts.signalr ?? {}) as SignalRClient,
    logger: makeLogger() as unknown as Logger,
    signal: opts.signal ?? new AbortController().signal,
  }
}

describe('ConnectingStage', () => {
  beforeEach(() => {
    vi.useFakeTimers()
  })
  afterEach(() => {
    vi.useRealTimers()
    vi.restoreAllMocks()
  })

  it('returns ok immediately when signalr.isConnected() is already true', async () => {
    const isConnected = vi.fn(() => true)
    const reportBootstrapProgress = vi.fn(async () => {})
    const stage = new ConnectingStage({ signalr: { isConnected } })
    const result = await stage.run(makeContext({ signalr: { reportBootstrapProgress } }))
    expect(result).toEqual({ ok: true })
    // No poll loop necessary — should return on the fast path.
    expect(isConnected).toHaveBeenCalledTimes(1)
  })

  it('polls until isConnected() returns true, then succeeds', async () => {
    let connected = false
    const isConnected = vi.fn(() => connected)
    const reportBootstrapProgress = vi.fn(async () => {})
    const stage = new ConnectingStage({
      signalr: { isConnected },
      pollIntervalMs: 100,
      timeoutMs: 5_000,
    })

    const promise = stage.run(makeContext({ signalr: { reportBootstrapProgress } }))
    // After 250ms still not connected.
    await vi.advanceTimersByTimeAsync(250)
    // Flip on at this point — next poll picks it up.
    connected = true
    await vi.advanceTimersByTimeAsync(200)
    const result = await promise
    expect(result).toEqual({ ok: true })
  })

  it('returns recoverable failure on timeout', async () => {
    const isConnected = vi.fn(() => false)
    const reportBootstrapProgress = vi.fn(async () => {})
    const stage = new ConnectingStage({
      signalr: { isConnected },
      pollIntervalMs: 100,
      timeoutMs: 500,
    })

    const promise = stage.run(makeContext({ signalr: { reportBootstrapProgress } }))
    await vi.advanceTimersByTimeAsync(1_000)
    const result = await promise
    expect(result.ok).toBe(false)
    if (!result.ok) {
      expect(result.recoverable).toBe(true)
      expect(result.reason).toMatch(/did not reach Connected/)
    }
  })

  it('short-circuits with recoverable failure when signal is pre-aborted', async () => {
    const isConnected = vi.fn(() => false)
    const reportBootstrapProgress = vi.fn(async () => {})
    const ac = new AbortController()
    ac.abort()
    const stage = new ConnectingStage({
      signalr: { isConnected },
      pollIntervalMs: 50,
      timeoutMs: 1_000,
    })
    const result = await stage.run(
      makeContext({ signal: ac.signal, signalr: { reportBootstrapProgress } }),
    )
    expect(result).toEqual({ ok: false, reason: 'aborted', recoverable: true })
    expect(isConnected).not.toHaveBeenCalled()
  })

  it('aborts mid-poll loop when signal fires', async () => {
    const isConnected = vi.fn(() => false)
    const reportBootstrapProgress = vi.fn(async () => {})
    const ac = new AbortController()
    const stage = new ConnectingStage({
      signalr: { isConnected },
      pollIntervalMs: 100,
      timeoutMs: 10_000,
    })

    const promise = stage.run(
      makeContext({ signal: ac.signal, signalr: { reportBootstrapProgress } }),
    )
    await vi.advanceTimersByTimeAsync(150)
    ac.abort()
    await vi.advanceTimersByTimeAsync(150)
    const result = await promise
    expect(result.ok).toBe(false)
    if (!result.ok) {
      expect(result.reason).toBe('aborted')
      expect(result.recoverable).toBe(true)
    }
  })

  it('emits completed progress event when already connected (fast path)', async () => {
    vi.useRealTimers()
    const isConnected = vi.fn(() => true)
    const reportBootstrapProgress = vi.fn(async () => {})
    const stage = new ConnectingStage({
      signalr: { isConnected },
      pollIntervalMs: 50,
      timeoutMs: 1_000,
    })

    const result = await stage.run(makeContext({ signalr: { reportBootstrapProgress } }))
    expect(result.ok).toBe(true)
    // Microtask flush so fire-and-forget progress emit lands.
    for (let i = 0; i < 5; i++) await Promise.resolve()
    await new Promise((resolve) => setImmediate(resolve))

    const stages = (
      reportBootstrapProgress.mock.calls as unknown as Array<[{ status: string }]>
    ).map((c) => c[0].status)
    expect(stages).toContain('completed')
  })

  it('progress emit failures are swallowed (never fail the stage)', async () => {
    const isConnected = vi.fn(() => true)
    const reportBootstrapProgress = vi.fn(async () => {
      throw new Error('hub down')
    })
    const stage = new ConnectingStage({ signalr: { isConnected } })
    const result = await stage.run(makeContext({ signalr: { reportBootstrapProgress } }))
    expect(result).toEqual({ ok: true })
  })
})
