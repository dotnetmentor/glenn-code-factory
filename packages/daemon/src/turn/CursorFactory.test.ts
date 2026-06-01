// CursorFactory unit tests — exercise the factory's per-turn behavior
// against a stubbed `@cursor/sdk` module.
//
// These tests focus on what the factory does AROUND `Agent.create/resume` and
// `agent.send` (BYOK env handoff, system-prompt prepending, MCP map
// construction, abort wiring, resume vs create dispatch) — the actual frame
// translation is covered by CursorEventMapper.test.ts.

import { describe, expect, it, vi } from 'vitest'
import { pino } from 'pino'

import { buildCursorFactory } from './CursorFactory.js'
import type {
  CursorSdkAgent,
  CursorSdkModule,
  CursorSdkRun,
  CursorAgentOptions,
} from './CursorFactory.js'
import type { CursorSdkMessage } from './CursorEventMapper.js'
import type { TurnEvent } from './TurnEvent.js'
import type { TurnOptions } from './TurnOptions.js'

const logger = pino({ level: 'silent' })

// ---------------------------------------------------------------------------
// Stubs / helpers
// ---------------------------------------------------------------------------

interface StubObservations {
  createCalls: CursorAgentOptions[]
  resumeCalls: Array<{ agentId: string; options?: Partial<CursorAgentOptions> }>
  sentMessages: string[]
  closedAgents: string[]
  cancelCalled: boolean
  waitCalled: boolean
  envAtSend: string | undefined
}

/**
 * Build a fake `CursorSdkModule` whose `Agent.create()` / `Agent.resume()`
 * record their arguments and return a fake `SDKAgent` whose `send()` returns
 * a fake `Run` whose `stream()` yields the provided frames.
 *
 * Default frames: a single `status: FINISHED` so the iterator terminates
 * cleanly.
 */
function buildStubSdk(
  frames: CursorSdkMessage[] = [
    { type: 'status', agent_id: 'agent-stub', status: 'FINISHED' },
  ],
  waitResult: {
    id: string
    status: 'finished' | 'error' | 'cancelled'
    durationMs?: number
    result?: string
  } = { id: 'run-1', status: 'finished', durationMs: 42 },
): { module: CursorSdkModule; observed: StubObservations } {
  const observed: StubObservations = {
    createCalls: [],
    resumeCalls: [],
    sentMessages: [],
    closedAgents: [],
    cancelCalled: false,
    waitCalled: false,
    envAtSend: undefined,
  }

  const makeAgent = (agentId: string): CursorSdkAgent => {
    const fakeRun: CursorSdkRun = {
      id: 'run-1',
      agentId,
      stream: async function* () {
        for (const f of frames) yield f
      },
      cancel: async () => {
        observed.cancelCalled = true
      },
      wait: async () => {
        observed.waitCalled = true
        return waitResult
      },
      supports: (operation) => operation === 'wait' || operation === 'stream',
    }
    return {
      agentId,
      send: async (msg: string) => {
        observed.sentMessages.push(msg)
        observed.envAtSend = process.env['CURSOR_API_KEY']
        return fakeRun
      },
      close: () => {
        observed.closedAgents.push(agentId)
      },
    }
  }

  const module: CursorSdkModule = {
    Agent: {
      create: async (options) => {
        observed.createCalls.push(options)
        return makeAgent('agent-fresh')
      },
      resume: async (agentId, options) => {
        observed.resumeCalls.push({
          agentId,
          ...(options !== undefined ? { options } : {}),
        })
        return makeAgent(agentId)
      },
    },
  }
  return { module, observed }
}

async function drain(iter: AsyncIterable<TurnEvent>): Promise<TurnEvent[]> {
  const out: TurnEvent[] = []
  for await (const e of iter) out.push(e)
  return out
}

