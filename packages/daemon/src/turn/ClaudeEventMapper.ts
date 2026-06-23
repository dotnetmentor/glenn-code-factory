// ClaudeEventMapper — translates the Claude Agent SDK (`@anthropic-ai/
// claude-agent-sdk`) `SDKMessage` stream into the daemon's cursor-native wire
// vocabulary (`MappedCursorEvent`, re-exported as `TurnEvent`), the exact
// pre-`EmitEventPayload` shape `TurnRunner` stamps with `sessionId` /
// `emittedAt` and ships over SignalR.
//
// === Why this exists ===
//
// The daemon's wire contract (`EmitEventPayload`, `AgentEventKind`) is backend
// agnostic — it already carries PromptReceived / AssistantText / Thinking /
// ToolUse / Status / Task discriminators that the React chat panel renders.
// `CursorEventMapper` is the Cursor→wire surface; THIS module is the parallel
// Claude→wire surface. Both emit the IDENTICAL `MappedCursorEvent` shapes so
// the frontend chat stream and the .NET hub need no Claude-specific changes
// (Phase 1 of the claude-agent-backend spec).
//
// === Critical mapping rules (parity with CursorEventMapper) ===
//
//   - `text` blocks → `AssistantText` (one event per block; smooth incremental
//     streaming — the Claude SDK delivers assistant content as discrete
//     message frames so each frame's text block is forwarded as it arrives,
//     mirroring how CursorEventMapper forwards each assistant frame's text).
//   - `thinking` blocks → `Thinking` (NEVER `AssistantText` — thinking must
//     render as a thinking block, not the answer; this was the exact bug the
//     old ReasoningOptions path warned about).
//   - `tool_use` blocks (assistant message) → `ToolUse` with
//     `toolStatus: Running`, callId = block `id`, deduped per callId.
//   - `tool_result` blocks (user message) → `ToolUse` with
//     `toolStatus: Completed` / `Error`, callId = `tool_use_id`, deduped.
//   - `system.init` → System carrier capturing `session_id` (no chat-panel
//     surface; the daemon reads `agentId` for the resume hint — same role as
//     Cursor's `agent_id`). Mirrors `CursorEventMapper.mapSystem`.
//   - `result` → terminal `Status` (Finished / Error) carrying the
//     `RunResultPayload` aggregate (durationMs, model, usage→nothing on the
//     wire here — usage is reported separately — cost is captured by the
//     factory). The factory stages the run-result via `noteTerminalRunResult`
//     and `synthesizeClaudeTerminalStatus` exactly like the Cursor path.
//
// === Stateful concerns ===
//
// Tool-call lifecycle dedupe (one ToolUse Running per tool_use id, one
// terminal ToolUse per tool_result id) and pending run-result staging live in
// `ClaudeMapperState`, threaded one-per-turn by the factory through every
// `mapClaudeMessage()` call. Same design as `CursorMapperState`.

import { AgentEventKind, AgentEventRunStatus, AgentEventToolStatus } from '../signalr/types.js'
import type { RunResultPayload } from '../generated/signalr/Source.Features.SignalR.Contracts.js'
import type {
  MappedCursorEvent,
  MappedAssistantTextEvent,
  MappedThinkingEvent,
  MappedToolUseEvent,
  MappedStatusEvent,
  MappedSystemEvent,
} from './CursorEventMapper.js'

// ---------------------------------------------------------------------------
// Claude SDK shape aliases — tolerantly typed structural views.
//
// Mirrors CursorEventMapper's approach: we declare the NARROW subset of the
// SDK's `SDKMessage` union we actually read, rather than importing the full
// (and large, fast-moving) `@anthropic-ai/claude-agent-sdk` type. This keeps
// the mapper version-tolerant — a future SDK minor that adds a new message
// type or block field doesn't break compilation — and lets tests inject
// hand-rolled stub frames. `mapClaudeMessage` narrows on `frame.type` /
// `frame.subtype` at runtime.
// ---------------------------------------------------------------------------

