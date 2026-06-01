/**
 * TurnTrace — the expanded, chronological, formatter-driven trace for one
 * turn (cursor-native-chat-ux scene 8, card 7 rewrite).
 *
 * <p>Replaces the previous code-block-driven trace. Each event in the turn
 * renders as a single quiet row driven by the {@link formatTool} registry
 * (for tool calls) or a minimal type-driven affordance (thinking, task,
 * status). Every row carries a "view raw" affordance that expands to the
 * original payload as {@code <pre>JSON</pre>} — this is the universal
 * fallback for unknown shapes and the debug surface.</p>
 *
 * <h3>Rules per event kind</h3>
 * <ul>
 *   <li>{@code ToolUseEvent} → formatter {@code .summary} (or
 *       {@code .errorVariant} on {@code Error}) prefixed with the glyph.
 *       Coalesces the running + terminal frames sharing a {@code callId} —
 *       the terminal one wins for display, the running one is still visible
 *       via "view raw".</li>
 *   <li>{@code ThinkingEvent} → full italic text + small "Thought for X"
 *       badge.</li>
 *   <li>{@code TaskEvent} → centered milestone divider — a horizontal rule
 *       with the task title centered in muted weight.</li>
 *   <li>{@code StatusEvent} → muted timeline marker. Skipped when redundant
 *       (e.g. multiple consecutive {@code Running}s); kept for the meaningful
 *       transitions (Creating / Finished / Cancelled / Error / Expired).</li>
 *   <li>{@code AssistantTextEvent} → skipped here; already rendered in the
 *       bubble body.</li>
 *   <li>{@code PromptReceivedEvent} → skipped here; user bubble owns it.</li>
 * </ul>
 *
 * <h3>No orphan handling, no pairing logic</h3>
 * <p>Each {@code ToolUseEvent} is self-contained — it carries both args
 * and result columns once it transitions to a terminal status. We coalesce
 * the running + terminal frames into ONE row keyed by {@code callId}.</p>
 */
import { useMemo, useState } from 'react'
import { Box, Typography } from '@mui/material'
import { keyframes } from '@mui/system'
import CheckCircleOutlineIcon from '@mui/icons-material/CheckCircleOutline'
import ErrorOutlineIcon from '@mui/icons-material/ErrorOutline'
import PsychologyIcon from '@mui/icons-material/Psychology'
import TerminalIcon from '@mui/icons-material/Terminal'
import DescriptionIcon from '@mui/icons-material/Description'
import EditIcon from '@mui/icons-material/Edit'
import SearchIcon from '@mui/icons-material/Search'
import FolderOpenIcon from '@mui/icons-material/FolderOpen'
import NoteAddIcon from '@mui/icons-material/NoteAdd'
import PublicIcon from '@mui/icons-material/Public'
import BuildIcon from '@mui/icons-material/Build'
import type { ComponentType, SVGProps } from 'react'
import type { AgentEventDto } from '../../../../../api/queries-commands'
import {
  isAssistantText,
  isPromptReceived,
  isStatus,
  isTask,
  isThinking,
  isToolUse,
} from './chatEvents'
import { formatTool } from './toolFormatters'

import {
  chromeTokens,
  semanticTokens,
  surfaceTokens,
  workspaceText,
} from '../../../shared/designTokens'

const tokens = {
  ...surfaceTokens,
  ...chromeTokens,
}

const fadeIn = keyframes`
  from { opacity: 0; }
  to   { opacity: 1; }
`

const MONO_STACK =
  '"SF Mono", "Menlo", "Monaco", "Consolas", monospace'

// ── Icon resolution ────────────────────────────────────────────────────────

type IconComponent = ComponentType<SVGProps<SVGSVGElement> & { fontSize?: string }>

const GLYPH_MAP: Readonly<Record<string, IconComponent>> = {
  Terminal: TerminalIcon as unknown as IconComponent,
  Description: DescriptionIcon as unknown as IconComponent,
  Edit: EditIcon as unknown as IconComponent,
  Search: SearchIcon as unknown as IconComponent,
  FolderOpen: FolderOpenIcon as unknown as IconComponent,
  NoteAdd: NoteAddIcon as unknown as IconComponent,
  Public: PublicIcon as unknown as IconComponent,
  Build: BuildIcon as unknown as IconComponent,
  Psychology: PsychologyIcon as unknown as IconComponent,
}

function resolveGlyph(name: string | undefined): IconComponent {
  if (!name) return BuildIcon as unknown as IconComponent
  return GLYPH_MAP[name] ?? (BuildIcon as unknown as IconComponent)
}

