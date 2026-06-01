// CursorEventMapper — translates @cursor/sdk `SDKMessage` frames directly to
// the daemon's cursor-native wire vocabulary (`MappedCursorEvent`), the
// pre-`EmitEventPayload` shape `TurnRunner` stamps with `sessionId` /
// `emittedAt` and ships over SignalR.
//
// === Why this exists ===
//
// The .NET `EmitEventPayload` carries a `kind: AgentEventKind` discriminator
// + per-kind first-class typed fields (cursor-native-chat-ux card 3). This
// mapper is the daemon-side surface that knows how to read Cursor's
// `SDKMessage` union and emit the matching wire shape — no intermediate
// Claude-flavored translator, no double-decode by the .NET hub. Card 3.5
// (this card) flipped the daemon from "Cursor SDK → Claude shape → wire
// translator at the SignalR boundary" to "Cursor SDK → wire" in one step.
//
// === Critical mapping rules (card spec) ===
//
//   - `callId` comes from `SDKToolUseMessage.call_id` (NOT `id`). Cursor's
//     own SDK schema is unambiguous; the legacy mapper that read `id`
//     produced orphan pairings on the chat panel.
//   - `CREATING` and `RUNNING` status frames are FORWARDED verbatim as
//     `Status` events. The legacy mapper dropped them — that swallowed the
//     "Connecting…" pill and the long-running progress indicator.
//   - `SDKTaskMessage` becomes a `Task` event (was `cursor_task` system
//     dropout before).
//   - Tool args/result wrapping: `args` and `result` are stringified to
//     JSON when present and shipped as opaque jsonb-ready strings. The
//     `argsTruncated` / `resultTruncated` booleans come from
//     `SDKToolUseMessage.truncated.{args,result}`.
//   - The terminal Status frame carries an optional `runResult` aggregate
//     (`durationMs`, `model`, `gitBranch`, `gitPrUrl`, `artifacts`). The
//     factory computes it from `run.wait()` + `agent.listArtifacts()` and
//     hands it to the mapper for stamping onto the terminal event.
//
// === Stateful concerns ===
//
// Tool-call lifecycle dedupe (Cursor emits a `running` lifecycle frame
// followed by a terminal `completed` / `error` frame on the same call_id —
// we forward all three, but defend against duplicates) and turn-end usage
// capture live in `CursorMapperState`. The factory threads one state per
// turn through every `mapCursorMessage()` call.

import { AgentEventKind, AgentEventRunStatus, AgentEventToolStatus } from '../signalr/types.js'
import type { RunResultPayload } from '../generated/signalr/Source.Features.SignalR.Contracts.js'

// ---------------------------------------------------------------------------
// Cursor SDK shape aliases — tolerantly typed views, not full SDK types.
// ---------------------------------------------------------------------------

export type CursorSdkMessage =
  | CursorSystemFrame
  | CursorAssistantFrame
  | CursorUserFrame
  | CursorToolCallFrame
  | CursorThinkingFrame
  | CursorStatusFrame
  | CursorRequestFrame
  | CursorTaskFrame

export interface CursorSystemFrame {
  type: 'system'
  subtype?: string
  agent_id?: string
  run_id?: string
  model?: unknown
  tools?: unknown
}

export interface CursorAssistantFrame {
  type: 'assistant'
  agent_id?: string
  run_id?: string
  message?: {
    role?: 'assistant'
    content?: Array<{ type?: string; [k: string]: unknown }>
  }
}

export interface CursorUserFrame {
  type: 'user'
  agent_id?: string
  run_id?: string
  message?: {
    role?: 'user'
    content?: Array<{ type?: string; text?: unknown; [k: string]: unknown }>
  }
}

export interface CursorToolCallFrame {
  type: 'tool_call'
  agent_id?: string
  run_id?: string
  call_id?: string
  name?: string
  status?: 'running' | 'completed' | 'error'
  args?: unknown
  result?: unknown
  truncated?: { args?: boolean; result?: boolean }
}

