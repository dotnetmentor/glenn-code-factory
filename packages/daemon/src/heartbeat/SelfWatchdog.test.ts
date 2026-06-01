// SelfWatchdog tests. We don't spawn a real worker_threads worker — that's
// hard to drive deterministically and we don't want a stray SIGKILL to take
// out the vitest runner. Instead we exercise:
//
//   1. The pure `isStalled` decision function — all the edge cases of the
//      worker's tick logic without needing a worker at all.
//   2. The main-thread half via the `workerFactory` + `onStall` test seams.
//      A fake "worker" lets us hand-fire `message`/`error`/`exit` events
//      synchronously and assert the watchdog reacts correctly.
//
// The inline `WORKER_SRC` string is exercised indirectly: it's exactly the
// same shape as `isStalled` + a `setInterval` + `process.kill`. Worth a
// later integration test that spawns the real worker against a stalled
// SharedArrayBuffer, but the unit-test gate is the more valuable signal.

import { EventEmitter } from 'node:events'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import {
  DEFAULT_CHECK_INTERVAL_MS,
  DEFAULT_GRACE_MS,
  DEFAULT_THRESHOLD_MS,
  DEFAULT_UPDATE_INTERVAL_MS,
  isStalled,
  SelfWatchdog,
  type StallMessage,
  type WorkerLike,
  type WorkerOpts,
} from './SelfWatchdog.js'

// ============================================================================
// Test helpers
// ============================================================================

function makeLogger() {
  const calls: Array<{ level: string; obj: unknown; msg: unknown }> = []
  const log = {
    trace: vi.fn((obj: unknown, msg?: unknown) => calls.push({ level: 'trace', obj, msg })),
    debug: vi.fn((obj: unknown, msg?: unknown) => calls.push({ level: 'debug', obj, msg })),
    info: vi.fn((obj: unknown, msg?: unknown) => calls.push({ level: 'info', obj, msg })),
    warn: vi.fn((obj: unknown, msg?: unknown) => calls.push({ level: 'warn', obj, msg })),
    error: vi.fn((obj: unknown, msg?: unknown) => calls.push({ level: 'error', obj, msg })),
    fatal: vi.fn((obj: unknown, msg?: unknown) => calls.push({ level: 'fatal', obj, msg })),
    child: vi.fn(() => log),
  }
  // Cast to any — the real Logger surface is much wider; we only need the
  // methods SelfWatchdog actually calls (.error / .info / .warn / .debug /
  // .child).
  return { log: log as unknown as import('pino').Logger, calls }
}

/**
 * EventEmitter-shaped fake worker. Captures construction args so tests can
 * assert the SharedArrayBuffer + opts were forwarded correctly, and lets
 * tests emit synthetic `message`/`error`/`exit` events to drive the
 * SelfWatchdog's handlers.
 */
function makeFakeWorker() {
  const ee = new EventEmitter()
  const terminate = vi.fn(async () => 0)
  const captured: { sab: SharedArrayBuffer | null; opts: WorkerOpts | null } = {
    sab: null,
    opts: null,
  }
  const factory = (sab: SharedArrayBuffer, opts: WorkerOpts): WorkerLike => {
    captured.sab = sab
    captured.opts = opts
    // The EventEmitter pass-through is type-erased on purpose: WorkerLike has
    // an overloaded `on()` and the structural-typed fake doesn't bother
    // matching every overload — it just forwards to the underlying emitter.
    const worker = {
      on: (event: string, listener: (...args: unknown[]) => void) => {
        ee.on(event, listener)
      },
      terminate,
    }
    return worker as unknown as WorkerLike
  }
  return { factory, ee, terminate, captured }
}

// ============================================================================
// Tests
// ============================================================================

