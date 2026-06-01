// Tests for TurnRunner — Cursor-only path.

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import { DaemonConfig } from '../config/DaemonConfig.js'
import type { SignalRClient } from '../signalr/SignalRClient.js'
import type {
  AgentSecretsDto,
  CancelTurnPayload,
  EmitEventPayload,
  StartTurnPayload,
  TurnCompletedPayload,
} from '../signalr/types.js'
import { AgentEventKind, AgentEventRunStatus } from '../signalr/types.js'

import type { AgentFactory } from './AgentFactory.js'
import type { TurnEvent } from './TurnEvent.js'
import type { TurnOptions } from './TurnOptions.js'
import { TurnRunner } from './TurnRunner.js'
import type { AfterPromptHook } from './types.js'
import { DaemonToolsMcpServer } from '../mcp/DaemonToolsMcpServer.js'

// ============================================================================
// Test helpers
// ============================================================================

const VALID_TOKEN =
  'eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJ0ZXN0In0.aGVsbG8td29ybGQtc2lnbmF0dXJlLXNlZ21lbnQ'
const RUNTIME_ID = '11111111-2222-3333-4444-555555555555'

function makeConfig(): DaemonConfig {
  return DaemonConfig.fromEnv({
    GLENN_RUNTIME_TOKEN: VALID_TOKEN,
    MAIN_API_URL: 'http://localhost:5338',
    RUNTIME_ID: RUNTIME_ID,
    DAEMON_VERSION: '0.1.0-dev',
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

interface SignalrStub {
  stub: SignalRClient
  emitEvent: ReturnType<typeof vi.fn<(p: EmitEventPayload) => Promise<void>>>
  invoke: ReturnType<typeof vi.fn<(method: string, ...args: unknown[]) => Promise<unknown>>>
  getSecrets: ReturnType<typeof vi.fn<() => Promise<AgentSecretsDto>>>
  fireStartTurn: (p: StartTurnPayload) => Promise<void> | void
  fireCancelTurn: (p: CancelTurnPayload) => Promise<void> | void
}

function makeSignalrStub(): SignalrStub {
  let startHandler: ((p: StartTurnPayload) => void | Promise<void>) | null = null
  let cancelHandler: ((p: CancelTurnPayload) => void | Promise<void>) | null = null

  const onStartTurn = vi.fn((h: (p: StartTurnPayload) => void | Promise<void>) => {
    startHandler = h
  })
  const onCancelTurn = vi.fn((h: (p: CancelTurnPayload) => void | Promise<void>) => {
    cancelHandler = h
  })
  const emitEvent = vi.fn(async (_p: EmitEventPayload) => {})
  const invoke = vi.fn(async (_method: string, ..._args: unknown[]) => undefined as unknown)
  // BYOK per-turn fetch. Default stub returns a non-empty Cursor key so the
  // happy-path tests reach the SDK; the daemon defense refuses turns with no
  // credentials, and returning null here would short-circuit every test that
  // expects the fake SDK to be exercised. The dedicated no-credentials test
  // below swaps this resolved value to assert the refusal path.
  const getSecrets = vi.fn(async (): Promise<AgentSecretsDto> => ({
    cursorApiKey: 'test-cursor-key',
  }))

  const stub = {
    onStartTurn,
    onCancelTurn,
    emitEvent,
    invoke,
    getSecrets,
  } as unknown as SignalRClient

  return {
    stub,
    emitEvent,
    invoke,
    getSecrets,
    fireStartTurn: (p) => startHandler?.(p),
    fireCancelTurn: (p) => cancelHandler?.(p),
  }
}

// Card 3.5 (cursor-native-chat-ux): the runner's terminal envelope is a
// `kind: Status, runStatus: Finished | Error` envelope carrying the
// TurnCompletedPayload JSON in `eventData`. These helpers walk the recorded
// `emitEvent` invocations and pull out the terminal envelopes so individual
// tests can stay terse and assert on the strongly-typed payload directly.
//
// The runner-authoritative terminal envelope is discriminated from
// turn-rejected envelopes (which share kind:Status,runStatus:Error) by the
// eventData shape: TurnCompletedPayload carries `success` + `runtimeId`;
// rejection eventData carries `type: 'turn_rejected'`.
function isTerminalStatus(p: EmitEventPayload): boolean {
  if (
    p.kind !== AgentEventKind.Status ||
    (p.runStatus !== AgentEventRunStatus.Finished &&
      p.runStatus !== AgentEventRunStatus.Error)
  ) {
    return false
  }
  // Discriminate runner-authoritative TurnCompleted from rejection/no-creds/
  // branch-divergent envelopes (which all also ride on kind:Status+Error).
  try {
    const parsed = JSON.parse(p.eventData) as { success?: unknown; type?: unknown }
    return typeof parsed.success === 'boolean'
  } catch {
    return false
  }
}

function turnCompletedCalls(
  emitEvent: ReturnType<typeof vi.fn<(p: EmitEventPayload) => Promise<void>>>,
): TurnCompletedPayload[] {
  return emitEvent.mock.calls
    .filter((c) => isTerminalStatus(c[0]))
    .map((c) => JSON.parse(c[0].eventData) as TurnCompletedPayload)
}

function lastTurnCompleted(
  emitEvent: ReturnType<typeof vi.fn<(p: EmitEventPayload) => Promise<void>>>,
): TurnCompletedPayload | undefined {
  const all = turnCompletedCalls(emitEvent)
  return all[all.length - 1]
}

/**
 * Async-iterable factory that yields canned events. Captures the options it
 * was invoked with so tests can assert what TurnRunner passed through.
 *
 * `behavior` defaults to 'iterate'. 'throw' makes the iterator throw the
 * supplied error mid-stream (after yielding all events). 'wait-for-abort'
 * yields the events and then waits for the abort signal — used for cancel
 * tests so the runner doesn't race past the cancel arrival.
 */
type FakeSdkBehavior =
  | { kind: 'iterate' }
  | { kind: 'throw'; error: Error }
  | { kind: 'wait-for-abort' }

interface FakeCursor {
  factory: AgentFactory
  optionsCalls: TurnOptions[]
  factoryCallCount: () => number
}

function fakeCursorFactory(
  events: TurnEvent[],
  behavior: FakeSdkBehavior = { kind: 'iterate' },
): FakeCursor {
  const optionsCalls: TurnOptions[] = []
  const factory: AgentFactory = (opts) => {
    optionsCalls.push(opts)
    return {
      [Symbol.asyncIterator]: async function* () {
        for (const e of events) {
          yield e
        }
        if (behavior.kind === 'throw') {
          throw behavior.error
        }
        if (behavior.kind === 'wait-for-abort') {
          // Sleep until the caller's abort signal fires; throw an AbortError
          // so the runner sees the SDK's standard cancellation contract.
          await new Promise<void>((resolve) => {
            if (opts.abortSignal?.aborted) {
              resolve()
              return
            }
            opts.abortSignal?.addEventListener('abort', () => resolve(), { once: true })
          })
          const err = new Error('aborted')
          err.name = 'AbortError'
          throw err
        }
      },
    }
  }
  return { factory, optionsCalls, factoryCallCount: () => optionsCalls.length }
}

interface BuildOpts {
  events?: TurnEvent[]
  behavior?: FakeSdkBehavior
  hooks?: readonly AfterPromptHook[]
}

function build(opts: BuildOpts = {}) {
  const config = makeConfig()
  const log = makeLogger()
  const sig = makeSignalrStub()
  const cursor = fakeCursorFactory(opts.events ?? [], opts.behavior)
  const daemonToolsMcpServer = new DaemonToolsMcpServer({
    tools: [],
    logger: log as unknown as import('pino').Logger,
  })
  const runner = new TurnRunner({
    signalr: sig.stub,
    config,
    cursorFactory: cursor.factory,
    daemonToolsMcpServer,
    ...(opts.hooks !== undefined ? { afterPromptHooks: opts.hooks } : {}),
    logger: log as unknown as import('pino').Logger,
  })
  runner.start()
  return { runner, config, log, sig, cursor }
}

const SESSION_ID = 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee'
const CONVERSATION_ID = 'cccccccc-cccc-cccc-cccc-cccccccccccc'

function makeStartTurn(overrides: Partial<StartTurnPayload> = {}): StartTurnPayload {
  return {
    sessionId: SESSION_ID,
    conversationId: CONVERSATION_ID,
    prompt: 'hello',
    yolo: false,
    pullBeforeStart: false,
    ...overrides,
  }
}

/** Wait until `runner.state().kind === 'idle'` (or fail after timeout). */
async function waitForIdle(runner: TurnRunner, timeoutMs = 1000): Promise<void> {
  if (runner.state().kind === 'idle') return
  await new Promise<void>((resolve, reject) => {
    const t = setTimeout(() => {
      reject(new Error(`waitForIdle timeout, state=${runner.state().kind}`))
    }, timeoutMs)
    runner.once('idle', () => {
      clearTimeout(t)
      resolve()
    })
  })
}

// ============================================================================
// Tests
// ============================================================================

describe('TurnRunner', () => {
  beforeEach(() => {
    // Real timers — the runner doesn't use timers itself, and we need real
    // microtask flushing for the await-driven event loop.
  })

  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('happy path: streams cursor-native events, captures agentId, sends terminal Status envelope', async () => {
    // Cursor-native MappedCursorEvent shapes. The mapper produces:
    //   - system:init → MappedSystemEvent (kind='System')
    //   - assistant.text → MappedAssistantTextEvent (kind=AssistantText)
    //   - status:FINISHED → MappedStatusEvent (kind=Status,runStatus=Finished)
    //   - then the runner emits its own authoritative terminal envelope.
    const events: TurnEvent[] = [
      { kind: 'System', subtype: 'init', agentId: 'sess-123', eventData: { subtype: 'init', agentId: 'sess-123', model: 'cursor-x' } },
      { kind: AgentEventKind.AssistantText, text: 'hi' },
      { kind: AgentEventKind.Status, runStatus: AgentEventRunStatus.Finished },
    ]
    const { runner, sig } = build({ events })

    await sig.fireStartTurn(makeStartTurn())
    await waitForIdle(runner)

    // 3 mapper-emitted events + 1 runner-authoritative terminal envelope.
    expect(sig.emitEvent).toHaveBeenCalledTimes(4)
    const kinds = sig.emitEvent.mock.calls.map((c) => c[0].kind)
    expect(kinds).toEqual([
      AgentEventKind.Status, // System carrier flattened to Status on the wire
      AgentEventKind.AssistantText,
      AgentEventKind.Status, // mapper terminal Status (runStatus=Finished)
      AgentEventKind.Status, // runner-authoritative terminal envelope
    ])

    // The AssistantText event carries a clean `text` first-class field —
    // post-cursor-native, payload is on the typed envelope, not eventData.
    const assistantEnvelope = sig.emitEvent.mock.calls[1]![0] as {
      text?: string
      eventData: string
    }
    expect(assistantEnvelope.text).toBe('hi')
    expect(assistantEnvelope.eventData).toBe('{}')

    // The runner-authoritative TurnCompletedPayload envelope (discriminated
    // from the mapper's terminal Status by carrying `success` in eventData).
    expect(turnCompletedCalls(sig.emitEvent)).toHaveLength(1)
    const completed = lastTurnCompleted(sig.emitEvent)!
    expect(completed.success).toBe(true)
    expect(completed.newAgentId).toBe('sess-123')
    expect(completed.runtimeId).toBe(RUNTIME_ID)
    expect(completed.sessionId).toBe(SESSION_ID)
    expect(completed.reason).toBeUndefined()

    expect(runner.state().kind).toBe('idle')
  })

  it('captures agentId from the first event that carries one (not necessarily the first event)', async () => {
    const events: TurnEvent[] = [
      { kind: AgentEventKind.AssistantText, text: 'pre' }, // no agentId
      { kind: 'System', subtype: 'init', agentId: 'sess-late', eventData: {} },
      { kind: AgentEventKind.AssistantText, text: 'after', agentId: 'sess-other' }, // ignored — first wins
    ]
    const { runner, sig } = build({ events })

    await sig.fireStartTurn(makeStartTurn())
    await waitForIdle(runner)

    expect(lastTurnCompleted(sig.emitEvent)?.newAgentId).toBe('sess-late')
  })

  it('forwards agentId hint to the factory as `resume`', async () => {
    const { runner, sig, cursor } = build({
      events: [{ kind: AgentEventKind.Status, runStatus: AgentEventRunStatus.Finished, agentId: 'prev-session' }],
    })
    await sig.fireStartTurn(makeStartTurn({ agentId: 'prev-session' }))
    await waitForIdle(runner)

    expect(cursor.optionsCalls).toHaveLength(1)
    expect(cursor.optionsCalls[0]?.resume).toBe('prev-session')
    expect(lastTurnCompleted(sig.emitEvent)?.newAgentId).toBe('prev-session')
  })

  it('uses /data/project/repo as cwd', async () => {
    const { runner, sig, cursor } = build({
      events: [{ kind: AgentEventKind.Status, runStatus: AgentEventRunStatus.Finished }],
    })
    await sig.fireStartTurn(makeStartTurn())
    await waitForIdle(runner)
    expect(cursor.optionsCalls[0]?.cwd).toBe('/data/project/repo')
  })

  it('refuses a concurrent StartTurn while one is running via invoke("TurnRefused")', async () => {
    const { runner, sig, cursor } = build({ events: [], behavior: { kind: 'wait-for-abort' } })

    // Fire first turn (A); it parks waiting on its abort signal.
    void sig.fireStartTurn(makeStartTurn({ sessionId: SESSION_ID }))
    // Yield so the runner enters `running`.
    await Promise.resolve()
    await Promise.resolve()
    expect(runner.state().kind).toBe('running')

    // SDK factory was called exactly once (turn A).
    expect(cursor.factoryCallCount()).toBe(1)

    // Fire a second StartTurn (B) for a DIFFERENT session — must be refused via
    // the hub's TurnRefused method, not via emitEvent.
    const otherSession = 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb'
    await sig.fireStartTurn(makeStartTurn({ sessionId: otherSession }))

    // Single TurnRefused invoke with the exact wire shape.
    const refusedCalls = sig.invoke.mock.calls.filter((c) => c[0] === 'TurnRefused')
    expect(refusedCalls).toHaveLength(1)
    expect(refusedCalls[0]?.[1]).toEqual({
      sessionId: otherSession,
      reason: 'turn_already_running',
      currentSessionId: SESSION_ID,
    })

    // No emitEvent for the rejected session (the old `turn_rejected` path is
    // gone for the single-turn-invariant case).
    const rejectedEmits = sig.emitEvent.mock.calls
      .map((c) => c[0])
      .filter((p) => p.sessionId === otherSession)
    expect(rejectedEmits).toHaveLength(0)

    // State unchanged: still running on A.
    const state = runner.state()
    expect(state.kind).toBe('running')
    if (state.kind === 'running') {
      expect(state.sessionId).toBe(SESSION_ID)
    }

    // SDK factory was NOT called a second time.
    expect(cursor.factoryCallCount()).toBe(1)

    // Original turn still running. Cancel it cleanly so the test exits.
    await runner.cancel('test-cleanup')
    await waitForIdle(runner)
  })

  it('refuses a concurrent StartTurn while canceling via invoke("TurnRefused") with the original sessionId', async () => {
    const { runner, sig, cursor } = build({ events: [], behavior: { kind: 'wait-for-abort' } })

    // Fire first turn (A); enters running.
    void sig.fireStartTurn(makeStartTurn({ sessionId: SESSION_ID }))
    await Promise.resolve()
    await Promise.resolve()
    expect(runner.state().kind).toBe('running')
    expect(cursor.factoryCallCount()).toBe(1)

    // Cancel A — runner transitions to `canceling` (the SDK iterator is still
    // unwinding because it's a wait-for-abort fake).
    await runner.cancel('user_aborted')
    expect(runner.state().kind).toBe('canceling')

    // Fire B while the runner is still in `canceling`. Must be refused with
    // currentSessionId pointing at A — the canceling state still exposes it.
    const otherSession = 'cccccccc-1111-2222-3333-444444444444'
    await sig.fireStartTurn(makeStartTurn({ sessionId: otherSession }))

    const refusedCalls = sig.invoke.mock.calls.filter((c) => c[0] === 'TurnRefused')
    expect(refusedCalls).toHaveLength(1)
    expect(refusedCalls[0]?.[1]).toEqual({
      sessionId: otherSession,
      reason: 'turn_already_running',
      currentSessionId: SESSION_ID,
    })

    // SDK factory was not called for B.
    expect(cursor.factoryCallCount()).toBe(1)

    // Drain so the test exits cleanly.
    await waitForIdle(runner)
  })

  it('CancelTurn for the current session aborts it; TurnCompleted reports canceled', async () => {
    const { runner, sig, cursor } = build({ behavior: { kind: 'wait-for-abort' } })

    void sig.fireStartTurn(makeStartTurn())
    // Allow the runner to enter `running` and the SDK iterator to subscribe.
    await Promise.resolve()
    await Promise.resolve()
    expect(runner.state().kind).toBe('running')

    await sig.fireCancelTurn({ sessionId: SESSION_ID, reason: 'user_aborted' })
    await waitForIdle(runner)

    // The SDK saw the abort signal fire.
    expect(cursor.optionsCalls[0]?.abortSignal?.aborted).toBe(true)

    expect(turnCompletedCalls(sig.emitEvent)).toHaveLength(1)
    const completed = lastTurnCompleted(sig.emitEvent)!
    expect(completed.success).toBe(false)
    expect(completed.reason).toBe('canceled')
    expect(completed.error).toBeUndefined()
  })

  it('CancelTurn for a different session is ignored; current turn continues', async () => {
    const { runner, sig } = build({ behavior: { kind: 'wait-for-abort' } })

    void sig.fireStartTurn(makeStartTurn({ sessionId: SESSION_ID }))
    await Promise.resolve()
    await Promise.resolve()
    expect(runner.state().kind).toBe('running')

    await sig.fireCancelTurn({
      sessionId: 'dddddddd-dddd-dddd-dddd-dddddddddddd',
      reason: 'wrong-session',
    })

    // Still running.
    expect(runner.state().kind).toBe('running')

    // Cleanup.
    await runner.cancel('test-cleanup')
    await waitForIdle(runner)
  })

  it('SDK throws non-abort error mid-stream → TurnCompleted has reason sdk_error + error', async () => {
    const events: TurnEvent[] = [{ kind: AgentEventKind.AssistantText, text: 'partial' }]
    const { runner, sig } = build({
      events,
      behavior: { kind: 'throw', error: new Error('nope') },
    })

    await sig.fireStartTurn(makeStartTurn())
    await waitForIdle(runner)

    const completed = lastTurnCompleted(sig.emitEvent)!
    expect(completed.success).toBe(false)
    expect(completed.reason).toBe('sdk_error')
    expect(completed.error).toBe('nope')

    expect(runner.state().kind).toBe('idle') // recoverable — next turn can start
  })

  it('afterPromptHook throws → next hook still runs; turn still completes successfully', async () => {
    const order: string[] = []
    const hooks: AfterPromptHook[] = [
      async () => {
        order.push('h1')
        throw new Error('h1 boom')
      },
      async (ctx) => {
        order.push(`h2(${ctx.agentId ?? 'none'})`)
      },
    ]
    const events: TurnEvent[] = [
      { kind: 'System', subtype: 'init', agentId: 'sess-7', eventData: { agentId: 'sess-7' } },
      { kind: AgentEventKind.AssistantText, text: 'hi' },
      { kind: AgentEventKind.Status, runStatus: AgentEventRunStatus.Finished },
    ]
    const { runner, sig, log } = build({ events, hooks })

    await sig.fireStartTurn(makeStartTurn())
    await waitForIdle(runner)

    expect(order).toEqual(['h1', 'h2(sess-7)'])

    // Turn still success.
    const completed = lastTurnCompleted(sig.emitEvent)!
    expect(completed.success).toBe(true)

    // Hook throw was logged at error level.
    expect(log.error).toHaveBeenCalled()
    const errMsgs = log.error.mock.calls.map((c) => c[1])
    expect(errMsgs.some((m) => typeof m === 'string' && m.includes('afterPromptHook threw'))).toBe(
      true,
    )
  })

  it('runtimeId mismatch in StartTurn drops the message silently (state unchanged)', async () => {
    const { runner, sig } = build({
      events: [{ kind: AgentEventKind.Status, runStatus: AgentEventRunStatus.Finished }],
    })

    const wrong = '99999999-9999-9999-9999-999999999999'
    await sig.fireStartTurn(makeStartTurn({ runtimeId: wrong }))

    // No turn started; nothing emitted; nothing completed.
    expect(runner.state().kind).toBe('idle')
    expect(sig.emitEvent).not.toHaveBeenCalled()
    expect(turnCompletedCalls(sig.emitEvent)).toHaveLength(0)
  })

  it('setAcceptingNewTurns(false) → StartTurn rejected with daemon_draining', async () => {
    const { runner, sig } = build({
      events: [{ kind: AgentEventKind.Status, runStatus: AgentEventRunStatus.Finished }],
    })
    runner.setAcceptingNewTurns(false)

    await sig.fireStartTurn(makeStartTurn())

    expect(runner.state().kind).toBe('idle')
    // Single TurnRefused emit — TurnRunner skips the TurnCompleted envelope on
    // the draining path because no turn was actually started.
    expect(sig.emitEvent).toHaveBeenCalledTimes(1)
    const data = JSON.parse(sig.emitEvent.mock.calls[0]![0].eventData) as { reason: string }
    expect(data.reason).toBe('daemon_draining')
    expect(turnCompletedCalls(sig.emitEvent)).toHaveLength(0)
  })

  it('emits `activity` on turn start and `idle` on turn end', async () => {
    const events: TurnEvent[] = [{ kind: AgentEventKind.Status, runStatus: AgentEventRunStatus.Finished }]
    const { runner, sig } = build({ events })

    const order: string[] = []
    runner.on('activity', () => order.push('activity'))
    runner.on('idle', () => order.push('idle'))

    await sig.fireStartTurn(makeStartTurn())
    await waitForIdle(runner)

    expect(order).toEqual(['activity', 'idle'])
  })

  it('a single emitEvent rejection does NOT fail the turn — later events still ship and terminal Status still sends', async () => {
    const events: TurnEvent[] = [
      { kind: AgentEventKind.AssistantText, text: 'x' },
      { kind: AgentEventKind.Thinking, text: 'b' },
      { kind: AgentEventKind.Status, runStatus: AgentEventRunStatus.Finished },
    ]
    const { runner, sig } = build({ events })
    sig.emitEvent.mockRejectedValueOnce(new Error('first emit failed'))

    await sig.fireStartTurn(makeStartTurn())
    await waitForIdle(runner)

    // 3 mapper events + 1 runner terminal envelope. The first emit rejected
    // but the loop kept going.
    expect(sig.emitEvent).toHaveBeenCalledTimes(4)

    // Only the runner-authoritative envelope carries TurnCompletedPayload —
    // the mapper's terminal Status uses `eventData: '{}'`.
    expect(turnCompletedCalls(sig.emitEvent)).toHaveLength(1)
    expect(lastTurnCompleted(sig.emitEvent)?.success).toBe(true)
  })

  it('terminal Status envelope emit rejection is logged but does not throw', async () => {
    const events: TurnEvent[] = [{ kind: AgentEventKind.Status, runStatus: AgentEventRunStatus.Finished }]
    const { runner, sig, log } = build({ events })
    // The mapper-emitted terminal Status goes first; the runner then emits
    // its own authoritative terminal envelope which we reject.
    sig.emitEvent.mockResolvedValueOnce(undefined)
    sig.emitEvent.mockRejectedValueOnce(new Error('hub method missing'))

    await sig.fireStartTurn(makeStartTurn())
    await waitForIdle(runner)

    expect(runner.state().kind).toBe('idle')
    expect(log.error).toHaveBeenCalled()
    const errMsgs = log.error.mock.calls.map((c) => c[1])
    expect(
      errMsgs.some(
        (m) => typeof m === 'string' && m.includes('failed to emit terminal Status envelope'),
      ),
    ).toBe(true)
  })

  it('mcpUrls (when present) get forwarded as keyed Bearer-auth HTTP configs', async () => {
    const { runner, sig, cursor, config } = build({
      events: [{ kind: AgentEventKind.Status, runStatus: AgentEventRunStatus.Finished }],
    })

    // Cast through unknown — the .NET DTO doesn't carry mcpUrls today; the
    // runner reads it tolerantly. This exercises the forward path.
    const start = makeStartTurn() as StartTurnPayload & { mcpUrls: string[] }
    start.mcpUrls = ['https://api.example.com/mcp/kanban', 'https://api.example.com/mcp/files']
    await sig.fireStartTurn(start)
    await waitForIdle(runner)

    const passed = cursor.optionsCalls[0]?.mcpServers as
      | Record<string, { type: string; url: string; headers: Record<string, string> }>
      | undefined
    expect(passed).toBeDefined()
    expect(Object.keys(passed!).sort()).toEqual(['files', 'kanban'])
    expect(passed!.kanban?.type).toBe('http')
    expect(passed!.kanban?.url).toBe('https://api.example.com/mcp/kanban')
    expect(passed!.kanban?.headers.Authorization).toBe(`Bearer ${config.runtimeToken}`)
  })

  it('cancel() while idle is a no-op; cancel() while canceling is idempotent', async () => {
    const { runner, sig } = build({ behavior: { kind: 'wait-for-abort' } })

    // Idle cancel — does nothing.
    await runner.cancel('reason-1')
    expect(runner.state().kind).toBe('idle')

    // Run a turn.
    void sig.fireStartTurn(makeStartTurn())
    await Promise.resolve()
    await Promise.resolve()
    expect(runner.state().kind).toBe('running')

    // First cancel transitions to canceling.
    await runner.cancel('reason-2')
    expect(runner.state().kind).toBe('canceling')

    // Second cancel while canceling — still canceling, no double-abort throw.
    await runner.cancel('reason-3')
    expect(runner.state().kind).toBe('canceling')

    // Drain.
    await waitForIdle(runner)
    expect(runner.state().kind).toBe('idle')
  })

  // Helper: locate the no_credentials carrier among emitted events. Both
  // the carrier and the terminal envelope ride the same wire shape
  // (kind: Status, runStatus: Error); we disambiguate by parsing eventData
  // and looking at the `type` discriminator the runner stamps.
  const findNoCredsCarrier = (
    sig: ReturnType<typeof makeSignalrStub>,
  ): EmitEventPayload | undefined =>
    sig.emitEvent.mock.calls.find((c) => {
      if (c[0].kind !== AgentEventKind.Status) return false
      try {
        const d = JSON.parse(c[0].eventData) as { type?: string }
        return d.type === 'no_credentials'
      } catch {
        return false
      }
    })?.[0]

  it('no-credentials defense: GetSecrets returns null → emits no_credentials carrier, skips SDK, completes with reason no_credentials', async () => {
    const events: TurnEvent[] = [{ kind: AgentEventKind.Status, runStatus: AgentEventRunStatus.Finished }]
    const { runner, sig, cursor } = build({ events })

    // Override the default stub: simulate the host having no Cursor
    // credentials anywhere on the stack.
    sig.getSecrets.mockResolvedValueOnce({
      cursorApiKey: null,
    })

    await sig.fireStartTurn(makeStartTurn())
    await waitForIdle(runner)

    // SDK was NEVER called — Layer B refuses the turn before instantiating
    // the Cursor agent.
    expect(cursor.factoryCallCount()).toBe(0)

    // Two emit calls: (1) the no_credentials Status carrier; (2) the runner's
    // terminal Status envelope that closes the session.
    expect(sig.emitEvent).toHaveBeenCalledTimes(2)

    const failed = findNoCredsCarrier(sig)
    expect(failed).toBeDefined()
    const failedData = JSON.parse(failed!.eventData) as {
      type?: string
      reason?: string
      message?: string
    }
    expect(failedData.type).toBe('no_credentials')
    expect(failedData.reason).toBe('no_credentials')
    expect(typeof failedData.message).toBe('string')
    expect(failedData.message!.length).toBeGreaterThan(0)

    // Terminal envelope closes the session out with success=false / reason.
    // Both carriers ride kind:Status/runStatus:Error; the helper filters on
    // eventData shape to find the runner-authoritative one.
    const completed = sig.emitEvent.mock.calls
      .map((c) => {
        try {
          return JSON.parse(c[0].eventData) as Partial<TurnCompletedPayload>
        } catch {
          return undefined
        }
      })
      .find((d): d is TurnCompletedPayload => d?.reason === 'no_credentials' && typeof d.success === 'boolean')
    expect(completed).toBeDefined()
    expect(completed!.success).toBe(false)
    expect(completed!.reason).toBe('no_credentials')

    expect(runner.state().kind).toBe('idle')
  })

  it('cursor no-credentials defense: refuses turn when GetSecrets returns no key', async () => {
    const events: TurnEvent[] = [{ kind: AgentEventKind.Status, runStatus: AgentEventRunStatus.Finished }]
    const { runner, sig, cursor } = build({ events })

    sig.getSecrets.mockResolvedValueOnce({
      cursorApiKey: null,
    })

    await sig.fireStartTurn(makeStartTurn())
    await waitForIdle(runner)

    expect(cursor.factoryCallCount()).toBe(0)

    const failed = findNoCredsCarrier(sig)
    expect(failed).toBeDefined()
    const failedData = JSON.parse(failed!.eventData) as {
      type?: string
      reason?: string
      message?: string
    }
    expect(failedData.type).toBe('no_credentials')
    expect(failedData.reason).toBe('no_credentials')
    expect(failedData.message).toContain('Cursor API key')
  })

  it('terminal failure wins over visible assistant content for success=false', async () => {
    const events: TurnEvent[] = [
      { kind: AgentEventKind.AssistantText, text: 'partial' },
      {
        kind: AgentEventKind.Status,
        runStatus: AgentEventRunStatus.Error,
        statusMessage: 'model failed after streaming',
      },
    ]
    const { runner, sig } = build({ events })

    await sig.fireStartTurn(makeStartTurn())
    await waitForIdle(runner)

    const completed = lastTurnCompleted(sig.emitEvent)!
    expect(completed.success).toBe(false)
    expect(completed.reason).toBe('error')
    expect(completed.error).toBe('model failed after streaming')
  })
})
