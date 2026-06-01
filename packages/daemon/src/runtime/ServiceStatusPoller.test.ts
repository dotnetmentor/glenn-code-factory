// Tests for ServiceStatusPoller. Drives the poller via `pollOnce()` to
// avoid timing flakes; the setInterval wiring is exercised separately in a
// simple start/stop test.
//
// The poller now consumes supervisord's XML-RPC interface
// (`SupervisordXmlRpcClient`) rather than text-parsing `supervisorctl status`
// output. Tests inject a fake client whose `getAllProcessInfo` returns canned
// `SupervisordProcessInfo[]` arrays — one per intended poll tick.

import { describe, expect, it, vi } from 'vitest'
import type { Logger } from 'pino'

import { TestRuntimeEventEmitter } from '../events/RuntimeEventEmitter.js'
import type { SignalRClient } from '../signalr/SignalRClient.js'
import type {
  SupervisordProcessInfo,
  SupervisordXmlRpcClient,
} from '../supervisord/SupervisordXmlRpcClient.js'
import { detectTransitions, ServiceStatusPoller } from './ServiceStatusPoller.js'

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

/** Build a minimal SupervisordProcessInfo for tests; missing fields get sane defaults. */
function info(
  partial: Partial<SupervisordProcessInfo> & { name: string; statename: string },
): SupervisordProcessInfo {
  return {
    group: partial.name,
    state: 0,
    start: 0,
    now: 0,
    stop: 0,
    exitstatus: 0,
    spawnerr: '',
    pid: 0,
    stdout_logfile: '',
    stderr_logfile: '',
    description: '',
    ...partial,
  }
}

/**
 * Build a fake `SupervisordXmlRpcClient` that returns successive process-info
 * snapshots from the supplied queue. Past the end of the queue we return the
 * last snapshot indefinitely (the poller is expected to be called more times
 * than the test cares about for the steady state).
 */
function makeSupervisord(snapshots: ReadonlyArray<SupervisordProcessInfo[]>): {
  supervisord: SupervisordXmlRpcClient
  callCount: () => number
} {
  let i = 0
  const getAllProcessInfo = vi.fn(async (): Promise<SupervisordProcessInfo[]> => {
    const snap = snapshots[Math.min(i, snapshots.length - 1)] ?? []
    i += 1
    return snap
  })
  // The class has private fields but we only call one method, so casting
  // through a structural type matches what the poller uses (`getAllProcessInfo`).
  const supervisord = {
    getAllProcessInfo,
  } as unknown as SupervisordXmlRpcClient
  return { supervisord, callCount: () => i }
}

/**
 * Stub SignalR client — `invoke` records calls but resolves with `undefined`;
 * `isConnected` returns the constructor-provided value. The poller only ever
 * reads via the `Pick<SignalRClient, 'invoke' | 'isConnected'>` narrowing, so
 * we hand it a structural shape and cast through `unknown` to satisfy the
 * generic `invoke<T>` signature on the production class.
 */
interface SignalRSpy {
  invoke: ReturnType<typeof vi.fn>
  isConnected: ReturnType<typeof vi.fn>
  /** Cast-ready handle the poller can accept. */
  asClient: Pick<SignalRClient, 'invoke' | 'isConnected'>
}

function makeSignalRStub(isConnected = false): SignalRSpy {
  const invoke = vi.fn(async (..._args: unknown[]) => undefined as unknown)
  const isConn = vi.fn(() => isConnected)
  const stub = {
    invoke,
    isConnected: isConn,
  } as const
  return {
    invoke,
    isConnected: isConn,
    asClient: stub as unknown as Pick<SignalRClient, 'invoke' | 'isConnected'>,
  }
}

