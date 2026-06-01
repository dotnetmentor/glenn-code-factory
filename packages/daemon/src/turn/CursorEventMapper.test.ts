// CursorEventMapper unit tests — exercise every `frame.type` branch of the
// cursor-native mapper. Card 3.5/9 (CursorEventMapper rewrite) flipped the
// daemon from "Cursor SDK → Claude-shape → boundary translator" to
// "Cursor SDK → wire vocabulary" in one hop. These tests pin the contract.
//
// The mapper is the load-bearing translator between Cursor SDK messages and
// the daemon's cursor-native wire vocabulary (`MappedCursorEvent`). Each test
// asserts the EXACT wire shape so a regression — e.g. silently dropping a
// CREATING status, mis-reading `id` instead of `call_id` on a tool call, or
// dropping a `task` frame as `cursor_task` — surfaces here before reaching
// the chat UI.
//
// Required pinning tests (per card 3.5 spec):
//   1. `tool_call.call_id` → `toolCallId` (NOT `id`).
//   2. CREATING status flows through as a Status event (legacy mapper
//      dropped it, killing the "Connecting…" pill).
//   3. RUNNING status flows through as a Status event (legacy mapper
//      dropped it, killing long-running progress indicators).
//   4. `SDKTaskMessage` → MappedTaskEvent (was emitted as a
//      `cursor_task` system carrier before).
//   5. A terminal Status frame stamps the staged RunResult aggregate
//      (durationMs + model required, gitBranch/PrUrl/artifacts optional).

import { describe, expect, it } from 'vitest'

import {
  makeCursorMapperState,
  mapCursorMessage,
  noteTerminalRunResult,
  noteTurnEndedUsage,
  synthesizeTerminalStatus,
  type CursorSdkMessage,
  type MappedAssistantTextEvent,
  type MappedPromptReceivedEvent,
  type MappedStatusEvent,
  type MappedSystemEvent,
  type MappedTaskEvent,
  type MappedThinkingEvent,
  type MappedToolUseEvent,
} from './CursorEventMapper.js'
import {
  AgentEventKind,
  AgentEventRunStatus,
  AgentEventToolStatus,
} from '../signalr/types.js'