/** A single content block inside an assistant/user message. */
export interface ClaudeContentBlock {
  type?: string
  // text block
  text?: unknown
  // thinking block
  thinking?: unknown
  // tool_use block
  id?: unknown
  name?: unknown
  input?: unknown
  // tool_result block
  tool_use_id?: unknown
  content?: unknown
  is_error?: unknown
  [k: string]: unknown
}

export interface ClaudeAssistantMessage {
  type: 'assistant'
  session_id?: string
  message?: {
    role?: string
    model?: unknown
    content?: ClaudeContentBlock[]
  }
}

export interface ClaudeUserMessage {
  type: 'user'
  session_id?: string
  message?: {
    role?: string
    // user content may be a string (plain prompt echo) or block array
    content?: string | ClaudeContentBlock[]
  }
}

export interface ClaudeSystemMessage {
  type: 'system'
  subtype?: string
  session_id?: string
  model?: unknown
  tools?: unknown
}

export interface ClaudeResultUsage {
  input_tokens?: number
  output_tokens?: number
  cache_read_input_tokens?: number
  cache_creation_input_tokens?: number
  [k: string]: unknown
}

export interface ClaudeResultMessage {
  type: 'result'
  subtype?: string
  session_id?: string
  is_error?: boolean
  duration_ms?: number
  result?: unknown
  usage?: ClaudeResultUsage
  total_cost_usd?: number
  modelUsage?: Record<string, { model?: string } & Record<string, unknown>>
  errors?: unknown
}

export type ClaudeSdkMessage =
  | ClaudeAssistantMessage
  | ClaudeUserMessage
  | ClaudeSystemMessage
  | ClaudeResultMessage
  | { type?: string; [k: string]: unknown }

// ---------------------------------------------------------------------------
// Mapper state — tool-call lifecycle dedupe + pending run-result + usage
// ---------------------------------------------------------------------------

/**
 * Per-turn state threaded across frames. Direct analogue of `CursorMapperState`.
 *
 *   - `emittedToolUseCallIds` — tool_use ids we've forwarded as Running.
 *   - `emittedToolResultCallIds` — tool_result ids we've forwarded as terminal.
 *   - `pendingRunResult` — staged by the factory; drained onto the terminal
 *     Status event (mirrors Cursor's pre-stage-then-drain contract).
 *   - `pendingUsage` — token usage from the SDK `result` message. Daemon-
 *     internal: read by the factory post-stream and reported via the dedicated
 *     `ReportSessionCost` channel (NOT EmitEvent), same as Cursor's
 *     turn-ended usage.
 *   - `lastModelId` — last model id observed (from assistant message or result
 *     modelUsage); used to stamp the RunResultPayload.model field.
 */
export interface ClaudeMapperState {
  emittedToolUseCallIds: Set<string>
  emittedToolResultCallIds: Set<string>
  pendingRunResult: RunResultPayload | null
  pendingUsage:
    | {
        inputTokens: number
        outputTokens: number
        cacheReadTokens: number
        cacheWriteTokens: number
      }
    | null
  lastModelId: string | null
}

export function makeClaudeMapperState(): ClaudeMapperState {
  return {
    emittedToolUseCallIds: new Set(),
    emittedToolResultCallIds: new Set(),
    pendingRunResult: null,
    pendingUsage: null,
    lastModelId: null,
  }
}

/**
 * Stage a `RunResultPayload` for the next terminal Status event. Called by
 * `ClaudeFactory` immediately before emitting the terminal status (the SDK's
 * `result` message carries the aggregate, so the factory derives it then
 * stages it for `mapResult`/`synthesizeClaudeTerminalStatus` to attach).
 */
export function noteClaudeTerminalRunResult(
  state: ClaudeMapperState,
  runResult: RunResultPayload,
): void {
  state.pendingRunResult = runResult
}

/**
 * Record token usage observed from the SDK `result` message. Daemon-internal —
 * read by the factory post-stream and reported via `ReportSessionCost`.
 * Idempotent (one `result` per turn; a later call overwrites).
 */