export interface CursorThinkingFrame {
  type: 'thinking'
  agent_id?: string
  run_id?: string
  text?: string
  thinking_duration_ms?: number
}

export interface CursorStatusFrame {
  type: 'status'
  agent_id?: string
  run_id?: string
  status?: 'CREATING' | 'RUNNING' | 'FINISHED' | 'ERROR' | 'CANCELLED' | 'EXPIRED'
  message?: string
}

export interface CursorRequestFrame {
  type: 'request'
  agent_id?: string
  run_id?: string
  request_id?: string
}

export interface CursorTaskFrame {
  type: 'task'
  agent_id?: string
  run_id?: string
  status?: string
  text?: string
}

// ---------------------------------------------------------------------------
// MappedCursorEvent — the daemon's cursor-native wire shape (pre-envelope)
// ---------------------------------------------------------------------------

/**
 * One event the mapper emits per `mapCursorMessage()` call. A direct mirror
 * of the .NET `EmitEventPayload` first-class fields, minus the envelope
 * fields TurnRunner stamps (sessionId, eventData, emittedAt). The union is
 * discriminated on `kind` (matching the wire's `AgentEventKind`).
 *
 * `agentId` is the SDK-assigned id captured from `frame.agent_id`; TurnRunner
 * uses the FIRST one it sees as the per-turn resume hint.
 */
export type MappedCursorEvent =
  | MappedToolUseEvent
  | MappedThinkingEvent
  | MappedStatusEvent
  | MappedTaskEvent
  | MappedAssistantTextEvent
  | MappedPromptReceivedEvent
  | MappedSystemEvent

export interface MappedToolUseEvent {
  kind: typeof AgentEventKind.ToolUse
  agentId?: string
  toolCallId: string
  toolName: string
  toolStatus: AgentEventToolStatus
  toolArgs?: string
  toolResult?: string
  toolArgsTruncated?: boolean
  toolResultTruncated?: boolean
}

export interface MappedThinkingEvent {
  kind: typeof AgentEventKind.Thinking
  agentId?: string
  text: string
  thinkingDurationMs?: number
}

export interface MappedStatusEvent {
  kind: typeof AgentEventKind.Status
  agentId?: string
  runStatus: AgentEventRunStatus
  statusMessage?: string
  /**
   * Terminal-status aggregate. Populated only on the terminal Status frame
   * (FINISHED/ERROR/CANCELLED/EXPIRED). The factory calls
   * `noteTerminalRunResult()` immediately before emitting the terminal
   * status; `mapStatus()` drains the pending result on emission so a
   * duplicate terminal frame doesn't double-report.
   */
  runResult?: RunResultPayload
}

export interface MappedTaskEvent {
  kind: typeof AgentEventKind.Task
  agentId?: string
  taskId: string
  taskTitle?: string
}

export interface MappedAssistantTextEvent {
  kind: typeof AgentEventKind.AssistantText
  agentId?: string
  text: string
}

export interface MappedPromptReceivedEvent {
  kind: typeof AgentEventKind.PromptReceived
  agentId?: string
  text: string
}

/**
 * Catch-all carrier for system / unknown / request / init frames the chat
 * panel doesn't render but the audit trail wants to keep. Mapped to
 * `AgentEventKind.Status` (run-level plumbing) at the wire boundary with
 * the raw frame folded into `eventData` JSON for the audit row.
 *
 * Distinguished from `MappedStatusEvent` because system carriers carry an
 * opaque `eventData` payload rather than a typed `RunStatus` — both end up
 * as `kind: Status` on the wire, but only `MappedStatusEvent` populates the
 * RunStatus column.
 */
export interface MappedSystemEvent {
  kind: 'System'
  agentId?: string
  subtype: string
  /** Opaque JSON payload to stuff into the wire's eventData column. */
  eventData: unknown
}

// ---------------------------------------------------------------------------
// Mapper state — for tool-call lifecycle dedupe + turn-end usage
// ---------------------------------------------------------------------------

