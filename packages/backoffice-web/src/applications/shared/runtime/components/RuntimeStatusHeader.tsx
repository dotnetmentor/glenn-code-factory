import { useEffect, useMemo, useRef, useState } from 'react'
import {
  Box,
  Chip,
  IconButton,
  Popover,
  Stack,
  Tooltip,
  Typography,
} from '@mui/material'
import CloseIcon from '@mui/icons-material/Close'
import WarningAmberIcon from '@mui/icons-material/WarningAmber'
import {
  RuntimeState,
  type RuntimeEventDto,
  type RuntimeStatusResponse,
} from '@/api/queries-commands'
import {
  formatDuration,
  formatRelativeTime,
} from '@/applications/super-admin/features/project-runtime/utils/runtimeEventDisplay'
import { isDaemonMidBootConnected } from '@/applications/shared/runtime/runtimeDaemonConnectivity'
import {
  workspaceRuntime,
  workspaceText,
} from '@/applications/workspace/shared/designTokens'
import { IdChip, RuntimePill } from '@/applications/workspace/shared/primitives'
import { BootHistoryStrip } from './BootHistoryStrip'
import { RuntimeDegradedBanner } from './RuntimeDegradedBanner'

export interface RuntimeStatusHeaderProps {
  /**
   * Branch-scoped runtime status row. Drives the state chip, time-in-state
   * caption, respawn pill, and heartbeat freshness caption. May be {@code
   * undefined} during the initial fetch — the header renders "Unknown" in
   * that case rather than blanking out.
   */
  status: RuntimeStatusResponse | undefined
  /**
   * Runtime id for the current project, when known. Rendered as a compact
   * mono {@link IdChip} after the "RUNTIME" caption (the prototype's
   * {@code DrawerHeader} treatment). Omit on surfaces that don't have the id
   * resolved yet — the chip simply isn't shown and the caption + pill carry
   * the row on their own.
   */
  runtimeId?: string
  /**
   * Latest supervisord snapshot from {@code useRuntimeEventStream}. Used by
   * {@link derivePhase} to flip the Online phase between "Healthy" and
   * "Crashlooping" based on whether any individual service is FATAL /
   * BACKOFF / EXITED.
   */
  supervisordSnapshot:
    | { sampledAt: string | Date; processes: Array<{ name: string; state: string }> }
    | null
  /**
   * Reverse-chrono runtime event feed from {@code useRuntimeEventStream}. When
   * provided, the header inspects this list for {@code RuntimeFlyDriftDetected}
   * events occurring inside the last 5 minutes and renders an amber drift
   * banner above the status row. Pass {@code undefined} on surfaces that
   * don't have a live event stream wired in — the banner simply stays hidden.
   */
  events?: RuntimeEventDto[]
  /**
   * SignalR liveness for the runtime event stream that feeds this header.
   * Surfaces a small "Live"/"Offline" pill on the right edge of the row.
   * Omit on surfaces with no stream wired in — the pill stays hidden.
   *
   * <p>Accepts {@code 'reconnecting'} so callers can light the pill amber
   * once the stream hook exposes reconnect cadence; today only boolean is
   * passed through.</p>
   */
  isLive?: boolean | 'reconnecting'
  /**
   * Optional close handler. When omitted the close icon is not rendered —
   * lets the panel use of this component skip the drawer-style close button
   * while the super-admin drawer keeps it.
   */
  onClose?: () => void
  /**
   * Fired when the operator clicks "Let agent fix it" on the degraded-spec
   * banner. The banner only surfaces when {@code status.specHealth ===
   * 'Degraded'}. The owner of this prop holds the repair mutation + the status
   * query key to invalidate on success. Omit on surfaces that can't dispatch a
   * repair — the banner still renders (read-only) but its button stays inert.
   */
  onRepair?: () => void
  /**
   * True while the repair dispatch from {@link onRepair} is in flight. Disables
   * the banner button and swaps its label to "Agent is working on it…".
   */
  isRepairing?: boolean
}

/**
 * In-flight states where the runtime is doing work but isn't fully Online
 * yet. The header shows an animated phase + elapsed counter in these states
 * ("Installing... 23s") instead of the steady-state "Running for 2m" copy.
 */