export function noteClaudeUsage(
  state: ClaudeMapperState,
  usage:
    | {
        inputTokens: number
        outputTokens: number
        cacheReadTokens: number
        cacheWriteTokens: number
      }
    | undefined,
): void {
  if (usage === undefined || usage === null) return
  state.pendingUsage = usage
}

// ---------------------------------------------------------------------------
// Public mapper entrypoint
// ---------------------------------------------------------------------------

/**
 * Map one Claude `SDKMessage` to zero or more cursor-native wire events.
 *
 * Returns an empty array for frames we deliberately drop (partial-assistant
 * deltas, status pills, hook/task notifications the chat panel doesn't render).
 * Unknown frame types become a `System` carrier so the audit trail keeps the
 * shape rather than going silent — same defensive posture as
 * `mapCursorMessage`.
 */
export function mapClaudeMessage(
  msg: ClaudeSdkMessage,
  state: ClaudeMapperState,
): MappedCursorEvent[] {
  const t = (msg as { type?: unknown }).type
  if (typeof t !== 'string') {
    return [
      {
        kind: 'System',
        subtype: 'claude_unknown',
        eventData: { raw: msg },
      },
    ]
  }

  switch (t) {
    case 'assistant':
      return mapAssistant(msg as ClaudeAssistantMessage, state)
    case 'user':
      return mapUser(msg as ClaudeUserMessage, state)
    case 'system':
      return mapSystem(msg as ClaudeSystemMessage)
    case 'result':
      return mapResult(msg as ClaudeResultMessage, state)
    default:
      // Partial assistant deltas, status pills, hook/task notifications,
      // rate-limit events, etc. carry no first-class chat-panel surface in
      // Phase 1. Drop silently — emitting a System carrier for each would
      // flood the audit trail with high-frequency plumbing frames.
      return []
  }
}

// ---------------------------------------------------------------------------
// Per-type handlers
// ---------------------------------------------------------------------------

function mapAssistant(
  frame: ClaudeAssistantMessage,
  state: ClaudeMapperState,
): MappedCursorEvent[] {
  const sessionId =
    typeof frame.session_id === 'string' ? frame.session_id : undefined
  // Capture model id (from message.model) for the RunResultPayload stamp.
  const model = frame.message?.model
  if (typeof model === 'string' && model !== '') {
    state.lastModelId = model
  }

  const content = frame.message?.content
  if (!Array.isArray(content)) {
    return [
      {
        kind: 'System',
        subtype: 'claude_unknown_assistant',
        eventData: { raw: frame },
        ...(sessionId !== undefined ? { agentId: sessionId } : {}),
      },
    ]
  }

  const out: MappedCursorEvent[] = []
  for (const block of content) {
    const blockType = (block as { type?: unknown }).type
    if (blockType === 'text') {
      // text → AssistantText. The Claude SDK delivers assistant content as
      // discrete message frames; each text block is forwarded as it arrives,
      // giving incremental streaming (one increment per block) — the chat
      // panel concatenates AssistantText events into the running answer.
      const text = readString(block.text) ?? ''
      if (text === '') continue
      const ev: MappedAssistantTextEvent = {
        kind: AgentEventKind.AssistantText,
        text,
      }
      if (sessionId !== undefined) ev.agentId = sessionId
      out.push(ev)
    } else if (blockType === 'thinking') {
      // thinking → Thinking (NEVER AssistantText). Hard requirement: the UI
      // shows a thinking block, not the answer.
      const text =
        readString(block.thinking) ??
        readString(block.text) ??
        readString(block.content) ??
        ''
      if (text === '') continue
      const ev: MappedThinkingEvent = {
        kind: AgentEventKind.Thinking,
        text,
      }
      if (sessionId !== undefined) ev.agentId = sessionId
      out.push(ev)
    } else if (blockType === 'tool_use') {
      // tool_use → ToolUse(Running). callId = block.id. Deduped: the Claude
      // SDK emits the tool_use once per assistant frame, but we defend against
      // a duplicate (e.g. a replayed frame) the same way Cursor does.
      const callId = readString(block.id) ?? ''
      if (callId !== '' && state.emittedToolUseCallIds.has(callId)) continue
      if (callId !== '') state.emittedToolUseCallIds.add(callId)
      const toolName = readString(block.name) ?? ''
      const ev: MappedToolUseEvent = {
        kind: AgentEventKind.ToolUse,
        toolCallId: callId,
        toolName,
        toolStatus: AgentEventToolStatus.Running,
      }
      const argsJson = stringifyJson(block.input)
      if (argsJson !== undefined) ev.toolArgs = argsJson
      if (sessionId !== undefined) ev.agentId = sessionId
      out.push(ev)
    } else if (blockType === 'tool_result') {
      // Anthropic occasionally inlines a tool_result into an assistant frame;
      // normally it rides on the user message. Handle both via the shared
      // helper so dedupe is consistent.
      const ev = mapToolResultBlock(block, sessionId, state)
      if (ev !== null) out.push(ev)
    } else {
      // Unknown block kind — keep an audit row but don't render.
      out.push({
        kind: 'System',
        subtype: 'claude_unknown_assistant_block',
        eventData: {
          blockType: typeof blockType === 'string' ? blockType : 'unknown',
          raw: block,
        },
        ...(sessionId !== undefined ? { agentId: sessionId } : {}),
      })
    }
  }
  return out
}

