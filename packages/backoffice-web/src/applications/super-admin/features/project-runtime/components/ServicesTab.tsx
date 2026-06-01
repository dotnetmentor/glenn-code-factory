import { useCallback, useMemo, useState, Fragment } from 'react'
import {
  Box,
  Button,
  Collapse,
  Stack,
  Table,
  TableBody,
  TableCell,
  TableContainer,
  TableHead,
  TableRow,
  Typography,
} from '@mui/material'
import KeyboardArrowDownIcon from '@mui/icons-material/KeyboardArrowDown'
import KeyboardArrowRightIcon from '@mui/icons-material/KeyboardArrowRight'
import { RuntimeState, type RuntimeEventDto } from '@/api/queries-commands'
import type {
  LiveSupervisordSnapshotPayload,
  LiveSupervisordSnapshotProcess,
} from '@/generated/signalr/Source.Features.SignalR.Contracts'
import { formatBytes } from '@/lib/format/formatBytes'
import {
  monoNumberSx,
  workspaceAccent,
  workspaceFontFamily,
  workspaceRuntime,
  workspaceText,
} from '@/applications/workspace/shared/designTokens'
import { StatusDot } from '@/applications/workspace/shared/primitives'
import {
  getServiceName,
  parseEventPayload,
} from '../utils/runtimeEventDisplay'
import type { HeartbeatProcess, HeartbeatSnapshot } from '../hooks/useRuntimeEventStream'

/**
 * The Services tab renders a live table of the runtime's supervised
 * processes. Primary source of truth is the live supervisord XML-RPC
 * snapshot pushed every 10s over SignalR ({@code supervisordSnapshot});
 * if no snapshot has arrived yet we fall back to deriving rows from the
 * structured event stream (cold-load only — the snapshot replaces this on
 * the first push).
 *
 * <p>Per-row expanders surface the most recent {@code ServiceCrashed} or
 * {@code ServiceFailedToStart} event for that service, including the
 * 50-line stderr tail captured at crash time — closeable evidence without
 * leaving the drawer.</p>
 */
export interface ServicesTabProps {
  /**
   * Reverse-chrono structured event stream. Consumed for crash-history
   * lookup (last ServiceCrashed / ServiceFailedToStart per service) and as
   * the cold-load fallback for the row list when supervisordSnapshot is
   * not yet available.
   */
  events: RuntimeEventDto[]
  /**
   * Latest live supervisord poll. When non-null this is the primary state
   * source — it covers FATAL / BACKOFF / STOPPED that the discrete event
   * stream can't represent end-to-end.
   */
  supervisordSnapshot: LiveSupervisordSnapshotPayload | null
  /**
   * Latest heartbeat sysstats snapshot (top-50 by RSS). Matched by service
   * name to surface Mem RSS + CPU% per row. Null until the first
   * heartbeat lands.
   */
  heartbeatSnapshot: HeartbeatSnapshot | null
  /**
   * Called when the user clicks "View logs" on a row. May be omitted, in
   * which case the button renders disabled.
   */
  onViewLogs?: (serviceName: string) => void
}

interface ServiceRow {
  name: string
  state: string
  pid: number
  spawnErr: string | null
  /** From most recent ServiceCrashed event for this service. */
  lastExitCode: number | null
}

interface CrashHistoryEntry {
  /** "ServiceCrashed" | "ServiceFailedToStart" */
  type: string
  /** Best-effort timestamp string for context. */
  timestamp: string | null
  /** Exit code if present (only on ServiceCrashed). */
  exitCode: number | null
  /** Spawn error if present (only on ServiceFailedToStart). */
  spawnErr: string | null
  /** stderr tail captured at the moment of crash / FATAL. */
  stderrTailLines: string[]
}

/**
 * Walk the reverse-chrono event list once per service to find the most
 * recent ServiceCrashed / ServiceFailedToStart entry. Returns a Map keyed
 * by service name — services without crash history are absent.
 */