const IN_FLIGHT_STATES: ReadonlySet<RuntimeState> = new Set([
  RuntimeState.Pending,
  RuntimeState.Booting,
  RuntimeState.Bootstrapping,
  RuntimeState.Waking,
])

/**
 * Drift-banner freshness window. Any {@code RuntimeFlyDriftDetected} event
 * with an {@code occurredAt}/{@code timestamp} newer than this is treated as
 * "current drift" and surfaces the amber banner. 5 minutes is short enough to
 * stay correlated with the active failure mode and long enough to span the
 * supervisord-snapshot polling cadence + the human eye-blink between tabs.
 */
const DRIFT_BANNER_WINDOW_MS = 5 * 60 * 1000

/**
 * Drawer / panel header: state chip, phase + time-in-state, respawn pill,
 * last heartbeat caption, optional close button. When recent Fly drift
 * events are present in {@code events}, an amber banner with a payload
 * popover renders above the status row.
 *
 * <p>Header lines:
 * <ul>
 *   <li><b>Drift banner</b> (when applicable) — single-line amber strip
 *       "Fly state drift detected · {relative}" with a {@code WarningAmberIcon}.
 *       Clickable; opens a popover with the latest drift event's payload
 *       pretty-printed. Hides itself when no drift events have arrived in
 *       the last 5 minutes.</li>
 *   <li><b>State chip</b> — coloured by {@link mapState} (Pending/Running/
 *       Failed/Crashed/Booting/Bootstrapping). Reads "Unknown" only when no
 *       status row has been fetched yet (during the initial drawer open).</li>
 *   <li><b>Phase + elapsed</b> — derives a human phase from
 *       {@link RuntimeState} + the supervisord snapshot:
 *       <ul>
 *         <li>Bootstrapping → "Installing"</li>
 *         <li>Booting → "Starting services"</li>
 *         <li>Online + all services RUNNING → "Healthy"</li>
 *         <li>Online + any service FATAL/BACKOFF → "Crashlooping"</li>
 *         <li>Crashed / Failed → mapState's label as-is</li>
 *       </ul>
 *       For in-flight states (Pending/Booting/Bootstrapping/Waking) the
 *       elapsed counter ticks every second ("Installing... 23s"). For
 *       terminal states the counter freezes at the steady-state copy
 *       ("Running for 2m 14s" / "Failed 5m ago").</li>
 *   <li><b>Respawn pill</b> — amber chip "Respawned 3×" when
 *       {@code respawnRetries > 0}.</li>
 *   <li><b>Heartbeat caption</b> — "Last seen 4s ago" ticking every
 *       second; goes amber at >30s, red at >60s. Tooltip shows absolute
 *       timestamp. When {@code lastHeartbeatAt} is null the caption renders
 *       an explicit red "Heartbeat: never" indicator with a tooltip
 *       explaining the daemon hasn't connected yet — never blank.</li>
 * </ul></p>
 */
