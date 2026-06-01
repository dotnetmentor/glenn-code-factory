import { useCallback, useEffect, useRef, useState } from 'react'
import {
  Alert,
  Box,
  Button,
  Chip,
  IconButton,
  Stack,
  Tooltip,
  Typography,
} from '@mui/material'
import ClearAllIcon from '@mui/icons-material/ClearAll'
import ContentCopyIcon from '@mui/icons-material/ContentCopy'
import PauseIcon from '@mui/icons-material/Pause'
import PlayArrowIcon from '@mui/icons-material/PlayArrow'
import type { AgentHubConnection } from '@/lib/signalr'
import type { DaemonLogLineNotification } from '@/generated/signalr/Source.Features.SignalR.Contracts'
import {
  TerminalCursor,
  TerminalLine,
  terminalEmptyCaptionSx,
  terminalSurfaceSx,
} from './terminalLine'

/**
 * Daemon logs are more voluminous than per-service logs (the daemon
 * multiplexes every supervised service, every poll tick, and every SignalR
 * round-trip into its own stdout/stderr). Cap at 2000 lines so the buffer
 * still fits comfortably in a few hundred KB while giving the operator
 * meaningful scrollback.
 */
const DAEMON_LOG_BUFFER_CAP = 2000

/** "Close enough to the bottom" tolerance for auto-scroll detection (pixels). */
const SCROLL_BOTTOM_TOLERANCE_PX = 24

interface DaemonLogLine {
  /** ISO timestamp string (always serialised so render is cheap). */
  timestamp: string
  /** The raw log line as emitted by the daemon. */
  line: string
  /** Monotonic id so React's key prop stays stable through FIFO trims. */
  key: number
  /** "stdout" | "stderr" — tints the row. */
  stream: 'stdout' | 'stderr' | string
}

export interface DaemonLogsViewProps {
  /** Live AgentHub connection. May be null while the hub is connecting. */
  connection: AgentHubConnection | null
  /** Current runtime id — required for the subscribe/unsubscribe round-trip. */
  runtimeId: string | undefined
}

/**
 * Daemon-logs view. Tails the daemon's own stdout/stderr (the
 * supervisord-managed agent process) over SignalR. Lifecycle: subscribe on
 * mount, unsubscribe on unmount or runtime change. Super-admin gated
 * server-side; the backend rejects subscribes for non-super-admin
 * connections.
 *
 * <p>Lines arrive with a {@code stream} discriminator — stderr is tinted a
 * subtle red so the operator can scan for errors without parsing the line
 * text. Pausing buffers new arrivals into a separate counter so the
 * operator knows what they're missing (unlike the service-logs panel,
 * which drops outright — daemon-debug sessions are more likely to want a
 * "you missed N lines" indicator).</p>
 */