function buildCrashHistory(
  events: RuntimeEventDto[],
): Map<string, CrashHistoryEntry> {
  const out = new Map<string, CrashHistoryEntry>()
  for (const event of events) {
    const type = event.type
    if (type !== 'ServiceCrashed' && type !== 'ServiceFailedToStart') continue
    const name = getServiceName(event)
    if (!name) continue
    // events array is newest-first so the first hit per service wins.
    if (out.has(name)) continue
    const payload = parseEventPayload(event.payload) ?? {}
    const rawTail = payload['stderrTailLines']
    const stderrTailLines = Array.isArray(rawTail)
      ? rawTail.filter((l): l is string => typeof l === 'string')
      : []
    const exitCode =
      typeof payload['exitCode'] === 'number'
        ? (payload['exitCode'] as number)
        : null
    const spawnErr =
      typeof payload['spawnErr'] === 'string'
        ? (payload['spawnErr'] as string)
        : null
    const timestamp =
      typeof event.timestamp === 'string'
        ? event.timestamp
        : event.timestamp != null
        ? new Date(event.timestamp).toISOString()
        : null
    out.set(name, {
      type,
      timestamp,
      exitCode,
      spawnErr,
      stderrTailLines,
    })
  }
  return out
}

/**
 * Sort key for service-state — RUNNING first, then alphabetical by state,
 * then alphabetical by name. Matches what an operator most wants to scan
 * (healthy at top, sick at bottom).
 */
function stateSortKey(state: string): string {
  const upper = state.toUpperCase()
  if (upper === 'RUNNING') return '0_RUNNING'
  return `1_${upper}`
}

function buildLiveRows(
  processes: LiveSupervisordSnapshotProcess[],
  crashHistory: Map<string, CrashHistoryEntry>,
): ServiceRow[] {
  return processes
    .map((p): ServiceRow => {
      const crash = crashHistory.get(p.name)
      return {
        name: p.name,
        state: (p.state ?? 'UNKNOWN').toUpperCase(),
        pid: typeof p.pid === 'number' ? p.pid : 0,
        spawnErr:
          (typeof p.spawnErr === 'string' && p.spawnErr.length > 0
            ? p.spawnErr
            : null) ?? crash?.spawnErr ?? null,
        lastExitCode: crash?.exitCode ?? null,
      }
    })
    .sort((a, b) => {
      const ka = stateSortKey(a.state)
      const kb = stateSortKey(b.state)
      if (ka !== kb) return ka.localeCompare(kb)
      return a.name.localeCompare(b.name)
    })
}

/**
 * Cold-load fallback — derive a single row per service name that appears
 * in the event stream. Used only while supervisordSnapshot is null
 * (e.g. drawer just opened, daemon hasn't pushed its first poll yet).
 */
function buildFallbackRows(
  events: RuntimeEventDto[],
  crashHistory: Map<string, CrashHistoryEntry>,
): ServiceRow[] {
  const states = new Map<string, string>()
  // events is newest-first — the first STATE-BEARING event we see per
  // service is the most-recent state transition. We deliberately ignore
  // non-state events (output chunks, healthcheck probe details, healthy
  // pings) — those don't change supervisord state and including them
  // would lock the row at UNKNOWN whenever the newest event per service
  // happens to be an output chunk (which is almost always, while a
  // service is actively running and printing logs).
  //
  // `ServiceHealthy` / `ServiceHealthcheckTimedOut` BOTH mean the service
  // process is alive (RUNNING) — the only difference is whether the
  // user-supplied `/health` probe answered. Treat them both as RUNNING
  // for state purposes.
  for (const event of events) {
    const name = getServiceName(event)
    if (!name) continue
    if (states.has(name)) continue
    const type = event.type
    let state: string | null = null
    if (type === 'ServiceRunning') state = 'RUNNING'
    else if (type === 'ServiceHealthy') state = 'RUNNING'
    else if (type === 'ServiceHealthcheckTimedOut') state = 'RUNNING'
    else if (type === 'ServiceStarting') state = 'STARTING'
    else if (type === 'ServiceCrashed') state = 'EXITED'
    else if (type === 'ServiceFailedToStart') state = 'FATAL'
    else if (type === 'ServiceRestarted') state = 'STARTING'
    else if (type === 'ServiceFatal') state = 'FATAL'
    if (state) states.set(name, state)
  }
  return Array.from(states.entries())
    .map(([name, state]): ServiceRow => {
      const crash = crashHistory.get(name)
      return {
        name,
        state,
        pid: 0,
        spawnErr: crash?.spawnErr ?? null,
        lastExitCode: crash?.exitCode ?? null,
      }
    })
    .sort((a, b) => {
      const ka = stateSortKey(a.state)
      const kb = stateSortKey(b.state)
      if (ka !== kb) return ka.localeCompare(kb)
      return a.name.localeCompare(b.name)
    })
}