export function RuntimeStatusHeader({
  status,
  runtimeId,
  supervisordSnapshot,
  events,
  isLive,
  onClose,
  onRepair,
  isRepairing,
}: RuntimeStatusHeaderProps) {
  // Compute everything off a single ticking clock so the time-in-state, the
  // live phase counter, and the heartbeat caption all advance in lockstep.
  // 1s cadence is fine — header is a single re-render per second while open.
  const [now, setNow] = useState(() => new Date())
  useEffect(() => {
    const interval = setInterval(() => setNow(new Date()), 1000)
    return () => clearInterval(interval)
  }, [])

  // Only the label is consumed now (terminal-state time caption: "Failed 5m
  // ago"). The state chip's color is owned by {@link RuntimePill}, which
  // derives its own palette from the runtime state.
  const { label } = useMemo(() => mapState(status?.state), [status?.state])

  const phase = useMemo(
    () => derivePhase(status?.state, supervisordSnapshot),
    [status?.state, supervisordSnapshot],
  )

  // Elapsed time in the current state. For in-flight states this is the live
  // boot/install counter; for terminal states it's the steady "Running for X"
  // / "Failed Xm ago" line.
  const elapsed = useMemo(() => {
    if (!status?.stateChangedAt) return null
    const since = new Date(status.stateChangedAt)
    if (Number.isNaN(since.getTime())) return null
    const diffMs = now.getTime() - since.getTime()
    return diffMs >= 0 ? diffMs : 0
  }, [status?.stateChangedAt, now])

  const isInFlight = status?.state ? IN_FLIGHT_STATES.has(status.state) : false

  // Time-in-state caption — phrasing differs per state bucket.
  // "Installing... 23s" while in-flight; "Running for 2m" once Online;
  // "Failed 5m ago" once terminal.
  const timeInStateCaption = useMemo(() => {
    if (elapsed == null) return null
    const dur = formatDuration(elapsed)
    if (!dur) return null
    if (isInFlight) return `${phase}... ${dur}`
    if (status?.state === RuntimeState.Online) return `Running for ${dur}`
    if (
      status?.state === RuntimeState.Failed ||
      status?.state === RuntimeState.Crashed
    ) {
      return `${label} ${dur} ago`
    }
    return `${phase} · ${dur}`
  }, [elapsed, isInFlight, phase, status?.state, label])

  // Heartbeat freshness — text + tint. Goes amber >30s, red >60s. When the
  // daemon has never connected ({@code lastHeartbeatAt} null) we surface
  // an explicit red "Heartbeat: never" indicator with an explanatory tooltip
  // so the operator immediately knows nothing is reporting in — much louder
  // than a blank slot. Stays in this state until the first heartbeat lands.
  const heartbeat = useMemo(() => {
    if (!status?.lastHeartbeatAt) {
      // Only meaningful once a status row has been fetched. While the row is
      // still loading we render nothing rather than flashing "never".
      if (!status) return null
      if (isDaemonMidBootConnected(status)) {
        return {
          text: 'Connected (bootstrapping)',
          tint: 'normal' as const,
          tooltip:
            'Daemon is on RuntimeHub and installing. Heartbeats start after bootstrap finishes.',
          showDot: true,
        }
      }
      return {
        text: 'Heartbeat: never',
        tint: 'error' as const,
        tooltip: 'The daemon has not connected to this runtime yet.',
        showDot: true,
      }
    }
    const beat = new Date(status.lastHeartbeatAt)
    if (Number.isNaN(beat.getTime())) return null
    const ageMs = now.getTime() - beat.getTime()
    const ageSec = Math.max(0, Math.floor(ageMs / 1000))
    let text: string
    if (ageSec < 60) text = `Last seen ${ageSec}s ago`
    else if (ageSec < 3600) text = `Last seen ${Math.floor(ageSec / 60)}m ago`
    else text = 'Last seen stale'
    let tint: 'normal' | 'warn' | 'error' = 'normal'
    if (ageMs > 60_000) tint = 'error'
    else if (ageMs > 30_000) tint = 'warn'
    return {
      text,
      tint,
      tooltip: beat.toLocaleString(),
      showDot: false,
    }
  }, [status, now])

  const respawnRetries = status?.respawnRetries ?? 0

  // Most-recent fly-drift event within the freshness window. Events come in
  // newest-first per useRuntimeEventStream so a linear find is fine; we
  // intentionally re-evaluate against {@link now} so the banner auto-hides
  // when the latest event ages past the 5-minute window without requiring
  // any new event push.
  const recentDrift = useMemo(() => {
    if (!events || events.length === 0) return null
    const cutoff = now.getTime() - DRIFT_BANNER_WINDOW_MS
    for (const ev of events) {
      if (ev.type !== 'RuntimeFlyDriftDetected') continue
      const ts = typeof ev.timestamp === 'string' ? new Date(ev.timestamp) : ev.timestamp
      if (!(ts instanceof Date) || Number.isNaN(ts.getTime())) continue
      if (ts.getTime() < cutoff) {
        // Events are newest-first; everything after this is older.
        return null
      }
      return { event: ev, occurredAt: ts }
    }
    return null
  }, [events, now])

  const driftAnchorRef = useRef<HTMLButtonElement | null>(null)
  const [driftPopoverOpen, setDriftPopoverOpen] = useState(false)
  // Auto-close the popover if the drift banner ages out while it's still
  // open — avoid showing a popover anchored to a vanished button.
  useEffect(() => {
    if (!recentDrift && driftPopoverOpen) setDriftPopoverOpen(false)
  }, [recentDrift, driftPopoverOpen])

  const driftPayloadJson = useMemo(() => {
    if (!recentDrift) return ''
    try {
      const raw = recentDrift.event.payload
      const parsed = typeof raw === 'string' ? JSON.parse(raw) : raw
      return JSON.stringify(parsed, null, 2)
    } catch {
      // Bad JSON — fall back to the raw string so the operator at least
      // sees what the daemon emitted, even if it's not pretty.
      return typeof recentDrift.event.payload === 'string'
        ? recentDrift.event.payload
        : ''
    }
  }, [recentDrift])

  return (
    <Box sx={{ flexShrink: 0 }}>
      <RuntimeDegradedBanner
        status={status}
        onRepair={onRepair ?? (() => {})}
        isRepairing={isRepairing ?? false}
      />
      {recentDrift && (
        <Box
          component="button"
          type="button"
          ref={driftAnchorRef}
          onClick={() => setDriftPopoverOpen((v) => !v)}
          aria-label="Show Fly state drift details"
          sx={{
            display: 'flex',
            alignItems: 'center',
            gap: 1,
            width: '100%',
            border: 0,
            cursor: 'pointer',
            textAlign: 'left',
            px: 2,
            py: 0.75,
            // Warning tint — semantic orange, calm alert without screaming.
            backgroundColor: 'rgba(255, 149, 0, 0.10)',
            borderLeft: `3px solid ${workspaceRuntime.booting}`,
            borderBottom: '1px solid rgba(255, 149, 0, 0.30)',
            color: workspaceRuntime.booting,
            transition: 'background-color 160ms ease',
            '&:hover': {
              backgroundColor: 'rgba(255, 149, 0, 0.16)',
            },
            '&:focus-visible': {
              outline: `2px solid ${workspaceRuntime.booting}`,
              outlineOffset: -2,
            },
          }}
        >
          <WarningAmberIcon sx={{ fontSize: 16, color: workspaceRuntime.booting }} />
          <Typography
            variant="caption"
            sx={{
              fontSize: '0.8125rem',
              fontWeight: 500,
              color: workspaceRuntime.booting,
              flex: 1,
              minWidth: 0,
              overflow: 'hidden',
              textOverflow: 'ellipsis',
              whiteSpace: 'nowrap',
            }}
          >
            Fly state drift detected
            <Box component="span" sx={{ mx: 0.5, color: 'rgba(122, 85, 39, 0.6)' }}>
              ·
            </Box>
            {formatRelativeTime(recentDrift.occurredAt, now)}
          </Typography>
        </Box>
      )}
      <Popover
        open={driftPopoverOpen && !!recentDrift}
        anchorEl={driftAnchorRef.current}
        onClose={() => setDriftPopoverOpen(false)}
        anchorOrigin={{ vertical: 'bottom', horizontal: 'left' }}
        transformOrigin={{ vertical: 'top', horizontal: 'left' }}
        PaperProps={{
          sx: {
            maxWidth: 560,
            maxHeight: 360,
            overflow: 'auto',
            p: 1.5,
            fontFamily: 'ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace',
          },
        }}
      >
        <Typography
          variant="caption"
          sx={{
            display: 'block',
            mb: 0.75,
            color: 'text.secondary',
            fontFamily: 'inherit',
          }}
        >
          RuntimeFlyDriftDetected ·{' '}
          {recentDrift ? new Date(recentDrift.event.timestamp).toLocaleString() : ''}
        </Typography>
        <Box
          component="pre"
          sx={{
            m: 0,
            fontSize: '0.75rem',
            lineHeight: 1.5,
            whiteSpace: 'pre-wrap',
            wordBreak: 'break-word',
          }}
        >
          {driftPayloadJson || '(empty payload)'}
        </Box>
      </Popover>
      <Stack
        direction="row"
        alignItems="center"
        justifyContent="space-between"
        sx={{
          flexShrink: 0,
          px: 2,
          py: 1.5,
          borderBottom: 1,
          borderColor: 'divider',
          gap: 1,
        }}
      >
        <Stack direction="row" spacing={1.25} alignItems="center" flexWrap="wrap">
          <Typography
            component="span"
            sx={{
              fontSize: '0.65625rem',
              fontWeight: 600,
              letterSpacing: '0.12em',
              textTransform: 'uppercase',
              color: workspaceText.faint,
              lineHeight: 1,
            }}
          >
            Runtime
          </Typography>
          {runtimeId && <IdChip title="Runtime id">{runtimeId}</IdChip>}
          <RuntimePill state={status?.state ?? null} subLabel={phase} />
          {timeInStateCaption && (
            <Typography
              variant="caption"
              color="text.secondary"
              sx={{
                // Subtle pulse on the in-flight counter so a stuck boot still
                // visibly ticks even if the elapsed string changes slowly
                // (e.g. when it crosses the 60s→1m formatting boundary).
                animation: isInFlight ? 'rd-pulse 1.6s ease-in-out infinite' : undefined,
                '@keyframes rd-pulse': {
                  '0%, 100%': { opacity: 1 },
                  '50%': { opacity: 0.55 },
                },
                // Respect the OS reduced-motion preference: hold the caption at
                // full opacity instead of running the breathing pulse.
                '@media (prefers-reduced-motion: reduce)': {
                  animation: 'none',
                  opacity: 1,
                },
              }}
            >
              {timeInStateCaption}
            </Typography>
          )}
          {respawnRetries > 0 && (
            <Tooltip
              title="Supervisor has respawned this runtime after a crash this many times since its last successful boot."
            >
              <Chip
                size="small"
                label={`Respawned ${respawnRetries}×`}
                color="warning"
                variant="outlined"
              />
            </Tooltip>
          )}
          <BootHistoryStrip events={events} />
          {heartbeat && (
            <Tooltip title={heartbeat.tooltip}>
              <Stack
                direction="row"
                spacing={0.5}
                alignItems="center"
                component="span"
              >
                {heartbeat.showDot && (
                  <Box
                    aria-hidden
                    sx={{
                      width: 8,
                      height: 8,
                      borderRadius: '50%',
                      // Muted red — same vocabulary as the failed/crashed
                      // states. We use a calmer rust here rather than MUI's
                      // alarm red so the banner stays readable next to it.
                      backgroundColor: workspaceRuntime.failed,
                      display: 'inline-block',
                      flexShrink: 0,
                    }}
                  />
                )}
                <Typography
                  variant="caption"
                  component="span"
                  color={
                    heartbeat.tint === 'error'
                      ? 'error.main'
                      : heartbeat.tint === 'warn'
                      ? 'warning.main'
                      : 'text.secondary'
                  }
                >
                  {heartbeat.text}
                </Typography>
              </Stack>
            </Tooltip>
          )}
        </Stack>
        <Stack direction="row" alignItems="center" spacing={0.5}>
          {/* TODO: Surface 'reconnecting' state when useRuntimeEventStream
              exposes reconnect cadence. Today only boolean isLive arrives. */}
          <ConnectionPill isLive={isLive} />
          {onClose && (
            <IconButton
              size="small"
              aria-label="Close runtime drawer"
              onClick={onClose}
            >
              <CloseIcon fontSize="small" />
            </IconButton>
          )}
        </Stack>
      </Stack>
    </Box>
  )
}

