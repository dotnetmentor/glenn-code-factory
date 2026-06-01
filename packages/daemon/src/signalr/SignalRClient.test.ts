// Tests for SignalRClient. We avoid `vi.mock('@microsoft/signalr')` and instead
// build a hand-rolled fake `HubConnectionBuilder` + `HubConnection` that
// records every interaction. This keeps the assertions strongly typed and
// makes failure messages much easier to read.

import { HttpTransportType, type IHttpConnectionOptions, type IRetryPolicy } from '@microsoft/signalr'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import { DaemonConfig } from '../config/DaemonConfig.js'
import { IndefiniteReconnectPolicy } from './retryPolicy.js'
import { SignalRClient, SignalRConnectError } from './SignalRClient.js'
import { AgentEventKind } from './types.js'

// ============================================================================
// Test helpers
// ============================================================================

const VALID_TOKEN =
  'eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJ0ZXN0In0.aGVsbG8td29ybGQtc2lnbmF0dXJlLXNlZ21lbnQ'
const ROTATED_TOKEN =
  'AAAAAAAAAAAAAAAAAAAAAA.BBBBBBBBBBBBBBBBBBBBBB.CCCCCCCCCCCCCCCCCCCCCC'
const RUNTIME_ID = '11111111-2222-3333-4444-555555555555'

function makeConfig(): DaemonConfig {
  return DaemonConfig.fromEnv({
    GLENN_RUNTIME_TOKEN: VALID_TOKEN,
    MAIN_API_URL: 'http://localhost:5338',
    RUNTIME_ID: RUNTIME_ID,
    DAEMON_VERSION: '0.1.0-dev',
  })
}

/**
 * Pino-shaped stub. We collect every call so individual tests can assert on
 * them. `child` returns the same instance so messages emitted via
 * `.child({module: 'signalr'})` still land in the same log array.
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

type InvokeRecord = {
  method: string
  args: unknown[]
  resolve: (value?: unknown) => void
  reject: (err: unknown) => void
}

type FakeConnection = {
  state: 'Disconnected' | 'Connecting' | 'Connected' | 'Disconnecting' | 'Reconnecting'
  started: boolean
  stopped: boolean
  startError: Error | null
  on: (method: string, handler: (...args: unknown[]) => unknown) => void
  start: () => Promise<void>
  stop: () => Promise<void>
  invoke: (method: string, ...args: unknown[]) => Promise<unknown>
  onreconnecting: (cb: (err?: Error) => void) => void
  onreconnected: (cb: (id?: string) => void) => void
  onclose: (cb: (err?: Error) => void) => void

  // Test-side hooks
  handlers: Map<string, Array<(...args: unknown[]) => unknown>>
  invocations: InvokeRecord[]
  fireInbound: (method: string, payload: unknown) => Promise<void>
  fireClose: (err?: Error) => void
  fireReconnected: (id?: string) => void
  closeHandlers: Array<(err?: Error) => void>
  reconnectingHandlers: Array<(err?: Error) => void>
  reconnectedHandlers: Array<(id?: string) => void>
}

type FakeBuilder = {
  withUrl: (url: string, opts?: IHttpConnectionOptions) => FakeBuilder
  withAutomaticReconnect: (policy: IRetryPolicy | number[]) => FakeBuilder
  configureLogging: (lvl: unknown) => FakeBuilder
  build: () => FakeConnection

  // Test-side hooks
  withUrlCalls: Array<{ url: string; options?: IHttpConnectionOptions }>
  reconnectPolicy: IRetryPolicy | number[] | null
  loggingLevel: unknown
  built: FakeConnection | null
}

function makeFakeConnection(): FakeConnection {
  const conn: FakeConnection = {
    state: 'Disconnected',
    started: false,
    stopped: false,
    startError: null,
    handlers: new Map(),
    invocations: [],
    closeHandlers: [],
    reconnectingHandlers: [],
    reconnectedHandlers: [],

    on(method, handler) {
      const arr = conn.handlers.get(method) ?? []
      arr.push(handler)
      conn.handlers.set(method, arr)
    },
    async start() {
      if (conn.startError) throw conn.startError
      conn.started = true
      conn.state = 'Connected'
    },
    async stop() {
      conn.stopped = true
      conn.state = 'Disconnected'
    },
    invoke(method, ...args) {
      return new Promise<unknown>((resolve, reject) => {
        conn.invocations.push({ method, args, resolve, reject })
      })
    },
    onreconnecting(cb) {
      conn.reconnectingHandlers.push(cb)
    },
    onreconnected(cb) {
      conn.reconnectedHandlers.push(cb)
    },
    onclose(cb) {
      conn.closeHandlers.push(cb)
    },

    async fireInbound(method, payload) {
      const arr = conn.handlers.get(method) ?? []
      for (const h of arr) await h(payload)
    },
    fireClose(err) {
      conn.state = 'Disconnected'
      for (const h of conn.closeHandlers) h(err)
    },
    fireReconnected(id) {
      conn.state = 'Connected'
      for (const h of conn.reconnectedHandlers) h(id)
    },
  }
  return conn
}

function makeFakeBuilder(): FakeBuilder {
  const builder: FakeBuilder = {
    withUrlCalls: [],
    built: null,
    reconnectPolicy: null,
    loggingLevel: undefined,
    withUrl(url, options) {
      builder.withUrlCalls.push({ url, ...(options !== undefined ? { options } : {}) })
      return builder
    },
    withAutomaticReconnect(policy) {
      builder.reconnectPolicy = policy
      return builder
    },
    configureLogging(lvl) {
      builder.loggingLevel = lvl
      return builder
    },
    build() {
      const conn = makeFakeConnection()
      builder.built = conn
      // Cast through unknown — we only implement the slice of HubConnection
      // that SignalRClient touches. SignalRClient never passes the connection
      // back out, so the leakage is contained.
      return conn
    },
  }
  return builder
}

/** Build a SignalRClient over a fake builder; return everything for assertion. */
function buildClient() {
  const config = makeConfig()
  const { log, calls } = makeLogger()
  const fakeBuilder = makeFakeBuilder()
  const client = new SignalRClient({
    config,
    // The pino-shaped stub satisfies the structural slice of `pino.Logger`
    // that SignalRClient uses (.child + .info/.warn/.error). Cast through
    // unknown is the cleanest way past the exactOptionalPropertyTypes
    // strictness on Logger's broader API surface.
    logger: log as unknown as import('pino').Logger,
    hubBuilderFactory: () =>
      fakeBuilder as unknown as import('@microsoft/signalr').HubConnectionBuilder,
  })
  return { client, config, log, calls, fakeBuilder }
}