describe('isStalled', () => {
  it('treats zero last as not-yet-started — never a stall', () => {
    expect(isStalled(1_000_000, 0, 100)).toBe(false)
    expect(isStalled(Number.MAX_SAFE_INTEGER, 0, 1)).toBe(false)
  })

  it('treats negative last as not-yet-started (defensive)', () => {
    expect(isStalled(1_000_000, -1, 100)).toBe(false)
  })

  it('returns false when within threshold', () => {
    expect(isStalled(1_100, 1_000, 200)).toBe(false) // 100ms gap, 200ms threshold
    expect(isStalled(1_200, 1_000, 200)).toBe(false) // exactly at threshold — strict greater-than
  })

  it('returns true when beyond threshold', () => {
    expect(isStalled(1_201, 1_000, 200)).toBe(true) // 201ms gap, 200ms threshold
    expect(isStalled(100_000, 1_000, 25_000)).toBe(true)
  })

  it('handles realistic 50s threshold', () => {
    const now = Date.now()
    expect(isStalled(now, now - 1_000, DEFAULT_THRESHOLD_MS)).toBe(false) // 1s silent
    expect(isStalled(now, now - 49_000, DEFAULT_THRESHOLD_MS)).toBe(false) // 49s silent
    expect(isStalled(now, now - 55_000, DEFAULT_THRESHOLD_MS)).toBe(true) // 55s silent
  })
})

