// Tests for HeartbeatModule. Following the same pattern as SignalRClient.test.ts:
// no `vi.mock` of any module — we hand-roll a stub `SignalRClient` that exposes
// just the `sendHeartbeat` method and a stub pino-shaped logger.
//
// We use vitest's fake timers to drive the ticker deterministically. Note that
// the inner `await this.#signalr.sendHeartbeat(payload)` resolves on a
// microtask, so after advancing time we must flush microtasks before asserting
// on the resolved/rejected branches. `vi.runAllTimersAsync()` handles both.

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import { DaemonConfig } from '../config/DaemonConfig.js'
import type { SignalRClient } from '../signalr/SignalRClient.js'
import type { HeartbeatPayload } from '../signalr/types.js'
import { HeartbeatModule } from './HeartbeatModule.js'

// ============================================================================
// Test helpers
// ============================================================================

const VALID_TOKEN =
  'eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJ0ZXN0In0.aGVsbG8td29ybGQtc2lnbmF0dXJlLXNlZ21lbnQ'
const RUNTIME_ID = '11111111-2222-3333-4444-555555555555'

function makeConfig(overrides: NodeJS.ProcessEnv = {}): DaemonConfig {
  return DaemonConfig.fromEnv({
    GLENN_RUNTIME_TOKEN: VALID_TOKEN,
    MAIN_API_URL: 'http://localhost:5338',
    RUNTIME_ID: RUNTIME_ID,
    DAEMON_VERSION: '0.1.0-dev',
    ...overrides,
  })
}

/**
 * Pino-shaped stub. `child` returns the same instance so that messages emitted
 * via `.child({module: 'heartbeat'})` still land in the same mock array.
 */
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
  return { log, calls }
}

/** Stub SignalRClient — only exposes `sendHeartbeat`. */
function makeSignalrStub() {
  const sendHeartbeat = vi.fn(async (_payload: HeartbeatPayload) => {})
  const stub = { sendHeartbeat } as unknown as SignalRClient
  return { stub, sendHeartbeat }
}

const SAMPLE_PAYLOAD: HeartbeatPayload = {
  emittedAt: '2026-05-08T12:00:00.000Z',
  daemonVersion: '0.1.0-dev',
  cpuPercent: 12.5,
  memoryUsedMb: 200,
  diskUsedPct: null,
  supervisedServicesUp: null,
  activeSessionId: null,
  disk: null,
  sysstatsSnapshotJson: null,
}

function makeGather(payload: HeartbeatPayload = SAMPLE_PAYLOAD) {
  return vi.fn<() => HeartbeatPayload>(() => payload)
}

interface BuildOpts {
  heartbeatIntervalMs?: number
  gather?: ReturnType<typeof makeGather>
}

function buildModule(opts: BuildOpts = {}) {
  const env = opts.heartbeatIntervalMs
    ? { DAEMON_HEARTBEAT_INTERVAL_MS: String(opts.heartbeatIntervalMs) }
    : {}
  const config = makeConfig(env)
  const { log, calls } = makeLogger()
  const { stub, sendHeartbeat } = makeSignalrStub()
  const gather = opts.gather ?? makeGather()
  const module = new HeartbeatModule({
    signalr: stub,
    config,
    gather,
    logger: log as unknown as import('pino').Logger,
  })
  return { module, config, log, calls, sendHeartbeat, gather }
}

// ============================================================================
// Tests
// ============================================================================