/**
 * Tiny "Live / Reconnecting / Offline" pill anchored to the right edge of the
 * status row. Surfaces the SignalR connection state for the runtime event
 * stream that feeds this header. Hidden when {@code isLive} is undefined so
 * surfaces without a stream don't render an empty pill.
 *
 * <p>Hidden on narrow viewports (xs) — the row already runs hot and the pill
 * is the second-tier indicator after the heartbeat caption (which carries
 * its own freshness colour).</p>
 */
function ConnectionPill({
  isLive,
}: {
  isLive: boolean | 'reconnecting' | undefined
}) {
  if (isLive === undefined) return null

  const state: 'live' | 'reconnecting' | 'offline' =
    isLive === true ? 'live' : isLive === 'reconnecting' ? 'reconnecting' : 'offline'

  const config = {
    live: {
      label: 'Live',
      color: workspaceRuntime.online,
      tooltip: 'SignalR connected — receiving live runtime events.',
    },
    reconnecting: {
      label: 'Reconnecting…',
      color: workspaceRuntime.booting,
      tooltip: 'SignalR is re-establishing the connection.',
    },
    offline: {
      label: 'Offline',
      color: workspaceRuntime.failed,
      tooltip: 'SignalR is disconnected — events are not flowing.',
    },
  }[state]

  return (
    <Tooltip title={config.tooltip}>
      <Stack
        direction="row"
        alignItems="center"
        spacing={0.5}
        component="span"
        sx={{
          display: { xs: 'none', sm: 'inline-flex' },
          px: 0.75,
          py: 0.125,
          mr: 1,
          borderRadius: 999,
          border: '1px solid rgba(0, 0, 0, 0.10)',
          backgroundColor: 'transparent',
          // Quietly co-exist with the close button — same vertical rhythm
          // as the inline chips on the left.
          lineHeight: 1,
        }}
      >
        <Box
          aria-hidden
          sx={{
            width: 10,
            height: 10,
            borderRadius: '50%',
            backgroundColor: config.color,
            flexShrink: 0,
          }}
        />
        <Typography
          variant="caption"
          component="span"
          sx={{
            fontSize: 11,
            color: 'text.secondary',
            letterSpacing: '-0.005em',
          }}
        >
          {config.label}
        </Typography>
      </Stack>
    </Tooltip>
  )
}

