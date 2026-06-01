// LivenessWorker tests. Same approach as SelfWatchdog.test.ts: don't spawn a
// real worker_threads worker — substitute a fake via the `workerFactory`
// test seam. We exercise:
//
//   1. URL construction (trailing slash on masterUrl, runtime id interpolation).
//   2. start()/stop() idempotency + cleanup.
//   3. rotateToken() — postMessage forwarding, pre-start vs post-start
//      behaviour, empty-token rejection.
//   4. Worker → main message handlers — BEAT_OK logs debug, BEAT_FAILED
//      logs warn, error/exit handlers don't crash.
//   5. Initial token flows into WorkerInit at spawn time, including the
//      case where rotateToken() ran before start().

import { EventEmitter } from 'node:events'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import {
  DEFAULT_BEAT_INTERVAL_MS,
  DEFAULT_REQUEST_TIMEOUT_MS,
  LivenessWorker,
  type BeatFailedMessage,
  type BeatOkMessage,
  type WorkerInboundMessage,
  type WorkerInit,
  type WorkerLike,
} from './LivenessWorker.js'

// ============================================================================
// Helpers
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
  return { log: log as unknown as import('pino').Logger, calls }
}

/**
 * EventEmitter-shaped fake worker. Captures construction args + every
 * postMessage so tests can assert token rotation lands correctly.
 */
function makeFakeWorker() {
  const ee = new EventEmitter()
  const terminate = vi.fn(async () => 0)
  const posted: WorkerInboundMessage[] = []
  const captured: { init: WorkerInit | null } = { init: null }
  const factory = (init: WorkerInit): WorkerLike => {
    captured.init = init
    const worker = {
      on: (event: string, listener: (...args: unknown[]) => void) => {
        ee.on(event, listener)
      },
      postMessage: (msg: WorkerInboundMessage) => {
        posted.push(msg)
      },
      terminate,
    }
    return worker as unknown as WorkerLike
  }
  return { factory, ee, terminate, posted, captured }
}

const VALID_TOKEN_A = 'eyJhbGciOiJIUzI1NiJ9.aaaa.bbbb'
const VALID_TOKEN_B = 'eyJhbGciOiJIUzI1NiJ9.cccc.dddd'
const RUNTIME_ID = '11111111-2222-3333-4444-555555555555'

// ============================================================================
// Tests
// ============================================================================

