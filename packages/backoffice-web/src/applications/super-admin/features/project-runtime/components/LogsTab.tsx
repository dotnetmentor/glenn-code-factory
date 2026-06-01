import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import {
  Alert,
  Box,
  Button,
  Chip,
  FormControl,
  IconButton,
  InputLabel,
  MenuItem,
  Select,
  Stack,
  Tab,
  Tabs,
  Tooltip,
  Typography,
  type SelectChangeEvent,
} from '@mui/material'
import ClearAllIcon from '@mui/icons-material/ClearAll'
import ContentCopyIcon from '@mui/icons-material/ContentCopy'
import PauseIcon from '@mui/icons-material/Pause'
import PlayArrowIcon from '@mui/icons-material/PlayArrow'
import type { ServiceInstance } from '@/api/queries-commands'
import type { AgentHubConnection } from '@/lib/signalr'
import type {
  LiveSupervisordSnapshotPayload,
  ServiceLogLineNotification,
} from '@/generated/signalr/Source.Features.SignalR.Contracts'
import { DaemonLogsView } from '@/applications/shared/runtime/components/DaemonLogsView'

/**
 * In-memory ring buffer cap for the per-service log viewer. The Logs tab is
 * intentionally client-only — no persistence — so we keep the most recent N
 * lines and drop older ones FIFO. 1000 lines × ~120 chars ≈ ~120 KB,
 * comfortable for a browser to keep around.
 */
const LOG_BUFFER_CAP = 1000

/** "Close enough to the bottom" tolerance for auto-scroll detection (pixels). */
const SCROLL_BOTTOM_TOLERANCE_PX = 24

type LogsSubTab = 'service' | 'daemon'

interface LogLine {
  /** ISO timestamp string (always serialised so render is cheap). */
  timestamp: string
  /** The raw log line as emitted by the daemon. */
  line: string
  /** Monotonic id so React's key prop stays stable through FIFO trims. */
  key: number
}

export interface LogsTabProps {
  /** Live AgentHub connection. May be null while the hub is connecting. */
  connection: AgentHubConnection | null
  /** Current runtime id — required for the subscribe/unsubscribe round-trip. */
  runtimeId: string | undefined
  /**
   * Services pulled from the current applied spec. Used as the fallback
   * picker source when {@code supervisordSnapshot} is null (cold-load,
   * before the daemon has pushed its first XML-RPC poll).
   */
  services: ServiceInstance[]
  /**
   * Live supervisord snapshot. When non-null its process list is the
   * canonical picker source — covers FATAL / BACKOFF services that are in
   * supervisord but not in the spec.
   */
  supervisordSnapshot: LiveSupervisordSnapshotPayload | null
  /**
   * Optional pre-selected service. Used by the cross-link from the Services
   * tab so clicking "View logs" lands the user on the right service.
   */
  initialServiceName?: string
}

/**
 * The Logs tab of the runtime drawer. Two sub-tabs:
 *
 * <ul>
 *   <li><b>Service logs</b> — on-demand live tail of a single supervised
 *       service's stdout/stderr over SignalR.</li>
 *   <li><b>Daemon logs</b> — live tail of the daemon's own
 *       stdout/stderr, super-admin gated.</li>
 * </ul>
 *
 * <p>Each sub-tab owns its own ring buffer + subscription lifecycle. Switching
 * sub-tabs unsubscribes from the inactive one — daemon log lines aren't
 * meaningful in the background, and keeping the SignalR group joined while
 * the user is reading service logs wastes bandwidth.</p>
 */
export function LogsTab({
  connection,
  runtimeId,
  services,
  supervisordSnapshot,
  initialServiceName,
}: LogsTabProps) {
  const [subTab, setSubTab] = useState<LogsSubTab>('service')

  return (
    <Stack spacing={1.5} sx={{ height: '100%', minHeight: 0 }}>
      <Tabs
        value={subTab}
        onChange={(_e, value: LogsSubTab) => setSubTab(value)}
        sx={{ borderBottom: 1, borderColor: 'divider', minHeight: 36 }}
      >
        <Tab value="service" label="Service logs" sx={{ minHeight: 36 }} />
        <Tab value="daemon" label="Daemon logs" sx={{ minHeight: 36 }} />
      </Tabs>

      {subTab === 'service' ? (
        <ServiceLogsPanel
          connection={connection}
          runtimeId={runtimeId}
          services={services}
          supervisordSnapshot={supervisordSnapshot}
          initialServiceName={initialServiceName}
        />
      ) : (
        <DaemonLogsView connection={connection} runtimeId={runtimeId} />
      )}
    </Stack>
  )
}

interface ServiceLogsPanelProps {
  connection: AgentHubConnection | null
  runtimeId: string | undefined
  services: ServiceInstance[]
  supervisordSnapshot: LiveSupervisordSnapshotPayload | null
  initialServiceName?: string
}