function mapUser(
  frame: ClaudeUserMessage,
  state: ClaudeMapperState,
): MappedCursorEvent[] {
  const sessionId =
    typeof frame.session_id === 'string' ? frame.session_id : undefined
  const content = frame.message?.content
  // String content is the SDK echoing the user's own prompt back — the daemon
  // already surfaces the prompt via the hub's PromptReceived; suppress the
  // echo to avoid a duplicate user bubble (Cursor's mapUser emits
  // PromptReceived, but the Cursor SDK only echoes on resume; the Claude SDK
  // echoes the full prompt on every turn, which would double-render). Drop.
  if (typeof content === 'string') return []
  if (!Array.isArray(content)) return []

  const out: MappedCursorEvent[] = []
  for (const block of content) {
    const blockType = (block as { type?: unknown }).type
    if (blockType === 'tool_result') {
      const ev = mapToolResultBlock(block, sessionId, state)
      if (ev !== null) out.push(ev)
    }
    // Plain text blocks inside a user message are prompt echoes — suppressed
    // for the same reason as the string-content case above.
  }
  return out
}

/**
 * Translate a `tool_result` content block into a terminal ToolUse event.
 * callId comes from `tool_use_id` (pairs with the earlier tool_use `id`).
 * Deduped per callId. Returns null when the result is empty / already emitted.
 */
function mapToolResultBlock(
  block: ClaudeContentBlock,
  sessionId: string | undefined,
  state: ClaudeMapperState,
): MappedToolUseEvent | null {
  const callId = readString(block.tool_use_id) ?? ''
  if (callId !== '' && state.emittedToolResultCallIds.has(callId)) return null
  if (callId !== '') state.emittedToolResultCallIds.add(callId)

  const isError = block.is_error === true
  const ev: MappedToolUseEvent = {
    kind: AgentEventKind.ToolUse,
    toolCallId: callId,
    // The tool name isn't repeated on the result block; the chat panel pairs
    // the terminal row to the running row by callId, so an empty name here is
    // expected (matches how the Cursor terminal tool_call frame carries the
    // name only when the SDK supplies it).
    toolName: '',
    toolStatus: isError ? AgentEventToolStatus.Error : AgentEventToolStatus.Completed,
  }
  const resultJson = stringifyJson(block.content)
  if (resultJson !== undefined) ev.toolResult = resultJson
  if (sessionId !== undefined) ev.agentId = sessionId
  return ev
}

