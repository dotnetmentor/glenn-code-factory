import { useCallback, useEffect, useRef, useState } from 'react'
import { Box, Chip, Typography } from '@mui/material'
import {
  workspaceColors,
  workspaceFontFamily,
  workspaceText,
} from '@/applications/workspace/shared/designTokens'
import {
  TerminalCursor,
  TerminalLine,
  terminalEmptyCaptionSx,
  terminalSurfaceSx,
} from './terminalLine'

/** Shape of a single buffered log line — mirrors {@code LogsTab} internals. */
export interface LogViewerLine {
  /** ISO timestamp string (rendered as-is). */
  timestamp: string
  /** Raw log line as emitted by the daemon. */
  line: string
  /** Monotonic id so React keys stay stable through FIFO trims. */
  key: number
}

export interface LogViewerProps {
  /** Reverse-chronological list of lines, ordered oldest → newest. */
  lines: LogViewerLine[]
  /** Whether the upstream tail is currently paused (gates the "waiting" caption). */
  paused: boolean
  /** Name of the service being tailed — surfaced in the empty-state caption. */
  serviceName: string | undefined
}

/** Tolerance (in pixels) for "close enough to the bottom" auto-scroll detection. */
const SCROLL_BOTTOM_TOLERANCE_PX = 24

/**
 * Presentational log tail viewer shared by every workspace surface that
 * shows live service logs. Owns its scroll buffer + auto-scroll behaviour
 * but takes lines as a prop so the caller controls subscription lifecycle.
 *
 * <p>Adopts the prototype's terminal look via the theme-aware terminal tokens:
 * greyed timestamps, severity-colored level labels, a monospace body, and a
 * blinking tail cursor (frozen under reduced-motion). The surface stays light
 * in today's light-only app and flips to the dark console automatically once
 * app-wide dark mode lands — the dark palette lives entirely in the
 * {@code --ws-terminal-*} token layer, not inline here.</p>
 */
export function LogViewer({ lines, paused, serviceName }: LogViewerProps) {
  const viewerRef = useRef<HTMLDivElement | null>(null)
  const [autoScroll, setAutoScroll] = useState(true)

  const handleScroll = useCallback(() => {
    const el = viewerRef.current
    if (!el) return
    const distanceFromBottom = el.scrollHeight - el.scrollTop - el.clientHeight
    setAutoScroll(distanceFromBottom <= SCROLL_BOTTOM_TOLERANCE_PX)
  }, [])

  useEffect(() => {
    if (!autoScroll) return
    const el = viewerRef.current
    if (!el) return
    el.scrollTop = el.scrollHeight
  }, [lines, autoScroll])

  return (
    <Box sx={{ position: 'relative', flex: 1, minHeight: 0 }}>
      <Box
        ref={viewerRef}
        onScroll={handleScroll}
        sx={{
          position: 'absolute',
          inset: 0,
          overflowY: 'auto',
          whiteSpace: 'pre-wrap',
          wordBreak: 'break-word',
          px: 2,
          py: 1.25,
          ...terminalSurfaceSx,
        }}
      >
        {lines.length === 0 ? (
          <Typography variant="caption" sx={terminalEmptyCaptionSx}>
            {paused
              ? 'Paused. New lines are being dropped until you resume.'
              : `Waiting for ${serviceName ?? 'service'} to emit log lines…`}
          </Typography>
        ) : (
          <>
            {lines.map((l) => (
              <TerminalLine key={l.key} timestamp={l.timestamp} line={l.line} />
            ))}
            {!paused && <TerminalCursor />}
          </>
        )}
      </Box>

      {!autoScroll && lines.length > 0 && (
        <Chip
          size="small"
          label="Resume auto-scroll"
          onClick={() => {
            const el = viewerRef.current
            if (!el) return
            el.scrollTop = el.scrollHeight
            setAutoScroll(true)
          }}
          sx={{
            position: 'absolute',
            right: 12,
            bottom: 12,
            backgroundColor: workspaceColors.chromeBg,
            border: `1px solid ${workspaceColors.hairline}`,
            color: workspaceText.primary,
            fontFamily: workspaceFontFamily.sans,
            fontSize: '0.6875rem',
            opacity: 0.95,
            '&:hover': {
              backgroundColor: workspaceColors.chipHoverBg,
            },
          }}
        />
      )}
    </Box>
  )
}