// ── Public props ────────────────────────────────────────────────────────────

export interface TurnTraceProps {
  /** All events for the turn, in sequence order. */
  events: AgentEventDto[]
}

// ── Duration formatter (used by thinking badges) ───────────────────────────

function formatThoughtDuration(ms: number | null | undefined): string {
  if (ms === null || ms === undefined || !Number.isFinite(ms) || ms < 0) {
    return 'a moment'
  }
  if (ms < 1000) return `${Math.round(ms)}ms`
  if (ms < 60_000) return `${(ms / 1000).toFixed(1)}s`
  const totalSeconds = Math.floor(ms / 1000)
  const minutes = Math.floor(totalSeconds / 60)
  const seconds = totalSeconds % 60
  return `${minutes}m ${seconds.toString().padStart(2, '0')}s`
}

// ── Raw payload toggle ──────────────────────────────────────────────────────

interface RawPayloadProps {
  event: AgentEventDto | AgentEventDto[]
}

function RawPayload({ event }: RawPayloadProps) {
  // Pretty-print the event(s) verbatim — this is the universal "show me what
  // the wire actually delivered" affordance. Never throws even for cyclic
  // references thanks to the try/catch.
  let json: string
  try {
    json = JSON.stringify(event, null, 2)
  } catch {
    json = String(event)
  }
  return (
    <Box
      component="pre"
      sx={{
        mt: 0.5,
        p: 1,
        fontFamily: MONO_STACK,
        fontSize: 11.5,
        lineHeight: 1.5,
        color: workspaceText.primary,
        background: tokens.codeBg,
        border: `1px solid ${tokens.codeBorder}`,
        borderRadius: '6px',
        whiteSpace: 'pre-wrap',
        wordBreak: 'break-word',
        overflowX: 'auto',
        maxHeight: 320,
        overflow: 'auto',
      }}
    >
      {json}
    </Box>
  )
}

// ── "view raw" toggle row ──────────────────────────────────────────────────

interface ViewRawButtonProps {
  shown: boolean
  onToggle: () => void
}

function ViewRawButton({ shown, onToggle }: ViewRawButtonProps) {
  return (
    <Box
      component="button"
      type="button"
      onClick={onToggle}
      sx={{
        background: 'none',
        border: 'none',
        padding: 0,
        cursor: 'pointer',
        fontSize: 11,
        color: workspaceText.faint,
        letterSpacing: '0.01em',
        fontFamily: 'inherit',
        textDecoration: 'underline',
        textUnderlineOffset: '2px',
        textDecorationColor: 'transparent',
        transition: 'color 180ms ease, text-decoration-color 180ms ease',
        '&:hover, &:focus-visible': {
          outline: 'none',
          color: workspaceText.muted,
          textDecorationColor: 'rgba(0,0,0,0.18)',
        },
      }}
    >
      {shown ? 'hide raw' : 'view raw'}
    </Box>
  )
}

// ── Truncation badge ───────────────────────────────────────────────────────

function TruncatedBadge() {
  return (
    <Box
      component="span"
      sx={{
        display: 'inline-flex',
        alignItems: 'center',
        flexShrink: 0,
        fontSize: 10,
        fontWeight: 600,
        letterSpacing: '0.05em',
        textTransform: 'uppercase',
        color: semanticTokens.warning,
        // Amber-tinted background with a hairline border, calm.
        backgroundColor: 'rgba(255, 159, 28, 0.08)',
        border: '1px solid rgba(255, 159, 28, 0.25)',
        borderRadius: '4px',
        px: 0.5,
        py: 0,
        ml: 0.5,
      }}
    >
      Truncated
    </Box>
  )
}

// ── Tool row ────────────────────────────────────────────────────────────────

interface ToolRowProps {
  /**
   * The coalesced event for this {@code callId} — terminal frame if seen,
   * else the running frame. The original frame array is preserved for the
   * raw payload toggle.
   */
  displayEvent: AgentEventDto & { eventKind: 'toolUse' }
  /** Both running + terminal frames keyed by callId for "view raw". */
  rawFrames: AgentEventDto[]
}