describe('CursorEventMapper', () => {
  // ----------------------------------------------------------------------
  // system frame — maps to MappedSystemEvent (audit carrier)
  // ----------------------------------------------------------------------

  it('maps system:init to a System carrier preserving subtype + agent/run/model/tools in eventData', () => {
    const state = makeCursorMapperState()
    const out = mapCursorMessage(
      {
        type: 'system',
        subtype: 'init',
        agent_id: 'agent-xyz',
        run_id: 'run-1',
        model: { id: 'gpt-5' },
        tools: ['edit', 'bash'],
      } satisfies CursorSdkMessage,
      state,
    )
    expect(out).toHaveLength(1)
    const ev = out[0] as MappedSystemEvent
    expect(ev.kind).toBe('System')
    expect(ev.subtype).toBe('init')
    expect(ev.agentId).toBe('agent-xyz')
    expect(ev.eventData).toEqual({
      subtype: 'init',
      agentId: 'agent-xyz',
      runId: 'run-1',
      model: { id: 'gpt-5' },
      tools: ['edit', 'bash'],
    })
  })

  it('defaults system subtype to "init" when missing and still folds agentId onto the carrier', () => {
    const state = makeCursorMapperState()
    const out = mapCursorMessage(
      { type: 'system', agent_id: 'agent-defaulted' } as CursorSdkMessage,
      state,
    )
    const ev = out[0] as MappedSystemEvent
    expect(ev.kind).toBe('System')
    expect(ev.subtype).toBe('init')
    expect(ev.agentId).toBe('agent-defaulted')
  })

  // ----------------------------------------------------------------------
  // assistant frame
  // ----------------------------------------------------------------------

  it('passes assistant text content through as MappedAssistantTextEvent', () => {
    const state = makeCursorMapperState()
    const out = mapCursorMessage(
      {
        type: 'assistant',
        agent_id: 'agent-a',
        run_id: 'run-1',
        message: {
          role: 'assistant',
          content: [{ type: 'text', text: 'hello world' }],
        },
      },
      state,
    )
    expect(out).toHaveLength(1)
    const ev = out[0] as MappedAssistantTextEvent
    expect(ev.kind).toBe(AgentEventKind.AssistantText)
    expect(ev.text).toBe('hello world')
    expect(ev.agentId).toBe('agent-a')
  })

  it('emits Thinking events for assistant thinking blocks (text from `thinking`/`content`/`text`)', () => {
    const state = makeCursorMapperState()
    const out = mapCursorMessage(
      {
        type: 'assistant',
        agent_id: 'agent-a',
        message: {
          role: 'assistant',
          content: [{ type: 'thinking', thinking: 'pondering' }],
        },
      },
      state,
    )
    const ev = out[0] as MappedThinkingEvent
    expect(ev.kind).toBe(AgentEventKind.Thinking)
    expect(ev.text).toBe('pondering')
  })

  it('emits ToolUse running for assistant tool_use blocks and dedupes by id within a state', () => {
    const state = makeCursorMapperState()
    const frame = (id: string): CursorSdkMessage => ({
      type: 'assistant',
      agent_id: 'agent-a',
      message: {
        role: 'assistant',
        content: [
          { type: 'tool_use', id, name: 'edit', input: { path: '/x' } },
        ],
      },
    })
    const first = mapCursorMessage(frame('tu-1'), state)
    expect(first).toHaveLength(1)
    const ev = first[0] as MappedToolUseEvent
    expect(ev.kind).toBe(AgentEventKind.ToolUse)
    expect(ev.toolCallId).toBe('tu-1')
    expect(ev.toolName).toBe('edit')
    expect(ev.toolStatus).toBe(AgentEventToolStatus.Running)
    expect(ev.toolArgs).toBe(JSON.stringify({ path: '/x' }))
    // Duplicate emission with the same id is suppressed.
    const second = mapCursorMessage(frame('tu-1'), state)
    expect(second).toHaveLength(0)
  })

  it('emits a System carrier for unknown assistant block kinds (audit-only)', () => {
    const state = makeCursorMapperState()
    const out = mapCursorMessage(
      {
        type: 'assistant',
        agent_id: 'agent-a',
        message: {
          role: 'assistant',
          content: [{ type: 'future_unknown_block', payload: 'x' }],
        },
      },
      state,
    )
    const ev = out[0] as MappedSystemEvent
    expect(ev.kind).toBe('System')
    expect(ev.subtype).toBe('cursor_unknown_assistant_block')
  })

  it('emits a System carrier for assistant frames with non-array content', () => {
    const state = makeCursorMapperState()
    const out = mapCursorMessage(
      {
        type: 'assistant',
        agent_id: 'agent-a',
        message: {
          role: 'assistant',
          // shape is intentionally wrong to exercise the defensive branch
          content: undefined,
        },
      } as unknown as CursorSdkMessage,
      state,
    )
    const ev = out[0] as MappedSystemEvent
    expect(ev.kind).toBe('System')
    expect(ev.subtype).toBe('cursor_unknown_assistant')
  })

  // ----------------------------------------------------------------------
  // user frame → PromptReceived
  // ----------------------------------------------------------------------

  it('maps user text blocks to PromptReceived events', () => {
    const state = makeCursorMapperState()
    const out = mapCursorMessage(
      {
        type: 'user',
        agent_id: 'agent-a',
        message: {
          role: 'user',
          content: [{ type: 'text', text: 'please help' }],
        },
      },
      state,
    )
    const ev = out[0] as MappedPromptReceivedEvent
    expect(ev.kind).toBe(AgentEventKind.PromptReceived)
    expect(ev.text).toBe('please help')
    expect(ev.agentId).toBe('agent-a')
  })

  it('drops user frames with no content (no audit row)', () => {
    const state = makeCursorMapperState()
    const out = mapCursorMessage(
      {
        type: 'user',
        agent_id: 'agent-a',
        message: { role: 'user', content: [] },
      },
      state,
    )
    expect(out).toHaveLength(0)
  })

  // ----------------------------------------------------------------------
  // tool_call frame
  //
  // CRITICAL: the SDK's lifecycle frame uses `call_id` (not `id`). Reading
  // `id` (as the legacy mapper did) produced orphan pairings on the chat
  // panel. The first test below pins this.
  // ----------------------------------------------------------------------

  it('REQUIRED: reads `call_id` from tool_call frames (NOT `id`) onto toolCallId', () => {
    const state = makeCursorMapperState()
    const out = mapCursorMessage(
      {
        type: 'tool_call',
        agent_id: 'agent-a',
        call_id: 'cc-real',
        // `id` is set to a different value to prove we ignore it.
        id: 'sdk-internal-block-id',
        name: 'bash',
        status: 'running',
      } as unknown as CursorSdkMessage,
      state,
    )
    expect(out).toHaveLength(1)
    const ev = out[0] as MappedToolUseEvent
    expect(ev.kind).toBe(AgentEventKind.ToolUse)
    expect(ev.toolCallId).toBe('cc-real')
    expect(ev.toolCallId).not.toBe('sdk-internal-block-id')
  })

  it('forwards tool_call running → ToolUse(Running)', () => {
    const state = makeCursorMapperState()
    const out = mapCursorMessage(
      {
        type: 'tool_call',
        call_id: 'cc-1',
        name: 'bash',
        status: 'running',
        args: { cmd: 'ls' },
      },
      state,
    )
    const ev = out[0] as MappedToolUseEvent
    expect(ev.toolStatus).toBe(AgentEventToolStatus.Running)
    expect(ev.toolArgs).toBe(JSON.stringify({ cmd: 'ls' }))
  })

  it('forwards tool_call completed → ToolUse(Completed) with stringified result + truncation flag', () => {
    const state = makeCursorMapperState()
    const out = mapCursorMessage(
      {
        type: 'tool_call',
        call_id: 'cc-2',
        name: 'bash',
        status: 'completed',
        result: { exitCode: 0, stdout: 'hi' },
        truncated: { result: true },
      },
      state,
    )
    const ev = out[0] as MappedToolUseEvent
    expect(ev.toolStatus).toBe(AgentEventToolStatus.Completed)
    expect(ev.toolResult).toBe(JSON.stringify({ exitCode: 0, stdout: 'hi' }))
    expect(ev.toolResultTruncated).toBe(true)
  })

  it('forwards tool_call error → ToolUse(Error)', () => {
    const state = makeCursorMapperState()
    const out = mapCursorMessage(
      {
        type: 'tool_call',
        call_id: 'cc-3',
        name: 'bash',
        status: 'error',
        result: 'command not found',
      },
      state,
    )
    const ev = out[0] as MappedToolUseEvent
    expect(ev.toolStatus).toBe(AgentEventToolStatus.Error)
    // Already a string → no double-encoding.
    expect(ev.toolResult).toBe('command not found')
  })

  it('dedupes per-status by call_id (no double running, no double terminal)', () => {
    const state = makeCursorMapperState()
    const running1 = mapCursorMessage(
      { type: 'tool_call', call_id: 'cc-d', name: 'edit', status: 'running' },
      state,
    )
    const running2 = mapCursorMessage(
      { type: 'tool_call', call_id: 'cc-d', name: 'edit', status: 'running' },
      state,
    )
    expect(running1).toHaveLength(1)
    expect(running2).toHaveLength(0)

    const completed1 = mapCursorMessage(
      { type: 'tool_call', call_id: 'cc-d', name: 'edit', status: 'completed' },
      state,
    )
    const completed2 = mapCursorMessage(
      { type: 'tool_call', call_id: 'cc-d', name: 'edit', status: 'completed' },
      state,
    )
    expect(completed1).toHaveLength(1)
    expect(completed2).toHaveLength(0)
  })

  it('drops tool_call frames with no status (assistant tool_use block already covers the surface)', () => {
    const state = makeCursorMapperState()
    const out = mapCursorMessage(
      { type: 'tool_call', call_id: 'cc-no-status', name: 'bash' },
      state,
    )
    expect(out).toHaveLength(0)
  })

  // ----------------------------------------------------------------------
  // thinking frame
  // ----------------------------------------------------------------------

  it('maps thinking frame to MappedThinkingEvent with optional duration', () => {
    const state = makeCursorMapperState()
    const out = mapCursorMessage(
      {
        type: 'thinking',
        agent_id: 'agent-a',
        text: 'considering',
        thinking_duration_ms: 1234,
      },
      state,
    )
    const ev = out[0] as MappedThinkingEvent
    expect(ev.kind).toBe(AgentEventKind.Thinking)
    expect(ev.text).toBe('considering')
    expect(ev.thinkingDurationMs).toBe(1234)
  })

  it('drops thinking frames with empty text', () => {
    const state = makeCursorMapperState()
    const out = mapCursorMessage({ type: 'thinking', text: '' }, state)
    expect(out).toHaveLength(0)
  })

  // ----------------------------------------------------------------------
  // status frame — CRITICAL: CREATING and RUNNING must flow through
  // ----------------------------------------------------------------------

  it('REQUIRED: CREATING status flows through as a MappedStatusEvent (not dropped)', () => {
    const state = makeCursorMapperState()
    const out = mapCursorMessage(
      { type: 'status', agent_id: 'agent-a', status: 'CREATING', message: 'spinning up' },
      state,
    )
    expect(out).toHaveLength(1)
    const ev = out[0] as MappedStatusEvent
    expect(ev.kind).toBe(AgentEventKind.Status)
    expect(ev.runStatus).toBe(AgentEventRunStatus.Creating)
    expect(ev.statusMessage).toBe('spinning up')
    expect(ev.agentId).toBe('agent-a')
    // CREATING is non-terminal so no runResult should be attached.
    expect(ev.runResult).toBeUndefined()
  })

  it('REQUIRED: RUNNING status flows through as a MappedStatusEvent (not dropped)', () => {
    const state = makeCursorMapperState()
    const out = mapCursorMessage(
      { type: 'status', agent_id: 'agent-a', status: 'RUNNING' },
      state,
    )
    expect(out).toHaveLength(1)
    const ev = out[0] as MappedStatusEvent
    expect(ev.kind).toBe(AgentEventKind.Status)
    expect(ev.runStatus).toBe(AgentEventRunStatus.Running)
    expect(ev.runResult).toBeUndefined()
  })

  it('maps FINISHED/ERROR/CANCELLED/EXPIRED to their AgentEventRunStatus enum values', () => {
    const cases: Array<['FINISHED' | 'ERROR' | 'CANCELLED' | 'EXPIRED', AgentEventRunStatus]> = [
      ['FINISHED', AgentEventRunStatus.Finished],
      ['ERROR', AgentEventRunStatus.Error],
      ['CANCELLED', AgentEventRunStatus.Cancelled],
      ['EXPIRED', AgentEventRunStatus.Expired],
    ]
    for (const [s, want] of cases) {
      const state = makeCursorMapperState()
      const out = mapCursorMessage({ type: 'status', status: s }, state)
      expect((out[0] as MappedStatusEvent).runStatus).toBe(want)
    }
  })

  it('REQUIRED: terminal Status drains the staged runResult onto the wire event (durationMs + model)', () => {
    const state = makeCursorMapperState()
    noteTerminalRunResult(state, {
      durationMs: 4321,
      model: 'gpt-5',
      gitBranch: 'feature/x',
      gitPrUrl: 'https://example.test/pr/1',
      artifacts: [
        { path: 'out.log', sizeBytes: 1024, updatedAt: '2026-05-01T00:00:00Z' },
      ],
    })
    const out = mapCursorMessage(
      { type: 'status', agent_id: 'agent-a', status: 'FINISHED' },
      state,
    )
    const ev = out[0] as MappedStatusEvent
    expect(ev.kind).toBe(AgentEventKind.Status)
    expect(ev.runStatus).toBe(AgentEventRunStatus.Finished)
    expect(ev.runResult).toBeDefined()
    expect(ev.runResult?.durationMs).toBe(4321)
    expect(ev.runResult?.model).toBe('gpt-5')
    expect(ev.runResult?.gitBranch).toBe('feature/x')
    expect(ev.runResult?.gitPrUrl).toBe('https://example.test/pr/1')
    expect(ev.runResult?.artifacts).toHaveLength(1)
    // State is drained — second terminal frame (defensive) shouldn't double-stamp.
    expect(state.pendingRunResult).toBeNull()
  })

  it('non-terminal status does NOT drain a staged runResult (stays on state for the real terminal)', () => {
    const state = makeCursorMapperState()
    noteTerminalRunResult(state, {
      durationMs: 100,
      model: 'gpt-5',
      artifacts: [],
    })
    mapCursorMessage({ type: 'status', status: 'RUNNING' }, state)
    expect(state.pendingRunResult).not.toBeNull()
  })

  it('drops status frames with no status field', () => {
    const state = makeCursorMapperState()
    const out = mapCursorMessage({ type: 'status' }, state)
    expect(out).toHaveLength(0)
  })

  // ----------------------------------------------------------------------
  // request frame — silently dropped
  // ----------------------------------------------------------------------

  it('drops request frames (pure SDK-internal correlation, no wire surface)', () => {
    const state = makeCursorMapperState()
    const out = mapCursorMessage(
      { type: 'request', request_id: 'req-1' } as CursorSdkMessage,
      state,
    )
    expect(out).toHaveLength(0)
  })

  // ----------------------------------------------------------------------
  // task frame — REQUIRED: must map to MappedTaskEvent (NOT cursor_task)
  // ----------------------------------------------------------------------

  it('REQUIRED: SDKTaskMessage maps to MappedTaskEvent (NOT a `cursor_task` system carrier)', () => {
    const state = makeCursorMapperState()
    const out = mapCursorMessage(
      {
        type: 'task',
        agent_id: 'agent-a',
        run_id: 'task-run-99',
        status: 'completed',
        text: 'subagent finished',
      },
      state,
    )
    expect(out).toHaveLength(1)
    const ev = out[0] as MappedTaskEvent
    expect(ev.kind).toBe(AgentEventKind.Task)
    expect(ev.taskId).toBe('task-run-99')
    expect(ev.taskTitle).toBe('subagent finished')
    expect(ev.agentId).toBe('agent-a')
  })

  it('task frame with no text omits taskTitle', () => {
    const state = makeCursorMapperState()
    const out = mapCursorMessage(
      { type: 'task', run_id: 'task-x' },
      state,
    )
    const ev = out[0] as MappedTaskEvent
    expect(ev.kind).toBe(AgentEventKind.Task)
    expect(ev.taskId).toBe('task-x')
    expect(ev.taskTitle).toBeUndefined()
  })

  // ----------------------------------------------------------------------
  // defensive: unknown frame type
  // ----------------------------------------------------------------------

  it('emits cursor_unknown System carrier for unknown frame types (no silent drops)', () => {
    const state = makeCursorMapperState()
    const out = mapCursorMessage(
      { type: 'future_unknown', agent_id: 'agent-a' } as unknown as CursorSdkMessage,
      state,
    )
    const ev = out[0] as MappedSystemEvent
    expect(ev.kind).toBe('System')
    expect(ev.subtype).toBe('cursor_unknown')
  })

  it('emits cursor_unknown for frames lacking a type field entirely', () => {
    const state = makeCursorMapperState()
    const out = mapCursorMessage(
      { agent_id: 'agent-a' } as unknown as CursorSdkMessage,
      state,
    )
    const ev = out[0] as MappedSystemEvent
    expect(ev.kind).toBe('System')
    expect(ev.subtype).toBe('cursor_unknown')
  })

  // ----------------------------------------------------------------------
  // noteTerminalRunResult / synthesizeTerminalStatus
  // ----------------------------------------------------------------------

  it('synthesizeTerminalStatus drains the staged runResult onto a synthesized terminal Status', () => {
    const state = makeCursorMapperState()
    noteTerminalRunResult(state, {
      durationMs: 999,
      model: 'gpt-5',
      artifacts: [],
    })
    const ev = synthesizeTerminalStatus(state, {
      runStatus: AgentEventRunStatus.Error,
      agentId: 'agent-a',
      statusMessage: 'wait failed',
    })
    expect(ev.kind).toBe(AgentEventKind.Status)
    expect(ev.runStatus).toBe(AgentEventRunStatus.Error)
    expect(ev.statusMessage).toBe('wait failed')
    expect(ev.agentId).toBe('agent-a')
    expect(ev.runResult?.durationMs).toBe(999)
    expect(state.pendingRunResult).toBeNull()
  })

  // ----------------------------------------------------------------------
  // noteTurnEndedUsage — capture token usage from onDelta(turn-ended)
  // ----------------------------------------------------------------------

  it('noteTurnEndedUsage stores token counts onto state for the factory to read out', () => {
    const state = makeCursorMapperState()
    noteTurnEndedUsage(state, {
      inputTokens: 100,
      outputTokens: 200,
      cacheReadTokens: 50,
      cacheWriteTokens: 25,
    })
    expect(state.pendingUsage).toEqual({
      inputTokens: 100,
      outputTokens: 200,
      cacheReadTokens: 50,
      cacheWriteTokens: 25,
    })
  })

  it('noteTurnEndedUsage no-ops on undefined input', () => {
    const state = makeCursorMapperState()
    noteTurnEndedUsage(state, undefined)
    expect(state.pendingUsage).toBeNull()
  })
})
