/**
 * Terminal line rendering — shared presentation helpers for the runtime debug
 * panel's log terminals ({@link LogViewer} and {@link DaemonLogsView}).
 *
 * <p>Mirrors the prototype's {@code LogsPane} look: a greyed timestamp, a
 * severity-colored 5-char uppercase level label, then the monospace body, plus
 * a blinking cursor at the tail.</p>
 *
 * <p><strong>Purely presentational.</strong> {@link detectSeverity} only
 * inspects the already-buffered line text to choose a <em>display color</em> —
 * it never changes which lines are shown, how they're buffered, copied, or
 * counted. Severity is derived for tinting only; the body is always rendered
 * verbatim.</p>
 */
import { Box } from '@mui/material'
import { keyframes } from '@mui/system'
import {
  semanticTokens,
  workspaceFontFamily,
  workspaceTerminal,
} from '@/applications/workspace/shared/designTokens'

/** Severity buckets — drive the level label color only. */
export type LogSeverity = 'error' | 'warn' | 'note' | 'info'

/** Five-char uppercase labels, matching the prototype's fixed-width column. */
const SEVERITY_LABEL: Record<LogSeverity, string> = {
  error: 'ERROR',
  warn: 'WARN ',
  note: 'NOTE ',
  info: 'INFO ',
}

/**
 * Classify a raw log line into a severity bucket for <em>coloring only</em>.
 * Cheap, allocation-light substring scan over a lowercased copy — no regex
 * backtracking on long lines. Defaults to {@code info} when nothing matches.
 *
 * <p>This reads the line content but does not mutate or filter it; callers
 * always render the original {@code line} text unchanged.</p>
 */
export function detectSeverity(line: string): LogSeverity {
  const l = line.toLowerCase()
  if (
    l.includes('error') ||
    l.includes('err]') ||
    l.includes('fatal') ||
    l.includes('panic') ||
    l.includes('exception') ||
    l.includes('failed') ||
    l.includes('[stderr]')
  ) {
    return 'error'
  }
  if (l.includes('warn')) return 'warn'
  if (l.includes('note') || l.includes('debug') || l.includes('trace')) {
    return 'note'
  }
  return 'info'
}

/** Severity → label color. Matches the prototype's {@code lvlColor}. */
function severityColor(severity: LogSeverity): string {
  switch (severity) {
    case 'error':
      return semanticTokens.error
    case 'warn':
      return semanticTokens.warning
    case 'note':
      return workspaceTerminal.textDim
    case 'info':
    default:
      return semanticTokens.success
  }
}

/** Blink keyframe for the tail cursor — frozen under reduced-motion (below). */
const blink = keyframes`
  0%, 49%  { opacity: 1; }
  50%, 100% { opacity: 0; }
`

export interface TerminalLineProps {
  /** Raw timestamp string, rendered greyed and verbatim. May be empty. */
  timestamp?: string
  /** The raw, unmodified log line body. Always rendered as-is. */
  line: string
  /**
   * Pre-computed severity override. When omitted, derived from {@link line}
   * via {@link detectSeverity}. Pass an explicit value when the caller already
   * knows the stream (e.g. daemon stderr → {@code 'error'}).
   */
  severity?: LogSeverity
}

/**
 * Render one terminal row: greyed timestamp, severity-colored fixed-width
 * level label, then the monospace body. The body color comes from the
 * theme-aware terminal text token so it reads correctly on either surface.
 */
export function TerminalLine({ timestamp, line, severity }: TerminalLineProps) {
  const level = severity ?? detectSeverity(line)
  const labelColor = severityColor(level)
  return (
    <Box component="div">
      {timestamp ? (
        <Box component="span" sx={{ color: workspaceTerminal.textDim }}>
          {timestamp}{' '}
        </Box>
      ) : null}
      <Box
        component="span"
        sx={{
          display: 'inline-block',
          minWidth: 46,
          color: labelColor,
          fontWeight: 600,
          textTransform: 'uppercase',
          letterSpacing: '0.04em',
          fontSize: '0.65rem',
        }}
      >
        {SEVERITY_LABEL[level]}
      </Box>
      <Box component="span" sx={{ color: workspaceTerminal.text }}>
        {' '}
        {line}
      </Box>
    </Box>
  )
}

/**
 * Blinking block cursor pinned at the tail of the buffer — the prototype's
 * {@code ▌} affordance. Respects the OS reduced-motion preference by freezing
 * to a steady glyph (same guard Card A applied to the breathing status dots).
 */
export function TerminalCursor() {
  return (
    <Box component="div" sx={{ color: workspaceTerminal.textDim }}>
      <Box
        component="span"
        aria-hidden
        sx={{
          animation: `${blink} 1.1s steps(1, end) infinite`,
          '@media (prefers-reduced-motion: reduce)': {
            animation: 'none',
            opacity: 1,
          },
        }}
      >
        &#9612;
      </Box>
    </Box>
  )
}

/** Shared `sx` for the scrollable terminal surface — theme-aware. */
export const terminalSurfaceSx = {
  backgroundColor: workspaceTerminal.bg,
  color: workspaceTerminal.text,
  fontFamily: workspaceFontFamily.mono,
  fontSize: 12.5,
  lineHeight: 1.65,
  '&::-webkit-scrollbar': { width: 8, height: 8 },
  '&::-webkit-scrollbar-thumb': {
    backgroundColor: workspaceTerminal.scrollThumb,
    borderRadius: 4,
  },
} as const

/** Empty-state caption color, theme-aware (dim on either surface). */
export const terminalEmptyCaptionSx = {
  color: workspaceTerminal.textDim,
  fontFamily: workspaceFontFamily.mono,
} as const
