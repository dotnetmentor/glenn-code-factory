// Tests for TokenManager — exercise cold miss, warm hit, near-expiry refresh,
// forced refresh, single-flight dedup, invalidate, and error propagation.
// All asynchrony is driven by a hand-rolled clock + a controllable
// getRepoAccessToken stub (no `vi.useFakeTimers` needed).

import { describe, expect, it, vi } from 'vitest'
import type { Logger } from 'pino'

import { createTokenManager } from './TokenManager.js'

function makeLogger(): Logger {
  const log = {
    trace: vi.fn(),
    debug: vi.fn(),
    info: vi.fn(),
    warn: vi.fn(),
    error: vi.fn(),
    fatal: vi.fn(),
    child: vi.fn(() => log),
  }
  return log as unknown as Logger
}

/**
 * Deterministic clock. The tests advance time by reassigning `current`.
 */
function makeClock(initialIso: string): { now: () => Date; advance(ms: number): void } {
  let current = new Date(initialIso).getTime()
  return {
    now: () => new Date(current),
    advance(ms) {
      current += ms
    },
  }
}

describe('TokenManager', () => {
  it('cold miss → fetches', async () => {
    const getRepoAccessToken = vi.fn(async () => ({
      token: 'tok-1',
      expiresAt: '2026-05-11T01:00:00Z',
    }))
    const clock = makeClock('2026-05-11T00:00:00Z')
    const tm = createTokenManager({
      signalr: { getRepoAccessToken },
      logger: makeLogger(),
      now: clock.now,
    })

    const tok = await tm.getToken('glenn/proj')
    expect(tok).toBe('tok-1')
    expect(getRepoAccessToken).toHaveBeenCalledTimes(1)
    expect(getRepoAccessToken).toHaveBeenCalledWith('glenn/proj')
  })

  it('warm hit → no fetch', async () => {
    const getRepoAccessToken = vi.fn(async () => ({
      token: 'tok-1',
      expiresAt: '2026-05-11T01:00:00Z',
    }))
    const clock = makeClock('2026-05-11T00:00:00Z')
    const tm = createTokenManager({
      signalr: { getRepoAccessToken },
      logger: makeLogger(),
      now: clock.now,
    })

    await tm.getToken('glenn/proj')
    // 30 minutes later, still well inside the 5-minute buffer.
    clock.advance(30 * 60_000)
    const tok = await tm.getToken('glenn/proj')

    expect(tok).toBe('tok-1')
    expect(getRepoAccessToken).toHaveBeenCalledTimes(1)
  })

  it('within 5-min buffer → refreshes', async () => {
    let callCount = 0
    const getRepoAccessToken = vi.fn(async () => {
      callCount += 1
      return { token: `tok-${callCount}`, expiresAt: '2026-05-11T01:00:00Z' }
    })
    const clock = makeClock('2026-05-11T00:00:00Z')
    const tm = createTokenManager({
      signalr: { getRepoAccessToken },
      logger: makeLogger(),
      now: clock.now,
    })

    await tm.getToken('glenn/proj')
    // Jump to t = 56 min — only 4 min of life left, inside the 5-min buffer.
    clock.advance(56 * 60_000)
    const tok = await tm.getToken('glenn/proj')

    expect(tok).toBe('tok-2')
    expect(getRepoAccessToken).toHaveBeenCalledTimes(2)
  })

  it('forceRefresh → refetches even on warm cache', async () => {
    let callCount = 0
    const getRepoAccessToken = vi.fn(async () => {
      callCount += 1
      return { token: `tok-${callCount}`, expiresAt: '2026-05-11T01:00:00Z' }
    })
    const clock = makeClock('2026-05-11T00:00:00Z')
    const tm = createTokenManager({
      signalr: { getRepoAccessToken },
      logger: makeLogger(),
      now: clock.now,
    })

    await tm.getToken('glenn/proj')
    const tok = await tm.getToken('glenn/proj', { forceRefresh: true })

    expect(tok).toBe('tok-2')
    expect(getRepoAccessToken).toHaveBeenCalledTimes(2)
  })

  it('concurrent cold calls → single fetch (single-flight)', async () => {
    let resolveInner: ((v: { token: string; expiresAt: string }) => void) | undefined
    const getRepoAccessToken = vi.fn(
      () =>
        new Promise<{ token: string; expiresAt: string }>((res) => {
          resolveInner = res
        }),
    )
    const tm = createTokenManager({
      signalr: { getRepoAccessToken },
      logger: makeLogger(),
      now: makeClock('2026-05-11T00:00:00Z').now,
    })

    const p1 = tm.getToken('glenn/proj')
    const p2 = tm.getToken('glenn/proj')
    const p3 = tm.getToken('glenn/proj')
    // All three must observe the SAME underlying fetch.
    expect(getRepoAccessToken).toHaveBeenCalledTimes(1)

    resolveInner!({ token: 'tok-shared', expiresAt: '2026-05-11T01:00:00Z' })

    const [t1, t2, t3] = await Promise.all([p1, p2, p3])
    expect(t1).toBe('tok-shared')
    expect(t2).toBe('tok-shared')
    expect(t3).toBe('tok-shared')
    expect(getRepoAccessToken).toHaveBeenCalledTimes(1)
  })

  it('different repos do not share inflight', async () => {
    const getRepoAccessToken = vi.fn(async (repo: string) => ({
      token: `tok-${repo}`,
      expiresAt: '2026-05-11T01:00:00Z',
    }))
    const tm = createTokenManager({
      signalr: { getRepoAccessToken },
      logger: makeLogger(),
      now: makeClock('2026-05-11T00:00:00Z').now,
    })

    const [a, b] = await Promise.all([tm.getToken('foo/a'), tm.getToken('foo/b')])
    expect(a).toBe('tok-foo/a')
    expect(b).toBe('tok-foo/b')
    expect(getRepoAccessToken).toHaveBeenCalledTimes(2)
  })

  it('invalidate → next call fetches fresh', async () => {
    let callCount = 0
    const getRepoAccessToken = vi.fn(async () => {
      callCount += 1
      return { token: `tok-${callCount}`, expiresAt: '2026-05-11T01:00:00Z' }
    })
    const tm = createTokenManager({
      signalr: { getRepoAccessToken },
      logger: makeLogger(),
      now: makeClock('2026-05-11T00:00:00Z').now,
    })

    await tm.getToken('glenn/proj')
    tm.invalidate('glenn/proj')
    const tok = await tm.getToken('glenn/proj')

    expect(tok).toBe('tok-2')
    expect(getRepoAccessToken).toHaveBeenCalledTimes(2)
  })

  it('fetch error propagates and leaves cache empty', async () => {
    const err = new Error('hub down')
    let attempt = 0
    const getRepoAccessToken = vi.fn(async () => {
      attempt += 1
      if (attempt === 1) throw err
      return { token: 'tok-after', expiresAt: '2026-05-11T01:00:00Z' }
    })
    const tm = createTokenManager({
      signalr: { getRepoAccessToken },
      logger: makeLogger(),
      now: makeClock('2026-05-11T00:00:00Z').now,
    })

    await expect(tm.getToken('glenn/proj')).rejects.toBe(err)
    // Cache must not be poisoned — next call attempts a fresh fetch.
    const tok = await tm.getToken('glenn/proj')
    expect(tok).toBe('tok-after')
    expect(getRepoAccessToken).toHaveBeenCalledTimes(2)
  })

  it('accepts expiresAt as Date object (not just ISO string)', async () => {
    const getRepoAccessToken = vi.fn(async () => ({
      token: 'tok-date',
      expiresAt: new Date('2026-05-11T01:00:00Z'),
    }))
    const tm = createTokenManager({
      signalr: { getRepoAccessToken },
      logger: makeLogger(),
      now: makeClock('2026-05-11T00:00:00Z').now,
    })

    const tok = await tm.getToken('glenn/proj')
    expect(tok).toBe('tok-date')
  })

  it('never logs the token value', async () => {
    const logger = makeLogger()
    const getRepoAccessToken = vi.fn(async () => ({
      token: 'SUPER-SECRET-TOKEN-VALUE',
      expiresAt: '2026-05-11T01:00:00Z',
    }))
    const tm = createTokenManager({
      signalr: { getRepoAccessToken },
      logger,
      now: makeClock('2026-05-11T00:00:00Z').now,
    })

    await tm.getToken('glenn/proj')

    const allCalls = JSON.stringify([
      ...(logger.info as ReturnType<typeof vi.fn>).mock.calls,
      ...(logger.debug as ReturnType<typeof vi.fn>).mock.calls,
      ...(logger.warn as ReturnType<typeof vi.fn>).mock.calls,
      ...(logger.error as ReturnType<typeof vi.fn>).mock.calls,
    ])
    expect(allCalls).not.toContain('SUPER-SECRET-TOKEN-VALUE')
  })
})