describe('HeartbeatModule', () => {
  beforeEach(() => {
    vi.useFakeTimers()
  })

  afterEach(() => {
    vi.useRealTimers()
    vi.restoreAllMocks()
  })

  it('fires immediately on start()', async () => {
    const { module, sendHeartbeat } = buildModule({ heartbeatIntervalMs: 5000 })
    module.start()
    // The immediate tick is scheduled via `void this.#tick()` — its
    // sendHeartbeat invocation is a synchronous push into the mock; the awaited
    // resolution is a microtask. Flush the microtask queue.
    await Promise.resolve()
    await Promise.resolve()
    expect(sendHeartbeat).toHaveBeenCalledTimes(1)
    module.stop()
  })

  it('ticks at the configured interval', async () => {
    const { module, sendHeartbeat } = buildModule({ heartbeatIntervalMs: 5000 })
    module.start()
    // Allow the immediate tick to flush.
    await vi.advanceTimersByTimeAsync(0)
    expect(sendHeartbeat).toHaveBeenCalledTimes(1)

    // Advance three intervals; expect 3 more calls (4 total).
    await vi.advanceTimersByTimeAsync(5000)
    await vi.advanceTimersByTimeAsync(5000)
    await vi.advanceTimersByTimeAsync(5000)

    expect(sendHeartbeat).toHaveBeenCalledTimes(4)
    module.stop()
  })

  it('passes the payload from gather() to sendHeartbeat', async () => {
    const customPayload: HeartbeatPayload = {
      emittedAt: '2026-05-08T13:00:00.000Z',
      daemonVersion: '0.1.0-dev',
      cpuPercent: null,
      memoryUsedMb: null,
      diskUsedPct: null,
      supervisedServicesUp: null,
      activeSessionId: null,
      disk: null,
      sysstatsSnapshotJson: null,
    }
    const gather = makeGather(customPayload)
    const { module, sendHeartbeat } = buildModule({ heartbeatIntervalMs: 5000, gather })
    module.start()
    await vi.advanceTimersByTimeAsync(0)
    expect(sendHeartbeat).toHaveBeenCalledTimes(1)
    expect(sendHeartbeat.mock.calls[0]?.[0]).toEqual(customPayload)

    // Verify shape: only the canonical fields.
    const arg = sendHeartbeat.mock.calls[0]?.[0] as HeartbeatPayload
    expect(Object.keys(arg).sort()).toEqual(
      [
        'activeSessionId',
        'cpuPercent',
        'daemonVersion',
        'disk',
        'diskUsedPct',
        'emittedAt',
        'memoryUsedMb',
        'supervisedServicesUp',
        'sysstatsSnapshotJson',
      ].sort(),
    )
    module.stop()
  })

  it('calls gather() once per tick (1:1 with sendHeartbeat)', async () => {
    const { module, sendHeartbeat, gather } = buildModule({ heartbeatIntervalMs: 5000 })
    module.start()
    await vi.advanceTimersByTimeAsync(0)
    await vi.advanceTimersByTimeAsync(5000)
    await vi.advanceTimersByTimeAsync(5000)
    expect(gather).toHaveBeenCalledTimes(sendHeartbeat.mock.calls.length)
    expect(gather).toHaveBeenCalledTimes(3)
    module.stop()
  })

  it('keeps ticking when sendHeartbeat rejects', async () => {
    const { module, sendHeartbeat } = buildModule({ heartbeatIntervalMs: 5000 })
    sendHeartbeat.mockRejectedValueOnce(new Error('disconnected'))

    // start must not throw or reject even though the immediate tick rejects.
    expect(() => module.start()).not.toThrow()

    await vi.advanceTimersByTimeAsync(0)
    expect(sendHeartbeat).toHaveBeenCalledTimes(1)

    // Subsequent ticks still happen.
    await vi.advanceTimersByTimeAsync(5000)
    expect(sendHeartbeat).toHaveBeenCalledTimes(2)

    await vi.advanceTimersByTimeAsync(5000)
    expect(sendHeartbeat).toHaveBeenCalledTimes(3)

    module.stop()
  })

  it('skips the tick (no sendHeartbeat call) when gather() throws, but keeps ticking', async () => {
    const gather = vi.fn<() => HeartbeatPayload>()
    gather.mockImplementationOnce(() => {
      throw new Error('gather boom')
    })
    gather.mockImplementation(() => SAMPLE_PAYLOAD)

    const { module, sendHeartbeat } = buildModule({ heartbeatIntervalMs: 5000, gather })
    module.start()
    await vi.advanceTimersByTimeAsync(0)
    // The first (immediate) tick: gather threw → sendHeartbeat NOT called.
    expect(gather).toHaveBeenCalledTimes(1)
    expect(sendHeartbeat).not.toHaveBeenCalled()

    // Next interval — gather no longer throws, sendHeartbeat fires.
    await vi.advanceTimersByTimeAsync(5000)
    expect(gather).toHaveBeenCalledTimes(2)
    expect(sendHeartbeat).toHaveBeenCalledTimes(1)

    module.stop()
  })

  it('stop() halts ticking', async () => {
    const { module, sendHeartbeat } = buildModule({ heartbeatIntervalMs: 5000 })
    module.start()
    await vi.advanceTimersByTimeAsync(0)
    expect(sendHeartbeat).toHaveBeenCalledTimes(1)

    module.stop()

    await vi.advanceTimersByTimeAsync(60_000)
    // Still just the initial immediate tick.
    expect(sendHeartbeat).toHaveBeenCalledTimes(1)
  })

  it('stop() is idempotent', async () => {
    const { module } = buildModule({ heartbeatIntervalMs: 5000 })
    module.start()
    expect(() => module.stop()).not.toThrow()
    expect(() => module.stop()).not.toThrow()
  })

  it('start() is idempotent', async () => {
    const { module, sendHeartbeat } = buildModule({ heartbeatIntervalMs: 5000 })
    module.start()
    module.start() // second call must not double-fire the immediate tick or schedule a 2nd interval
    await vi.advanceTimersByTimeAsync(0)
    expect(sendHeartbeat).toHaveBeenCalledTimes(1)

    await vi.advanceTimersByTimeAsync(5000)
    // After one interval, exactly one additional tick (not two from a duplicate timer).
    expect(sendHeartbeat).toHaveBeenCalledTimes(2)

    module.stop()
  })

  it('logs at error level when gather() throws', async () => {
    const gather = vi.fn<() => HeartbeatPayload>(() => {
      throw new Error('gather boom')
    })
    const { module, log } = buildModule({ heartbeatIntervalMs: 5000, gather })
    module.start()
    await vi.advanceTimersByTimeAsync(0)

    expect(log.error).toHaveBeenCalledTimes(1)
    const errCall = log.error.mock.calls[0]
    expect(errCall?.[1]).toMatch(/gather/)
    expect(log.debug).not.toHaveBeenCalled()
    module.stop()
  })

  it('logs at debug level (NOT error) when sendHeartbeat rejects', async () => {
    const { module, sendHeartbeat, log } = buildModule({ heartbeatIntervalMs: 5000 })
    sendHeartbeat.mockRejectedValueOnce(new Error('disconnected'))
    module.start()
    await vi.advanceTimersByTimeAsync(0)

    expect(log.debug).toHaveBeenCalledTimes(1)
    const dbgCall = log.debug.mock.calls[0]
    expect(dbgCall?.[1]).toMatch(/heartbeat invoke failed/)
    // Disconnects are normal — never error level.
    expect(log.error).not.toHaveBeenCalled()
    module.stop()
  })
})
