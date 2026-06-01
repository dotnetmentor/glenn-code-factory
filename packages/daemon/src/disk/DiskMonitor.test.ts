// Tests for DiskMonitor. Uses vitest fake timers like HeartbeatModule.test.ts;
// `vi.advanceTimersByTimeAsync` is the only correct way to flush both the
// scheduled tick AND the inner `await statfs(...)` microtask before assertions.
//
// We mock `node:fs/promises` so we can drive the sample sequence deterministically.

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

vi.mock('node:fs/promises', () => ({
  statfs: vi.fn(),
}))

import { statfs } from 'node:fs/promises'

import { DiskMonitor, type DiskPressureLevel, type DiskSample } from './DiskMonitor.js'

// ============================================================================
// Test helpers
// ============================================================================

const TOTAL_BYTES = 100 * 1024 * 1024 * 1024 // 100 GB
const BSIZE = 4096
const TOTAL_BLOCKS = TOTAL_BYTES / BSIZE

/** Build a Number-typed StatsFs result for a given used fraction. */
function statfsResult(usedFraction: number) {
  const usedBlocks = Math.round(TOTAL_BLOCKS * usedFraction)
  const bavail = TOTAL_BLOCKS - usedBlocks
  return {
    type: 0,
    bsize: BSIZE,
    blocks: TOTAL_BLOCKS,
    bfree: bavail,
    bavail,
    files: 0,
    ffree: 0,
  }
}

/** Build a BigInt-typed StatsFs result (matches BigIntStatsFs shape). */
function bigIntStatfsResult(usedFraction: number) {
  const usedBlocks = Math.round(TOTAL_BLOCKS * usedFraction)
  const bavail = TOTAL_BLOCKS - usedBlocks
  return {
    type: 0n,
    bsize: BigInt(BSIZE),
    blocks: BigInt(TOTAL_BLOCKS),
    bfree: BigInt(bavail),
    bavail: BigInt(bavail),
    files: 0n,
    ffree: 0n,
  }
}

/**
 * Pino-shaped stub. `child` returns the same instance so messages from
 * `.child({module: 'disk-monitor'})` still land in the same calls map.
 */
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

/** Cast a StatsFs-shaped object to whatever `statfs` returns (covers BigInt mock too). */
function asStatfsReturn(value: unknown): never {
  return value as never
}

const statfsMock = vi.mocked(statfs)

// ============================================================================
// Tests
// ============================================================================