/**
 * Service-logs sub-tab. Lifecycle: when this component mounts (or the user
 * picks a different service in the dropdown) it asks the AgentHub to join
 * the matching {@code service-logs:{runtimeId}:{serviceName}} group, which
 * bounces down to the daemon and ref-counts a {@code tail -F} on the
 * supervisord log file. On unmount or selection change the prior group is
 * left and the daemon decrements its ref count — when the count hits zero
 * the tail is SIGTERMed. No persistence anywhere: late subscribers only see
 * lines from the moment they joined.
 *
 * <p>The buffer is a local FIFO capped at {@link LOG_BUFFER_CAP}. Pausing
 * drops incoming lines outright rather than buffering them off-screen — the
 * intent is "let me read what's on the screen right now without it moving",
 * not "queue everything I missed".</p>
 */
function ServiceLogsPanel({
  connection,
  runtimeId,
  services,
  supervisordSnapshot,
  initialServiceName,
}: ServiceLogsPanelProps) {
  // Picker source: prefer the live supervisord snapshot (covers FATAL/BACKOFF
  // services that may exist in supervisord but not in the spec); fall back to
  // the spec's service list while the snapshot is loading.
  const serviceNames = useMemo(() => {
    if (
      supervisordSnapshot &&
      supervisordSnapshot.processes.length > 0
    ) {
      // RUNNING first, then by state alphabetical, then by name.
      const ranked = supervisordSnapshot.processes
        .map((p) => ({
          name: p.name,
          state: (p.state ?? 'UNKNOWN').toUpperCase(),
        }))
        .sort((a, b) => {
          const ka = a.state === 'RUNNING' ? '0' : `1_${a.state}`
          const kb = b.state === 'RUNNING' ? '0' : `1_${b.state}`
          if (ka !== kb) return ka.localeCompare(kb)
          return a.name.localeCompare(b.name)
        })
      // Dedupe by name (defensive — supervisord shouldn't ship two processes
      // with the same name, but the picker should still survive it).
      const seen = new Set<string>()
      const out: string[] = []
      for (const p of ranked) {
        if (seen.has(p.name)) continue
        seen.add(p.name)
        out.push(p.name)
      }
      return out
    }
    return services.map((s) => s.name).sort((a, b) => a.localeCompare(b))
  }, [supervisordSnapshot, services])

  // Pick a sensible default service:
  //   1. The initialServiceName from the cross-link, if it exists in the list.
  //   2. Otherwise the first service in display order.
  //   3. Otherwise undefined (renders a "no services" empty state below).
  const [selectedService, setSelectedService] = useState<string | undefined>(
    () => {
      if (initialServiceName && serviceNames.includes(initialServiceName)) {
        return initialServiceName
      }
      return serviceNames[0]
    },
  )

  // Honour an updated initialServiceName prop (e.g. the user clicked "View
  // logs" for a second service after the Logs tab was already open). Also
  // resync if the service list changes such that the current selection no
  // longer exists.
  useEffect(() => {
    if (initialServiceName && serviceNames.includes(initialServiceName)) {
      setSelectedService(initialServiceName)
    }
  }, [initialServiceName, serviceNames])

  useEffect(() => {
    if (selectedService && !serviceNames.includes(selectedService)) {
      setSelectedService(serviceNames[0])
    }
  }, [serviceNames, selectedService])

  const [lines, setLines] = useState<LogLine[]>([])
  const [paused, setPaused] = useState(false)
  const [autoScroll, setAutoScroll] = useState(true)
  const [subscribeError, setSubscribeError] = useState<string | null>(null)

  // Mutable refs avoid re-running the SignalR effect when paused or the
  // line counter changes — both would orphan the daemon's tail process.
  const pausedRef = useRef(paused)
  pausedRef.current = paused
  const nextKeyRef = useRef(0)

  const viewerRef = useRef<HTMLDivElement | null>(null)

  const handleSelectChange = useCallback((event: SelectChangeEvent<string>) => {
    setSelectedService(event.target.value)
    // Switching services resets the buffer — old service's tail context
    // isn't meaningful for the new service.
    setLines([])
    setAutoScroll(true)
  }, [])

  const handleClear = useCallback(() => {
    setLines([])
  }, [])

  const handleCopy = useCallback(async () => {
    const blob = lines.map((l) => `${l.timestamp} ${l.line}`).join('\n')
    if (typeof navigator !== 'undefined' && navigator.clipboard?.writeText) {
      try {
        await navigator.clipboard.writeText(blob)
      } catch (err) {
        // Clipboard API failures (e.g. in insecure contexts) shouldn't crash
        // the drawer. Log + ignore — the user can still select-and-copy.
        // eslint-disable-next-line no-console
        console.warn('[LogsTab] clipboard write failed:', err)
      }
    }
  }, [lines])

  const togglePause = useCallback(() => {
    setPaused((p) => !p)
  }, [])

  // ── SignalR subscribe / receive / unsubscribe ──────────────────────────
  //
  // The dependency list intentionally only includes connection + runtimeId
  // + selectedService. `paused` is read through a ref so toggling pause
  // doesn't churn the subscription (which would tear down + recreate the
  // daemon-side tail process — wasteful and racy).
  useEffect(() => {
    if (!connection || !runtimeId || !selectedService) return

    let cancelled = false
    let subscribed = false
    const subscribedService = selectedService
    setSubscribeError(null)

    connection
      .subscribeToServiceLogs(runtimeId, subscribedService)
      .then(() => {
        if (!cancelled) subscribed = true
      })
      .catch((err: unknown) => {
        if (cancelled) return
        // eslint-disable-next-line no-console
        console.warn('[LogsTab] subscribe failed:', err)
        setSubscribeError(
          'Failed to subscribe to live logs. Try reopening the drawer.',
        )
      })

    const unsubListener = connection.onServiceLogLine(
      (payload: ServiceLogLineNotification) => {
        if (payload.runtimeId !== runtimeId) return
        if (payload.serviceName !== subscribedService) return
        if (pausedRef.current) return

        const ts =
          typeof payload.timestamp === 'string'
            ? payload.timestamp
            : payload.timestamp.toISOString()
        const key = nextKeyRef.current++
        setLines((prev) => {
          const next = [...prev, { timestamp: ts, line: payload.line, key }]
          if (next.length > LOG_BUFFER_CAP) {
            // FIFO trim — drop oldest. slice() is fine at this size; the
            // perf budget is dominated by React reconciliation not array ops.
            next.splice(0, next.length - LOG_BUFFER_CAP)
          }
          return next
        })
      },
    )

    return () => {
      cancelled = true
      unsubListener()
      if (subscribed) {
        connection
          .unsubscribeFromServiceLogs(runtimeId, subscribedService)
          .catch(() => {
            // Best-effort teardown — the connection might already be closing.
          })
      }
    }
  }, [connection, runtimeId, selectedService])

  // ── Auto-scroll handling ───────────────────────────────────────────────
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

  if (serviceNames.length === 0) {
    return (
      <Stack spacing={1.5}>
        <Typography variant="body2" color="text.secondary">
          No services available yet.
        </Typography>
        <Typography variant="caption" color="text.secondary">
          The supervisord poll hasn't reported any processes and the current
          spec defines none. Once a service appears in either, it will show up
          here.
        </Typography>
      </Stack>
    )
  }

  return (
    <Stack spacing={1.5} sx={{ height: '100%', minHeight: 0 }}>
      <Stack
        direction="row"
        spacing={1}
        alignItems="center"
        flexWrap="wrap"
        useFlexGap
      >
        <FormControl size="small" sx={{ minWidth: 180 }}>
          <InputLabel id="logs-service-picker-label">Service</InputLabel>
          <Select
            labelId="logs-service-picker-label"
            label="Service"
            value={selectedService ?? ''}
            onChange={handleSelectChange}
          >
            {serviceNames.map((name) => (
              <MenuItem key={name} value={name}>
                {name}
              </MenuItem>
            ))}
          </Select>
        </FormControl>

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
        <Tooltip title="Copy visible lines to clipboard">
          <span>
            <IconButton
              size="small"
              onClick={handleCopy}
              disabled={lines.length === 0}
              aria-label="Copy logs to clipboard"
            >
              <ContentCopyIcon fontSize="small" />
            </IconButton>
          </span>
        </Tooltip>
        <Tooltip title="Clear log buffer">
          <span>
            <IconButton
              size="small"
              onClick={handleClear}
              disabled={lines.length === 0}
              aria-label="Clear log buffer"
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
            bgcolor: 'grey.900',
            color: 'grey.100',
            fontFamily: 'monospace',
            fontSize: 12.5,
            lineHeight: 1.45,
            whiteSpace: 'pre-wrap',
            wordBreak: 'break-word',
            p: 1.25,
            borderRadius: 1,
          }}
        >
          {lines.length === 0 ? (
            <Typography
              variant="caption"
              sx={{ color: 'grey.500', fontFamily: 'monospace' }}
            >
              {paused
                ? 'Paused. New lines are being dropped until you resume.'
                : `Waiting for ${selectedService ?? 'service'} to emit log lines…`}
            </Typography>
          ) : (
            lines.map((l) => (
              <Box key={l.key} component="div">
                {l.line}
              </Box>
            ))
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
          {lines.length} / {LOG_BUFFER_CAP} lines buffered
        </Typography>
        {paused && (
          <Chip size="small" label="Paused" color="warning" variant="outlined" />
        )}
      </Stack>
    </Stack>
  )
}