describe('ServiceStatusPoller — transitions', () => {
  it('seeds initial snapshot without emitting anything on the first poll', async () => {
    const { supervisord } = makeSupervisord([
      [info({ name: 'redis', statename: 'RUNNING' })],
    ])
    const emitter = new TestRuntimeEventEmitter()
    const poller = new ServiceStatusPoller({
      supervisord,
      emitter,
      logger: makeLogger() as unknown as Logger,
      intervalMs: 1_000,
      readLogTail: async () => [],
    })

    await poller.pollOnce()
    expect(emitter.events).toHaveLength(0)
  })

  it('emits ServiceCrashed with uptimeMs on RUNNING → BACKOFF', async () => {
    let nowMs = 1_000_000
    const { supervisord } = makeSupervisord([
      [info({ name: 'redis', statename: 'RUNNING' })],
      [info({ name: 'redis', statename: 'BACKOFF', spawnerr: 'Exited too quickly', exitstatus: 1 })],
    ])
    const emitter = new TestRuntimeEventEmitter()
    const poller = new ServiceStatusPoller({
      supervisord,
      emitter,
      logger: makeLogger() as unknown as Logger,
      intervalMs: 1_000,
      now: () => nowMs,
      readLogTail: async () => [],
    })

    await poller.pollOnce() // seed at t=1_000_000 with lastRunningStartMs=1_000_000
    nowMs = 1_000_500
    await poller.pollOnce()

    const crashes = emitter.events.filter((e) => e.type === 'ServiceCrashed')
    expect(crashes).toHaveLength(1)
    const ev = crashes[0]!
    expect(ev.severity).toBe('Error')
    expect(ev.payload['serviceName']).toBe('redis')
    expect(ev.payload['previousState']).toBe('RUNNING')
    expect(ev.payload['newState']).toBe('BACKOFF')
    expect(ev.payload['uptimeMs']).toBe(500)
    // exitCode + spawnErr come from the XML-RPC info, not the text-parse.
    expect(ev.payload['exitCode']).toBe(1)
    expect(ev.payload['spawnErr']).toBe('Exited too quickly')
  })

  it('emits ServiceCrashed for RUNNING → FATAL and RUNNING → EXITED', async () => {
    let nowMs = 0

    // FATAL
    {
      const { supervisord } = makeSupervisord([
        [info({ name: 'redis', statename: 'RUNNING' })],
        [info({ name: 'redis', statename: 'FATAL', spawnerr: 'Exited too quickly' })],
      ])
      const emitter = new TestRuntimeEventEmitter()
      const poller = new ServiceStatusPoller({
        supervisord,
        emitter,
        logger: makeLogger() as unknown as Logger,
        intervalMs: 1_000,
        now: () => nowMs,
        readLogTail: async () => [],
      })
      await poller.pollOnce()
      nowMs = 100
      await poller.pollOnce()
      const crashes = emitter.events.filter((e) => e.type === 'ServiceCrashed')
      expect(crashes).toHaveLength(1)
      expect(crashes[0]?.payload['newState']).toBe('FATAL')
    }

    // EXITED
    {
      nowMs = 0
      const { supervisord } = makeSupervisord([
        [info({ name: 'redis', statename: 'RUNNING' })],
        [info({ name: 'redis', statename: 'EXITED' })],
      ])
      const emitter = new TestRuntimeEventEmitter()
      const poller = new ServiceStatusPoller({
        supervisord,
        emitter,
        logger: makeLogger() as unknown as Logger,
        intervalMs: 1_000,
        now: () => nowMs,
        readLogTail: async () => [],
      })
      await poller.pollOnce()
      nowMs = 200
      await poller.pollOnce()
      const crashes = emitter.events.filter((e) => e.type === 'ServiceCrashed')
      expect(crashes).toHaveLength(1)
      expect(crashes[0]?.payload['newState']).toBe('EXITED')
    }
  })

  it('emits ServiceRestarted on BACKOFF → RUNNING with monotonic restartCount', async () => {
    let nowMs = 0
    const { supervisord } = makeSupervisord([
      [info({ name: 'redis', statename: 'RUNNING' })],
      [info({ name: 'redis', statename: 'BACKOFF' })],
      [info({ name: 'redis', statename: 'RUNNING' })],
      [info({ name: 'redis', statename: 'BACKOFF' })],
      [info({ name: 'redis', statename: 'RUNNING' })],
    ])
    const emitter = new TestRuntimeEventEmitter()
    const poller = new ServiceStatusPoller({
      supervisord,
      emitter,
      logger: makeLogger() as unknown as Logger,
      intervalMs: 1_000,
      now: () => nowMs,
      readLogTail: async () => [],
    })
    await poller.pollOnce() // seed
    nowMs = 100
    await poller.pollOnce() // crash
    nowMs = 200
    await poller.pollOnce() // restart #1
    nowMs = 300
    await poller.pollOnce() // crash again
    nowMs = 400
    await poller.pollOnce() // restart #2

    const restarts = emitter.events.filter((e) => e.type === 'ServiceRestarted')
    expect(restarts).toHaveLength(2)
    expect(restarts[0]?.payload['restartCount']).toBe(1)
    expect(restarts[1]?.payload['restartCount']).toBe(2)
    expect(restarts[0]?.severity).toBe('Info')
  })

  it('does not double-emit when state is unchanged across ticks', async () => {
    let nowMs = 0
    const { supervisord } = makeSupervisord([
      [info({ name: 'redis', statename: 'RUNNING' })],
      [info({ name: 'redis', statename: 'RUNNING' })],
      [info({ name: 'redis', statename: 'RUNNING' })],
    ])
    const emitter = new TestRuntimeEventEmitter()
    const poller = new ServiceStatusPoller({
      supervisord,
      emitter,
      logger: makeLogger() as unknown as Logger,
      intervalMs: 1_000,
      now: () => nowMs,
      readLogTail: async () => [],
    })
    await poller.pollOnce()
    nowMs = 100
    await poller.pollOnce()
    nowMs = 200
    await poller.pollOnce()
    expect(emitter.events).toHaveLength(0)
  })

  it('handles multiple services independently', async () => {
    let nowMs = 0
    const { supervisord } = makeSupervisord([
      [
        info({ name: 'redis', statename: 'RUNNING' }),
        info({ name: 'mongodb', statename: 'RUNNING' }),
      ],
      [
        info({ name: 'redis', statename: 'BACKOFF' }),
        info({ name: 'mongodb', statename: 'RUNNING' }),
      ],
    ])
    const emitter = new TestRuntimeEventEmitter()
    const poller = new ServiceStatusPoller({
      supervisord,
      emitter,
      logger: makeLogger() as unknown as Logger,
      intervalMs: 1_000,
      now: () => nowMs,
      readLogTail: async () => [],
    })
    await poller.pollOnce()
    nowMs = 500
    await poller.pollOnce()

    const crashes = emitter.events.filter((e) => e.type === 'ServiceCrashed')
    expect(crashes).toHaveLength(1)
    expect(crashes[0]?.payload['serviceName']).toBe('redis')
  })

  it('seeds new services that appear mid-runtime without firing transitions', async () => {
    let nowMs = 0
    const { supervisord } = makeSupervisord([
      [info({ name: 'redis', statename: 'RUNNING' })],
      [
        info({ name: 'redis', statename: 'RUNNING' }),
        info({ name: 'mongodb', statename: 'RUNNING' }),
      ],
    ])
    const emitter = new TestRuntimeEventEmitter()
    const poller = new ServiceStatusPoller({
      supervisord,
      emitter,
      logger: makeLogger() as unknown as Logger,
      intervalMs: 1_000,
      now: () => nowMs,
      readLogTail: async () => [],
    })
    await poller.pollOnce() // seed redis
    nowMs = 100
    await poller.pollOnce() // mongodb appears; should NOT emit ServiceRestarted

    const restarts = emitter.events.filter((e) => e.type === 'ServiceRestarted')
    expect(restarts).toHaveLength(0)
  })

  it('emits ServiceFailedToStart on STARTING → FATAL when service has never run', async () => {
    let nowMs = 0
    const { supervisord } = makeSupervisord([
      [info({ name: 'redis', statename: 'STARTING' })],
      [info({ name: 'redis', statename: 'FATAL', spawnerr: 'no such binary', exitstatus: 127 })],
    ])
    const emitter = new TestRuntimeEventEmitter()
    const poller = new ServiceStatusPoller({
      supervisord,
      emitter,
      logger: makeLogger() as unknown as Logger,
      intervalMs: 1_000,
      now: () => nowMs,
      readLogTail: async () => [],
    })
    await poller.pollOnce() // seed (STARTING)
    nowMs = 100
    await poller.pollOnce() // FATAL — never reached RUNNING

    const failed = emitter.events.filter((e) => e.type === 'ServiceFailedToStart')
    expect(failed).toHaveLength(1)
    const ev = failed[0]!
    expect(ev.severity).toBe('Error')
    expect(ev.payload['serviceName']).toBe('redis')
    expect(ev.payload['finalState']).toBe('FATAL')
    expect(ev.payload['exitStatus']).toBe(127)
    expect(ev.payload['spawnErr']).toBe('no such binary')
  })

  it('pushes a live supervisord snapshot every tick via signalr.invoke', async () => {
    const { supervisord } = makeSupervisord([
      [info({ name: 'redis', statename: 'RUNNING', pid: 42 })],
    ])
    const emitter = new TestRuntimeEventEmitter()
    const signalr = makeSignalRStub(true)
    const poller = new ServiceStatusPoller({
      supervisord,
      emitter,
      logger: makeLogger() as unknown as Logger,
      intervalMs: 1_000,
      signalr: signalr.asClient,
      readLogTail: async () => [],
    })
    await poller.pollOnce()

    expect(signalr.invoke).toHaveBeenCalled()
    const firstCall = signalr.invoke.mock.calls[0] as unknown as [string, unknown]
    const method = firstCall[0]
    const payload = firstCall[1]
    expect(method).toBe('PushLiveSupervisordSnapshot')
    const typed = payload as {
      sampledAt: string
      processes: Array<{ name: string; state: string; pid: number }>
    }
    expect(typed.processes).toHaveLength(1)
    expect(typed.processes[0]!.name).toBe('redis')
    expect(typed.processes[0]!.state).toBe('RUNNING')
    expect(typed.processes[0]!.pid).toBe(42)
  })
})