export function DaemonLogsView({ connection, runtimeId }: DaemonLogsViewProps) {
  const [lines, setLines] = useState<DaemonLogLine[]>([])
  const [paused, setPaused] = useState(false)
  const [missedWhilePaused, setMissedWhilePaused] = useState(0)
  const [autoScroll, setAutoScroll] = useState(true)
  const [subscribeError, setSubscribeError] = useState<string | null>(null)

  const pausedRef = useRef(paused)
  pausedRef.current = paused
  const nextKeyRef = useRef(0)
  const viewerRef = useRef<HTMLDivElement | null>(null)

  const handleClear = useCallback(() => {
    setLines([])
    setMissedWhilePaused(0)
  }, [])

  const handleCopy = useCallback(async () => {
    const blob = lines
      .map((l) => `${l.timestamp} [${l.stream}] ${l.line}`)
      .join('\n')
    if (typeof navigator !== 'undefined' && navigator.clipboard?.writeText) {
      try {
        await navigator.clipboard.writeText(blob)
      } catch (err) {
        // eslint-disable-next-line no-console
        console.warn('[DaemonLogsView] clipboard write failed:', err)
      }
    }
  }, [lines])

  const togglePause = useCallback(() => {
    setPaused((p) => {
      // Resuming clears the "while paused" counter — we don't replay missed
      // lines, the counter is just an operator-facing breadcrumb.
      if (p) setMissedWhilePaused(0)
      return !p
    })
  }, [])

  // ── SignalR subscribe / receive / unsubscribe ──────────────────────────
  useEffect(() => {
    if (!connection || !runtimeId) return

    let cancelled = false
    let subscribed = false
    setSubscribeError(null)

    connection
      .subscribeToDaemonLogs(runtimeId)
      .then(() => {
        if (!cancelled) subscribed = true
      })
      .catch((err: unknown) => {
        if (cancelled) return
        // eslint-disable-next-line no-console
        console.warn('[DaemonLogsView] subscribe failed:', err)
        setSubscribeError(
          'Failed to subscribe to daemon logs. The daemon may be offline or the connection unauthenticated.',
        )
      })

    const unsubListener = connection.onDaemonLogLineReceived(
      (payload: DaemonLogLineNotification) => {
        if (payload.runtimeId !== runtimeId) return
        if (pausedRef.current) {
          setMissedWhilePaused((n) => n + 1)
          return
        }
        const ts =
          typeof payload.timestamp === 'string'
            ? payload.timestamp
            : payload.timestamp.toISOString()
        const key = nextKeyRef.current++
        setLines((prev) => {
          const next: DaemonLogLine[] = [
            ...prev,
            {
              timestamp: ts,
              line: payload.line,
              stream: payload.stream,
              key,
            },
          ]
          if (next.length > DAEMON_LOG_BUFFER_CAP) {
            next.splice(0, next.length - DAEMON_LOG_BUFFER_CAP)
          }
          return next
        })
      },
    )

    return () => {
      cancelled = true
      unsubListener()
      if (subscribed) {
        connection.unsubscribeFromDaemonLogs(runtimeId).catch(() => {
          // Best-effort teardown.
        })
      }
    }
  }, [connection, runtimeId])

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
    <Stack spacing={1.5} sx={{ height: '100%', minHeight: 0 }}>
      <Stack
        direction="row"
        spacing={1}
        alignItems="center"
        flexWrap="wrap"
        useFlexGap
      >
        <Typography
          variant="caption"
          color="text.secondary"
          sx={{ fontFamily: 'monospace' }}
        >
          Tailing daemon stdout/stderr
        </Typography>

        <Box sx={{ flex: 1 }} />

        <Tooltip title={paused ? 'Resume live tail' : 'Pause live tail'}>
          <Button
            size="small"
            variant={paused ? 'contained' : 'outlined'}
            color={paused ? 'warning' : 'primary'}
            startIcon={paused ? <PlayArrowIcon /> : <PauseIcon />}
            onClick={togglePause}
          >
            {paused ? 'Resume' : 'Pause'}
          </Button>
        </Tooltip>
        <Tooltip title="Copy buffer to clipboard">
          <span>
            <IconButton
              size="small"
              onClick={handleCopy}
              disabled={lines.length === 0}
              aria-label="Copy daemon logs to clipboard"
            >
              <ContentCopyIcon fontSize="small" />
            </IconButton>
          </span>
        </Tooltip>
        <Tooltip title="Clear daemon log buffer">
          <span>
            <IconButton
              size="small"
              onClick={handleClear}
              disabled={lines.length === 0 && missedWhilePaused === 0}
              aria-label="Clear daemon log buffer"
            >
              <ClearAllIcon fontSize="small" />
            </IconButton>
          </span>
        </Tooltip>
      </Stack>

      {subscribeError && <Alert severity="warning">{subscribeError}</Alert>}

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
            p: 1.25,
            borderRadius: 1,
            ...terminalSurfaceSx,
          }}
        >
          {lines.length === 0 ? (
            <Typography variant="caption" sx={terminalEmptyCaptionSx}>
              {paused
                ? 'Paused. New lines are being counted but not displayed.'
                : 'Waiting for daemon log lines…'}
            </Typography>
          ) : (
            <>
              {lines.map((l) => (
                // stderr is always tinted as an error so the operator can scan
                // for problems without reading the body; stdout falls back to
                // content-derived severity. Presentation-only — the line body
                // is rendered verbatim regardless.
                <TerminalLine
                  key={l.key}
                  timestamp={l.timestamp}
                  line={l.line}
                  severity={l.stream === 'stderr' ? 'error' : undefined}
                />
              ))}
              {!paused && <TerminalCursor />}
            </>
          )}
        </Box>

        {!autoScroll && lines.length > 0 && (
          <Chip
            size="small"
            color="warning"
            label="Auto-scroll paused"
            sx={{
              position: 'absolute',
              right: 12,
              bottom: 12,
              opacity: 0.95,
            }}
            onClick={() => {
              const el = viewerRef.current
              if (!el) return
              el.scrollTop = el.scrollHeight
              setAutoScroll(true)
            }}
          />
        )}
      </Box>

      <Stack direction="row" spacing={1.5} alignItems="center">
        <Typography variant="caption" color="text.secondary">
          {lines.length} / {DAEMON_LOG_BUFFER_CAP} lines buffered
        </Typography>
        {paused && (
          <Chip size="small" label="Paused" color="warning" variant="outlined" />
        )}
        {paused && missedWhilePaused > 0 && (
          <Typography variant="caption" color="warning.main">
            {missedWhilePaused} new line{missedWhilePaused === 1 ? '' : 's'}{' '}
            while paused
          </Typography>
        )}
      </Stack>
    </Stack>
  )
}