/**
 * Map a {@link RuntimeState} to the header chip's label + MUI palette key.
 * Each state gets its own label so the chip mirrors the backend state
 * machine 1:1 — earlier versions collapsed Booting / Bootstrapping / Waking
 * into a single "Booting" pill, which contradicted the data the timeline
 * showed below. Now {@code Bootstrapping} reads "Bootstrapping", and the
 * intermediate phase string in {@link derivePhase} keeps the "Installing /
 * Starting services" affordance for richer surfaces.
 */
function mapState(state: RuntimeState | undefined): {
  label: string
  color: 'success' | 'error' | 'warning' | 'default'
} {
  if (!state) return { label: 'Unknown', color: 'default' }
  switch (state) {
    case RuntimeState.Online:
      return { label: 'Running', color: 'success' }
    case RuntimeState.Failed:
      return { label: 'Failed', color: 'error' }
    case RuntimeState.Crashed:
      return { label: 'Crashed', color: 'error' }
    case RuntimeState.Booting:
      return { label: 'Booting', color: 'warning' }
    case RuntimeState.Bootstrapping:
      return { label: 'Bootstrapping', color: 'warning' }
    case RuntimeState.Waking:
      return { label: 'Waking', color: 'warning' }
    case RuntimeState.Pending:
      return { label: 'Pending', color: 'default' }
    default:
      return { label: String(state), color: 'default' }
  }
}