/**
 * Map a raw supervisord process state onto the workspace {@link RuntimeState}
 * vocabulary so the shared {@link StatusDot} renders the correct tone and
 * breathing behaviour: green steady for RUNNING, amber breathing for the
 * transitional STARTING / STOPPING / BACKOFF states, red for FATAL / EXITED,
 * grey for STOPPED, and a transparent "unknown" dot otherwise. This keeps the
 * dot's pulse-on-transition semantics in one place (StatusDot) rather than
 * re-deriving an animation here.
 */
function dotStateForSupervisordState(state: string): RuntimeState | null {
  switch (state.toUpperCase()) {
    case 'RUNNING':
      return RuntimeState.Online
    case 'STARTING':
    case 'STOPPING':
    case 'BACKOFF':
      return RuntimeState.Booting
    case 'FATAL':
      return RuntimeState.Failed
    case 'EXITED':
      return RuntimeState.Crashed
    case 'STOPPED':
      return RuntimeState.Suspended
    default:
      return null
  }
}

/**
 * Colour-code a supervisord state for the STATE label text. Green for healthy,
 * amber for in-flight, red for terminal/sick, grey otherwise.
 */
function colorForState(state: string): string {
  switch (state.toUpperCase()) {
    case 'RUNNING':
      return workspaceRuntime.online
    case 'STARTING':
    case 'STOPPING':
    case 'BACKOFF':
      return workspaceRuntime.booting
    case 'FATAL':
    case 'EXITED':
      return workspaceRuntime.failed
    case 'STOPPED':
      return workspaceRuntime.suspended
    default:
      return workspaceRuntime.unknown
  }
}

