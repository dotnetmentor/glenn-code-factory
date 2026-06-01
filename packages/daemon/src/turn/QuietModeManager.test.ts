// Tests for QuietModeManager. Pattern matches the sibling tests:
// hand-rolled fake for the dependency (here TurnRunner) and a pino-shaped
// logger stub. No `vi.mock` calls.
//
// Real TurnRunner construction would pull in SignalR + the SDK factory —
// way more surface than we need. A FakeTurnRunner extending EventEmitter
// gives us exactly the contract QuietMode consumes (`idle` / `activity`
// events + `state()`).

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { EventEmitter } from 'node:events'

import type { DaemonConfig } from '../config/DaemonConfig.js'
import type { TurnRunner } from './TurnRunner.js'
import { QuietModeManager } from './QuietModeManager.js'

// ============================================================================
// Fixtures
// ============================================================================

const QUIET_MS = 100 // fast for tests; production default is 300_000

type FakeState = { kind: 'idle' } | { kind: 'running' }

class FakeTurnRunner extends EventEmitter {
  #state: FakeState = { kind: 'idle' }
  setRunning(): void {
    this.#state = { kind: 'running' }
  }
  setIdle(): void {
    this.#state = { kind: 'idle' }
  }
  state(): FakeState {
    return this.#state
  }
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

function makeConfig(quietTimeoutMs = QUIET_MS): DaemonConfig {
  // We only ever read `quietTimeoutMs` — cast through unknown to avoid
  // building a full DaemonConfig (which validates a JWT etc.).
  return { quietTimeoutMs } as unknown as DaemonConfig
}

function makeManager(opts?: { quietTimeoutMs?: number }) {
  const turnRunner = new FakeTurnRunner()
  const logger = makeLogger()
  const config = makeConfig(opts?.quietTimeoutMs)
  const manager = new QuietModeManager({
    turnRunner: turnRunner as unknown as TurnRunner,
    config,
    // Pino's typed Logger is shaped like our stub; cast through unknown so we
    // don't have to satisfy the full surface.
    logger: logger as unknown as import('pino').Logger,
  })
  return { manager, turnRunner, logger }
}

// ============================================================================
// Setup
// ============================================================================

beforeEach(() => {
  vi.useFakeTimers()
})

afterEach(() => {
  vi.useRealTimers()
})

// ============================================================================
// Tests
// ============================================================================

describe('QuietModeManager', () => {
  it('starts not quiet, then transitions to quiet after the timeout', () => {
    const { manager } = makeManager()
    const sleeps: number[] = []
    manager.on('sleep', () => sleeps.push(Date.now()))

    manager.start()
    expect(manager.isQuiet()).toBe(false)

    vi.advanceTimersByTime(QUIET_MS - 1)
    expect(manager.isQuiet()).toBe(false)
    expect(sleeps).toHaveLength(0)

    vi.advanceTimersByTime(1)
    expect(manager.isQuiet()).toBe(true)
    expect(sleeps).toHaveLength(1)
  })

  it('resets the timer when activity arrives within the window', () => {
    const { manager, turnRunner } = makeManager()
    const sleeps: string[] = []
    manager.on('sleep', () => sleeps.push('sleep'))

    manager.start()

    // Move halfway, fire activity (should cancel), then idle again.
    vi.advanceTimersByTime(QUIET_MS / 2)
    turnRunner.emit('activity')
    vi.advanceTimersByTime(QUIET_MS / 2) // cumulative would be QUIET_MS w/o reset
    expect(sleeps).toHaveLength(0)

    turnRunner.emit('idle')
    // Now wait the full timeout from this point.
    vi.advanceTimersByTime(QUIET_MS - 1)
    expect(sleeps).toHaveLength(0)
    vi.advanceTimersByTime(1)
    expect(sleeps).toHaveLength(1)
  })

  it('emits wake synchronously when activity arrives while quiet', () => {
    const { manager, turnRunner } = makeManager()
    const events: string[] = []
    manager.on('sleep', () => events.push('sleep'))
    manager.on('wake', () => events.push('wake'))

    manager.start()
    vi.advanceTimersByTime(QUIET_MS)
    expect(manager.isQuiet()).toBe(true)
    expect(events).toEqual(['sleep'])

    // Synchronous-ness: assert wake is observable BEFORE control returns.
    turnRunner.emit('activity')
    expect(manager.isQuiet()).toBe(false)
    expect(events).toEqual(['sleep', 'wake'])
  })

  it('does not emit wake when activity arrives and we were not quiet', () => {
    const { manager, turnRunner } = makeManager()
    const wakes: number[] = []
    manager.on('wake', () => wakes.push(Date.now()))

    manager.start()
    // Half the timeout in, fire activity. We were not quiet → no wake.
    vi.advanceTimersByTime(QUIET_MS / 2)
    turnRunner.emit('activity')
    expect(manager.isQuiet()).toBe(false)
    expect(wakes).toHaveLength(0)
  })

  it('only emits sleep again after a full fresh timeout', () => {
    const { manager, turnRunner } = makeManager()
    const sleeps: number[] = []
    manager.on('sleep', () => sleeps.push(Date.now()))

    manager.start()
    vi.advanceTimersByTime(QUIET_MS)
    expect(sleeps).toHaveLength(1)

    // Activity → wake. Then a second turn ends.
    turnRunner.emit('activity')
    turnRunner.emit('idle')

    // Half the timeout: still no second sleep.
    vi.advanceTimersByTime(QUIET_MS / 2)
    expect(sleeps).toHaveLength(1)

    // Full additional timeout from the second idle: second sleep fires.
    vi.advanceTimersByTime(QUIET_MS / 2)
    expect(sleeps).toHaveLength(2)
  })

  it('stop() halts the timer and removes listeners', () => {
    const { manager, turnRunner } = makeManager()
    const sleeps: number[] = []
    const wakes: number[] = []
    manager.on('sleep', () => sleeps.push(Date.now()))
    manager.on('wake', () => wakes.push(Date.now()))

    manager.start()
    vi.advanceTimersByTime(QUIET_MS / 2)
    manager.stop()
    vi.advanceTimersByTime(QUIET_MS) // generous overshoot
    expect(sleeps).toHaveLength(0)

    // Listeners are detached — emitting further events on the runner should
    // not move our state.
    turnRunner.emit('activity')
    turnRunner.emit('idle')
    vi.advanceTimersByTime(QUIET_MS)
    expect(sleeps).toHaveLength(0)
    expect(wakes).toHaveLength(0)
  })

  it('start() is idempotent — does not double-register listeners', () => {
    const { manager, turnRunner } = makeManager()
    const sleeps: number[] = []
    manager.on('sleep', () => sleeps.push(Date.now()))

    manager.start()
    manager.start() // second start is a no-op

    // If listeners were double-registered, two timers would race; the visible
    // effect would be exactly one sleep regardless. The cleaner assertion is
    // on the underlying listener count.
    expect(turnRunner.listenerCount('idle')).toBe(1)
    expect(turnRunner.listenerCount('activity')).toBe(1)

    vi.advanceTimersByTime(QUIET_MS)
    expect(sleeps).toHaveLength(1)
  })

  it('stop() is idempotent', () => {
    const { manager, turnRunner } = makeManager()
    manager.start()
    manager.stop()
    manager.stop() // no-op

    expect(turnRunner.listenerCount('idle')).toBe(0)
    expect(turnRunner.listenerCount('activity')).toBe(0)
  })

  it('does not start a sleep timer if the runner is running on start()', () => {
    const { manager, turnRunner } = makeManager()
    const sleeps: number[] = []
    manager.on('sleep', () => sleeps.push(Date.now()))

    turnRunner.setRunning()
    manager.start()

    vi.advanceTimersByTime(QUIET_MS * 5)
    expect(sleeps).toHaveLength(0)

    // Once the runner finishes its turn, the next `idle` event arms the timer.
    turnRunner.setIdle()
    turnRunner.emit('idle')
    vi.advanceTimersByTime(QUIET_MS)
    expect(sleeps).toHaveLength(1)
  })

  it('logs the transitions through the child logger', () => {
    const { manager, turnRunner, logger } = makeManager()
    manager.start()

    vi.advanceTimersByTime(QUIET_MS)
    // pino-style: first arg may be an object; here it's just the message.
    const infoMessages = logger.info.mock.calls.map((call) => call[0])
    expect(infoMessages).toContain('entering quiet mode')

    turnRunner.emit('activity')
    const infoAfter = logger.info.mock.calls.map((call) => call[0])
    expect(infoAfter).toContain('waking from quiet mode')
  })
})
