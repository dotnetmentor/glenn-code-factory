// Tests for DefaultRuntimeEventEmitter.
//
// Coverage:
//   - emit() dispatches when connected
//   - emit() buffers when disconnected, drains on reconnect
//   - FIFO drop at cap with throttled warning
//   - startTimer pairs timestamps correctly (start carries `startedAt`;
//     end carries `durationMs`)
//   - severity defaults (inferred from suffix)
//   - never throws on invoke failure

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { Logger } from 'pino'

import {
  DefaultRuntimeEventEmitter,
  TestRuntimeEventEmitter,
  inferSeverity,
  type RuntimeEventEnvelope,
  type SignalRInvoker,
} from './RuntimeEventEmitter.js'
import { RuntimeEventTypes } from './RuntimeEventTypes.js'

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

function makeStub(opts: { connected?: boolean } = {}) {
  let connected = opts.connected ?? true
  let connectedCb: (() => void) | undefined
  let disconnectedCb: ((err?: Error) => void) | undefined
  const invokes: Array<{ method: string; payload: unknown }> = []
  const invoke = vi.fn(async (method: string, payload: unknown) => {
    invokes.push({ method, payload })
  })
  const stub: SignalRInvoker = {
    invoke,
    isConnected: () => connected,
    onConnected: (cb) => {
      connectedCb = cb
    },
    onDisconnected: (cb) => {
      disconnectedCb = cb
    },
  }
  return {
    stub,
    invokes,
    invoke,
    setConnected: (val: boolean) => {
      connected = val
      if (val && connectedCb) connectedCb()
      if (!val && disconnectedCb) disconnectedCb()
    },
  }
}

// The connected-path wire shape differs from the in-process envelope: the
// daemon JSON-stringifies `payload` at the wire boundary so it matches the
// server's `RuntimeEventPayloadDto.Payload: string` contract (see
// DefaultRuntimeEventEmitter.#sendNow — passing an object instead triggers an
// InvalidDataException on the .NET argument binder). This helper asserts that
// stringification contract AND decodes the payload back to the structured
// object so the tests can assert on its contents.
function decodeWire(invoke: { method: string; payload: unknown }): RuntimeEventEnvelope {
  const wire = invoke.payload as Omit<RuntimeEventEnvelope, 'payload'> & {
    payload: string
  }
  expect(typeof wire.payload).toBe('string')
  return { ...wire, payload: JSON.parse(wire.payload) as Record<string, unknown> }
}

describe('inferSeverity', () => {
  it('maps *Failed → Error', () => {
    expect(inferSeverity('InstallFailed')).toBe('Error')
    expect(inferSeverity('SetupCommandFailed')).toBe('Error')
  })
  it('maps *Crashed → Error', () => {
    expect(inferSeverity('ServiceCrashed')).toBe('Error')
  })
  it('maps *Started / *Completed / *Skipped → Info', () => {
    expect(inferSeverity('InstallStarted')).toBe('Info')
    expect(inferSeverity('InstallCompleted')).toBe('Info')
    expect(inferSeverity('InstallSkipped')).toBe('Info')
  })
  it('falls back to Info for unknown shapes', () => {
    expect(inferSeverity('ServiceStarting')).toBe('Info')
    expect(inferSeverity('ServiceRunning')).toBe('Info')
    expect(inferSeverity('Weird')).toBe('Info')
  })
})

describe('DefaultRuntimeEventEmitter — connected path', () => {
  it('emit() dispatches via signalr.invoke when connected', () => {
    const { stub, invokes } = makeStub({ connected: true })
    const emitter = new DefaultRuntimeEventEmitter({
      signalr: stub,
      logger: makeLogger() as unknown as Logger,
    })

    emitter.emit(RuntimeEventTypes.ServiceRunning, 'Info', { serviceName: 'redis' })

    expect(invokes).toHaveLength(1)
    expect(invokes[0]?.method).toBe('RecordRuntimeEvent')
    const env = decodeWire(invokes[0]!)
    expect(env.type).toBe('ServiceRunning')
    expect(env.severity).toBe('Info')
    expect(env.payload).toEqual({ serviceName: 'redis' })
    expect(typeof env.timestamp).toBe('string')
    expect(env.durationMs).toBeUndefined()
  })

  it('never throws when invoke rejects', async () => {
    const { stub, invoke } = makeStub({ connected: true })
    invoke.mockImplementationOnce(async () => {
      throw new Error('hub down')
    })
    const emitter = new DefaultRuntimeEventEmitter({
      signalr: stub,
      logger: makeLogger() as unknown as Logger,
    })

    expect(() => emitter.emit('AnyType', 'Info')).not.toThrow()
    // Let the rejection settle so it doesn't leak as unhandled.
    await new Promise((r) => setTimeout(r, 0))
  })
})