describe('SelfWatchdog', () => {
  beforeEach(() => {
    vi.useFakeTimers()
  })

  afterEach(() => {
    vi.useRealTimers()
  })

  it('start() seeds the buffer immediately and forwards opts to the worker factory', () => {
    const { log } = makeLogger()
    const { factory, captured } = makeFakeWorker()
    const wd = new SelfWatchdog({
      logger: log,
      workerFactory: factory,
      onStall: vi.fn(),
    })

    wd.start()

    // Seeded — buffer is non-zero immediately, before the first interval fires.
    expect(wd._lastWriteMs()).toBeGreaterThan(0)
    // Forwarded the SharedArrayBuffer + the resolved (default) opts.
    expect(captured.sab).toBeInstanceOf(SharedArrayBuffer)
    expect(captured.opts).toEqual({
      thresholdMs: DEFAULT_THRESHOLD_MS,
      checkIntervalMs: DEFAULT_CHECK_INTERVAL_MS,
      graceMs: DEFAULT_GRACE_MS,
    })
  })

  it('start() is idempotent — second call does not create a second worker', () => {
    const { log } = makeLogger()
    const factory = vi.fn(makeFakeWorker().factory)
    const wd = new SelfWatchdog({
      logger: log,
      workerFactory: factory,
      onStall: vi.fn(),
    })

    wd.start()
    wd.start()
    wd.start()

    expect(factory).toHaveBeenCalledTimes(1)
  })

  it('the updater interval refreshes the buffer timestamp', async () => {
    const { log } = makeLogger()
    const { factory } = makeFakeWorker()
    const wd = new SelfWatchdog({
      logger: log,
      updateIntervalMs: 100,
      workerFactory: factory,
      onStall: vi.fn(),
    })

    // Pin fake-timer clock to a known epoch.
    vi.setSystemTime(new Date(1_700_000_000_000))
    wd.start()
    const seeded = wd._lastWriteMs()
    expect(seeded).toBe(1_700_000_000_000)

    // Advance 250 ms — interval fires at +100, +200 ms relative to start.
    // Fake-timer clock auto-advances during `advanceTimersByTimeAsync`, so
    // by the time the second tick fires, `Date.now()` is already past
    // `seeded + 200`. We assert "buffer was refreshed at least once" rather
    // than a brittle exact equality.
    await vi.advanceTimersByTimeAsync(250)

    const after = wd._lastWriteMs()
    expect(after).toBeGreaterThanOrEqual(seeded + 100)
  })

  it('forwards a STALL message to the onStall handler with logging', () => {
    const { log, calls } = makeLogger()
    const { factory, ee } = makeFakeWorker()
    const onStall = vi.fn()
    const wd = new SelfWatchdog({
      logger: log,
      workerFactory: factory,
      onStall,
    })
    wd.start()

    const stall: StallMessage = { type: 'STALL', silentMs: 27_500, thresholdMs: 25_000 }
    ee.emit('message', stall)

    expect(onStall).toHaveBeenCalledWith(stall)
    const errorLog = calls.find((c) => c.level === 'error')
    expect(errorLog).toBeDefined()
    expect(errorLog?.msg).toContain('STALL')
  })

  it('ignores non-STALL messages (forward-compat — worker might post other things later)', () => {
    const { log } = makeLogger()
    const { factory, ee } = makeFakeWorker()
    const onStall = vi.fn()
    const wd = new SelfWatchdog({
      logger: log,
      workerFactory: factory,
      onStall,
    })
    wd.start()

    ee.emit('message', { type: 'SOMETHING_ELSE', foo: 1 } as unknown as StallMessage)
    ee.emit('message', null as unknown as StallMessage)
    ee.emit('message', undefined as unknown as StallMessage)

    expect(onStall).not.toHaveBeenCalled()
  })

  it('a worker error does NOT crash or stop the daemon — just logs', () => {
    const { log, calls } = makeLogger()
    const { factory, ee } = makeFakeWorker()
    const onStall = vi.fn()
    const wd = new SelfWatchdog({
      logger: log,
      workerFactory: factory,
      onStall,
    })
    wd.start()

    ee.emit('error', new Error('worker boom'))

    expect(onStall).not.toHaveBeenCalled()
    // Logged as error but no exit invoked.
    const errorLog = calls.find((c) => c.level === 'error' && String(c.msg).includes('safety net'))
    expect(errorLog).toBeDefined()
  })

  it('an unexpected worker exit logs at warn level', () => {
    const { log, calls } = makeLogger()
    const { factory, ee } = makeFakeWorker()
    const wd = new SelfWatchdog({
      logger: log,
      workerFactory: factory,
      onStall: vi.fn(),
    })
    wd.start()

    ee.emit('exit', 137)

    const warnLog = calls.find((c) => c.level === 'warn')
    expect(warnLog).toBeDefined()
  })

  it('a clean worker exit (code 0) does NOT warn', () => {
    const { log, calls } = makeLogger()
    const { factory, ee } = makeFakeWorker()
    const wd = new SelfWatchdog({
      logger: log,
      workerFactory: factory,
      onStall: vi.fn(),
    })
    wd.start()

    ee.emit('exit', 0)

    const warnLog = calls.find((c) => c.level === 'warn')
    expect(warnLog).toBeUndefined()
  })

  it('stop() terminates the worker and is idempotent', async () => {
    const { log } = makeLogger()
    const { factory, terminate } = makeFakeWorker()
    const wd = new SelfWatchdog({
      logger: log,
      workerFactory: factory,
      onStall: vi.fn(),
    })
    wd.start()

    await wd.stop()
    await wd.stop() // second call does nothing
    await wd.stop()

    expect(terminate).toHaveBeenCalledTimes(1)
    expect(wd._lastWriteMs()).toBe(0) // view was cleared
  })

  it('stop() swallows terminate() rejections', async () => {
    const { log } = makeLogger()
    const ee = new EventEmitter()
    const terminate = vi.fn(async () => {
      throw new Error('terminate boom')
    })
    const factory = (): WorkerLike =>
      ({
        on: (e: string, l: (...args: unknown[]) => void) => ee.on(e, l),
        terminate,
      }) as unknown as WorkerLike

    const wd = new SelfWatchdog({
      logger: log,
      workerFactory: factory,
      onStall: vi.fn(),
    })
    wd.start()

    await expect(wd.stop()).resolves.toBeUndefined()
  })

  it('stop() without prior start() is a no-op', async () => {
    const { log } = makeLogger()
    const { factory, terminate } = makeFakeWorker()
    const wd = new SelfWatchdog({
      logger: log,
      workerFactory: factory,
      onStall: vi.fn(),
    })
    await expect(wd.stop()).resolves.toBeUndefined()
    expect(terminate).not.toHaveBeenCalled()
  })

  it('respects custom thresholds passed in deps', () => {
    const { log } = makeLogger()
    const { factory, captured } = makeFakeWorker()
    const wd = new SelfWatchdog({
      logger: log,
      thresholdMs: 5_000,
      updateIntervalMs: 100,
      checkIntervalMs: 200,
      graceMs: 500,
      workerFactory: factory,
      onStall: vi.fn(),
    })
    wd.start()

    expect(captured.opts).toEqual({
      thresholdMs: 5_000,
      checkIntervalMs: 200,
      graceMs: 500,
    })

    // Sanity-check we didn't drop the DEFAULT_UPDATE_INTERVAL_MS path —
    // updater interval is main-thread, not part of worker opts.
    void wd
    void DEFAULT_UPDATE_INTERVAL_MS // keep import live for readers
  })
})