export function ServicesTab({
  events,
  supervisordSnapshot,
  heartbeatSnapshot,
  onViewLogs,
}: ServicesTabProps) {
  const crashHistory = useMemo(() => buildCrashHistory(events), [events])

  const rows = useMemo<ServiceRow[]>(() => {
    if (supervisordSnapshot && supervisordSnapshot.processes.length > 0) {
      return buildLiveRows(supervisordSnapshot.processes, crashHistory)
    }
    return buildFallbackRows(events, crashHistory)
  }, [supervisordSnapshot, events, crashHistory])

  // Index heartbeat processes by name once per render so per-row lookups
  // are O(1) — there can be up to 50 supervised processes per snapshot.
  const heartbeatByName = useMemo<Map<string, HeartbeatProcess>>(() => {
    const m = new Map<string, HeartbeatProcess>()
    for (const p of heartbeatSnapshot?.processes ?? []) {
      if (!m.has(p.name)) m.set(p.name, p)
    }
    return m
  }, [heartbeatSnapshot])

  const [expanded, setExpanded] = useState<Set<string>>(() => new Set())
  const toggleExpanded = useCallback((name: string) => {
    setExpanded((prev) => {
      const next = new Set(prev)
      if (next.has(name)) next.delete(name)
      else next.add(name)
      return next
    })
  }, [])

  if (rows.length === 0) {
    return (
      <Stack spacing={1.5} sx={{ py: 2 }}>
        <Typography
          sx={{ fontSize: 13.5, color: workspaceText.muted }}
        >
          No services observed yet.
        </Typography>
        <Typography
          sx={{ fontSize: 12, color: workspaceText.faint, fontFamily: workspaceFontFamily.mono }}
        >
          Once supervisord pushes its first poll (≤10s) or the daemon emits a
          Service* event, the table will populate.
        </Typography>
      </Stack>
    )
  }

  return (
    <TableContainer
      sx={{
        '& .MuiTableCell-root': {
          borderBottom: 1,
          borderColor: 'instrument.hairline',
          py: 1.25,
          fontSize: 13,
          letterSpacing: '-0.005em',
        },
        '& .MuiTableHead-root .MuiTableCell-root': {
          color: workspaceText.muted,
          fontSize: '0.6875rem',
          fontWeight: 600,
          letterSpacing: '0.08em',
          textTransform: 'uppercase',
        },
      }}
    >
      <Table size="small" aria-label="Services">
        <TableHead>
          <TableRow>
            <TableCell sx={{ width: 36 }} />
            <TableCell>Name</TableCell>
            <TableCell>State</TableCell>
            <TableCell align="right">PID</TableCell>
            <TableCell align="right">Last exit</TableCell>
            <TableCell>Spawn err</TableCell>
            <TableCell align="right">Mem RSS</TableCell>
            <TableCell align="right">CPU%</TableCell>
            <TableCell align="right">Logs</TableCell>
          </TableRow>
        </TableHead>
        <TableBody>
          {rows.map((row) => {
            const stateColor = colorForState(row.state)
            const dotState = dotStateForSupervisordState(row.state)
            const isExpanded = expanded.has(row.name)
            const crash = crashHistory.get(row.name)
            const heartbeatProc = heartbeatByName.get(row.name)
            return (
              <Fragment key={row.name}>
                <TableRow
                  onClick={() => toggleExpanded(row.name)}
                  sx={{
                    cursor: 'pointer',
                    transition: 'background-color 120ms ease',
                    '&:hover': { backgroundColor: 'instrument.chipBg' },
                  }}
                >
                  <TableCell sx={{ width: 36, pr: 0 }}>
                    {isExpanded ? (
                      <KeyboardArrowDownIcon
                        fontSize="small"
                        sx={{ color: workspaceText.muted }}
                      />
                    ) : (
                      <KeyboardArrowRightIcon
                        fontSize="small"
                        sx={{ color: workspaceText.muted }}
                      />
                    )}
                  </TableCell>
                  <TableCell>
                    <Box
                      component="code"
                      sx={{
                        fontFamily: workspaceFontFamily.mono,
                        fontSize: 12.5,
                        color: workspaceText.primary,
                        fontWeight: 500,
                        letterSpacing: 0,
                        wordBreak: 'break-all',
                      }}
                    >
                      {row.name}
                    </Box>
                  </TableCell>
                  <TableCell>
                    <Stack direction="row" spacing={0.75} alignItems="center">
                      {dotState != null ? (
                        <StatusDot state={dotState} size={7} hideTooltip />
                      ) : (
                        <Box
                          aria-hidden
                          sx={{
                            width: 7,
                            height: 7,
                            borderRadius: '50%',
                            backgroundColor: stateColor,
                            flexShrink: 0,
                          }}
                        />
                      )}
                      <Typography
                        sx={{
                          fontSize: 12.5,
                          fontWeight: 500,
                          color: stateColor,
                        }}
                      >
                        {row.state}
                      </Typography>
                    </Stack>
                  </TableCell>
                  <TableCell align="right">
                    <Typography
                      sx={{
                        ...monoNumberSx,
                        fontSize: 12.5,
                        color: workspaceText.primary,
                      }}
                    >
                      {row.pid > 0 ? row.pid : '—'}
                    </Typography>
                  </TableCell>
                  <TableCell align="right">
                    <Typography
                      sx={{
                        ...monoNumberSx,
                        fontSize: 12.5,
                        color:
                          row.lastExitCode != null && row.lastExitCode !== 0
                            ? workspaceRuntime.failed
                            : workspaceText.primary,
                      }}
                    >
                      {row.lastExitCode != null ? row.lastExitCode : '—'}
                    </Typography>
                  </TableCell>
                  <TableCell>
                    <Typography
                      sx={{
                        fontSize: 12,
                        fontFamily: workspaceFontFamily.mono,
                        color: row.spawnErr
                          ? workspaceRuntime.failed
                          : workspaceText.muted,
                        maxWidth: 240,
                        overflow: 'hidden',
                        textOverflow: 'ellipsis',
                        whiteSpace: 'nowrap',
                      }}
                      title={row.spawnErr ?? undefined}
                    >
                      {row.spawnErr ?? '—'}
                    </Typography>
                  </TableCell>
                  <TableCell align="right">
                    <Typography
                      sx={{
                        ...monoNumberSx,
                        fontSize: 12.5,
                        color: workspaceText.primary,
                      }}
                    >
                      {heartbeatProc ? formatBytes(heartbeatProc.rssBytes) : '—'}
                    </Typography>
                  </TableCell>
                  <TableCell align="right">
                    <Typography
                      sx={{
                        ...monoNumberSx,
                        fontSize: 12.5,
                        color: workspaceText.primary,
                      }}
                    >
                      {heartbeatProc != null
                        ? `${heartbeatProc.cpuPercent.toFixed(1)}%`
                        : '—'}
                    </Typography>
                  </TableCell>
                  <TableCell align="right" onClick={(e) => e.stopPropagation()}>
                    <Button
                      size="small"
                      onClick={
                        onViewLogs ? () => onViewLogs(row.name) : undefined
                      }
                      disabled={!onViewLogs}
                      sx={{
                        px: 1,
                        py: 0.25,
                        fontSize: 12,
                        '&:hover': {
                          color: workspaceAccent.ink,
                          bgcolor: 'instrument.chipHoverBg',
                        },
                      }}
                    >
                      View logs
                    </Button>
                  </TableCell>
                </TableRow>
                <TableRow>
                  <TableCell
                    colSpan={9}
                    sx={{
                      p: 0,
                      borderBottom: isExpanded ? 1 : 0,
                      borderColor: 'instrument.hairline',
                    }}
                  >
                    <Collapse in={isExpanded} timeout="auto" unmountOnExit>
                      <Box
                        sx={{
                          px: 2,
                          py: 1.5,
                          bgcolor: 'instrument.chrome',
                        }}
                      >
                        <CrashDetail crash={crash} />
                      </Box>
                    </Collapse>
                  </TableCell>
                </TableRow>
              </Fragment>
            )
          })}
        </TableBody>
      </Table>
    </TableContainer>
  )
}