describe('DefaultRuntimeEventEmitter — buffering', () => {
  it('buffers events while disconnected and drains on reconnect', () => {
    const ctrl = makeStub({ connected: false })
    const emitter = new DefaultRuntimeEventEmitter({
      signalr: ctrl.stub,
      logger: makeLogger() as unknown as Logger,
    })

    emitter.emit('A', 'Info')
    emitter.emit('B', 'Info')
    emitter.emit('C', 'Info')
    expect(ctrl.invokes).toHaveLength(0)

    ctrl.setConnected(true)
    expect(ctrl.invokes.map((c) => (c.payload as RuntimeEventEnvelope).type)).toEqual([
      'A',
      'B',
      'C',
    ])
  })

  it('drops oldest at buffer cap (FIFO) and warns at most once per minute', () => {
    let nowMs = 1_000_000
    const ctrl = makeStub({ connected: false })
    const logger = makeLogger()
    const emitter = new DefaultRuntimeEventEmitter({
      signalr: ctrl.stub,
      logger: logger as unknown as Logger,
      bufferCap: 3,
      now: () => nowMs,
    })

    // Push 5 — buffer is 3, so first 2 should be dropped.
    emitter.emit('A', 'Info')
    emitter.emit('B', 'Info')
    emitter.emit('C', 'Info')
    emitter.emit('D', 'Info') // drops A
    emitter.emit('E', 'Info') // drops B
    ctrl.setConnected(true)
    expect(ctrl.invokes.map((c) => (c.payload as RuntimeEventEnvelope).type)).toEqual([
      'C',
      'D',
      'E',
    ])

    // The warning fired at least once.
    const warnCallsBefore = logger.warn.mock.calls.length
    expect(warnCallsBefore).toBeGreaterThanOrEqual(1)

    // Re-disconnect and push more — within 1 minute, warn should NOT fire again.
    ctrl.setConnected(false)
    emitter.emit('F', 'Info')
    emitter.emit('G', 'Info')
    emitter.emit('H', 'Info')
    emitter.emit('I', 'Info') // drops F — within throttle window, no extra warn
    expect(logger.warn.mock.calls.length).toBe(warnCallsBefore)

    // Advance time past the throttle window — next drop should warn again.
    nowMs += 70_000
    emitter.emit('J', 'Info') // drops G
    expect(logger.warn.mock.calls.length).toBe(warnCallsBefore + 1)
  })
})

describe('DefaultRuntimeEventEmitter — startTimer', () => {
  it('stamps startedAt on the start event and durationMs on the end event', () => {
    let nowMs = 100
    const ctrl = makeStub({ connected: true })
    const emitter = new DefaultRuntimeEventEmitter({
      signalr: ctrl.stub,
      logger: makeLogger() as unknown as Logger,
      now: () => nowMs,
    })

    const timer = emitter.startTimer('InstallStarted', { hash: 'abc' })
    nowMs = 350
    timer.complete('InstallCompleted', { hash: 'abc' })

    expect(ctrl.invokes).toHaveLength(2)
    const start = decodeWire(ctrl.invokes[0]!)
    const end = decodeWire(ctrl.invokes[1]!)

    expect(start.type).toBe('InstallStarted')
    expect(start.severity).toBe('Info')
    expect(start.durationMs).toBeUndefined()
    expect(start.payload['startedAt']).toEqual(expect.any(String))
    expect(start.payload['hash']).toBe('abc')

    expect(end.type).toBe('InstallCompleted')
    expect(end.severity).toBe('Info')
    expect(end.durationMs).toBe(250)
    expect(end.payload['hash']).toBe('abc')
  })

  it('fail() uses Error severity regardless of inferred suffix', () => {
    let nowMs = 0
    const ctrl = makeStub({ connected: true })
    const emitter = new DefaultRuntimeEventEmitter({
      signalr: ctrl.stub,
      logger: makeLogger() as unknown as Logger,
      now: () => nowMs,
    })
    const timer = emitter.startTimer('InstallStarted')
    nowMs = 10
    timer.fail('InstallFailed', { errorMessage: 'boom' })

    const end = decodeWire(ctrl.invokes[1]!)
    expect(end.severity).toBe('Error')
    expect(end.durationMs).toBe(10)
    expect(end.payload['errorMessage']).toBe('boom')
  })

  it('skip() uses Info severity and records duration', () => {
    let nowMs = 0
    const ctrl = makeStub({ connected: true })
    const emitter = new DefaultRuntimeEventEmitter({
      signalr: ctrl.stub,
      logger: makeLogger() as unknown as Logger,
      now: () => nowMs,
    })
    const timer = emitter.startTimer('InstallStarted')
    nowMs = 5
    timer.skip('InstallSkipped', { hash: 'xx' })

    const end = decodeWire(ctrl.invokes[1]!)
    expect(end.type).toBe('InstallSkipped')
    expect(end.severity).toBe('Info')
    expect(end.durationMs).toBe(5)
    expect(end.payload['hash']).toBe('xx')
  })
})

describe('TestRuntimeEventEmitter', () => {
  it('records emit() events', () => {
    const recorder = new TestRuntimeEventEmitter()
    recorder.emit('A', 'Info', { foo: 1 })
    expect(recorder.events).toHaveLength(1)
    expect(recorder.events[0]?.type).toBe('A')
    expect(recorder.events[0]?.payload).toEqual({ foo: 1 })
  })

  it('records start + end as paired events with durationMs', async () => {
    const recorder = new TestRuntimeEventEmitter()
    const timer = recorder.startTimer('XStarted', { a: 1 })
    // Tiny await to ensure durationMs >= 0; we just want the event shape.
    await new Promise((r) => setTimeout(r, 1))
    timer.complete('XCompleted', { b: 2 })

    expect(recorder.events).toHaveLength(2)
    expect(recorder.events[0]?.type).toBe('XStarted')
    expect(recorder.events[1]?.type).toBe('XCompleted')
    expect(recorder.events[1]?.durationMs).toBeGreaterThanOrEqual(0)
    expect(recorder.events[1]?.payload).toEqual({ b: 2 })
  })

  it('reset() clears recorded events', () => {
    const recorder = new TestRuntimeEventEmitter()
    recorder.emit('A', 'Info')
    expect(recorder.events).toHaveLength(1)
    recorder.reset()
    expect(recorder.events).toHaveLength(0)
  })
})