const BASE_OPTS: TurnOptions = {
  prompt: 'hello',
  cwd: '/data/project/repo',
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

describe('CursorFactory', () => {
  // -----------------------------------------------------------------------
  // Resume vs create dispatch
  // -----------------------------------------------------------------------

  it('calls Agent.create when no resume hint is provided', async () => {
    const { module, observed } = buildStubSdk()
    const factory = buildCursorFactory({
      logger,
      importSdk: async () => module,
      defaultModel: { id: 'auto' },
    })
    await drain(factory(BASE_OPTS))
    expect(observed.createCalls).toHaveLength(1)
    expect(observed.resumeCalls).toHaveLength(0)
  })

  it('calls Agent.resume(agentId) when opts.resume is provided', async () => {
    const { module, observed } = buildStubSdk()
    const factory = buildCursorFactory({
      logger,
      importSdk: async () => module,
      defaultModel: { id: 'auto' },
    })
    await drain(factory({ ...BASE_OPTS, resume: 'agent-prior' }))
    expect(observed.resumeCalls).toHaveLength(1)
    expect(observed.resumeCalls[0]?.agentId).toBe('agent-prior')
    expect(observed.createCalls).toHaveLength(0)
  })

  it('treats empty-string resume as "create fresh", not resume', async () => {
    const { module, observed } = buildStubSdk()
    const factory = buildCursorFactory({
      logger,
      importSdk: async () => module,
      defaultModel: { id: 'auto' },
    })
    await drain(factory({ ...BASE_OPTS, resume: '' }))
    expect(observed.createCalls).toHaveLength(1)
    expect(observed.resumeCalls).toHaveLength(0)
  })

  // -----------------------------------------------------------------------
  // Model resolution
  // -----------------------------------------------------------------------

  it('forwards opts.model as the AgentOptions.model id', async () => {
    const { module, observed } = buildStubSdk()
    const factory = buildCursorFactory({
      logger,
      importSdk: async () => module,
      defaultModel: { id: 'auto' },
    })
    await drain(factory({ ...BASE_OPTS, model: 'gpt-5-codex' }))
    expect(observed.createCalls[0]?.model).toEqual({ id: 'gpt-5-codex' })
  })

  it('falls back to defaultModel when opts.model is absent', async () => {
    const { module, observed } = buildStubSdk()
    const factory = buildCursorFactory({
      logger,
      importSdk: async () => module,
      defaultModel: { id: 'sonnet-4-5' },
    })
    await drain(factory(BASE_OPTS))
    expect(observed.createCalls[0]?.model).toEqual({ id: 'sonnet-4-5' })
  })

  it('throws when no model can be resolved (neither opts.model nor defaultModel)', async () => {
    const { module } = buildStubSdk()
    const factory = buildCursorFactory({
      logger,
      importSdk: async () => module,
      // no defaultModel
    })
    // The error surfaces when the iterator starts running.
    await expect(drain(factory(BASE_OPTS))).rejects.toThrow(/no model resolved/)
  })

  // -----------------------------------------------------------------------
  // BYOK env handoff
  // -----------------------------------------------------------------------

  it('scopes CURSOR_API_KEY to the SDK call and restores prior value after', async () => {
    const prior = process.env['CURSOR_API_KEY']
    process.env['CURSOR_API_KEY'] = 'prior-value'
    try {
      const { module, observed } = buildStubSdk()
      const factory = buildCursorFactory({
        logger,
        importSdk: async () => module,
        defaultModel: { id: 'auto' },
      })
      await drain(
        factory({
          ...BASE_OPTS,
          secrets: { cursorApiKey: 'scoped-key' } as TurnOptions['secrets'],
        }),
      )
      // During send(), the env var should be the scoped value.
      expect(observed.envAtSend).toBe('scoped-key')
      // After the iterator drains, the prior value is restored.
      expect(process.env['CURSOR_API_KEY']).toBe('prior-value')
      // Also forwarded as `apiKey` on AgentOptions.
      expect(observed.createCalls[0]?.apiKey).toBe('scoped-key')
    } finally {
      if (prior === undefined) {
        delete process.env['CURSOR_API_KEY']
      } else {
        process.env['CURSOR_API_KEY'] = prior
      }
    }
  })

  it('deletes CURSOR_API_KEY for the SDK call when no key is provided (no stale daemon-env leak)', async () => {
    const prior = process.env['CURSOR_API_KEY']
    process.env['CURSOR_API_KEY'] = 'should-not-leak'
    try {
      const { module, observed } = buildStubSdk()
      const factory = buildCursorFactory({
        logger,
        importSdk: async () => module,
        defaultModel: { id: 'auto' },
      })
      await drain(factory(BASE_OPTS))
      expect(observed.envAtSend).toBeUndefined()
      // No apiKey forwarded either.
      expect(observed.createCalls[0]?.apiKey).toBeUndefined()
      // Prior value restored after the iterator drains.
      expect(process.env['CURSOR_API_KEY']).toBe('should-not-leak')
    } finally {
      if (prior === undefined) {
        delete process.env['CURSOR_API_KEY']
      } else {
        process.env['CURSOR_API_KEY'] = prior
      }
    }
  })

  // -----------------------------------------------------------------------
  // System prompt prepending
  // -----------------------------------------------------------------------

  it('prepends the harness + project rules to the first user turn prompt', async () => {
    const { module, observed } = buildStubSdk()
    const factory = buildCursorFactory({
      logger,
      importSdk: async () => module,
      defaultModel: { id: 'auto' },
      // Non-existent dir → no rules; harness still lands.
      projectRepoDir: '/nonexistent/cursor-factory-test',
    })
    await drain(factory(BASE_OPTS))
    const sent = observed.sentMessages[0] ?? ''
    expect(sent).toContain(BASE_OPTS.prompt)
    // The harness body is non-empty; the prepended prompt should be strictly
    // longer than just the user prompt.
    expect(sent.length).toBeGreaterThan(BASE_OPTS.prompt.length)
    // The factory delimits harness from prompt with `\n\n---\n\n`.
    expect(sent).toMatch(/\n\n---\n\n/)
  })

  it('sends the user prompt verbatim on a resume turn (no re-injection)', async () => {
    const { module, observed } = buildStubSdk()
    const factory = buildCursorFactory({
      logger,
      importSdk: async () => module,
      defaultModel: { id: 'auto' },
      projectRepoDir: '/nonexistent/cursor-factory-test',
    })
    await drain(factory({ ...BASE_OPTS, resume: 'agent-prior' }))
    // On resume, the prompt body is the user prompt verbatim.
    expect(observed.sentMessages[0]).toBe(BASE_OPTS.prompt)
  })

  // -----------------------------------------------------------------------
  // local.cwd + settingSources
  // -----------------------------------------------------------------------

  it('passes opts.cwd as local.cwd with settingSources=[project]', async () => {
    const { module, observed } = buildStubSdk()
    const factory = buildCursorFactory({
      logger,
      importSdk: async () => module,
      defaultModel: { id: 'auto' },
    })
    await drain(factory(BASE_OPTS))
    expect(observed.createCalls[0]?.local).toEqual({
      cwd: '/data/project/repo',
      settingSources: ['project'],
    })
  })

  // -----------------------------------------------------------------------
  // Agent lifecycle (close on drain)
  // -----------------------------------------------------------------------

  it('calls agent.close() when the stream drains normally', async () => {
    const { module, observed } = buildStubSdk()
    const factory = buildCursorFactory({
      logger,
      importSdk: async () => module,
      defaultModel: { id: 'auto' },
    })
    await drain(factory(BASE_OPTS))
    expect(observed.closedAgents).toEqual(['agent-fresh'])
  })

  // -----------------------------------------------------------------------
  // Abort wiring
  // -----------------------------------------------------------------------

  it('returns immediately when the signal is already aborted (does not create the agent)', async () => {
    const { module, observed } = buildStubSdk()
    const factory = buildCursorFactory({
      logger,
      importSdk: async () => module,
      defaultModel: { id: 'auto' },
    })
    const ac = new AbortController()
    ac.abort()
    await drain(factory({ ...BASE_OPTS, abortSignal: ac.signal }))
    // No agent was created — the early-abort guard short-circuited.
    expect(observed.createCalls).toHaveLength(0)
  })

  it('calls run.cancel() when the signal aborts mid-stream', async () => {
    // Frames that never include a terminal status — we abort while the
    // stream is "in progress" and assert cancel() ran.
    const ac = new AbortController()
    const { module, observed } = buildStubSdk([
      {
        type: 'assistant',
        agent_id: 'agent-fresh',
        message: { role: 'assistant', content: [{ type: 'text', text: 'mid' }] },
      },
      { type: 'status', agent_id: 'agent-fresh', status: 'FINISHED' },
    ])
    const factory = buildCursorFactory({
      logger,
      importSdk: async () => module,
      defaultModel: { id: 'auto' },
    })
    // Fire abort the moment the iterator starts.
    queueMicrotask(() => ac.abort())
    await drain(factory({ ...BASE_OPTS, abortSignal: ac.signal }))
    expect(observed.cancelCalled).toBe(true)
  })

  // -----------------------------------------------------------------------
  // MCP server map
  // -----------------------------------------------------------------------

  it('projects mcpRegistry entries to AgentOptions.mcpServers with Bearer + branch headers', async () => {
    const { module, observed } = buildStubSdk()
    const factory = buildCursorFactory({
      logger,
      importSdk: async () => module,
      defaultModel: { id: 'auto' },
      mcpRegistry: {
        entries: () => [
          { name: 'kanban', baseUrl: 'http://kanban:8080' },
          { name: 'planning', baseUrl: 'http://planning:8080' },
        ],
      },
      getRuntimeToken: () => 'tok-abc',
      getGitBranch: async () => 'feat/cursor',
    })
    await drain(factory(BASE_OPTS))
    const map = observed.createCalls[0]?.mcpServers ?? {}
    expect(Object.keys(map).sort()).toEqual(['kanban', 'planning'])
    const kanban = map['kanban'] as {
      type: string
      url: string
      headers: Record<string, string>
    }
    expect(kanban.type).toBe('http')
    expect(kanban.url).toBe('http://kanban:8080')
    expect(kanban.headers.Authorization).toBe('Bearer tok-abc')
    expect(kanban.headers['X-Daemon-Git-Branch']).toBe('feat/cursor')
  })

  it('omits the X-Daemon-Git-Branch header when no branch resolves', async () => {
    const { module, observed } = buildStubSdk()
    const factory = buildCursorFactory({
      logger,
      importSdk: async () => module,
      defaultModel: { id: 'auto' },
      mcpRegistry: {
        entries: () => [{ name: 'kanban', baseUrl: 'http://kanban:8080' }],
      },
      getRuntimeToken: () => 'tok-abc',
      getGitBranch: async () => null,
    })
    await drain(factory(BASE_OPTS))
    const map = observed.createCalls[0]?.mcpServers ?? {}
    const kanban = map['kanban'] as { headers: Record<string, string> }
    expect(kanban.headers.Authorization).toBe('Bearer tok-abc')
    expect(kanban.headers).not.toHaveProperty('X-Daemon-Git-Branch')
  })

  it('omits mcpServers entirely when the registry is empty', async () => {
    const { module, observed } = buildStubSdk()
    const factory = buildCursorFactory({
      logger,
      importSdk: async () => module,
      defaultModel: { id: 'auto' },
      mcpRegistry: { entries: () => [] },
    })
    await drain(factory(BASE_OPTS))
    expect(observed.createCalls[0]?.mcpServers).toBeUndefined()
  })

  // -----------------------------------------------------------------------
  // Streaming integration: frames flow through the mapper
  // -----------------------------------------------------------------------

  it('streams Cursor frames through mapCursorMessage to produce cursor-native MappedCursorEvents', async () => {
    const { module } = buildStubSdk([
      {
        type: 'system',
        subtype: 'init',
        agent_id: 'agent-stream-test',
        run_id: 'run-1',
      },
      {
        type: 'assistant',
        agent_id: 'agent-stream-test',
        message: {
          role: 'assistant',
          content: [{ type: 'text', text: 'streamed' }],
        },
      },
      { type: 'status', agent_id: 'agent-stream-test', status: 'FINISHED' },
    ])
    const factory = buildCursorFactory({
      logger,
      importSdk: async () => module,
      defaultModel: { id: 'auto' },
    })
    const out = await drain(factory(BASE_OPTS))
    // Three Cursor SDK frames in. The factory BUFFERS the terminal Status
    // frame so it can stamp the wait()-derived runResult onto a synthesized
    // terminal Status. So output: System carrier + AssistantText + synthesized
    // terminal Status = 3 events.
    expect(out).toHaveLength(3)
    expect(out[0]).toMatchObject({
      kind: 'System',
      subtype: 'init',
      agentId: 'agent-stream-test',
    })
    expect(out[1]).toMatchObject({
      kind: 'AssistantText',
      text: 'streamed',
    })
    expect(out[2]).toMatchObject({
      kind: 'Status',
      runStatus: 'Finished',
    })
    // The synthesized terminal carries the runResult aggregate from wait().
    expect((out[2] as { runResult?: { durationMs: number; model: string } }).runResult).toBeDefined()
  })

  it('calls run.wait() after the stream drains', async () => {
    const { module, observed } = buildStubSdk()
    const factory = buildCursorFactory({
      logger,
      importSdk: async () => module,
      defaultModel: { id: 'auto' },
    })
    await drain(factory(BASE_OPTS))
    expect(observed.waitCalled).toBe(true)
  })

  it('synthesizes a terminal Status from run.wait() when the stream had no terminal status frame', async () => {
    const { module } = buildStubSdk(
      [
        {
          type: 'assistant',
          agent_id: 'agent-stub',
          message: {
            role: 'assistant',
            content: [{ type: 'text', text: 'hello' }],
          },
        },
      ],
      { id: 'run-1', status: 'finished', durationMs: 900 },
    )
    const factory = buildCursorFactory({
      logger,
      importSdk: async () => module,
      defaultModel: { id: 'auto' },
    })
    const out = await drain(factory(BASE_OPTS))
    const terminal = out.find(
      (e) => e.kind === 'Status' && e.runStatus === 'Finished',
    ) as { runResult?: { durationMs: number; model: string } } | undefined
    expect(terminal).toBeDefined()
    expect(terminal!.runResult?.durationMs).toBe(900)
    expect(terminal!.runResult?.model).toBe('auto')
  })

  it('emits a single terminal Status reflecting wait() failure status when wait disagrees with stream FINISHED', async () => {
    // The factory buffers the in-stream terminal status so the wait()-derived
    // RunResult lands on the terminal envelope. Because the in-stream status
    // was FINISHED but wait() says error, the runner-side success/failure
    // logic stamps reason='error' off the runStatus that the in-stream frame
    // delivered. Either way: exactly ONE terminal Status on the wire.
    const { module } = buildStubSdk(
      [{ type: 'status', agent_id: 'agent-stub', status: 'FINISHED' }],
      { id: 'run-1', status: 'error', result: 'model blew up', durationMs: 50 },
    )
    const factory = buildCursorFactory({
      logger,
      importSdk: async () => module,
      defaultModel: { id: 'auto' },
    })
    const out = await drain(factory(BASE_OPTS))
    const terminals = out.filter((e) => e.kind === 'Status')
    // One terminal Status total (in-stream FINISHED buffered, synthesized
    // terminal carries it forward with the runResult attached).
    expect(terminals).toHaveLength(1)
    const ev = terminals[0] as { runResult?: { durationMs: number; model: string } }
    expect(ev.runResult?.durationMs).toBe(50)
  })

  // -----------------------------------------------------------------------
  // Error path: importSdk throws
  // -----------------------------------------------------------------------

  it('rethrows when importSdk fails, after emitting a synthetic System:error carrier', async () => {
    const factory = buildCursorFactory({
      logger,
      importSdk: async () => {
        throw new Error('import failed')
      },
      defaultModel: { id: 'auto' },
    })
    // The iterator emits the synthetic error carrier before throwing — drain
    // collects partial output even when the iterator rejects.
    const collected: TurnEvent[] = []
    await expect(async () => {
      for await (const evt of factory(BASE_OPTS)) collected.push(evt)
    }).rejects.toThrow(/import failed/)
    expect(collected).toEqual([
      expect.objectContaining({ kind: 'System', subtype: 'error' }),
    ])
  })
})

// Silence the unused-import for vi to keep eslint quiet.
void vi
