// Tests for ShutdownCoordinator. Same pattern as the sibling modules:
// hand-rolled stubs (no vi.mock), pino-shaped logger, vitest fake timers.
//
// The coordinator's only "real" coupling to Node is `process.on/off` and
// `process.exit`. We inject both via `onSignal` and `exit` so the tests can
// drive signals manually without fighting vitest's own SIGINT handling.
//
// FakeTurnRunner extends EventEmitter and exposes the methods the coordinator
// reaches for: setAcceptingNewTurns, state(), cancel(), once(), off(). State
// is driven by the test (assign + emit `idle` to simulate a turn finishing).

import { EventEmitter } from 'node:events'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import { DaemonConfig } from '../config/DaemonConfig.js'
import type { DiskMonitor } from '../disk/DiskMonitor.js'
import type { HeartbeatModule } from '../heartbeat/HeartbeatModule.js'
import type { SignalRClient } from '../signalr/SignalRClient.js'
import type { EmitEventPayload } from '../signalr/types.js'
import type { QuietModeManager } from '../turn/QuietModeManager.js'
import type { TurnRunner } from '../turn/TurnRunner.js'

import { ShutdownCoordinator } from './ShutdownCoordinator.js'

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
 * Minimal stand-in for TurnRunner. We don't extend the real class because that
 * would force us to hand it real SignalR/SDK deps; instead we expose just the
 * surface the coordinator reaches: setAcceptingNewTurns, state(), cancel(),
 * once(), off().
 *
 * State is mutable so a test can simulate "running" then emit('idle') to
 * simulate the turn completing.
 */
type TurnStateKind = 'idle' | 'running' | 'canceling'

class FakeTurnRunner extends EventEmitter {
  stateKind: TurnStateKind = 'idle'
  setAcceptingNewTurns = vi.fn<(b: boolean) => void>()
  cancel = vi.fn<(reason: string) => Promise<void>>(async () => {})
  state = vi.fn<() => { kind: TurnStateKind }>(() => ({ kind: this.stateKind }))
}

function makeHeartbeatStub() {
  const stop = vi.fn<() => void>()
  return { stub: { stop } as unknown as HeartbeatModule, stop }
}

function makeDiskMonitorStub() {
  const stop = vi.fn<() => void>()
  return { stub: { stop } as unknown as DiskMonitor, stop }
}

function makeQuietModeStub() {
  const stop = vi.fn<() => void>()
  return { stub: { stop } as unknown as QuietModeManager, stop }
}

function makeSignalrStub() {
  const emitEvent = vi.fn(async (_p: EmitEventPayload) => {})
  const stop = vi.fn(async () => {})
  const stub = { emitEvent, stop } as unknown as SignalRClient
  return { stub, emitEvent, stop }
}

interface BuildOpts {
  turnTimeoutMs?: number
  initialState?: TurnStateKind
}

interface Built {
  coordinator: ShutdownCoordinator
  config: DaemonConfig
  log: ReturnType<typeof makeLogger>
  turnRunner: FakeTurnRunner
  heartbeatStop: ReturnType<typeof vi.fn>
  diskStop: ReturnType<typeof vi.fn>
  quietStop: ReturnType<typeof vi.fn>
  signalrStop: ReturnType<typeof vi.fn>
  signalrEmit: ReturnType<typeof vi.fn>
  exit: ReturnType<typeof vi.fn>
  /** Map of signal name → handler the coordinator registered. */
  signalHandlers: Map<NodeJS.Signals, () => void>
  /** Map of signal name → cleanup function returned by onSignal. */
  signalCleanups: Map<NodeJS.Signals, ReturnType<typeof vi.fn>>
  /** Mock of onSignal itself, for asserting call counts. */
  onSignal: ReturnType<typeof vi.fn>
}

function build(opts: BuildOpts = {}): Built {
  const env: NodeJS.ProcessEnv = {}
  if (opts.turnTimeoutMs !== undefined) {
    env['DAEMON_TURN_TIMEOUT_MS'] = String(opts.turnTimeoutMs)
  }
  const config = makeConfig(env)
  const log = makeLogger()

  const turnRunner = new FakeTurnRunner()
  if (opts.initialState !== undefined) turnRunner.stateKind = opts.initialState

  const heartbeat = makeHeartbeatStub()
  const disk = makeDiskMonitorStub()
  const quiet = makeQuietModeStub()
  const signalr = makeSignalrStub()
  const exit = vi.fn<(code: number) => void>()

  const signalHandlers = new Map<NodeJS.Signals, () => void>()
  const signalCleanups = new Map<NodeJS.Signals, ReturnType<typeof vi.fn>>()
  const onSignal = vi.fn(
    (sig: NodeJS.Signals, handler: () => void): (() => void) => {
      signalHandlers.set(sig, handler)
      const cleanup = vi.fn<() => void>()
      signalCleanups.set(sig, cleanup)
      return cleanup
    },
  )

  const coordinator = new ShutdownCoordinator({
    turnRunner: turnRunner as unknown as TurnRunner,
    heartbeat: heartbeat.stub,
    signalr: signalr.stub,
    diskMonitor: disk.stub,
    quietMode: quiet.stub,
    config,
    logger: log as unknown as import('pino').Logger,
    exit,
    onSignal,
  })

  return {
    coordinator,
    config,
    log,
    turnRunner,
    heartbeatStop: heartbeat.stop,
    diskStop: disk.stop,
    quietStop: quiet.stop,
    signalrStop: signalr.stop,
    signalrEmit: signalr.emitEvent,
    exit,
    signalHandlers,
    signalCleanups,
    onSignal,
  }
}