describe('LivenessWorker', () => {
  beforeEach(() => {
    vi.useFakeTimers()
  })

  afterEach(() => {
    vi.useRealTimers()
  })

  describe('URL construction', () => {
    it('combines masterUrl + runtimeId into the beat URL', () => {
      const { log } = makeLogger()
      const { factory } = makeFakeWorker()
      const lw = new LivenessWorker({
        logger: log,
        masterUrl: 'https://api.example.com',
        runtimeId: RUNTIME_ID,
        initialToken: VALID_TOKEN_A,
        workerFactory: factory,
      })
      expect(lw._beatUrl()).toBe(
        `https://api.example.com/api/runtimes/${RUNTIME_ID}/heartbeat-tick`,
      )
    })

    it('strips a single trailing slash on masterUrl', () => {
      const { log } = makeLogger()
      const { factory } = makeFakeWorker()
      const lw = new LivenessWorker({
        logger: log,
        masterUrl: 'https://api.example.com/',
        runtimeId: RUNTIME_ID,
        initialToken: VALID_TOKEN_A,
        workerFactory: factory,
      })
      expect(lw._beatUrl()).toBe(
        `https://api.example.com/api/runtimes/${RUNTIME_ID}/heartbeat-tick`,
      )
    })
  })

  describe('start()', () => {
    it('passes initial token + defaults to the worker factory', () => {
      const { log } = makeLogger()
      const { factory, captured } = makeFakeWorker()
      const lw = new LivenessWorker({
        logger: log,
        masterUrl: 'https://api.example.com',
        runtimeId: RUNTIME_ID,
        initialToken: VALID_TOKEN_A,
        workerFactory: factory,
      })
      lw.start()
      expect(captured.init).toEqual({
        beatUrl: `https://api.example.com/api/runtimes/${RUNTIME_ID}/heartbeat-tick`,
        initialToken: VALID_TOKEN_A,
        intervalMs: DEFAULT_BEAT_INTERVAL_MS,
        requestTimeoutMs: DEFAULT_REQUEST_TIMEOUT_MS,
      })
    })

    it('respects custom intervalMs + requestTimeoutMs', () => {
      const { log } = makeLogger()
      const { factory, captured } = makeFakeWorker()
      const lw = new LivenessWorker({
        logger: log,
        masterUrl: 'https://api.example.com',
        runtimeId: RUNTIME_ID,
        initialToken: VALID_TOKEN_A,
        intervalMs: 3_000,
        requestTimeoutMs: 1_500,
        workerFactory: factory,
      })
      lw.start()
      expect(captured.init?.intervalMs).toBe(3_000)
      expect(captured.init?.requestTimeoutMs).toBe(1_500)
    })

    it('is idempotent — second start() does not create a second worker', () => {
      const { log } = makeLogger()
      const inner = makeFakeWorker()
      const factory = vi.fn(inner.factory)
      const lw = new LivenessWorker({
        logger: log,
        masterUrl: 'https://api.example.com',
        runtimeId: RUNTIME_ID,
        initialToken: VALID_TOKEN_A,
        workerFactory: factory,
      })
      lw.start()
      lw.start()
      lw.start()
      expect(factory).toHaveBeenCalledTimes(1)
    })

    it('logs an info line on start with the resolved config', () => {
      const { log, calls } = makeLogger()
      const { factory } = makeFakeWorker()
      const lw = new LivenessWorker({
        logger: log,
        masterUrl: 'https://api.example.com',
        runtimeId: RUNTIME_ID,
        initialToken: VALID_TOKEN_A,
        workerFactory: factory,
      })
      lw.start()
      const info = calls.find((c) => c.level === 'info')
      expect(info).toBeDefined()
      expect(info?.msg).toContain('started')
    })
  })

  describe('rotateToken()', () => {
    it('post-start: postMessages UPDATE_TOKEN with the new value', () => {
      const { log } = makeLogger()
      const { factory, posted } = makeFakeWorker()
      const lw = new LivenessWorker({
        logger: log,
        masterUrl: 'https://api.example.com',
        runtimeId: RUNTIME_ID,
        initialToken: VALID_TOKEN_A,
        workerFactory: factory,
      })
      lw.start()
      lw.rotateToken(VALID_TOKEN_B)
      expect(posted).toEqual([{ type: 'UPDATE_TOKEN', token: VALID_TOKEN_B }])
      expect(lw._currentToken()).toBe(VALID_TOKEN_B)
    })

    it('pre-start: caches the new token and uses it when start() is called', () => {
      const { log } = makeLogger()
      const { factory, captured, posted } = makeFakeWorker()
      const lw = new LivenessWorker({
        logger: log,
        masterUrl: 'https://api.example.com',
        runtimeId: RUNTIME_ID,
        initialToken: VALID_TOKEN_A,
        workerFactory: factory,
      })
      lw.rotateToken(VALID_TOKEN_B)
      // The initial token field is immutable for traceability, but the spawn
      // uses the latest cached value.
      expect(lw._initialToken()).toBe(VALID_TOKEN_A)
      expect(lw._currentToken()).toBe(VALID_TOKEN_B)
      // No worker yet — nothing to postMessage to.
      expect(posted).toEqual([])

      lw.start()
      expect(captured.init?.initialToken).toBe(VALID_TOKEN_B)
    })

    it('empty token is rejected with a warn log — does NOT clobber the cache', () => {
      const { log, calls } = makeLogger()
      const { factory, posted } = makeFakeWorker()
      const lw = new LivenessWorker({
        logger: log,
        masterUrl: 'https://api.example.com',
        runtimeId: RUNTIME_ID,
        initialToken: VALID_TOKEN_A,
        workerFactory: factory,
      })
      lw.start()
      lw.rotateToken('')
      lw.rotateToken(undefined as unknown as string)
      lw.rotateToken(null as unknown as string)
      expect(lw._currentToken()).toBe(VALID_TOKEN_A)
      expect(posted).toEqual([])
      const warns = calls.filter(
        (c) => c.level === 'warn' && String(c.msg).includes('empty token'),
      )
      expect(warns.length).toBeGreaterThanOrEqual(3)
    })

    it('swallows postMessage failures (worker already terminated)', () => {
      const { log, calls } = makeLogger()
      const ee = new EventEmitter()
      const terminate = vi.fn(async () => 0)
      const factory = (): WorkerLike =>
        ({
          on: (e: string, l: (...args: unknown[]) => void) => ee.on(e, l),
          postMessage: () => {
            throw new Error('worker terminated')
          },
          terminate,
        }) as unknown as WorkerLike
      const lw = new LivenessWorker({
        logger: log,
        masterUrl: 'https://api.example.com',
        runtimeId: RUNTIME_ID,
        initialToken: VALID_TOKEN_A,
        workerFactory: factory,
      })
      lw.start()
      expect(() => lw.rotateToken(VALID_TOKEN_B)).not.toThrow()
      // Cache is still updated so a future re-start() picks up the value.
      expect(lw._currentToken()).toBe(VALID_TOKEN_B)
      const warn = calls.find(
        (c) => c.level === 'warn' && String(c.msg).includes('postMessage failed'),
      )
      expect(warn).toBeDefined()
    })
  })

  describe('worker message handlers', () => {
    it('BEAT_OK logs at debug with status + durationMs', () => {
      const { log, calls } = makeLogger()
      const { factory, ee } = makeFakeWorker()
      const lw = new LivenessWorker({
        logger: log,
        masterUrl: 'https://api.example.com',
        runtimeId: RUNTIME_ID,
        initialToken: VALID_TOKEN_A,
        workerFactory: factory,
      })
      lw.start()

      const beat: BeatOkMessage = { type: 'BEAT_OK', status: 204, durationMs: 42 }
      ee.emit('message', beat)

      const debug = calls.find(
        (c) => c.level === 'debug' && String(c.msg).includes('beat ok'),
      )
      expect(debug).toBeDefined()
      expect(debug?.obj).toEqual({ status: 204, durationMs: 42 })
    })

    it('BEAT_FAILED logs at warn with status + error', () => {
      const { log, calls } = makeLogger()
      const { factory, ee } = makeFakeWorker()
      const lw = new LivenessWorker({
        logger: log,
        masterUrl: 'https://api.example.com',
        runtimeId: RUNTIME_ID,
        initialToken: VALID_TOKEN_A,
        workerFactory: factory,
      })
      lw.start()

      const fail: BeatFailedMessage = {
        type: 'BEAT_FAILED',
        status: null,
        error: 'fetch failed',
      }
      ee.emit('message', fail)

      const warn = calls.find(
        (c) => c.level === 'warn' && String(c.msg).includes('beat failed'),
      )
      expect(warn).toBeDefined()
      expect(warn?.obj).toEqual({ status: null, error: 'fetch failed' })
    })

    it('ignores null/undefined/unknown messages (forward-compat)', () => {
      const { log, calls } = makeLogger()
      const { factory, ee } = makeFakeWorker()
      const lw = new LivenessWorker({
        logger: log,
        masterUrl: 'https://api.example.com',
        runtimeId: RUNTIME_ID,
        initialToken: VALID_TOKEN_A,
        workerFactory: factory,
      })
      lw.start()

      ee.emit('message', null)
      ee.emit('message', undefined)
      ee.emit('message', { type: 'SOMETHING_NEW' })

      // No new debug/warn entries from message handling — only the
      // `liveness worker started` info from start().
      expect(calls.filter((c) => c.level === 'debug')).toHaveLength(0)
      expect(calls.filter((c) => c.level === 'warn')).toHaveLength(0)
    })

    it('worker error event logs at error level but does NOT throw', () => {
      const { log, calls } = makeLogger()
      const { factory, ee } = makeFakeWorker()
      const lw = new LivenessWorker({
        logger: log,
        masterUrl: 'https://api.example.com',
        runtimeId: RUNTIME_ID,
        initialToken: VALID_TOKEN_A,
        workerFactory: factory,
      })
      lw.start()

      expect(() => ee.emit('error', new Error('worker boom'))).not.toThrow()

      const errLog = calls.find(
        (c) => c.level === 'error' && String(c.msg).includes('thread-independent path down'),
      )
      expect(errLog).toBeDefined()
    })

    it('unexpected worker exit (non-zero) logs at warn level', () => {
      const { log, calls } = makeLogger()
      const { factory, ee } = makeFakeWorker()
      const lw = new LivenessWorker({
        logger: log,
        masterUrl: 'https://api.example.com',
        runtimeId: RUNTIME_ID,
        initialToken: VALID_TOKEN_A,
        workerFactory: factory,
      })
      lw.start()

      ee.emit('exit', 137)

      const warn = calls.find(
        (c) => c.level === 'warn' && String(c.msg).includes('exited unexpectedly'),
      )
      expect(warn).toBeDefined()
    })

    it('clean worker exit (code 0) does NOT warn', () => {
      const { log, calls } = makeLogger()
      const { factory, ee } = makeFakeWorker()
      const lw = new LivenessWorker({
        logger: log,
        masterUrl: 'https://api.example.com',
        runtimeId: RUNTIME_ID,
        initialToken: VALID_TOKEN_A,
        workerFactory: factory,
      })
      lw.start()

      ee.emit('exit', 0)

      const warn = calls.find((c) => c.level === 'warn')
      expect(warn).toBeUndefined()
    })
  })

  describe('stop()', () => {
    it('terminates the worker and is idempotent', async () => {
      const { log } = makeLogger()
      const { factory, terminate } = makeFakeWorker()
      const lw = new LivenessWorker({
        logger: log,
        masterUrl: 'https://api.example.com',
        runtimeId: RUNTIME_ID,
        initialToken: VALID_TOKEN_A,
        workerFactory: factory,
      })
      lw.start()

      await lw.stop()
      await lw.stop()
      await lw.stop()

      expect(terminate).toHaveBeenCalledTimes(1)
    })

    it('swallows terminate() rejections', async () => {
      const { log } = makeLogger()
      const ee = new EventEmitter()
      const terminate = vi.fn(async () => {
        throw new Error('terminate boom')
      })
      const factory = (): WorkerLike =>
        ({
          on: (e: string, l: (...args: unknown[]) => void) => ee.on(e, l),
          postMessage: () => {},
          terminate,
        }) as unknown as WorkerLike
      const lw = new LivenessWorker({
        logger: log,
        masterUrl: 'https://api.example.com',
        runtimeId: RUNTIME_ID,
        initialToken: VALID_TOKEN_A,
        workerFactory: factory,
      })
      lw.start()

      await expect(lw.stop()).resolves.toBeUndefined()
    })

    it('without prior start() is a no-op', async () => {
      const { log } = makeLogger()
      const { factory, terminate } = makeFakeWorker()
      const lw = new LivenessWorker({
        logger: log,
        masterUrl: 'https://api.example.com',
        runtimeId: RUNTIME_ID,
        initialToken: VALID_TOKEN_A,
        workerFactory: factory,
      })
      await expect(lw.stop()).resolves.toBeUndefined()
      expect(terminate).not.toHaveBeenCalled()
    })

    it('after stop(), a subsequent start() spawns a fresh worker with the latest cached token', async () => {
      const { log } = makeLogger()
      const inner = makeFakeWorker()
      const factory = vi.fn(inner.factory)
      const lw = new LivenessWorker({
        logger: log,
        masterUrl: 'https://api.example.com',
        runtimeId: RUNTIME_ID,
        initialToken: VALID_TOKEN_A,
        workerFactory: factory,
      })
      lw.start()
      lw.rotateToken(VALID_TOKEN_B)
      await lw.stop()
      // Re-start should produce a fresh spawn with the rotated token.
      lw.start()
      expect(factory).toHaveBeenCalledTimes(2)
      expect(inner.captured.init?.initialToken).toBe(VALID_TOKEN_B)
    })
  })
})