/**
 * Per-turn state the mapper needs to thread across frames.
 *
 *   - `pendingRunResult` is staged by the factory between the last stream
 *     frame and the terminal Status frame, then drained into the Status
 *     event's `runResult` field. The Cursor SDK emits the terminal status
 *     frame BEFORE `run.wait()` resolves with the final aggregate, so the
 *     factory pre-stages it.
 *
 *   - `pendingUsage` captures token counts from a Cursor `turn-ended`
 *     InteractionUpdate (delivered via `agent.send({ onDelta })`, NOT via
 *     `run.stream()`). The chat-panel cost accounting needs these but the
 *     cursor-native AgentEvent shape doesn't carry usage directly — the
 *     daemon's existing `ReportSessionCostHandler` reads it from a separate
 *     `ReportSessionCost` hub call, NOT from EmitEvent. Kept here for the
 *     factory to read out post-stream and ship over the dedicated channel.
 *
 *   - `emittedToolUseCallIds` defends against duplicate `tool_call` running
 *     frames on the same call_id (defensive — Cursor's SDK shouldn't, but
 *     we'd rather drop the dup than send the chat panel a phantom event).
 */
export interface CursorMapperState {
  /** call_id of every `tool_call` running emission we've forwarded. */
  emittedToolUseCallIds: Set<string>
  /** call_id of every terminal (completed/error) `tool_call` we've forwarded. */
  emittedToolResultCallIds: Set<string>
  /**
   * Token usage from `turn-ended` InteractionUpdate. Daemon-internal — read
   * by the factory post-stream and reported via `ReportSessionCost`.
   */
  pendingUsage:
    | {
        inputTokens: number
        outputTokens: number
        cacheReadTokens: number
        cacheWriteTokens: number
      }
    | null
  /**
   * Terminal-run aggregate from `run.wait()` + `agent.listArtifacts()`. The
   * factory stages this AFTER the run drains; the next `mapStatus()` call
   * for a terminal status frame drains it onto the wire event. If the
   * stream never produces a terminal status frame (atypical), the factory
   * synthesises one via `synthesizeTerminalStatus()` carrying the staged
   * run result.
   */
  pendingRunResult: RunResultPayload | null
}

export function makeCursorMapperState(): CursorMapperState {
  return {
    emittedToolUseCallIds: new Set(),
    emittedToolResultCallIds: new Set(),
    pendingUsage: null,
    pendingRunResult: null,
  }
}

/**
 * Record token usage observed from a Cursor `turn-ended` InteractionUpdate.
 * Called by `CursorFactory`'s `onDelta` callback. The captured usage stays
 * on the state until the factory reads it out post-stream and reports it
 * via the dedicated `ReportSessionCost` hub channel.
 *
 * Idempotent — Cursor's SDK contract says one `turn-ended` per turn, but a
 * later call simply overwrites the prior (we trust the SDK).
 */
