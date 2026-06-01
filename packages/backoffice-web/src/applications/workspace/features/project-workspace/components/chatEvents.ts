/**
 * chatEvents — typed translation between the polymorphic
 * {@link AgentEventDto} wire union and the internal {@link Message} discriminated
 * union the chat surface renders.
 *
 * <p>Card 4 / cursor-native-chat-ux: the daemon now reads Cursor SDK frames
 * directly and ships a typed polymorphic {@code AgentEventDto} (discriminator
 * {@code eventKind} ∈ {@code 'toolUse' | 'thinking' | 'status' | 'task' |
 * 'assistantText' | 'promptReceived'}). Each subtype already carries its
 * first-class columns (no opaque {@code eventData} JSON, no call/result
 * pairing) so the parser is a pure switch on {@code event.eventKind} that
 * narrows to the concrete subtype and projects directly into a render-ready
 * {@link Message}.</p>
 *
 * <p>Deleted concepts (vs the pre-Cursor parser):
 * <ul>
 *   <li><i>pairToolCalls / ToolPair / orphan results.</i> A
 *       {@code ToolUseEventDto} carries both call and result columns; the
 *       running and terminal frames are two rows of the same kind keyed by
 *       {@code callId}. The UI either coalesces them or renders the latest
 *       — there is no concept of an unmatched result.</li>
 *   <li><i>formatPayload / parseEvent / safeParse.</i> No JSON string to
 *       parse — {@code args} and {@code result} are still strings (because
 *       Postgres jsonb columns don't transpile through Tapper as
 *       JsonElement), but they're already-formatted JSON the trace can
 *       render verbatim.</li>
 *   <li><i>extractFailureReasonFromEvents / deriveTurnLabel.</i> The
 *       activity-pill state machine in card 6 will own this. Removed
 *       here so consumers stop importing them.</li>
 *   <li><i>The {@code commit} message kind.</i> Auto-commit trailers were
 *       carried on a removed {@code CommitMade} event type; the new
 *       Cursor-native union has no equivalent. Card 7 will revisit how to
 *       surface this (likely via {@link RunResultDto.gitBranch /
 *       gitPrUrl}).</li>
 * </ul></p>
 *
 * <p>This module is framework-free so the helpers can be reused from both
 * the historical Orval fetch and the live SignalR push.</p>
 */
import { keyframes } from '@mui/system'
import type {
  AgentEventDto,
  AssistantTextEventDto,
  PromptReceivedEventDto,
  StatusEventDto,
  TaskEventDto,
  ThinkingEventDto,
  ToolUseEventDto,
} from '../../../../../api/queries-commands'

import { workspaceRuntime } from '../../../shared/designTokens'

// ── Shared runtime breathing animation ─────────────────────────────────────
//
// Two pulse rhythms used across the chat surface (canvas status row + sidebar
// row dot) so a soft-queued turn reads as quieter than a dispatched one:
//
//   * {@link breatheNormal} — 2s ease-in-out, opacity 0.5 → 1 → 0.5. The
//     dispatched, mid-turn "I'm working" rhythm.
//   * {@link breatheQuiet}  — 3s ease-in-out, opacity 0.3 → 0.7 → 0.3. The
//     "I'm still queued, hold tight" rhythm — slower + lower contrast so it
//     visually graduates to the normal pulse the moment the daemon starts
//     emitting events for the session.
//
// Lifted here (rather than duplicated in TurnStatusLine + ConversationSidebar)
// so the two surfaces can't drift apart silently.
export const RUNTIME_DOT_COLOR = workspaceRuntime.booting

// Muted failure hue for calm inline failure treatment (P3.5).
export const FAILURE_RUST_COLOR = workspaceRuntime.failed

export const breatheNormal = keyframes`
  0%   { opacity: 0.5; }
  50%  { opacity: 1; }
  100% { opacity: 0.5; }
`

export const breatheQuiet = keyframes`
  0%   { opacity: 0.3; }
  50%  { opacity: 0.7; }
  100% { opacity: 0.3; }
`

// ── Internal Message union ─────────────────────────────────────────────────
//
// Render-ready snapshot of one row of the transcript. {@code id} is the
// composite "sessionId:sequence" key so live merges dedupe cleanly — except
// optimistic user bubbles, which use an {@code optimistic:*} prefix until
// the matching {@code PromptReceived} event arrives.