function mapSystem(frame: ClaudeSystemMessage): MappedCursorEvent[] {
  // The Claude SDK emits a `system` frame with `subtype: 'init'` carrying
  // `session_id` (the resume handle — same role as Cursor's agent_id) plus
  // model + tools metadata. We forward a System carrier so the audit row keeps
  // the init metadata and TurnRunner can read `agentId` as the resume hint.
  // Non-init system subtypes (status, session_state_changed, etc.) are dropped
  // by `mapClaudeMessage`'s switch only handling 'system'→here; we keep the
  // carrier for init and emit a lightweight carrier for any other subtype so
  // the audit trail isn't silent.
  const subtype = typeof frame.subtype === 'string' ? frame.subtype : 'init'
  const sessionId =
    typeof frame.session_id === 'string' ? frame.session_id : undefined
  const ev: MappedSystemEvent = {
    kind: 'System',
    subtype: `claude_${subtype}`,
    eventData: {
      subtype,
      ...(sessionId !== undefined ? { sessionId } : {}),
      ...(frame.model !== undefined ? { model: frame.model } : {}),
      ...(Array.isArray(frame.tools) ? { tools: frame.tools } : {}),
    },
  }
  if (sessionId !== undefined) ev.agentId = sessionId
  return [ev]
}

function mapResult(
  frame: ClaudeResultMessage,
  state: ClaudeMapperState,
): MappedCursorEvent[] {
  const sessionId =
    typeof frame.session_id === 'string' ? frame.session_id : undefined
  const subtype = typeof frame.subtype === 'string' ? frame.subtype : ''
  const isError = frame.is_error === true || subtype.startsWith('error')
  const runStatus = isError
    ? AgentEventRunStatus.Error
    : AgentEventRunStatus.Finished

  const ev: MappedStatusEvent = {
    kind: AgentEventKind.Status,
    runStatus,
  }
  if (sessionId !== undefined) ev.agentId = sessionId

  // Error message surface: SDKResultError carries `errors[]`; success carries
  // a `result` string. Map the error case to statusMessage for the chat panel.
  if (isError) {
    const errs = frame.errors
    if (Array.isArray(errs) && errs.length > 0) {
      const joined = errs
        .filter((e): e is string => typeof e === 'string')
        .join('; ')
      if (joined !== '') ev.statusMessage = joined
    }
  }

  // Drain any staged run-result aggregate (factory stages it via
  // noteClaudeTerminalRunResult before yielding this result frame).
  if (state.pendingRunResult !== null) {
    ev.runResult = state.pendingRunResult
    state.pendingRunResult = null
  }
  return [ev]
}

/**
 * Synthesise a terminal Status event when the stream closes WITHOUT a `result`
 * frame (atypical — the SDK always emits one — but the factory's abort/error
 * paths need a fallback). Drains the staged `pendingRunResult` the same way
 * `mapResult` would. Mirrors `synthesizeTerminalStatus` on the Cursor side.
 */
export function synthesizeClaudeTerminalStatus(
  state: ClaudeMapperState,
  args: {
    runStatus: AgentEventRunStatus
    agentId?: string
    statusMessage?: string
  },
): MappedStatusEvent {
  const ev: MappedStatusEvent = {
    kind: AgentEventKind.Status,
    runStatus: args.runStatus,
  }
  if (args.agentId !== undefined) ev.agentId = args.agentId
  if (args.statusMessage !== undefined && args.statusMessage !== '') {
    ev.statusMessage = args.statusMessage
  }
  if (state.pendingRunResult !== null) {
    ev.runResult = state.pendingRunResult
    state.pendingRunResult = null
  }
  return ev
}

// ---------------------------------------------------------------------------
// Local helpers (mirrors CursorEventMapper)
// ---------------------------------------------------------------------------

function readString(v: unknown): string | undefined {
  return typeof v === 'string' ? v : undefined
}

/**
 * JSON-stringify a value for the wire's jsonb-ready string column. Strings
 * pass through unchanged; undefined / null → undefined (column stays null);
 * circular refs fall through to undefined so the wire row stays alive.
 */
function stringifyJson(v: unknown): string | undefined {
  if (v === undefined || v === null) return undefined
  if (typeof v === 'string') return v
  try {
    return JSON.stringify(v)
  } catch {
    return undefined
  }
}