// ============================================================================
// Tests
// ============================================================================

describe('SignalRClient', () => {
  beforeEach(() => {
    vi.useRealTimers()
  })
  afterEach(() => {
    vi.restoreAllMocks()
  })

  describe('start()', () => {
    it('builds the URL from config.mainApiUrl + /hubs/runtime', async () => {
      const { client, fakeBuilder } = buildClient()
      await client.start()

      expect(fakeBuilder.withUrlCalls).toHaveLength(1)
      expect(fakeBuilder.withUrlCalls[0]?.url).toBe('http://localhost:5338/hubs/runtime')
    })

    it('passes an accessTokenFactory that returns config.runtimeToken at call time', async () => {
      const { client, config, fakeBuilder } = buildClient()
      await client.start()

      const opts = fakeBuilder.withUrlCalls[0]?.options
      expect(opts?.accessTokenFactory).toBeTypeOf('function')
      expect(opts?.accessTokenFactory?.()).toBe(VALID_TOKEN)

      // Rotate the token after start; the same factory must observe the new value
      // (this is how reconnects pick up rotated tokens automatically).
      config.rotateToken(ROTATED_TOKEN)
      expect(opts?.accessTokenFactory?.()).toBe(ROTATED_TOKEN)
    })

    it('uses WebSockets transport with skipNegotiation: true', async () => {
      const { client, fakeBuilder } = buildClient()
      await client.start()

      const opts = fakeBuilder.withUrlCalls[0]?.options
      expect(opts?.transport).toBe(HttpTransportType.WebSockets)
      expect(opts?.skipNegotiation).toBe(true)
    })

    it('configures automatic reconnect with the IndefiniteReconnectPolicy', async () => {
      const { client, fakeBuilder } = buildClient()
      await client.start()

      expect(fakeBuilder.reconnectPolicy).toBeInstanceOf(IndefiniteReconnectPolicy)
    })

    it('rejects with a recoverable SignalRConnectError when the underlying start throws', async () => {
      const { client, fakeBuilder } = buildClient()
      // Inject the error before the client builds the connection: we mutate
      // `built` after `.build()` runs inside `start()`. Easiest is to swap the
      // builder's `build` to inject the error.
      const original = fakeBuilder.build.bind(fakeBuilder)
      fakeBuilder.build = () => {
        const c = original()
        c.startError = new Error('boom: dns failure')
        return c
      }

      await expect(client.start()).rejects.toBeInstanceOf(SignalRConnectError)

      // Re-run, this time inspect the error fields. Use a fresh client because
      // `start()` is one-shot.
      const second = buildClient()
      const orig2 = second.fakeBuilder.build.bind(second.fakeBuilder)
      second.fakeBuilder.build = () => {
        const c = orig2()
        c.startError = new Error('boom: dns failure')
        return c
      }
      let caught: SignalRConnectError | null = null
      try {
        await second.client.start()
      } catch (err) {
        caught = err as SignalRConnectError
      }
      expect(caught).not.toBeNull()
      expect(caught?.recoverable).toBe(true)
      expect(caught?.message).toContain('boom: dns failure')
    })

    it('throws when start() is called twice', async () => {
      const { client } = buildClient()
      await client.start()
      await expect(client.start()).rejects.toThrow(/start called twice/)
    })

    it('fires the onConnected listener on successful start', async () => {
      const { client } = buildClient()
      const cb = vi.fn()
      client.onConnected(cb)
      await client.start()
      expect(cb).toHaveBeenCalledTimes(1)
    })
  })

  describe('stop()', () => {
    it('is a no-op before start()', async () => {
      const { client } = buildClient()
      await expect(client.stop()).resolves.toBeUndefined()
    })

    it('calls underlying stop when the connection is Connected', async () => {
      const { client, fakeBuilder } = buildClient()
      await client.start()
      await client.stop()
      expect(fakeBuilder.built?.stopped).toBe(true)
    })

    it('skips the underlying stop when already Disconnected', async () => {
      const { client, fakeBuilder } = buildClient()
      await client.start()
      // Force the state to Disconnected to simulate the close-then-stop ordering.
      const built = fakeBuilder.built!
      built.state = 'Disconnected'
      built.stopped = false
      await client.stop()
      expect(built.stopped).toBe(false)
    })
  })

  describe('outbound invokes', () => {
    it('sendHeartbeat invokes "Heartbeat" with the payload', async () => {
      const { client, fakeBuilder } = buildClient()
      await client.start()

      const beat = {
        emittedAt: '2026-05-08T12:00:00Z',
        daemonVersion: '0.1.0-dev',
        cpuPercent: 12.5,
        memoryUsedMb: 200,
        diskUsedPct: null,
        supervisedServicesUp: null,
        activeSessionId: null,
        disk: null,
        sysstatsSnapshotJson: null,
      }

      const promise = client.sendHeartbeat(beat)
      const inv = fakeBuilder.built!.invocations[0]
      expect(inv?.method).toBe('Heartbeat')
      expect(inv?.args).toEqual([beat])

      inv?.resolve()
      await promise
    })

    it('emitEvent invokes "EmitEvent" with the payload verbatim (no boundary translation)', async () => {
      // Card cursor-native-chat-ux 3.5/9: the daemon now ships
      // EmitEventPayload directly — no `#translateLegacyEventType` shim.
      // `kind` is REQUIRED at the daemon source (TurnRunner, CursorFactory,
      // BootstrapOrchestrator, ShutdownCoordinator etc. all set it).
      const { client, fakeBuilder } = buildClient()
      await client.start()

      const payload = {
        sessionId: 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee',
        kind: AgentEventKind.AssistantText,
        eventData: '{"text":"hi"}',
        emittedAt: '2026-05-08T12:00:00Z',
      }

      const promise = client.emitEvent(payload)
      const inv = fakeBuilder.built!.invocations[0]
      expect(inv?.method).toBe('EmitEvent')
      const arg = inv?.args[0] as {
        sessionId: string
        kind: string
        eventData: string
        emittedAt: string
      }
      expect(arg.sessionId).toBe('aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee')
      expect(arg.kind).toBe('AssistantText')
      // Pass-through: eventData is what we passed in, unchanged.
      expect(JSON.parse(arg.eventData)).toEqual({ text: 'hi' })
      // No legacy fields stamped — the translator is gone.
      expect((arg as { eventType?: unknown }).eventType).toBeUndefined()

      inv?.resolve()
      await promise
    })

    // `proposeRuntimeSpec` and outbound `applyRuntimeSpecDelta` were removed
    // in Card 2 (daemon-codegen migration). Neither has a matching method on
    // the .NET hub — they were dead-code on the daemon side, surviving only
    // as shim wrappers around an `invoke()` against a method that doesn't
    // exist server-side. The proposal flow lives at HTTP today
    // (POST /api/runtimes/{id}/proposals via the propose_runtime_spec MCP
    // tool); the inbound `ApplyRuntimeSpecDelta` push is server-to-daemon
    // only, handled by `onApplyRuntimeSpecDelta` below.

    it('throws when an outbound is called before start()', async () => {
      const { client } = buildClient()
      await expect(
        client.sendHeartbeat({
          emittedAt: 'x',
          daemonVersion: 'v',
          cpuPercent: null,
          memoryUsedMb: null,
          diskUsedPct: null,
          supervisedServicesUp: null,
          activeSessionId: null,
          disk: null,
          sysstatsSnapshotJson: null,
        }),
      ).rejects.toThrow(/not started/)
    })
  })

  describe('inbound handlers', () => {
    it('fires the handler when runtimeId matches', async () => {
      const { client, fakeBuilder } = buildClient()
      await client.start()

      const handler = vi.fn()
      client.onStartTurn(handler)

      await fakeBuilder.built!.fireInbound('StartTurn', {
        runtimeId: RUNTIME_ID,
        sessionId: 's1',
        conversationId: 'c1',
        prompt: 'hello',
        agentId: null,
      })

      expect(handler).toHaveBeenCalledTimes(1)
      expect(handler.mock.calls[0]?.[0]).toMatchObject({ sessionId: 's1' })
    })

    it('fires the handler when runtimeId is absent (current .NET DTO)', async () => {
      const { client, fakeBuilder } = buildClient()
      await client.start()

      const handler = vi.fn()
      client.onStartTurn(handler)

      // Today's DTO ships without runtimeId — the guard must pass through.
      await fakeBuilder.built!.fireInbound('StartTurn', {
        sessionId: 's1',
        conversationId: 'c1',
        prompt: 'hello',
        agentId: null,
      })

      expect(handler).toHaveBeenCalledTimes(1)
    })

    it('drops + warns when runtimeId mismatches', async () => {
      const { client, log, fakeBuilder } = buildClient()
      await client.start()

      const handler = vi.fn()
      client.onCancelTurn(handler)

      await fakeBuilder.built!.fireInbound('CancelTurn', {
        runtimeId: '99999999-9999-9999-9999-999999999999',
        sessionId: 's1',
        reason: 'spoof',
      })

      expect(handler).not.toHaveBeenCalled()
      expect(log.warn).toHaveBeenCalled()
      const warnArgs = log.warn.mock.calls[0]
      expect(warnArgs?.[1]).toMatch(/runtimeId mismatch/)
    })

    it('catches + logs handler exceptions and keeps the connection alive', async () => {
      const { client, log, fakeBuilder } = buildClient()
      await client.start()

      const failing = vi.fn(() => {
        throw new Error('handler boom')
      })
      client.onUpdateConfig(failing)

      await fakeBuilder.built!.fireInbound('UpdateConfig', {
        runtimeId: RUNTIME_ID,
        version: 'v1',
      })

      expect(failing).toHaveBeenCalledTimes(1)
      expect(log.error).toHaveBeenCalled()
      const errArgs = log.error.mock.calls[0]
      expect(errArgs?.[1]).toMatch(/inbound handler threw/)

      // Subsequent invocations on a different method still work — connection
      // is not torn down by a handler throw.
      const ok = vi.fn()
      client.onCancelTurn(ok)
      await fakeBuilder.built!.fireInbound('CancelTurn', {
        runtimeId: RUNTIME_ID,
        sessionId: 's1',
        reason: 'ok',
      })
      expect(ok).toHaveBeenCalledTimes(1)
    })

    it('throws when an inbound handler is registered before start()', () => {
      const { client } = buildClient()
      expect(() => client.onStartTurn(() => {})).toThrow(/not started/)
    })
  })

  // ----------------------------------------------------------------------------
  // Early-arrival buffering (bootstrap-UpdateConfig race fix).
  //
  // The server's `OnConnectedAsync` pushes inbound messages (notably the
  // bootstrap `UpdateConfig`) immediately after handshake, often BEFORE the
  // daemon has wired its `on*()` handlers from main.ts. Previously these went
  // to a `handler === undefined` slot in `#guardAndDispatch` and were silently
  // dropped — auto-commit then never fired for the rest of the process
  // lifetime. The fix parks early arrivals in a bounded per-method ring buffer
  // and drains them when the matching `on*()` registrar finally lands.
  // ----------------------------------------------------------------------------
  describe('early-arrival buffering', () => {
    it('replays a buffered message when the handler registers late (UpdateConfig)', async () => {
      const { client, fakeBuilder } = buildClient()
      await client.start()

      // Server push lands BEFORE the daemon registers its handler — exactly
      // the bootstrap-UpdateConfig race that this fix targets.
      await fakeBuilder.built!.fireInbound('UpdateConfig', {
        runtimeId: RUNTIME_ID,
        autoCommit: true,
      })

      const handler = vi.fn()
      client.onUpdateConfig(handler)

      // Drain is fire-and-forget-async from the registrar. Flush the
      // microtask queue so the detached promise can deliver the buffered
      // payload before we assert.
      await Promise.resolve()
      await Promise.resolve()

      expect(handler).toHaveBeenCalledTimes(1)
      expect(handler.mock.calls[0]?.[0]).toMatchObject({ autoCommit: true })
    })

    it('replays a buffered message for a second server-push method (StartTurn) — proves buffering is generic', async () => {
      const { client, fakeBuilder } = buildClient()
      await client.start()

      await fakeBuilder.built!.fireInbound('StartTurn', {
        runtimeId: RUNTIME_ID,
        sessionId: 's-early',
        conversationId: 'c1',
        prompt: 'hello',
        agentId: null,
      })

      const handler = vi.fn()
      client.onStartTurn(handler)

      await Promise.resolve()
      await Promise.resolve()

      expect(handler).toHaveBeenCalledTimes(1)
      expect(handler.mock.calls[0]?.[0]).toMatchObject({ sessionId: 's-early' })
    })

    it('replays in FIFO order', async () => {
      const { client, fakeBuilder } = buildClient()
      await client.start()

      await fakeBuilder.built!.fireInbound('UpdateConfig', {
        runtimeId: RUNTIME_ID,
        version: 'v1',
      })
      await fakeBuilder.built!.fireInbound('UpdateConfig', {
        runtimeId: RUNTIME_ID,
        version: 'v2',
      })

      const handler = vi.fn()
      client.onUpdateConfig(handler)

      await Promise.resolve()
      await Promise.resolve()
      await Promise.resolve()

      expect(handler).toHaveBeenCalledTimes(2)
      expect(handler.mock.calls[0]?.[0]).toMatchObject({ version: 'v1' })
      expect(handler.mock.calls[1]?.[0]).toMatchObject({ version: 'v2' })
    })

    it('drops oldest on overflow with a warn log (17 messages → 16 replayed)', async () => {
      const { client, log, fakeBuilder } = buildClient()
      await client.start()

      // 17 messages, tagged 0..16. With a per-method cap of 16, the OLDEST
      // (index 0) should be dropped on overflow.
      for (let i = 0; i < 17; i++) {
        await fakeBuilder.built!.fireInbound('UpdateConfig', {
          runtimeId: RUNTIME_ID,
          version: `v${i}`,
        })
      }

      // Exactly one overflow warn for this method.
      const overflowWarns = log.warn.mock.calls.filter((c) =>
        String(c[1]).includes('early-arrival buffer overflow'),
      )
      expect(overflowWarns).toHaveLength(1)
      expect(overflowWarns[0]?.[0]).toMatchObject({ method: 'UpdateConfig' })

      const handler = vi.fn()
      client.onUpdateConfig(handler)
      // Drain pumps multiple microtasks (one per replayed message).
      for (let i = 0; i < 20; i++) await Promise.resolve()

      expect(handler).toHaveBeenCalledTimes(16)
      // Oldest dropped → first replayed should be v1, last v16.
      expect(handler.mock.calls[0]?.[0]).toMatchObject({ version: 'v1' })
      expect(handler.mock.calls[15]?.[0]).toMatchObject({ version: 'v16' })
    })

    it('live messages after registration bypass the buffer (straight to handler)', async () => {
      const { client, fakeBuilder } = buildClient()
      await client.start()

      const handler = vi.fn()
      client.onUpdateConfig(handler)

      // No buffering happened (handler was already registered) — the live
      // path runs `#guardAndDispatch` and awaits the handler directly.
      await fakeBuilder.built!.fireInbound('UpdateConfig', {
        runtimeId: RUNTIME_ID,
        version: 'live-1',
      })

      expect(handler).toHaveBeenCalledTimes(1)
      expect(handler.mock.calls[0]?.[0]).toMatchObject({ version: 'live-1' })
    })

    it('re-registering a handler does NOT re-replay old buffered messages', async () => {
      const { client, fakeBuilder } = buildClient()
      await client.start()

      await fakeBuilder.built!.fireInbound('UpdateConfig', {
        runtimeId: RUNTIME_ID,
        version: 'early',
      })

      const first = vi.fn()
      client.onUpdateConfig(first)
      await Promise.resolve()
      await Promise.resolve()
      expect(first).toHaveBeenCalledTimes(1)

      // Buffer is empty now (drained). Re-register: the SECOND handler
      // should NOT receive the old buffered message.
      const second = vi.fn()
      client.onUpdateConfig(second)
      await Promise.resolve()
      await Promise.resolve()
      expect(second).toHaveBeenCalledTimes(0)

      // But it does receive live messages after registration.
      await fakeBuilder.built!.fireInbound('UpdateConfig', {
        runtimeId: RUNTIME_ID,
        version: 'live-after',
      })
      expect(second).toHaveBeenCalledTimes(1)
      expect(second.mock.calls[0]?.[0]).toMatchObject({ version: 'live-after' })
    })

    it('runtimeId mismatch is dropped + warned BEFORE buffering (not parked for later)', async () => {
      const { client, log, fakeBuilder } = buildClient()
      await client.start()

      // No handler registered yet, but the runtime-id guard runs first and
      // discards the spoofed message outright — buffering only protects
      // messages that were meant for us.
      await fakeBuilder.built!.fireInbound('UpdateConfig', {
        runtimeId: '99999999-9999-9999-9999-999999999999',
        version: 'spoof',
      })

      // The mismatch warn ran.
      const mismatchWarns = log.warn.mock.calls.filter((c) =>
        String(c[1]).includes('runtimeId mismatch'),
      )
      expect(mismatchWarns).toHaveLength(1)

      // Now register — the spoofed message must NOT be replayed.
      const handler = vi.fn()
      client.onUpdateConfig(handler)
      await Promise.resolve()
      await Promise.resolve()
      expect(handler).toHaveBeenCalledTimes(0)
    })
  })

  describe('lifecycle listeners', () => {
    it('fires onDisconnected when the connection closes', async () => {
      const { client, fakeBuilder } = buildClient()
      const cb = vi.fn()
      client.onDisconnected(cb)
      await client.start()

      const closeErr = new Error('socket closed')
      fakeBuilder.built!.fireClose(closeErr)

      expect(cb).toHaveBeenCalledTimes(1)
      expect(cb.mock.calls[0]?.[0]).toBe(closeErr)
    })

    it('fires onConnected on reconnect (in addition to initial start)', async () => {
      const { client, fakeBuilder } = buildClient()
      const cb = vi.fn()
      client.onConnected(cb)
      await client.start()
      expect(cb).toHaveBeenCalledTimes(1)

      fakeBuilder.built!.fireReconnected('connection-id-2')
      expect(cb).toHaveBeenCalledTimes(2)
    })
  })
})