/**
 * Per-row expander body — renders the most recent crash / FATAL event's
 * captured stderr tail. Read-only; the full live tail is still the Logs
 * tab. Falls back to a "no crash history" placeholder for services that
 * have never crashed within the current event window.
 */
function CrashDetail({ crash }: { crash: CrashHistoryEntry | undefined }) {
  if (!crash) {
    return (
      <Typography
        sx={{
          fontSize: 12.5,
          color: workspaceText.muted,
          fontFamily: workspaceFontFamily.mono,
        }}
      >
        No crash history.
      </Typography>
    )
  }
  const headline =
    crash.type === 'ServiceFailedToStart'
      ? 'Failed to start'
      : 'Service crashed'
  return (
    <Stack spacing={1}>
      <Stack
        direction="row"
        spacing={1.5}
        alignItems="baseline"
        flexWrap="wrap"
      >
        <Typography
          sx={{
            fontSize: 12.5,
            color: workspaceRuntime.failed,
            fontWeight: 600,
            textTransform: 'uppercase',
            letterSpacing: '0.08em',
          }}
        >
          {headline}
        </Typography>
        {crash.timestamp && (
          <Typography
            sx={{
              fontSize: 11.5,
              color: workspaceText.muted,
              fontFamily: workspaceFontFamily.mono,
            }}
          >
            {crash.timestamp}
          </Typography>
        )}
        {crash.exitCode != null && (
          <Typography
            sx={{
              fontSize: 11.5,
              color: workspaceText.muted,
              fontFamily: workspaceFontFamily.mono,
            }}
          >
            exit {crash.exitCode}
          </Typography>
        )}
        {crash.spawnErr && (
          <Typography
            sx={{
              fontSize: 11.5,
              color: workspaceText.muted,
              fontFamily: workspaceFontFamily.mono,
            }}
          >
            {crash.spawnErr}
          </Typography>
        )}
      </Stack>
      {crash.stderrTailLines.length > 0 ? (
        <Box
          component="pre"
          sx={{
            m: 0,
            p: 1.25,
            bgcolor: 'grey.900',
            color: 'grey.100',
            fontFamily: workspaceFontFamily.mono,
            fontSize: 12,
            lineHeight: 1.45,
            borderRadius: 1,
            maxHeight: 300,
            overflow: 'auto',
            whiteSpace: 'pre-wrap',
            wordBreak: 'break-word',
          }}
        >
          {crash.stderrTailLines.join('\n')}
        </Box>
      ) : (
        <Typography
          sx={{
            fontSize: 12,
            color: workspaceText.muted,
            fontFamily: workspaceFontFamily.mono,
          }}
        >
          No stderr tail captured for this event.
        </Typography>
      )}
    </Stack>
  )
}