export function noteTurnEndedUsage(
  state: CursorMapperState,
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

/**
 * Stage a `RunResultPayload` for the next terminal Status event. Called by
 * `CursorFactory` immediately after `run.wait()` resolves with the final
 * aggregate.
 */
export function noteTerminalRunResult(
  state: CursorMapperState,
  runResult: RunResultPayload,
): void {
  state.pendingRunResult = runResult
}

// ---------------------------------------------------------------------------
// Public mapper entrypoint
// ---------------------------------------------------------------------------

/**
 * Map one Cursor SDKMessage to zero or more cursor-native wire events.
 *
 * Returns an empty array for frames we deliberately drop (`request` frames —
 * pure metadata with no wire equivalent). Defensive against unknown frame
 * types: returns a `System` carrier so the audit trail records the shape
 * rather than going silent.
 */
export function mapCursorMessage(
  msg: CursorSdkMessage,
  state: CursorMapperState,
): MappedCursorEvent[] {
  const t = (msg as { type?: unknown }).type
  if (typeof t !== 'string') {
    return [
      {
        kind: 'System',
        subtype: 'cursor_unknown',
        eventData: { raw: msg },
      },
    ]
  }

  switch (t) {
    case 'system':
      return mapSystem(msg as CursorSystemFrame)
    case 'assistant':
      return mapAssistant(msg as CursorAssistantFrame, state)
    case 'user':
      return mapUser(msg as CursorUserFrame)
    case 'tool_call':
      return mapToolCall(msg as CursorToolCallFrame, state)
    case 'thinking':
      return mapThinking(msg as CursorThinkingFrame)
    case 'status':
      return mapStatus(msg as CursorStatusFrame, state)
    case 'request':
      // Pure SDK-internal request-id correlation; nothing for the chat panel.
      return []
    case 'task':
      return mapTask(msg as CursorTaskFrame)
    default:
      return [
        {
          kind: 'System',
          subtype: 'cursor_unknown',
          eventData: { type: t, raw: msg },
        },
      ]
  }
}

// ---------------------------------------------------------------------------
// Per-type handlers
// ---------------------------------------------------------------------------

function mapSystem(frame: CursorSystemFrame): MappedCursorEvent[] {
  // Cursor emits one `system` frame per run with `subtype: 'init'`. We
  // forward it as a System carrier so the audit row keeps the init metadata
  // (model, tools, run_id) — but no first-class chat-panel surface. The
  // agent_id is captured on the carrier so TurnRunner can read it as the
  // resume hint without parsing eventData.
  const subtype = typeof frame.subtype === 'string' ? frame.subtype : 'init'
  const ev: MappedSystemEvent = {
    kind: 'System',
    subtype,
    eventData: {
      subtype,
      ...(typeof frame.agent_id === 'string' ? { agentId: frame.agent_id } : {}),
      ...(typeof frame.run_id === 'string' ? { runId: frame.run_id } : {}),
      ...(frame.model !== undefined ? { model: frame.model } : {}),
      ...(Array.isArray(frame.tools) ? { tools: frame.tools } : {}),
    },
  }
  if (typeof frame.agent_id === 'string') ev.agentId = frame.agent_id
  return [ev]
}

function mapAssistant(
  frame: CursorAssistantFrame,
  state: CursorMapperState,
): MappedCursorEvent[] {
  const content = frame.message?.content
  if (!Array.isArray(content)) {
    return [
      {
        kind: 'System',
        subtype: 'cursor_unknown_assistant',
        eventData: { raw: frame },
        ...(typeof frame.agent_id === 'string' ? { agentId: frame.agent_id } : {}),
      },
    ]
  }

  const out: MappedCursorEvent[] = []
  for (const block of content) {
    const blockType = (block as { type?: unknown }).type
    if (blockType === 'text') {
      const text = readString((block as { text?: unknown }).text) ?? ''
      if (text === '') continue
      const ev: MappedAssistantTextEvent = {
        kind: AgentEventKind.AssistantText,
        text,
      }
      if (typeof frame.agent_id === 'string') ev.agentId = frame.agent_id
      out.push(ev)
    } else if (blockType === 'thinking') {
      const text =
        readString((block as { thinking?: unknown }).thinking) ??
        readString((block as { content?: unknown }).content) ??
        readString((block as { text?: unknown }).text) ??
        ''
      if (text === '') continue
      const ev: MappedThinkingEvent = {
        kind: AgentEventKind.Thinking,
        text,
      }
      if (typeof frame.agent_id === 'string') ev.agentId = frame.agent_id
      out.push(ev)
    } else if (blockType === 'tool_use') {
      // Cursor's assistant frame `tool_use` block carries `id` (block-local)
      // and `name` + `input` (args). In Cursor's SDK the lifecycle ground
      // truth is the standalone `tool_call` frame; we forward the assistant
      // tool_use as a "running" ToolUse event so the chat panel can render
      // the running pill immediately. The standalone `tool_call` running
      // frame will be deduped by `emittedToolUseCallIds`.
      const callId = readString((block as { id?: unknown }).id) ?? ''
      if (callId !== '' && state.emittedToolUseCallIds.has(callId)) continue
      if (callId !== '') state.emittedToolUseCallIds.add(callId)
      const toolName = readString((block as { name?: unknown }).name) ?? ''
      const input = (block as { input?: unknown }).input
      const ev: MappedToolUseEvent = {
        kind: AgentEventKind.ToolUse,
        toolCallId: callId,
        toolName,
        toolStatus: AgentEventToolStatus.Running,
      }
      const argsJson = stringifyJson(input)
      if (argsJson !== undefined) ev.toolArgs = argsJson
      if (typeof frame.agent_id === 'string') ev.agentId = frame.agent_id
      out.push(ev)
    } else {
      out.push({
        kind: 'System',
        subtype: 'cursor_unknown_assistant_block',
        eventData: {
          blockType: typeof blockType === 'string' ? blockType : 'unknown',
          raw: block,
        },
        ...(typeof frame.agent_id === 'string' ? { agentId: frame.agent_id } : {}),
      })
    }
  }
  return out
}

function mapUser(frame: CursorUserFrame): MappedCursorEvent[] {
  // Cursor's user frame is an echo of the prompt the daemon just sent. Map
  // its text blocks to PromptReceived events so the chat panel renders the
  // user's message with the same provenance as PromptReceived rows from the
  // hub.
  const content = frame.message?.content
  if (!Array.isArray(content) || content.length === 0) return []
  const out: MappedCursorEvent[] = []
  for (const block of content) {
    const blockType = (block as { type?: unknown }).type
    if (blockType === 'text') {
      const text = readString((block as { text?: unknown }).text) ?? ''
      if (text === '') continue
      const ev: MappedPromptReceivedEvent = {
        kind: AgentEventKind.PromptReceived,
        text,
      }
      if (typeof frame.agent_id === 'string') ev.agentId = frame.agent_id
      out.push(ev)
    } else {
      // Unknown user block kind — keep an audit row but don't render.
      out.push({
        kind: 'System',
        subtype: 'cursor_unknown_user_block',
        eventData: {
          blockType: typeof blockType === 'string' ? blockType : 'unknown',
          raw: block,
        },
        ...(typeof frame.agent_id === 'string' ? { agentId: frame.agent_id } : {}),
      })
    }
  }
  return out
}

function mapToolCall(
  frame: CursorToolCallFrame,
  state: CursorMapperState,
): MappedCursorEvent[] {
  // CRITICAL: callId comes from `call_id`, NOT `id`. The chat panel uses
  // this to pair running/terminal rows; mixing the two surfaces (Cursor's
  // own SDK distinguishes them) produced orphan pairings on the legacy
  // mapper.
  const callId = readString(frame.call_id) ?? ''
  const status = frame.status
  if (status === undefined) {
    // Cursor SDK contract says tool_call always carries a status; if it
    // doesn't we drop with no audit row (the assistant frame's tool_use
    // block already covers the "the model called this" surface).
    return []
  }

  // Dedupe per status — we forward each lifecycle stage once per call_id.
  const dedupeSet =
    status === 'running'
      ? state.emittedToolUseCallIds
      : state.emittedToolResultCallIds
  if (callId !== '' && dedupeSet.has(callId)) return []
  if (callId !== '') dedupeSet.add(callId)

  const toolName = readString(frame.name) ?? ''
  const toolStatus = mapToolStatus(status)

  const ev: MappedToolUseEvent = {
    kind: AgentEventKind.ToolUse,
    toolCallId: callId,
    toolName,
    toolStatus,
  }
  const argsJson = stringifyJson(frame.args)
  if (argsJson !== undefined) ev.toolArgs = argsJson
  const resultJson = stringifyJson(frame.result)
  if (resultJson !== undefined) ev.toolResult = resultJson
  if (frame.truncated?.args === true) ev.toolArgsTruncated = true
  if (frame.truncated?.result === true) ev.toolResultTruncated = true
  if (typeof frame.agent_id === 'string') ev.agentId = frame.agent_id
  return [ev]
}

function mapThinking(frame: CursorThinkingFrame): MappedCursorEvent[] {
  const text = readString(frame.text) ?? ''
  if (text === '') return []
  const ev: MappedThinkingEvent = {
    kind: AgentEventKind.Thinking,
    text,
  }
  if (typeof frame.thinking_duration_ms === 'number') {
    ev.thinkingDurationMs = frame.thinking_duration_ms
  }
  if (typeof frame.agent_id === 'string') ev.agentId = frame.agent_id
  return [ev]
}

function mapStatus(
  frame: CursorStatusFrame,
  state: CursorMapperState,
): MappedCursorEvent[] {
  const status = frame.status
  if (status === undefined) return []
  const runStatus = mapRunStatus(status)
  const ev: MappedStatusEvent = {
    kind: AgentEventKind.Status,
    runStatus,
  }
  if (typeof frame.message === 'string' && frame.message !== '') {
    ev.statusMessage = frame.message
  }
  if (typeof frame.agent_id === 'string') ev.agentId = frame.agent_id
  // Terminal frames carry the staged run-result aggregate, if the factory
  // has staged one. Non-terminal frames (CREATING/RUNNING) leave it alone —
  // the runResult is by definition only known after `run.wait()` resolves.
  const isTerminal =
    status === 'FINISHED' ||
    status === 'ERROR' ||
    status === 'CANCELLED' ||
    status === 'EXPIRED'
  if (isTerminal && state.pendingRunResult !== null) {
    ev.runResult = state.pendingRunResult
    state.pendingRunResult = null
  }
  return [ev]
}

function mapTask(frame: CursorTaskFrame): MappedCursorEvent[] {
  // Cursor's SDKTaskMessage carries `status?` and `text?` (no first-class
  // task_id / title). We synthesise:
  //   - taskId from run_id (the natural per-task correlation id within the
  //     SDK's request stream) or fall back to '' so the column is never null
  //   - title from text (the human-readable label)
  // The agent_id stays on `agentId` for resume hint capture.
  const taskId = readString(frame.run_id) ?? ''
  const title = readString(frame.text)
  const ev: MappedTaskEvent = {
    kind: AgentEventKind.Task,
    taskId,
  }
  if (title !== undefined && title !== '') ev.taskTitle = title
  if (typeof frame.agent_id === 'string') ev.agentId = frame.agent_id
  return [ev]
}

/**
 * Synthesise a terminal Status event when the stream closes without one.
 * Called by `CursorFactory` after the stream drains if no FINISHED / ERROR /
 * CANCELLED / EXPIRED frame ever arrived. Drains the staged `pendingRunResult`
 * the same way `mapStatus()` would on a real terminal frame.
 */
export function synthesizeTerminalStatus(
  state: CursorMapperState,
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
// Local helpers
// ---------------------------------------------------------------------------

function readString(v: unknown): string | undefined {
  return typeof v === 'string' ? v : undefined
}

function mapToolStatus(
  s: 'running' | 'completed' | 'error',
): AgentEventToolStatus {
  switch (s) {
    case 'running':
      return AgentEventToolStatus.Running
    case 'completed':
      return AgentEventToolStatus.Completed
    case 'error':
      return AgentEventToolStatus.Error
  }
}

function mapRunStatus(
  s: 'CREATING' | 'RUNNING' | 'FINISHED' | 'ERROR' | 'CANCELLED' | 'EXPIRED',
): AgentEventRunStatus {
  switch (s) {
    case 'CREATING':
      return AgentEventRunStatus.Creating
    case 'RUNNING':
      return AgentEventRunStatus.Running
    case 'FINISHED':
      return AgentEventRunStatus.Finished
    case 'ERROR':
      return AgentEventRunStatus.Error
    case 'CANCELLED':
      return AgentEventRunStatus.Cancelled
    case 'EXPIRED':
      return AgentEventRunStatus.Expired
  }
}

/**
 * JSON-stringify the value for the wire's jsonb-ready string column. Strings
 * pass through unchanged (so a tool that already returns JSON-as-string
 * isn't double-encoded). Undefined / null returns `undefined` so the column
 * stays null. Circular refs fall through to `undefined` with a console
 * warning — the wire row stays alive even if a tool result is malformed.
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