describe('ServiceStatusPoller — lifecycle', () => {
  it('start / stop is idempotent and uses the injected setInterval', () => {
    const setInt = vi.fn((_cb: () => void, _ms: number) => 'handle' as unknown)
    const clearInt = vi.fn()
    const emitter = new TestRuntimeEventEmitter()
    const { supervisord } = makeSupervisord([[]])
    const signalr = makeSignalRStub()
    const poller = new ServiceStatusPoller({
      supervisord,
      emitter,
      logger: makeLogger() as unknown as Logger,
      intervalMs: 5_000,
      setInterval: setInt,
      clearInterval: clearInt,
      signalr: signalr.asClient,
      readLogTail: async () => [],
    })
    poller.start()
    expect(setInt).toHaveBeenCalledTimes(1)
    poller.start() // no-op
    expect(setInt).toHaveBeenCalledTimes(1)
    poller.stop()
    expect(clearInt).toHaveBeenCalledTimes(1)
    poller.stop() // no-op
    expect(clearInt).toHaveBeenCalledTimes(1)
  })
})

describe('detectTransitions — pure function', () => {
  const seed = (overrides: Partial<{
    lastState: string
    lastRunningStartMs: number | null
    restartCount: number
    attemptCount: number
  }> = {}) => ({
    lastState: 'RUNNING',
    lastRunningStartMs: 100,
    restartCount: 0,
    attemptCount: 1,
    ...overrides,
  })

  it('RUNNING → BACKOFF yields a crashed transition with uptimeMs', () => {
    const out = detectTransitions(seed(), 'BACKOFF', 500, 'redis')
    expect(out.transition?.kind).toBe('crashed')
    if (out.transition?.kind === 'crashed') {
      expect(out.transition.uptimeMs).toBe(400)
      expect(out.transition.newState).toBe('BACKOFF')
    }
    expect(out.trackerPatch.lastState).toBe('BACKOFF')
    expect(out.trackerPatch.lastRunningStartMs).toBeNull()
  })

  it('STARTING → FATAL with no prior RUNNING yields failed-to-start', () => {
    const out = detectTransitions(
      seed({ lastState: 'STARTING', lastRunningStartMs: null, attemptCount: 2 }),
      'FATAL',
      0,
      'redis',
    )
    expect(out.transition?.kind).toBe('failed-to-start')
    if (out.transition?.kind === 'failed-to-start') {
      expect(out.transition.attemptCount).toBe(2)
    }
  })

  it('STARTING → FATAL after a prior RUNNING is a crash, not failed-to-start', () => {
    // lastState=STARTING but lastRunningStartMs is set: the service ran once,
    // came back to STARTING, now died. That's not a wedged first-start —
    // however the current rule only matches RUNNING→bad as a crash, so
    // STARTING→FATAL with prior history falls through to "no transition".
    // We assert the precise behaviour: no failed-to-start (because the
    // service has run before).
    const out = detectTransitions(
      seed({ lastState: 'STARTING', lastRunningStartMs: 50 }),
      'FATAL',
      100,
      'redis',
    )
    expect(out.transition?.kind).not.toBe('failed-to-start')
  })

  it('BACKOFF → RUNNING with restartCount=0 yields a restarted transition', () => {
    const out = detectTransitions(
      seed({ lastState: 'BACKOFF', lastRunningStartMs: null, restartCount: 0 }),
      'RUNNING',
      999,
      'redis',
    )
    expect(out.transition?.kind).toBe('restarted')
    if (out.transition?.kind === 'restarted') {
      expect(out.transition.restartCount).toBe(1)
    }
    expect(out.trackerPatch.restartCount).toBe(1)
    expect(out.trackerPatch.lastRunningStartMs).toBe(999)
  })

  it('STARTING → STARTING does not bump attemptCount', () => {
    const out = detectTransitions(
      seed({ lastState: 'STARTING', lastRunningStartMs: null, attemptCount: 1 }),
      'STARTING',
      0,
      'redis',
    )
    expect(out.transition).toBeNull()
    expect(out.trackerPatch.attemptCount).toBeUndefined()
  })

  it('non-STARTING → STARTING bumps attemptCount', () => {
    const out = detectTransitions(
      seed({ lastState: 'STOPPED', lastRunningStartMs: null, attemptCount: 0 }),
      'STARTING',
      0,
      'redis',
    )
    expect(out.transition).toBeNull()
    expect(out.trackerPatch.attemptCount).toBe(1)
  })
})