/** Flush a few microtask turns so awaited paths get a chance to settle. */
async function flushMicro(rounds = 5): Promise<void> {
  for (let i = 0; i < rounds; i++) {
    await Promise.resolve()
  }
}

// ============================================================================
// Tests
// ============================================================================

describe('ShutdownCoordinator', () => {
  beforeEach(() => {
    vi.useFakeTimers()
  })

  afterEach(() => {
    vi.useRealTimers()
    vi.restoreAllMocks()
  })

  it('runs the full sequence in order when no turn is in flight', async () => {
    const b = build({ initialState: 'idle' })

    // Track call order across all the side-effecting mocks.
    const order: string[] = []
    b.turnRunner.setAcceptingNewTurns.mockImplementation(() =>
      void order.push('setAcceptingNewTurns'),
    )
    b.diskStop.mockImplementation(() => void order.push('disk.stop'))
    b.quietStop.mockImplementation(() => void order.push('quiet.stop'))
    b.heartbeatStop.mockImplementation(() => void order.push('heartbeat.stop'))
    b.signalrEmit.mockImplementation(async () => {
      order.push('signalr.emit')
    })
    b.signalrStop.mockImplementation(async () => {
      order.push('signalr.stop')
    })
    b.exit.mockImplementation(() => void order.push('exit'))

    await b.coordinator.shutdown('test')

    expect(b.turnRunner.setAcceptingNewTurns).toHaveBeenCalledWith(false)
    expect(b.exit).toHaveBeenCalledWith(0)

    // Expected order: refuse → disk → quiet → heartbeat → emit → signalr.stop → exit.
    expect(order).toEqual([
      'setAcceptingNewTurns',
      'disk.stop',
      'quiet.stop',
      'heartbeat.stop',
      'signalr.emit',
      'signalr.stop',
      'exit',
    ])
  })

  it('does not call cancel when the turn finishes before turnTimeoutMs', async () => {
    const b = build({ turnTimeoutMs: 60_000, initialState: 'running' })

    const promise = b.coordinator.shutdown('sigterm')

    // Let the coordinator wire its `once('idle')` listener. The drain race
    // is now sleeping on the 60s timeout vs the idle event.
    await flushMicro()

    // Simulate the turn finishing cleanly.
    b.turnRunner.stateKind = 'idle'
    b.turnRunner.emit('idle')

    // Advance enough to let any post-idle work (signalr emit/stop awaits) settle.
    await vi.advanceTimersByTimeAsync(0)
    await promise

    expect(b.turnRunner.cancel).not.toHaveBeenCalled()
    expect(b.exit).toHaveBeenCalledWith(0)
  })

  it('cancels the turn after turnTimeoutMs and still proceeds to exit(0)', async () => {
    const b = build({ turnTimeoutMs: 60_000, initialState: 'running' })

    // Resolve cancel without ever firing 'idle'. The coordinator must still
    // proceed via the post-cancel settle timeout.
    b.turnRunner.cancel.mockImplementation(async () => {})

    const promise = b.coordinator.shutdown('sigterm')

    // Advance past the drain timeout — coordinator should now call cancel.
    await vi.advanceTimersByTimeAsync(60_000)
    expect(b.turnRunner.cancel).toHaveBeenCalledWith('draining')

    // Settle window (5s) elapses without an idle event — coordinator carries on.
    await vi.advanceTimersByTimeAsync(5_000)
    await promise

    expect(b.exit).toHaveBeenCalledWith(0)
    // Ordering: cancel happened before signalr.stop and before exit.
    expect(b.turnRunner.cancel.mock.invocationCallOrder[0] ?? 0).toBeLessThan(
      b.signalrStop.mock.invocationCallOrder[0] ?? Infinity,
    )
  })

  it('emits the daemon_shutting_down event BEFORE signalr.stop', async () => {
    const b = build({ initialState: 'idle' })
    await b.coordinator.shutdown('sigterm')

    expect(b.signalrEmit).toHaveBeenCalledTimes(1)
    expect(b.signalrStop).toHaveBeenCalledTimes(1)

    const emitOrder = b.signalrEmit.mock.invocationCallOrder[0] ?? Infinity
    const stopOrder = b.signalrStop.mock.invocationCallOrder[0] ?? 0
    expect(emitOrder).toBeLessThan(stopOrder)

    // Verify the emitted payload's embedded event type.
    const payload = b.signalrEmit.mock.calls[0]?.[0] as EmitEventPayload
    const data = JSON.parse(payload.eventData)
    expect(data.type).toBe('daemon_shutting_down')
    expect(data.runtimeId).toBe(RUNTIME_ID)
    expect(data.reason).toBe('sigterm')
  })

  it('still completes shutdown and exits(0) when signalr.stop rejects', async () => {
    const b = build({ initialState: 'idle' })
    b.signalrStop.mockRejectedValueOnce(new Error('hub down'))

    await b.coordinator.shutdown('sigterm')

    expect(b.exit).toHaveBeenCalledWith(0)
    // The warn log captures the failure.
    const warnedAboutStop = b.log.warn.mock.calls.some(
      (call) =>
        typeof call[1] === 'string' && call[1].includes('signalr stop threw'),
    )
    expect(warnedAboutStop).toBe(true)
  })

  it('exits(1) on a second signal arriving during shutdown', async () => {
    const b = build({ turnTimeoutMs: 60_000, initialState: 'running' })
    b.coordinator.install()

    const sigtermHandler = b.signalHandlers.get('SIGTERM')
    expect(sigtermHandler).toBeDefined()

    // First SIGTERM kicks off shutdown — the turn is hanging, so the drain
    // race is still pending and exit(0) hasn't fired yet.
    sigtermHandler!()
    await flushMicro()
    expect(b.exit).not.toHaveBeenCalled()

    // Second SIGTERM during shutdown → forceful exit(1).
    sigtermHandler!()
    expect(b.exit).toHaveBeenCalledWith(1)
  })

  it('install() is idempotent — registers each handler only once', () => {
    const b = build({ initialState: 'idle' })
    b.coordinator.install()
    b.coordinator.install()
    b.coordinator.install()

    // Each signal should have exactly one registration despite three install()
    // calls.
    expect(b.onSignal).toHaveBeenCalledTimes(2) // SIGTERM + SIGINT, once each
    const signals = b.onSignal.mock.calls.map((c) => c[0])
    expect(signals.sort()).toEqual(['SIGINT', 'SIGTERM'])
  })

  it('shutdown() is idempotent — second concurrent call is a no-op', async () => {
    const b = build({ initialState: 'idle' })

    await b.coordinator.shutdown('first')
    await b.coordinator.shutdown('second')

    // setAcceptingNewTurns called exactly once across both invocations.
    expect(b.turnRunner.setAcceptingNewTurns).toHaveBeenCalledTimes(1)
    // exit also only called once.
    expect(b.exit).toHaveBeenCalledTimes(1)
  })

  it('detaches signal handlers on shutdown completion', async () => {
    const b = build({ initialState: 'idle' })
    b.coordinator.install()

    expect(b.signalCleanups.get('SIGTERM')).toBeDefined()
    expect(b.signalCleanups.get('SIGINT')).toBeDefined()

    await b.coordinator.shutdown('test')

    // Both cleanup-fns invoked.
    expect(b.signalCleanups.get('SIGTERM')).toHaveBeenCalledTimes(1)
    expect(b.signalCleanups.get('SIGINT')).toHaveBeenCalledTimes(1)
  })

  it('stops heartbeat BEFORE emitting daemon_shutting_down (so the shutdown event is the last wire activity)', async () => {
    const b = build({ initialState: 'idle' })
    await b.coordinator.shutdown('test')

    const heartbeatStopOrder = b.heartbeatStop.mock.invocationCallOrder[0] ?? Infinity
    const emitOrder = b.signalrEmit.mock.invocationCallOrder[0] ?? 0
    expect(heartbeatStopOrder).toBeLessThan(emitOrder)
  })

  it('continues shutdown when the daemon_shutting_down emit rejects', async () => {
    const b = build({ initialState: 'idle' })
    b.signalrEmit.mockRejectedValueOnce(new Error('hub gone'))

    await b.coordinator.shutdown('test')

    // signalr.stop and exit still happen.
    expect(b.signalrStop).toHaveBeenCalledTimes(1)
    expect(b.exit).toHaveBeenCalledWith(0)

    // Failure was logged at warn level.
    const warnedAboutEmit = b.log.warn.mock.calls.some(
      (call) =>
        typeof call[1] === 'string' && call[1].includes('daemon_shutting_down'),
    )
    expect(warnedAboutEmit).toBe(true)
  })

  it('skips drain entirely when state is idle (no cancel, no timeout wait)', async () => {
    const b = build({ turnTimeoutMs: 60_000, initialState: 'idle' })

    const startedAt = Date.now()
    await b.coordinator.shutdown('test')
    const elapsed = Date.now() - startedAt

    expect(b.turnRunner.cancel).not.toHaveBeenCalled()
    // We didn't have to advance any timers — drain returned synchronously.
    // (Fake timers report Date.now() as the simulated clock; a no-wait drain
    // means no advance, so elapsed is 0.)
    expect(elapsed).toBe(0)
  })
})