function ToolRow({ displayEvent, rawFrames }: ToolRowProps) {
  const [showRaw, setShowRaw] = useState(false)
  // The display event is narrowed to toolUse via the caller; we cast to the
  // formatter input type since the runtime shapes match.
  const formatted = formatTool(
    displayEvent as unknown as Parameters<typeof formatTool>[0],
  )
  const status = (displayEvent as unknown as { status: 'Running' | 'Completed' | 'Error' })
    .status
  const argsTruncated = !!(displayEvent as unknown as { argsTruncated?: boolean })
    .argsTruncated
  const resultTruncated = !!(
    displayEvent as unknown as { resultTruncated?: boolean }
  ).resultTruncated
  const showTruncated = argsTruncated || resultTruncated

  const Glyph = resolveGlyph(formatted.glyph)

  // For a still-running tool we render the active label (the user expanded
  // the trace mid-flight); the pill above already shows the same thing, so
  // they reinforce each other.
  const text =
    status === 'Error'
      ? formatted.errorVariant
      : status === 'Running'
        ? formatted.activeLabel
        : formatted.summary

  const tone =
    status === 'Error' ? semanticTokens.error : workspaceText.primary

  return (
    <Box>
      <Box
        sx={{
          display: 'flex',
          alignItems: 'flex-start',
          gap: 1,
          minHeight: 22,
        }}
      >
        <Box
          aria-hidden
          sx={{
            display: 'inline-flex',
            alignItems: 'center',
            justifyContent: 'center',
            width: 16,
            height: 16,
            flexShrink: 0,
            color: tone,
            opacity: 0.7,
            mt: 0.1,
          }}
        >
          <Glyph style={{ fontSize: 14, width: 14, height: 14 }} />
        </Box>
        <Typography
          component="div"
          sx={{
            flex: 1,
            minWidth: 0,
            fontSize: 13,
            lineHeight: 1.5,
            color: tone,
            letterSpacing: '-0.005em',
            wordBreak: 'break-word',
            fontFamily:
              '"Inter", -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif',
            // Inline code in the summary (paths, commands) — render as
            // monospaced inline tokens for legibility.
            '& code': {
              fontFamily: MONO_STACK,
              fontSize: '0.92em',
              backgroundColor: tokens.chipBg,
              padding: '0 4px',
              borderRadius: '3px',
              color: workspaceText.primary,
            },
          }}
          // The formatter output may contain backticks like `path/to/file.ts`.
          // Convert each ``…`` segment into a <code> for visual parity with
          // the pill. Simple, no markdown library needed.
          dangerouslySetInnerHTML={{
            __html: codeFenceToHtml(text),
          }}
        />
        <Box
          sx={{
            display: 'inline-flex',
            alignItems: 'center',
            gap: 0.5,
            flexShrink: 0,
          }}
        >
          {showTruncated && <TruncatedBadge />}
          {status === 'Completed' && (
            <CheckCircleOutlineIcon
              aria-hidden
              sx={{ fontSize: 13, color: semanticTokens.success, opacity: 0.7 }}
            />
          )}
          {status === 'Error' && (
            <ErrorOutlineIcon
              aria-hidden
              sx={{ fontSize: 13, color: semanticTokens.error, opacity: 0.8 }}
            />
          )}
          <ViewRawButton shown={showRaw} onToggle={() => setShowRaw((v) => !v)} />
        </Box>
      </Box>
      {showRaw && <RawPayload event={rawFrames} />}
    </Box>
  )
}