export type Message =
  | {
      kind: 'user'
      id: string
      sessionId: string
      sequence: number
      text: string
      optimistic?: boolean
      createdAt?: string
    }
  | {
      kind: 'assistant'
      id: string
      sessionId: string
      sequence: number
      text: string
      createdAt?: string
    }
  | {
      kind: 'thinking'
      id: string
      sessionId: string
      sequence: number
      text: string
      thinkingDurationMs: number | null
      createdAt?: string
    }
  | {
      kind: 'tool'
      id: string
      sessionId: string
      sequence: number
      callId: string
      name: string
      toolStatus: 'Running' | 'Completed' | 'Error'
      /** Raw JSON string the daemon shipped — render verbatim or re-parse per tool. */
      args: string | null
      /** Raw JSON string the daemon shipped — render verbatim or re-parse per tool. */
      result: string | null
      argsTruncated: boolean
      resultTruncated: boolean
      createdAt?: string
    }
  | {
      kind: 'status'
      id: string
      sessionId: string
      sequence: number
      status: 'Creating' | 'Running' | 'Finished' | 'Error' | 'Cancelled' | 'Expired'
      message: string | null
      createdAt?: string
    }
  | {
      kind: 'task'
      id: string
      sessionId: string
      sequence: number
      taskId: string | null
      title: string | null
      createdAt?: string
    }

// ── Type guards that narrow AgentEventDto by its eventKind discriminator ───
//
// The Orval-generated union is `(PromptReceivedEventDto & { eventKind: string
// }) | (AssistantTextEventDto & { eventKind: string }) | ...`. The
// per-subtype `eventKind` literal types collapse to `string` in the union
// because Orval widens the discriminator field. We narrow back with explicit
// runtime checks so the consumers get the concrete subtype.

export function isPromptReceived(
  event: AgentEventDto,
): event is PromptReceivedEventDto & { eventKind: string } {
  return event.eventKind === 'promptReceived'
}

export function isAssistantText(
  event: AgentEventDto,
): event is AssistantTextEventDto & { eventKind: string } {
  return event.eventKind === 'assistantText'
}

export function isThinking(
  event: AgentEventDto,
): event is ThinkingEventDto & { eventKind: string } {
  return event.eventKind === 'thinking'
}

export function isToolUse(
  event: AgentEventDto,
): event is ToolUseEventDto & { eventKind: string } {
  return event.eventKind === 'toolUse'
}

export function isStatus(
  event: AgentEventDto,
): event is StatusEventDto & { eventKind: string } {
  return event.eventKind === 'status'
}

export function isTask(
  event: AgentEventDto,
): event is TaskEventDto & { eventKind: string } {
  return event.eventKind === 'task'
}

// ── Wire → Message projection ───────────────────────────────────────────────
//
// Switch on the polymorphic discriminator and project into the internal
// Message union. Returns {@code null} for events that don't have a direct
// transcript representation (every kind has SOME representation today; the
// {@code null} return stays in the signature for forward-compat with future
// silent kinds).

export function eventToMessage(event: AgentEventDto): Message | null {
  const id = `${event.sessionId}:${event.sequence}`
  // {@code createdAt} is typed as {@code string} on the Orval REST shape but
  // arrives as {@code Date | string} from the SignalR push (Tapper preserves
  // {@code System.DateTime} as a union). Normalise to a single ISO string so
  // downstream renderers don't have to repeat this branch.
  const rawCreated = event.createdAt as unknown
  const createdAt =
    typeof rawCreated === 'string'
      ? rawCreated
      : rawCreated instanceof Date
        ? rawCreated.toISOString()
        : undefined
  const base = {
    id,
    sessionId: event.sessionId,
    sequence: event.sequence,
    createdAt,
  }

  if (isPromptReceived(event)) {
    if (!event.text) return null
    return { kind: 'user', ...base, text: event.text }
  }
  if (isAssistantText(event)) {
    if (!event.text) return null
    return { kind: 'assistant', ...base, text: event.text }
  }
  if (isThinking(event)) {
    return {
      kind: 'thinking',
      ...base,
      text: event.text ?? '',
      thinkingDurationMs: event.thinkingDurationMs ?? null,
    }
  }
  if (isToolUse(event)) {
    return {
      kind: 'tool',
      ...base,
      callId: event.callId,
      name: event.name,
      toolStatus: event.status,
      args: event.args ?? null,
      result: event.result ?? null,
      argsTruncated: event.argsTruncated,
      resultTruncated: event.resultTruncated,
    }
  }
  if (isStatus(event)) {
    return {
      kind: 'status',
      ...base,
      status: event.status,
      message: event.message ?? null,
    }
  }
  if (isTask(event)) {
    return {
      kind: 'task',
      ...base,
      taskId: event.taskId ?? null,
      title: event.title ?? null,
    }
  }
  return null
}