describe('DiskMonitor', () => {
  beforeEach(() => {
    vi.useFakeTimers()
    statfsMock.mockReset()
  })

  afterEach(() => {
    vi.useRealTimers()
    vi.restoreAllMocks()
  })

  describe('constructor', () => {
    it('throws when warnThreshold is 0', () => {
      expect(() => new DiskMonitor({ path: '/data', warnThreshold: 0 })).toThrow(RangeError)
    })

    it('throws when warnThreshold is >= 1', () => {
      expect(() => new DiskMonitor({ path: '/data', warnThreshold: 1.5 })).toThrow(RangeError)
      expect(() => new DiskMonitor({ path: '/data', warnThreshold: 1 })).toThrow(RangeError)
    })

    it('throws when criticalThreshold <= warnThreshold', () => {
      expect(
        () => new DiskMonitor({ path: '/data', warnThreshold: 0.8, criticalThreshold: 0.8 }),
      ).toThrow(RangeError)
      expect(
        () => new DiskMonitor({ path: '/data', warnThreshold: 0.8, criticalThreshold: 0.5 }),
      ).toThrow(RangeError)
    })

    it('throws when criticalThreshold >= 1', () => {
      expect(
        () => new DiskMonitor({ path: '/data', warnThreshold: 0.8, criticalThreshold: 1 }),
      ).toThrow(RangeError)
    })

    it('throws when intervalMs is < 1000', () => {
      expect(() => new DiskMonitor({ path: '/data', intervalMs: 500 })).toThrow(RangeError)
    })

    it('applies defaults when options are omitted', () => {
      const m = new DiskMonitor({ path: '/data' })
      // Defaults are private — observe behaviour: level() is 'ok' before any sample,
      // and constructing succeeds (which proves intervalMs/warn/critical defaults
      // pass their own validators).
      expect(m.level()).toBe('ok')
      expect(m.latest()).toBeNull()
    })
  })

  describe('latest() / level()', () => {
    it('latest() is null before first sample', () => {
      const m = new DiskMonitor({ path: '/data', intervalMs: 1000 })
      expect(m.latest()).toBeNull()
      expect(m.level()).toBe('ok')
    })

    it('first immediate sample populates latest()', async () => {
      statfsMock.mockResolvedValueOnce(asStatfsReturn(statfsResult(0.5)))
      const m = new DiskMonitor({ path: '/data', intervalMs: 1000 })
      m.start()
      // The immediate tick is `void this.#tick()` — flush microtasks so the inner
      // `await statfs(...)` resolves before we assert.
      await vi.advanceTimersByTimeAsync(0)

      const latest = m.latest()
      expect(latest).not.toBeNull()
      expect(latest?.totalBytes).toBe(TOTAL_BYTES)
      expect(latest?.usedBytes).toBe(Math.round(TOTAL_BLOCKS * 0.5) * BSIZE)
      expect(latest?.sampledAt).toBeInstanceOf(Date)
      m.stop()
    })
  })

  describe('pressure transitions', () => {
    it('emits exactly one event per transition (ok -> warn -> critical -> ok)', async () => {
      statfsMock
        .mockResolvedValueOnce(asStatfsReturn(statfsResult(0.5))) // ok
        .mockResolvedValueOnce(asStatfsReturn(statfsResult(0.85))) // warn
        .mockResolvedValueOnce(asStatfsReturn(statfsResult(0.96))) // critical
        .mockResolvedValueOnce(asStatfsReturn(statfsResult(0.7))) // ok

      const m = new DiskMonitor({ path: '/data', intervalMs: 1000 })
      const events: Array<{ level: DiskPressureLevel; sample: DiskSample }> = []
      m.on('pressure', (level, sample) => events.push({ level, sample }))

      m.start()
      // Immediate tick (sample 1): 50% — initial level is already 'ok', so NO event.
      await vi.advanceTimersByTimeAsync(0)
      expect(events).toHaveLength(0)
      expect(m.level()).toBe('ok')

      // Tick 2 (sample: 85%): ok -> warn.
      await vi.advanceTimersByTimeAsync(1000)
      expect(events).toHaveLength(1)
      expect(events[0]?.level).toBe('warn')
      expect(m.level()).toBe('warn')

      // Tick 3 (sample: 96%): warn -> critical.
      await vi.advanceTimersByTimeAsync(1000)
      expect(events).toHaveLength(2)
      expect(events[1]?.level).toBe('critical')
      expect(m.level()).toBe('critical')

      // Tick 4 (sample: 70%): critical -> ok.
      await vi.advanceTimersByTimeAsync(1000)
      expect(events).toHaveLength(3)
      expect(events[2]?.level).toBe('ok')
      expect(m.level()).toBe('ok')

      // Each event carries the sample at the time of transition.
      expect(events[0]?.sample.usedBytes).toBe(Math.round(TOTAL_BLOCKS * 0.85) * BSIZE)
      expect(events[1]?.sample.usedBytes).toBe(Math.round(TOTAL_BLOCKS * 0.96) * BSIZE)
      expect(events[2]?.sample.usedBytes).toBe(Math.round(TOTAL_BLOCKS * 0.7) * BSIZE)

      m.stop()
    })

    it('emits NO events when samples stay within the same band (ok)', async () => {
      statfsMock
        .mockResolvedValueOnce(asStatfsReturn(statfsResult(0.5)))
        .mockResolvedValueOnce(asStatfsReturn(statfsResult(0.6)))
        .mockResolvedValueOnce(asStatfsReturn(statfsResult(0.7)))

      const m = new DiskMonitor({ path: '/data', intervalMs: 1000 })
      const events: DiskPressureLevel[] = []
      m.on('pressure', (level) => events.push(level))

      m.start()
      await vi.advanceTimersByTimeAsync(0)
      await vi.advanceTimersByTimeAsync(1000)
      await vi.advanceTimersByTimeAsync(1000)

      expect(events).toEqual([])
      expect(m.level()).toBe('ok')
      m.stop()
    })

    it('emits ONE event for stable critical samples (no repeats)', async () => {
      statfsMock
        .mockResolvedValueOnce(asStatfsReturn(statfsResult(0.96)))
        .mockResolvedValueOnce(asStatfsReturn(statfsResult(0.97)))
        .mockResolvedValueOnce(asStatfsReturn(statfsResult(0.98)))

      const m = new DiskMonitor({ path: '/data', intervalMs: 1000 })
      const events: DiskPressureLevel[] = []
      m.on('pressure', (level) => events.push(level))

      m.start()
      // Immediate sample: ok -> critical (one event).
      await vi.advanceTimersByTimeAsync(0)
      // Two more stable-critical samples: no further events.
      await vi.advanceTimersByTimeAsync(1000)
      await vi.advanceTimersByTimeAsync(1000)

      expect(events).toEqual(['critical'])
      expect(m.level()).toBe('critical')
      m.stop()
    })
  })

  describe('lifecycle', () => {
    it('stop() halts ticking', async () => {
      statfsMock.mockResolvedValue(asStatfsReturn(statfsResult(0.5)))

      const m = new DiskMonitor({ path: '/data', intervalMs: 1000 })
      m.start()
      await vi.advanceTimersByTimeAsync(0)
      const callsAfterImmediate = statfsMock.mock.calls.length
      expect(callsAfterImmediate).toBe(1)

      m.stop()
      await vi.advanceTimersByTimeAsync(60_000)
      expect(statfsMock.mock.calls.length).toBe(callsAfterImmediate)
    })

    it('stop() is idempotent', () => {
      const m = new DiskMonitor({ path: '/data', intervalMs: 1000 })
      m.start()
      expect(() => m.stop()).not.toThrow()
      expect(() => m.stop()).not.toThrow()
    })

    it('start() is idempotent (no double immediate tick, no duplicate timer)', async () => {
      statfsMock.mockResolvedValue(asStatfsReturn(statfsResult(0.5)))

      const m = new DiskMonitor({ path: '/data', intervalMs: 1000 })
      m.start()
      m.start() // second call must be a no-op
      await vi.advanceTimersByTimeAsync(0)
      // Only the first start's immediate tick.
      expect(statfsMock).toHaveBeenCalledTimes(1)

      // After one interval, exactly one additional tick (not two from a duplicate timer).
      await vi.advanceTimersByTimeAsync(1000)
      expect(statfsMock).toHaveBeenCalledTimes(2)
      m.stop()
    })
  })

  describe('error handling', () => {
    it('does NOT emit pressure when statfs rejects, and keeps ticking', async () => {
      statfsMock
        .mockRejectedValueOnce(new Error('EIO'))
        .mockResolvedValueOnce(asStatfsReturn(statfsResult(0.96)))

      const log = makeLogger()
      const m = new DiskMonitor({
        path: '/data',
        intervalMs: 1000,
        logger: log as unknown as import('pino').Logger,
      })
      const events: DiskPressureLevel[] = []
      m.on('pressure', (level) => events.push(level))

      m.start()
      await vi.advanceTimersByTimeAsync(0)
      // No event from the failed sample.
      expect(events).toEqual([])
      // logger.error called.
      expect(log.error).toHaveBeenCalledTimes(1)
      const errCall = log.error.mock.calls[0]
      expect(errCall?.[1]).toMatch(/failed to sample disk usage/)
      // latest still null because the sample never succeeded.
      expect(m.latest()).toBeNull()

      // Next tick succeeds — transition fires.
      await vi.advanceTimersByTimeAsync(1000)
      expect(events).toEqual(['critical'])
      expect(m.level()).toBe('critical')
      m.stop()
    })

    it('handles BigInt-typed statfs fields (some Node 20.x versions)', async () => {
      statfsMock.mockResolvedValueOnce(asStatfsReturn(bigIntStatfsResult(0.5)))

      const m = new DiskMonitor({ path: '/data', intervalMs: 1000 })
      m.start()
      await vi.advanceTimersByTimeAsync(0)

      const latest = m.latest()
      expect(latest).not.toBeNull()
      // Both fields must be plain Numbers, not bigints — arithmetic should work.
      expect(typeof latest?.usedBytes).toBe('number')
      expect(typeof latest?.totalBytes).toBe('number')
      expect(latest?.totalBytes).toBe(TOTAL_BYTES)
      expect(latest!.usedBytes + 1).toBe(Math.round(TOTAL_BLOCKS * 0.5) * BSIZE + 1)
      m.stop()
    })

    it('is silent (no throw) when no logger is provided and a transition occurs', async () => {
      statfsMock
        .mockResolvedValueOnce(asStatfsReturn(statfsResult(0.5)))
        .mockResolvedValueOnce(asStatfsReturn(statfsResult(0.96)))

      const m = new DiskMonitor({ path: '/data', intervalMs: 1000 })
      const events: DiskPressureLevel[] = []
      m.on('pressure', (level) => events.push(level))

      m.start()
      await vi.advanceTimersByTimeAsync(0)
      await vi.advanceTimersByTimeAsync(1000)

      expect(events).toEqual(['critical'])
      m.stop()
    })

    it('is silent (no throw) when no logger is provided and statfs rejects', async () => {
      statfsMock.mockRejectedValueOnce(new Error('EIO'))
      const m = new DiskMonitor({ path: '/data', intervalMs: 1000 })

      m.start()
      // If the optional-logger pathway threw, advanceTimersByTimeAsync would
      // surface the error. The assertion here is simply that it does not.
      await vi.advanceTimersByTimeAsync(0)
      // Sample never succeeded, so latest stays null.
      expect(m.latest()).toBeNull()
      m.stop()
    })
  })
})