// Replace `…` segments with <code>…</code>. We escape the surrounding HTML so
// the only sanctioned markup is the <code> tag itself.
function codeFenceToHtml(s: string): string {
  const escaped = s
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
  return escaped.replace(/`([^`]+)`/g, '<code>$1</code>')
}

// ── Thinking row ────────────────────────────────────────────────────────────

interface ThinkingRowProps {
  event: AgentEventDto & { eventKind: 'thinking' }
}

function ThinkingRow({ event }: ThinkingRowProps) {
  const [showRaw, setShowRaw] = useState(false)
  const text =
    (event as unknown as { text?: string | null }).text?.trim() ?? ''
  const durationMs = (event as unknown as { thinkingDurationMs?: number | null })
    .thinkingDurationMs
  return (
    <Box>
      <Box
        sx={{
          display: 'flex',
          alignItems: 'flex-start',
          gap: 1,
          minHeight: 22,
        }}
      >
        <Box
          aria-hidden
          sx={{
            display: 'inline-flex',
            alignItems: 'center',
            justifyContent: 'center',
            width: 16,
            height: 16,
            flexShrink: 0,
            color: workspaceText.muted,
            opacity: 0.6,
            mt: 0.1,
          }}
        >
          <PsychologyIcon style={{ fontSize: 14, width: 14, height: 14 }} />
        </Box>
        <Typography
          component="div"
          sx={{
            flex: 1,
            minWidth: 0,
            fontSize: 13,
            lineHeight: 1.55,
            fontStyle: 'italic',
            color: workspaceText.muted,
            letterSpacing: '0.005em',
            whiteSpace: 'pre-wrap',
            wordBreak: 'break-word',
            fontFamily:
              '"Inter", -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif',
          }}
        >
          {text || '(no thought captured)'}
        </Typography>
        <Box
          sx={{
            display: 'inline-flex',
            alignItems: 'center',
            gap: 0.625,
            flexShrink: 0,
          }}
        >
          {durationMs !== null && durationMs !== undefined && durationMs > 0 && (
            <Box
              component="span"
              sx={{
                fontSize: 10.5,
                color: workspaceText.faint,
                fontFamily: MONO_STACK,
                fontVariantNumeric: 'tabular-nums',
                letterSpacing: '0.01em',
              }}
            >
              Thought for {formatThoughtDuration(durationMs)}
            </Box>
          )}
          <ViewRawButton shown={showRaw} onToggle={() => setShowRaw((v) => !v)} />
        </Box>
      </Box>
      {showRaw && <RawPayload event={event} />}
    </Box>
  )
}

// ── Task milestone divider ─────────────────────────────────────────────────

interface TaskDividerProps {
  event: AgentEventDto & { eventKind: 'task' }
}

function TaskDivider({ event }: TaskDividerProps) {
  const [showRaw, setShowRaw] = useState(false)
  const title =
    (event as unknown as { title?: string | null }).title ??
    (event as unknown as { taskId?: string | null }).taskId ??
    'Subtask'
  return (
    <Box>
      <Box
        sx={{
          display: 'flex',
          alignItems: 'center',
          gap: 1.5,
          my: 0.5,
        }}
      >
        <Box
          aria-hidden
          sx={{
            flex: 1,
            height: 1,
            background: surfaceTokens.hairline,
          }}
        />
        <Typography
          component="div"
          sx={{
            flexShrink: 0,
            fontSize: 11.5,
            fontWeight: 500,
            letterSpacing: '0.04em',
            textTransform: 'uppercase',
            color: workspaceText.muted,
            fontFamily:
              '"Inter", -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif',
          }}
        >
          Subtask: {title}
        </Typography>
        <Box
          aria-hidden
          sx={{
            flex: 1,
            height: 1,
            background: surfaceTokens.hairline,
          }}
        />
        <ViewRawButton shown={showRaw} onToggle={() => setShowRaw((v) => !v)} />
      </Box>
      {showRaw && <RawPayload event={event} />}
    </Box>
  )
}

// ── Status timeline marker ─────────────────────────────────────────────────

interface StatusRowProps {
  event: AgentEventDto & { eventKind: 'status' }
}

function StatusRow({ event }: StatusRowProps) {
  const [showRaw, setShowRaw] = useState(false)
  const status = (event as unknown as { status: string }).status
  const message = (event as unknown as { message?: string | null }).message
  // Render the matching verb. We only emit rows for transitions that mean
  // something to the reader — the buildPlan filter strips the redundant ones
  // (consecutive Runnings) before this component sees them.
  const verb =
    status === 'Creating'
      ? 'Started'
      : status === 'Running'
        ? 'Running'
        : status === 'Finished'
          ? 'Finished'
          : status === 'Cancelled'
            ? 'Cancelled'
            : status === 'Error'
              ? 'Stopped'
              : status === 'Expired'
                ? 'Timed out'
                : status
  return (
    <Box>
      <Box
        sx={{
          display: 'flex',
          alignItems: 'center',
          gap: 1,
          minHeight: 18,
        }}
      >
        <Box
          aria-hidden
          sx={{
            width: 5,
            height: 5,
            borderRadius: '50%',
            background: workspaceText.faint,
            flexShrink: 0,
            ml: 0.5,
          }}
        />
        <Typography
          component="div"
          sx={{
            flex: 1,
            minWidth: 0,
            fontSize: 11.5,
            color: workspaceText.faint,
            letterSpacing: '0.04em',
            textTransform: 'uppercase',
            fontFamily:
              '"Inter", -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif',
          }}
        >
          {verb}
          {message ? (
            <Box
              component="span"
              sx={{
                ml: 1,
                textTransform: 'none',
                letterSpacing: '0.005em',
                color: workspaceText.muted,
              }}
            >
              {message}
            </Box>
          ) : null}
        </Typography>
        <ViewRawButton shown={showRaw} onToggle={() => setShowRaw((v) => !v)} />
      </Box>
      {showRaw && <RawPayload event={event} />}
    </Box>
  )
}

// ── Plan builder ────────────────────────────────────────────────────────────

type TraceItem =
  | {
      kind: 'tool'
      key: string
      displayEvent: AgentEventDto & { eventKind: 'toolUse' }
      rawFrames: AgentEventDto[]
    }
  | { kind: 'thinking'; key: string; event: AgentEventDto & { eventKind: 'thinking' } }
  | { kind: 'task'; key: string; event: AgentEventDto & { eventKind: 'task' } }
  | { kind: 'status'; key: string; event: AgentEventDto & { eventKind: 'status' } }

/**
 * Build the chronological render plan. Tool frames sharing a {@code callId}
 * are coalesced so the row shows the terminal state once it arrives but the
 * raw payload still includes both frames.
 *
 * Status events are filtered to the non-trivial transitions:
 *   - Always render terminal (Finished / Cancelled / Error / Expired)
 *   - Always render Creating once
 *   - Skip Running (implied by tool/thinking activity)
 */
function buildPlan(events: readonly AgentEventDto[]): TraceItem[] {
  const out: TraceItem[] = []
  const toolFramesByCallId = new Map<string, AgentEventDto[]>()
  const toolItemIndexByCallId = new Map<string, number>()
  let seenCreating = false

  for (const event of events) {
    const key = `${event.sessionId}:${event.sequence}`

    if (isPromptReceived(event) || isAssistantText(event)) {
      // These render in the transcript bubble flow, not in the trace.
      continue
    }

    if (isToolUse(event)) {
      const callId = event.callId
      const frames = toolFramesByCallId.get(callId) ?? []
      frames.push(event)
      toolFramesByCallId.set(callId, frames)
      const existingIdx = toolItemIndexByCallId.get(callId)
      if (existingIdx !== undefined) {
        const prev = out[existingIdx]
        if (prev.kind === 'tool') {
          // Replace the displayEvent with the latest frame — terminal frames
          // carry both args and result so they win, but running frames also
          // remain in rawFrames.
          out[existingIdx] = {
            ...prev,
            displayEvent: event as AgentEventDto & { eventKind: 'toolUse' },
            rawFrames: frames,
          }
        }
      } else {
        toolItemIndexByCallId.set(callId, out.length)
        out.push({
          kind: 'tool',
          key,
          displayEvent: event as AgentEventDto & { eventKind: 'toolUse' },
          rawFrames: frames,
        })
      }
      continue
    }

    if (isThinking(event)) {
      out.push({
        kind: 'thinking',
        key,
        event: event as AgentEventDto & { eventKind: 'thinking' },
      })
      continue
    }

    if (isTask(event)) {
      out.push({
        kind: 'task',
        key,
        event: event as AgentEventDto & { eventKind: 'task' },
      })
      continue
    }

    if (isStatus(event)) {
      const status = (event as unknown as { status: string }).status
      // Drop redundant transitions — only surface meaningful ones in the
      // trace, leaving the implicit ones (a Running emitted whenever a tool
      // starts) to be inferred from the tool/thinking activity.
      if (status === 'Running') continue
      if (status === 'Creating') {
        if (seenCreating) continue
        seenCreating = true
      }
      out.push({
        kind: 'status',
        key,
        event: event as AgentEventDto & { eventKind: 'status' },
      })
      continue
    }
  }
  return out
}

// ── Component ───────────────────────────────────────────────────────────────

export function TurnTrace({ events }: TurnTraceProps) {
  const plan = useMemo(() => buildPlan(events), [events])

  return (
    <Box
      data-testid="turn-trace"
      sx={{
        ml: 3,
        maxWidth: 720,
        pl: 1.5,
        pt: 0.5,
        pb: 1,
        borderLeft: `1px solid ${tokens.hairline}`,
        animation: `${fadeIn} 200ms ease`,
        display: 'flex',
        flexDirection: 'column',
        gap: 0.875,
      }}
    >
      {plan.length === 0 ? (
        <Typography
          component="div"
          sx={{
            fontSize: 13,
            fontStyle: 'italic',
            color: workspaceText.muted,
            letterSpacing: '0.005em',
            fontFamily:
              '"Inter", -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif',
          }}
        >
          No agent-side detail recorded for this turn.
        </Typography>
      ) : (
        plan.map((item) => {
          switch (item.kind) {
            case 'tool':
              return (
                <ToolRow
                  key={item.key}
                  displayEvent={item.displayEvent}
                  rawFrames={item.rawFrames}
                />
              )
            case 'thinking':
              return <ThinkingRow key={item.key} event={item.event} />
            case 'task':
              return <TaskDivider key={item.key} event={item.event} />
            case 'status':
              return <StatusRow key={item.key} event={item.event} />
          }
        })
      )}
    </Box>
  )
}