/**
 * Human-readable phase label that combines the runtime state with a peek at
 * the live supervisord snapshot. The state chip already shows the underlying
 * lifecycle; this phase string surfaces the "what's it doing right now"
 * answer (Installing / Starting services / Healthy / Crashlooping) without
 * forcing the user to flip to Services.
 */
function derivePhase(
  state: RuntimeState | undefined,
  snapshot: RuntimeStatusHeaderProps['supervisordSnapshot'],
): string {
  if (!state) return 'Initializing'
  if (state === RuntimeState.Bootstrapping) return 'Installing'
  if (state === RuntimeState.Booting) return 'Starting services'
  if (state === RuntimeState.Waking) return 'Waking'
  if (state === RuntimeState.Pending) return 'Pending'
  if (state === RuntimeState.Suspended) return 'Suspended'
  if (state === RuntimeState.Suspending) return 'Suspending'
  if (state === RuntimeState.Deleting) return 'Deleting'
  if (state === RuntimeState.Deleted) return 'Deleted'
  if (state === RuntimeState.Failed) return 'Failed'
  if (state === RuntimeState.Crashed) return 'Crashed'
  // Online: peek at the supervisord snapshot if available — any service in
  // FATAL / BACKOFF / EXITED means we're crashlooping even though the
  // runtime row says Online.
  if (state === RuntimeState.Online) {
    if (snapshot && snapshot.processes.length > 0) {
      const sick = snapshot.processes.some((p) => {
        const s = p.state?.toUpperCase()
        return s === 'FATAL' || s === 'BACKOFF' || s === 'EXITED'
      })
      if (sick) return 'Crashlooping'
    }
    return 'Healthy'
  }
  return String(state)
}
