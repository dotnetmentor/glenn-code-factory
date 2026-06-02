import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import {
  Box,
  Chip,
  FormControl,
  IconButton,
  MenuItem,
  Select,
  Stack,
  Tab,
  Tabs,
  Tooltip,
  Typography,
  type SelectChangeEvent,
} from '@mui/material'
import CloseIcon from '@mui/icons-material/Close'
import ContentCopyIcon from '@mui/icons-material/ContentCopy'
import DeleteOutlineIcon from '@mui/icons-material/DeleteOutline'
import PauseIcon from '@mui/icons-material/Pause'
import PlayArrowIcon from '@mui/icons-material/PlayArrow'
import { type RuntimeStatusResponse } from '@/api/queries-commands'
import { useAgentHub } from '@/lib/signalr'
import type { ServiceLogLineNotification } from '@/generated/signalr/Source.Features.SignalR.Contracts'
import {
  workspaceAccent,
  workspaceFontFamily,
  workspaceRuntime,
  workspaceText,
} from '@/applications/workspace/shared/designTokens'
import { useRuntimeEventStream } from '@/applications/super-admin/features/project-runtime/hooks/useRuntimeEventStream'
import { useBranchRuntimeStatus } from '@/applications/shared/runtime/hooks/useBranchRuntimeStatus'
import { LogViewer, type LogViewerLine } from './LogViewer'
import { ServicesTabContainer } from './ServicesTabContainer'
import { ActivityTab } from './ActivityTab'
import { FlyMachineTab } from './FlyMachineTab'
import { RuntimeStatusHeader } from './RuntimeStatusHeader'
import { SysstatsView } from './SysstatsView'
import { DaemonLogsView } from './DaemonLogsView'
import { RuntimeTab } from './RuntimeTab'
import {
  DAEMON_LOGS_UNAVAILABLE_MESSAGE,
  isDaemonHubLikelyUnreachable,
} from '../runtimeDaemonConnectivity'
import {
  useRuntimeDebugPanel,
  type RuntimeDebugPanelView,
} from '../context/RuntimeDebugPanelContext'
import { useAuth } from '@/auth'
import { ApplicationRoles } from '@/applications/shared/constants/roles'

/** In-memory ring buffer cap. */
const LOG_BUFFER_CAP = 1000
/** Min + max heights for the resizable panel. */
const MIN_HEIGHT = 160
const MAX_HEIGHT = 600
/** Fallback when viewport height is unavailable (SSR). */
const SSR_DEFAULT_HEIGHT = 400

function resolveInitialPanelHeight(): number {
  if (typeof window === 'undefined') return SSR_DEFAULT_HEIGHT
  const stored = window.localStorage.getItem(HEIGHT_STORAGE_KEY)
  const parsed = stored ? Number.parseInt(stored, 10) : NaN
  if (Number.isFinite(parsed)) {
    return Math.min(MAX_HEIGHT, Math.max(MIN_HEIGHT, parsed))
  }
  const halfViewport = Math.round(window.innerHeight * 0.5)
  return Math.min(MAX_HEIGHT, Math.max(MIN_HEIGHT, halfViewport))
}
/** Match the panel's CSS transition so post-close unmount runs after the animation. */
const ANIMATION_DURATION_MS = 200
/** Fade duration when swapping between the three views — gentle enough to feel calm. */
const VIEW_FADE_MS = 150
/** localStorage key for the user's last-chosen height. */
const HEIGHT_STORAGE_KEY = 'workspace.runtimeLogsPanel.height'

export interface RuntimeLogsPanelProps {
  projectId: string
  /**
   * Branch the current workspace canvas is pinned to. Used to call the
   * branch-scoped runtime status endpoint that powers the new header strip
   * (state chip, time-in-state, heartbeat) and the Spec view's apply-history
   * section. Empty string is treated as "no branch yet" — the header
   * gracefully renders an "Unknown" chip until the route resolves.
   */
  branchId: string
  /** Whether the panel should be visible. The component handles its own exit. */
  open: boolean
  /** Optional service to pre-select when the panel opens. */
  initialServiceName?: string
  /** Called when the user clicks the close affordance in the panel header. */
  onClose: () => void
}

/**
 * Bottom-anchored multi-view debug panel that pushes the chat canvas up.
 * Hosts three surfaces over one shared chrome strip:
 * <ol>
 *   <li><strong>Logs</strong> — live service log tail (the original tenant).</li>
 *   <li><strong>Services</strong> — compact services table with cross-link to Logs.</li>
 *   <li><strong>Timeline</strong> — structured runtime event timeline with filters.</li>
 * </ol>
 *
 * <p>The active view is owned by {@code RuntimeDebugPanelContext} (so it
 * persists across reloads) and the segmented switcher in the header flips
 * between them with a short fade. State for each view is preserved across
 * switches — i.e. flipping Logs → Services → Logs returns to the same
 * service + same buffer.</p>
 *
 * <p>Lifecycle: SignalR subscribe/unsubscribe for the log tail is tied to the
 * {@code (runtimeId, selectedService)} pair AND to whether the Logs view is
 * currently active. While {@code open === false} we keep the component
 * mounted long enough for the height/opacity transition to finish, then
 * unmount the inner content (cleaning up the subscription) so the daemon's
 * tail process can ref-count down.</p>
 */
export function RuntimeLogsPanel({
  projectId,
  branchId,
  open,
  initialServiceName,
  onClose,
}: RuntimeLogsPanelProps) {
  // Live retainer for the inner content. We delay the actual teardown by
  // ANIMATION_DURATION_MS so the closing transition can play out cleanly.
  const [mounted, setMounted] = useState(open)
  useEffect(() => {
    if (open) {
      setMounted(true)
      return
    }
    const id = setTimeout(() => setMounted(false), ANIMATION_DURATION_MS)
    return () => clearTimeout(id)
  }, [open])

  // ── Resizable height (persisted) ────────────────────────────────────────
  const [height, setHeight] = useState<number>(resolveInitialPanelHeight)

  const dragStateRef = useRef<{ startY: number; startHeight: number } | null>(null)

  const onHandleMouseDown = useCallback(
    (e: React.MouseEvent<HTMLDivElement>) => {
      dragStateRef.current = { startY: e.clientY, startHeight: height }
      const onMove = (ev: MouseEvent) => {
        if (!dragStateRef.current) return
        // Dragging up (smaller clientY) should grow the panel — invert delta.
        const delta = dragStateRef.current.startY - ev.clientY
        const next = Math.min(
          MAX_HEIGHT,
          Math.max(MIN_HEIGHT, dragStateRef.current.startHeight + delta),
        )
        setHeight(next)
      }
      const onUp = () => {
        document.removeEventListener('mousemove', onMove)
        document.removeEventListener('mouseup', onUp)
        const finalHeight = dragStateRef.current?.startHeight ?? height
        dragStateRef.current = null
        if (typeof window !== 'undefined') {
          try {
            window.localStorage.setItem(
              HEIGHT_STORAGE_KEY,
              String(Math.round(finalHeight)),
            )
          } catch {
            // localStorage might be unavailable in incognito / quota-locked
            // contexts — best-effort persistence only.
          }
        }
      }
      document.addEventListener('mousemove', onMove)
      document.addEventListener('mouseup', onUp)
      e.preventDefault()
    },
    [height],
  )

  useEffect(() => {
    if (typeof window === 'undefined') return
    try {
      window.localStorage.setItem(HEIGHT_STORAGE_KEY, String(Math.round(height)))
    } catch {
      // Quota / availability errors are non-fatal.
    }
  }, [height])

  if (!mounted) {
    return null
  }

  return (
    <Box
      role="region"
      aria-label="Runtime debug panel"
      sx={{
        flexShrink: 0,
        display: 'flex',
        flexDirection: 'column',
        height: open ? `${height}px` : 0,
        opacity: open ? 1 : 0,
        overflow: 'hidden',
        borderTop: open ? 1 : 0,
        borderColor: 'instrument.hairline',
        backgroundColor: 'instrument.canvas',
        transition: `height ${ANIMATION_DURATION_MS}ms ease, opacity ${ANIMATION_DURATION_MS}ms ease`,
      }}
    >
      {/* Resize handle — 3px tall strip with ns-resize cursor at the top edge. */}
      <Box
        role="separator"
        aria-orientation="horizontal"
        aria-label="Resize debug panel"
        onMouseDown={onHandleMouseDown}
        sx={{
          height: 3,
          flexShrink: 0,
          cursor: 'ns-resize',
          backgroundColor: 'transparent',
          transition: 'background-color 120ms ease',
          '&:hover': { backgroundColor: 'instrument.hairline' },
        }}
      />

      <RuntimeDebugPanelBody
        key={`${projectId}-${branchId}`}
        projectId={projectId}
        branchId={branchId}
        initialServiceName={initialServiceName}
        onClose={onClose}
      />
    </Box>
  )
}

interface RuntimeDebugPanelBodyProps {
  projectId: string
  branchId: string
  initialServiceName: string | undefined
  onClose: () => void
}

/**
 * Inner body — header (segmented switcher + contextual controls) over the
 * three view bodies (Logs / Services / Timeline). All three views are
 * kept mounted via opacity + visibility toggles so flipping between them is
 * an instant fade rather than a re-fetch; the heavy SignalR subscription
 * for log tailing is the one exception and only attaches while Logs is
 * active.
 */
function RuntimeDebugPanelBody({
  projectId,
  branchId,
  initialServiceName,
  onClose,
}: RuntimeDebugPanelBodyProps) {
  const debugPanel = useRuntimeDebugPanel()
  const activeView = debugPanel.activeView
  const auth = useAuth()
  const isSuperAdmin = !!auth.user?.roles?.includes(ApplicationRoles.SuperAdmin)

  // If the user lost their SuperAdmin role between sessions but their
  // persisted view is one of the super-admin-only views (fly, spec), bounce
  // them back to Logs the first time they open the panel — the switcher
  // won't render those tabs anymore.
  useEffect(() => {
    if ((activeView === 'fly' || activeView === 'spec') && !isSuperAdmin) {
      debugPanel.setActiveView('logs')
    }
  }, [activeView, isSuperAdmin, debugPanel])

  // Track panel width so the header strip can drop the phase label below a
  // ~600px threshold. ResizeObserver is the right primitive — viewport width
  // doesn't match panel width when the chat sidebar takes a chunk on the left.
  const bodyRef = useRef<HTMLDivElement | null>(null)
  const [panelWidth, setPanelWidth] = useState<number>(() =>
    typeof window === 'undefined' ? 1024 : window.innerWidth,
  )
  useEffect(() => {
    const el = bodyRef.current
    if (!el || typeof ResizeObserver === 'undefined') return
    const ro = new ResizeObserver((entries) => {
      const w = entries[0]?.contentRect.width
      if (typeof w === 'number') setPanelWidth(w)
    })
    ro.observe(el)
    setPanelWidth(el.clientWidth)
    return () => ro.disconnect()
  }, [])
  const isNarrow = panelWidth > 0 && panelWidth < 600

  // Branch-scoped runtime status + id (see useBranchRuntimeStatus).
  const branchRuntime = useBranchRuntimeStatus(projectId, branchId, {
    refetchInterval: 5_000,
  })
  const { status: runtimeStatus, runtimeId } = branchRuntime

  // Match the parent workspace's hub key (projectId + branchId) so this panel
  // shares the same pooled connection rather than opening a second negotiate
  // round-trip on mount.
  const { connection } = useAgentHub({
    projectId: projectId || undefined,
    branchId: branchId || undefined,
    enabled: !!projectId,
  })

  // Panel-level event stream subscription. Feeds the header (supervisord
  // snapshot drives the phase derivation) and the Sysstats view (heartbeat
  // snapshot). Mounted whenever the panel body is mounted — which already
  // tracks the open/closed lifecycle (see {@link RuntimeLogsPanel} above).
  const stream = useRuntimeEventStream({
    connection,
    runtimeId,
    status: runtimeStatus,
    enabled: !!runtimeId,
  })

  // Service picker source. The picker is strictly bound to the live
  // supervisord snapshot — when no snapshot has arrived yet the list is
  // empty and the picker renders a disabled "Waiting for first
  // supervisord snapshot…" option. Earlier versions fell back to the
  // declared spec services on cold load, which produced a confusing
  // disagreement with the Services tab ("picker shows mysql" / "Services
  // tab says no services observed yet"). Single source of truth, single
  // story.
  //
  // Sort RUNNING first, then by state alphabetical, then by name — same
  // precedence the super-admin LogsTab uses.
  const serviceNames = useMemo(() => {
    const snapshot = stream.supervisordSnapshot
    if (!snapshot || snapshot.processes.length === 0) return []
    const ranked = snapshot.processes
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
    const seen = new Set<string>()
    const out: string[] = []
    for (const p of ranked) {
      if (seen.has(p.name)) continue
      seen.add(p.name)
      out.push(p.name)
    }
    return out
  }, [stream.supervisordSnapshot])

  const hasSupervisordSnapshot = !!stream.supervisordSnapshot

  const [selectedService, setSelectedService] = useState<string | undefined>(
    () => initialServiceName,
  )

  // Honour an updated initialServiceName prop and keep the selection alive
  // when the spec's service list changes underneath us.
  useEffect(() => {
    if (initialServiceName && serviceNames.includes(initialServiceName)) {
      setSelectedService(initialServiceName)
    }
  }, [initialServiceName, serviceNames])

  useEffect(() => {
    if (!selectedService && serviceNames.length > 0) {
      setSelectedService(serviceNames[0])
    }
    if (selectedService && !serviceNames.includes(selectedService) && serviceNames.length > 0) {
      setSelectedService(serviceNames[0])
    }
  }, [serviceNames, selectedService])

  // Logs view sub-tab: Service logs vs Daemon (super-admin only). Default to
  // Daemon for super-admins so boot/debug sessions surface agent stdout first.
  type LogsSubTab = 'service' | 'daemon'
  const [logsSubTab, setLogsSubTab] = useState<LogsSubTab>('daemon')
  useEffect(() => {
    // If the user lost super-admin and the persisted sub-tab is 'daemon',
    // snap back to 'service' so they don't see an empty/forbidden surface.
    if (logsSubTab === 'daemon' && !isSuperAdmin) {
      setLogsSubTab('service')
    }
  }, [logsSubTab, isSuperAdmin])

  const [lines, setLines] = useState<LogViewerLine[]>([])
  const [paused, setPaused] = useState(false)
  const pausedRef = useRef(paused)
  pausedRef.current = paused
  const nextKeyRef = useRef(0)

  // SignalR subscription lifecycle — only attaches while Logs is the active
  // view AND the Service-logs sub-tab is selected, so flipping to
  // Services/Timeline/Sysstats/Spec or to the Daemon sub-tab drops the tail
  // and frees the daemon's ref-count.
  useEffect(() => {
    if (activeView !== 'logs') return
    if (logsSubTab !== 'service') return
    if (!connection || !runtimeId || !selectedService) return

    let cancelled = false
    let subscribed = false
    const subscribedService = selectedService

    connection
      .subscribeToServiceLogs(runtimeId, subscribedService)
      .then(() => {
        if (!cancelled) subscribed = true
      })
      .catch((err: unknown) => {
        // eslint-disable-next-line no-console
        console.warn('[RuntimeDebugPanel] subscribe failed:', err)
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
            // Best-effort teardown.
          })
      }
    }
  }, [activeView, logsSubTab, connection, runtimeId, selectedService])

  const handleServiceChange = useCallback(
    (e: SelectChangeEvent<string>) => {
      setSelectedService(e.target.value)
      setLines([]) // fresh tail for a fresh service
    },
    [],
  )

  const handleClear = useCallback(() => setLines([]), [])
  const togglePause = useCallback(() => setPaused((p) => !p), [])

  const handleCopy = useCallback(async () => {
    if (typeof navigator === 'undefined' || !navigator.clipboard?.writeText) return
    const blob = lines.map((l) => `${l.timestamp} ${l.line}`).join('\n')
    try {
      await navigator.clipboard.writeText(blob)
    } catch (err) {
      // eslint-disable-next-line no-console
      console.warn('[RuntimeDebugPanel] clipboard write failed:', err)
    }
  }, [lines])

  // Services view → Logs view cross-link. Pre-selects the service and flips
  // the active view via the context setter.
  const handleViewLogsFromServices = useCallback(
    (serviceName: string) => {
      setSelectedService(serviceName)
      setLines([])
      setLogsSubTab('service')
      debugPanel.setActiveView('logs', serviceName)
    },
    [debugPanel],
  )

  const daemonDisconnected =
    isDaemonHubLikelyUnreachable(runtimeStatus) && !!runtimeId

  return (
    <Box
      ref={bodyRef}
      sx={{
        flex: 1,
        minHeight: 0,
        display: 'flex',
        flexDirection: 'column',
        backgroundColor: 'instrument.canvas',
      }}
    >
      {/* Always-visible status strip at the very top of the panel chrome.
          State chip + phase + time-in-state + respawn pill + heartbeat
          (and inline error message on terminal states). Compact single-row
          variant — the {@link RuntimeStatusHeader} drops phase under
          ~600px panel width to avoid wrapping. */}
      <PanelStatusStrip
        status={runtimeStatus}
        runtimeId={runtimeId}
        supervisordSnapshot={stream.supervisordSnapshot}
        events={stream.events}
        isLive={stream.isLive}
        showPhase={!isNarrow}
      />

      <PanelHeader
        activeView={activeView}
        onChangeView={(view) => debugPanel.setActiveView(view)}
        onClose={onClose}
        isSuperAdmin={isSuperAdmin}
        logsControls={{
          logsSubTab,
          serviceNames,
          selectedService,
          lineCount: lines.length,
          onServiceChange: handleServiceChange,
          hasSupervisordSnapshot,
          daemonDisconnected,
          paused,
          onTogglePause: togglePause,
          onClear: handleClear,
          onCopy: handleCopy,
        }}
      />

      {/* View bodies — mounted at all times to keep state warm, faded based
          on which view is active. Logs is the heavy one (SignalR
          subscription) and only fetches while it's the active view AND the
          Service sub-tab is selected, but the wrapper itself stays put. */}
      <Box sx={{ flex: 1, minHeight: 0, position: 'relative' }}>
        <ViewLayer active={activeView === 'logs'}>
          <LogsViewBody
            isSuperAdmin={isSuperAdmin}
            subTab={logsSubTab}
            onChangeSubTab={setLogsSubTab}
            lines={lines}
            paused={paused}
            selectedService={selectedService}
            connection={connection}
            runtimeId={runtimeId}
            runtimeStatus={runtimeStatus}
            daemonDisconnected={daemonDisconnected}
          />
        </ViewLayer>
        <ViewLayer active={activeView === 'services'}>
          <Box sx={{ height: '100%', minHeight: 0, overflowY: 'auto', px: 2, py: 1 }}>
            <ServicesTabContainer
              projectId={projectId}
              branchId={branchId}
              runtimeStatus={runtimeStatus}
              onViewLogs={handleViewLogsFromServices}
              stream={stream}
              runtimeState={runtimeStatus?.state}
              isLive={stream.isLive}
            />
          </Box>
        </ViewLayer>
        <ViewLayer active={activeView === 'timeline'}>
          <Box sx={{ height: '100%', minHeight: 0, overflowY: 'auto', px: 2, py: 1 }}>
            <ActivityTab projectId={projectId} branchId={branchId} stream={stream} />
          </Box>
        </ViewLayer>
        <ViewLayer active={activeView === 'sysstats'}>
          <Box sx={{ height: '100%', minHeight: 0, overflowY: 'auto' }}>
            <SysstatsView heartbeatSnapshot={stream.heartbeatSnapshot} embedded />
          </Box>
        </ViewLayer>
        {isSuperAdmin && (
          <ViewLayer active={activeView === 'spec'}>
            <Box sx={{ height: '100%', minHeight: 0, overflowY: 'auto' }}>
              <RuntimeTab projectId={projectId} branchId={branchId} />
            </Box>
          </ViewLayer>
        )}
        {isSuperAdmin && (
          <ViewLayer active={activeView === 'fly'}>
            <FlyMachineTab
              projectId={projectId}
              branchId={branchId}
              active={activeView === 'fly'}
              isSuperAdmin={isSuperAdmin}
            />
          </ViewLayer>
        )}
      </Box>
    </Box>
  )
}

interface PanelStatusStripProps {
  status: Parameters<typeof RuntimeStatusHeader>[0]['status']
  /** Runtime id forwarded to the header's mono {@link IdChip}. */
  runtimeId: Parameters<typeof RuntimeStatusHeader>[0]['runtimeId']
  supervisordSnapshot: Parameters<typeof RuntimeStatusHeader>[0]['supervisordSnapshot']
  events: Parameters<typeof RuntimeStatusHeader>[0]['events']
  /**
   * SignalR liveness for the event stream that feeds this strip. Threaded
   * through to the {@link RuntimeStatusHeader} so the right-edge "Live"
   * pill reflects whether {@code useRuntimeEventStream} is currently
   * connected to the AgentHub.
   */
  isLive: Parameters<typeof RuntimeStatusHeader>[0]['isLive']
  /** Drop the phase label when the panel is narrower than ~600px. */
  showPhase: boolean
}

/**
 * Thin wrapper around the shared {@link RuntimeStatusHeader} that compacts
 * the row for the panel context (shorter than the super-admin drawer) and
 * truncates the inline error message for terminal states. The 1Hz interval
 * inside {@link RuntimeStatusHeader} is mounted only while the panel is open
 * — when {@code RuntimeLogsPanel} sets {@code mounted=false} after the
 * exit animation, this component unmounts and the interval clears.
 *
 * <p>Below ~600px panel width the phase caption inside {@link
 * RuntimeStatusHeader} would compete with the state chip + heartbeat for
 * row space. We CSS-hide the wrapping span (rather than re-flowing the
 * inner header) so the cadence stays in lockstep with the drawer.</p>
 */
function PanelStatusStrip({
  status,
  runtimeId,
  supervisordSnapshot,
  events,
  isLive,
  showPhase,
}: PanelStatusStripProps) {
  // Truncated error caption for terminal states. The header itself doesn't
  // surface errorMessage yet; we layer it inline so the operator sees the
  // cause without expanding anything. Falls back to errorReason for older
  // states where errorMessage wasn't populated.
  const terminalError = useMemo(() => {
    if (!status) return null
    const isTerminal =
      status.state === 'Failed' || status.state === 'Crashed'
    if (!isTerminal) return null
    const raw =
      (status.errorMessage && status.errorMessage.length > 0
        ? status.errorMessage
        : status.errorReason) ?? null
    if (!raw) return null
    const truncated = raw.length > 60 ? `${raw.slice(0, 59)}…` : raw
    return { full: raw, truncated }
  }, [status])

  return (
    <Box
      sx={{
        flexShrink: 0,
        backgroundColor: 'instrument.chrome',
        borderBottom: 1,
        borderColor: 'instrument.hairline',
        // Compact row — about 2/3 the height of the super-admin drawer's
        // matching strip. Inner padding inside RuntimeStatusHeader is
        // py: 1.5; we override here.
        '& > .MuiStack-root': {
          py: 0.875,
          px: 2,
          borderBottom: 'none',
        },
        // Hide the phase caption on narrow panels. The header renders phase
        // as the second Typography child of the inner Stack — between the
        // chip and the respawn/heartbeat slots.
        ...(showPhase
          ? {}
          : {
              // Only the time-in-state caption is animated (it's the in-flight
              // phase counter); on terminal/steady states it's just a quiet
              // caption. Hide it on narrow widths to keep the strip clean.
              '& > .MuiStack-root .MuiTypography-caption:first-of-type': {
                display: 'none',
              },
            }),
      }}
    >
      <RuntimeStatusHeader
        status={status}
        runtimeId={runtimeId}
        supervisordSnapshot={supervisordSnapshot}
        events={events}
        isLive={isLive}
      />
      {terminalError && (
        <Box
          sx={{
            px: 2,
            pb: 0.875,
            display: 'flex',
            alignItems: 'center',
          }}
        >
          <Tooltip title={terminalError.full}>
            <Typography
              sx={{
                fontFamily: workspaceFontFamily.mono,
                fontSize: 11.5,
                color: workspaceRuntime.failed,
                maxWidth: '100%',
                overflow: 'hidden',
                textOverflow: 'ellipsis',
                whiteSpace: 'nowrap',
              }}
            >
              {terminalError.truncated}
            </Typography>
          </Tooltip>
        </Box>
      )}
    </Box>
  )
}

interface LogsViewBodyProps {
  isSuperAdmin: boolean
  subTab: 'service' | 'daemon'
  onChangeSubTab: (next: 'service' | 'daemon') => void
  lines: LogViewerLine[]
  paused: boolean
  selectedService: string | undefined
  connection: ReturnType<typeof useAgentHub>['connection']
  runtimeId: string | undefined
  runtimeStatus?: RuntimeStatusResponse
  daemonDisconnected: boolean
}

/**
 * Body of the Logs view. Hosts the existing Service-logs viewer alongside a
 * super-admin-only Daemon sub-tab that tails the supervisord-managed
 * agent's stdout/stderr. The MUI sub-tab strip is hidden entirely for
 * non-super-admin users so the single available surface gets the full
 * panel height back.
 */
function LogsViewBody({
  isSuperAdmin,
  subTab,
  onChangeSubTab,
  lines,
  paused,
  selectedService,
  connection,
  runtimeId,
  runtimeStatus,
  daemonDisconnected,
}: LogsViewBodyProps) {
  return (
    <Box
      sx={{
        height: '100%',
        minHeight: 0,
        display: 'flex',
        flexDirection: 'column',
      }}
    >
      {isSuperAdmin && (
        <Tabs
          value={subTab}
          onChange={(_e, value: 'service' | 'daemon') => onChangeSubTab(value)}
          sx={{
            flexShrink: 0,
            minHeight: 34,
            borderBottom: 1,
            borderColor: 'instrument.hairline',
            backgroundColor: 'instrument.chrome',
            px: 2,
            gap: 0.5,
            '& .MuiTabs-flexContainer': { gap: 0.5 },
            '& .MuiTab-root': {
              minHeight: 34,
              minWidth: 0,
              px: 0.75,
              py: 0.75,
              textTransform: 'none',
              fontSize: '0.8125rem',
              fontWeight: 500,
              letterSpacing: '-0.005em',
              color: workspaceText.muted,
              transition: 'color 120ms ease',
              '&:hover': { color: workspaceText.primary },
              '&.Mui-selected': { color: workspaceText.primary, fontWeight: 600 },
            },
            // Accent-underline indicator to match the primary switcher's
            // active bar (rounded 2px accent rule).
            '& .MuiTabs-indicator': {
              backgroundColor: workspaceAccent.ink,
              height: 2,
              borderRadius: 999,
            },
          }}
        >
          <Tab value="service" label="Service logs" />
          <Tab value="daemon" label="Daemon logs" />
        </Tabs>
      )}
      <Box
        sx={{
          flex: 1,
          minHeight: 0,
          display: 'flex',
          flexDirection: 'column',
          overflow: 'hidden',
        }}
      >
        {subTab === 'service' || !isSuperAdmin ? (
          <LogViewer
            lines={lines}
            paused={paused}
            serviceName={selectedService}
            disconnectedHint={
              daemonDisconnected ? DAEMON_LOGS_UNAVAILABLE_MESSAGE : null
            }
          />
        ) : (
          <Box
            sx={{
              flex: 1,
              minHeight: 0,
              display: 'flex',
              flexDirection: 'column',
              overflow: 'hidden',
              px: 1.5,
              py: 1,
              boxSizing: 'border-box',
            }}
          >
            <DaemonLogsView
              connection={connection}
              runtimeId={runtimeId}
              runtimeStatus={runtimeStatus}
            />
          </Box>
        )}
      </Box>
    </Box>
  )
}

interface ViewLayerProps {
  active: boolean
  children: React.ReactNode
}

/**
 * Absolute-positioned overlay used to keep each view mounted while only one
 * is visible. Faded with a short opacity transition; pointer events are
 * disabled on inactive layers so background views don't intercept clicks.
 */
function ViewLayer({ active, children }: ViewLayerProps) {
  return (
    <Box
      sx={{
        position: 'absolute',
        inset: 0,
        display: 'flex',
        flexDirection: 'column',
        opacity: active ? 1 : 0,
        visibility: active ? 'visible' : 'hidden',
        pointerEvents: active ? 'auto' : 'none',
        transition: `opacity ${VIEW_FADE_MS}ms ease`,
      }}
      aria-hidden={!active}
    >
      {children}
    </Box>
  )
}

interface PanelHeaderProps {
  activeView: RuntimeDebugPanelView
  onChangeView: (view: RuntimeDebugPanelView) => void
  onClose: () => void
  isSuperAdmin: boolean
  logsControls: {
    logsSubTab: 'service' | 'daemon'
    serviceNames: string[]
    selectedService: string | undefined
    lineCount: number
    onServiceChange: (e: SelectChangeEvent<string>) => void
    /**
     * True once at least one supervisord snapshot has arrived. Drives the
     * picker's empty-state copy: "Waiting for first supervisord snapshot…"
     * (cold load, daemon offline) vs. "No services" (snapshot received but
     * empty).
     */
    hasSupervisordSnapshot: boolean
    daemonDisconnected: boolean
    paused: boolean
    onTogglePause: () => void
    onClear: () => void
    onCopy: () => void
  }
}

/**
 * 40px chrome strip with three segments on the left, contextual controls in
 * the middle, and per-view action icons on the right. Mirrors the
 * {@code ChatChrome} pattern: hairline-bottom, chromeBg, muted iconography
 * with bronze on hover.
 */
function PanelHeader({
  activeView,
  onChangeView,
  onClose,
  isSuperAdmin,
  logsControls,
}: PanelHeaderProps) {
  return (
    <Stack
      direction="row"
      alignItems="center"
      spacing={1.5}
      sx={{
        flexShrink: 0,
        height: 40,
        px: 2,
        backgroundColor: 'instrument.chrome',
        borderBottom: 1,
        borderColor: 'instrument.hairline',
      }}
    >
      <SegmentedSwitcher
        activeView={activeView}
        onChange={onChangeView}
        isSuperAdmin={isSuperAdmin}
      />

      <Box sx={{ ml: 0.5, flex: 1, minWidth: 0, display: 'flex', alignItems: 'center', gap: 1.25 }}>
        {activeView === 'logs' &&
          (logsControls.logsSubTab === 'daemon' ? (
            <DaemonLogsHeaderCaption />
          ) : (
            <LogsControls
              serviceNames={logsControls.serviceNames}
              selectedService={logsControls.selectedService}
              lineCount={logsControls.lineCount}
              onServiceChange={logsControls.onServiceChange}
              hasSupervisordSnapshot={logsControls.hasSupervisordSnapshot}
              daemonDisconnected={logsControls.daemonDisconnected}
            />
          ))}
        {activeView === 'services' && <ServicesControls />}
        {activeView === 'timeline' && <TimelineControls />}
        {activeView === 'fly' && <FlyControls />}
      </Box>

      {/* Per-view action icons */}
      {activeView === 'logs' && (
        <>
          <PanelIconButton
            tooltip={logsControls.paused ? 'Resume live tail' : 'Pause live tail'}
            ariaLabel={logsControls.paused ? 'Resume live tail' : 'Pause live tail'}
            onClick={logsControls.onTogglePause}
            active={logsControls.paused}
            icon={
              logsControls.paused ? (
                <PlayArrowIcon sx={{ fontSize: 16 }} />
              ) : (
                <PauseIcon sx={{ fontSize: 16 }} />
              )
            }
          />
          <PanelIconButton
            tooltip="Clear buffer"
            ariaLabel="Clear log buffer"
            onClick={logsControls.onClear}
            disabled={logsControls.lineCount === 0}
            icon={<DeleteOutlineIcon sx={{ fontSize: 16 }} />}
          />
          <PanelIconButton
            tooltip="Copy visible lines"
            ariaLabel="Copy logs to clipboard"
            onClick={logsControls.onCopy}
            disabled={logsControls.lineCount === 0}
            icon={<ContentCopyIcon sx={{ fontSize: 16 }} />}
          />
        </>
      )}
      <PanelIconButton
        tooltip="Close debug panel"
        ariaLabel="Close debug panel"
        onClick={onClose}
        icon={<CloseIcon sx={{ fontSize: 16 }} />}
      />
    </Stack>
  )
}

interface SegmentedSwitcherProps {
  activeView: RuntimeDebugPanelView
  onChange: (view: RuntimeDebugPanelView) => void
  /** Gate for the superadmin-only Fly tab. */
  isSuperAdmin: boolean
}

const VIEW_LABELS: Record<RuntimeDebugPanelView, string> = {
  logs: 'Logs',
  services: 'Services',
  timeline: 'Timeline',
  sysstats: 'Sysstats',
  spec: 'Spec',
  fly: 'Fly',
}

/**
 * Accent-underline switcher between the panel's surfaces — the prototype's
 * {@code DrawerTabs} treatment: a calm row where the active tab carries a 2px
 * accent underline and brighter, heavier text (rather than the older pill
 * group). Mirrors the {@code UnderlineTabs} primitive, but rendered inline so
 * it can coexist with the per-view controls in the same 40px chrome strip
 * (the primitive owns a full-width hairline-divided container that doesn't fit
 * here).
 *
 * <p>The {@code spec} and {@code fly} segments are rendered only for
 * SuperAdmin users — for everyone else they simply aren't there. We don't
 * render them disabled because we don't want regular users to perceive
 * hidden surfaces.</p>
 */
function SegmentedSwitcher({ activeView, onChange, isSuperAdmin }: SegmentedSwitcherProps) {
  // Final order is general-to-specialized, super-admin-only items at the
  // tail: Logs · Services · Timeline · Sysstats · Spec · Fly. Sysstats sits
  // before Spec because it's open to everyone — keeping the always-visible
  // group contiguous on the left.
  const views: ReadonlyArray<RuntimeDebugPanelView> = isSuperAdmin
    ? ['logs', 'services', 'timeline', 'sysstats', 'spec', 'fly']
    : ['logs', 'services', 'timeline', 'sysstats']

  return (
    <Stack
      direction="row"
      role="tablist"
      aria-label="Debug panel view"
      sx={{
        flexShrink: 0,
        gap: 0.5,
        // Stretch the underline track to the full chrome-strip height so the
        // 2px accent bar lands on the strip's bottom hairline like the
        // prototype's DrawerTabs.
        alignSelf: 'stretch',
      }}
    >
      {views.map((view) => {
        const active = activeView === view
        return (
          <Box
            key={view}
            component="button"
            type="button"
            role="tab"
            aria-selected={active}
            tabIndex={active ? 0 : -1}
            onClick={() => onChange(view)}
            sx={{
              position: 'relative',
              display: 'inline-flex',
              alignItems: 'center',
              border: 0,
              outline: 0,
              cursor: 'pointer',
              backgroundColor: 'transparent',
              px: 0.75,
              fontSize: '0.8125rem',
              fontWeight: active ? 600 : 500,
              letterSpacing: '-0.005em',
              lineHeight: 1,
              color: active ? workspaceText.primary : workspaceText.muted,
              transition: 'color 120ms ease',
              '&:hover': !active ? { color: workspaceText.primary } : undefined,
              '&:focus-visible': {
                outline: `2px solid ${workspaceText.primary}`,
                outlineOffset: -2,
                borderRadius: '4px',
              },
            }}
          >
            {VIEW_LABELS[view]}
            {active ? (
              <Box
                aria-hidden
                sx={{
                  position: 'absolute',
                  left: 4,
                  right: 4,
                  bottom: -1,
                  height: 2,
                  backgroundColor: workspaceAccent.ink,
                  borderRadius: 999,
                }}
              />
            ) : null}
          </Box>
        )
      })}
    </Stack>
  )
}

function DaemonLogsHeaderCaption() {
  return (
    <Typography
      variant="caption"
      sx={{
        color: workspaceText.muted,
        fontSize: '0.75rem',
        letterSpacing: '-0.005em',
        fontFamily: workspaceFontFamily.mono,
      }}
    >
      Daemon stdout / stderr
    </Typography>
  )
}

interface LogsControlsProps {
  serviceNames: string[]
  selectedService: string | undefined
  lineCount: number
  onServiceChange: (e: SelectChangeEvent<string>) => void
  hasSupervisordSnapshot: boolean
  daemonDisconnected: boolean
}

function LogsControls({
  serviceNames,
  selectedService,
  lineCount,
  onServiceChange,
  hasSupervisordSnapshot,
  daemonDisconnected,
}: LogsControlsProps) {
  return (
    <>
      <FormControl size="small" sx={{ minWidth: 180 }}>
        <Select
          value={serviceNames.includes(selectedService ?? '') ? (selectedService ?? '') : ''}
          onChange={onServiceChange}
          displayEmpty
          aria-label="Service to tail"
          sx={{
            fontSize: '0.8125rem',
            fontFamily: workspaceFontFamily.mono,
            backgroundColor: 'instrument.canvas',
          }}
        >
          {serviceNames.length === 0 ? (
            <MenuItem value="" disabled>
              {hasSupervisordSnapshot
                ? 'No services'
                : 'Waiting for first supervisord snapshot…'}
            </MenuItem>
          ) : (
            serviceNames.map((name) => (
              <MenuItem
                key={name}
                value={name}
                sx={{
                  fontSize: '0.8125rem',
                  fontFamily: workspaceFontFamily.mono,
                }}
              >
                {name}
              </MenuItem>
            ))
          )}
        </Select>
      </FormControl>

      <Typography
        variant="caption"
        sx={{
          color: workspaceText.muted,
          fontSize: '0.75rem',
          letterSpacing: '-0.005em',
        }}
      >
        {selectedService ? (
          <>
            tailing{' '}
            <Box
              component="span"
              sx={{
                fontFamily: workspaceFontFamily.mono,
                color: workspaceText.primary,
              }}
            >
              {selectedService}
            </Box>
            {' · '}
            {lineCount} line{lineCount === 1 ? '' : 's'}
          </>
        ) : hasSupervisordSnapshot ? (
          'No service selected'
        ) : daemonDisconnected ? (
          'Daemon not connected'
        ) : (
          'Awaiting supervisord snapshot'
        )}
      </Typography>
    </>
  )
}

function ServicesControls() {
  return (
    <Typography
      variant="caption"
      sx={{
        color: workspaceText.muted,
        fontSize: '0.75rem',
        letterSpacing: '-0.005em',
      }}
    >
      live services derived from runtime events
    </Typography>
  )
}

function FlyControls() {
  return (
    <Typography
      variant="caption"
      sx={{
        color: workspaceText.muted,
        fontSize: '0.75rem',
        letterSpacing: '-0.005em',
      }}
    >
      DB vs Fly machine — superadmin only
    </Typography>
  )
}

function TimelineControls() {
  // Note: the Timeline tab owns its own filter chips inline; we intentionally
  // don't duplicate them here. A short caption mirrors the Services view's
  // approach and keeps the header layout balanced.
  return (
    <Stack direction="row" spacing={0.5} alignItems="center">
      <Chip
        size="small"
        label="reverse-chronological"
        sx={{
          height: 20,
          fontSize: '0.6875rem',
          color: workspaceText.muted,
          bgcolor: 'instrument.chipBg',
          border: 1,
          borderColor: 'instrument.hairline',
          '& .MuiChip-label': { px: 1 },
        }}
      />
    </Stack>
  )
}

interface PanelIconButtonProps {
  tooltip: string
  ariaLabel: string
  icon: React.ReactNode
  onClick: () => void
  disabled?: boolean
  active?: boolean
}

function PanelIconButton({
  tooltip,
  ariaLabel,
  icon,
  onClick,
  disabled,
  active,
}: PanelIconButtonProps) {
  const button = (
    <IconButton
      size="small"
      aria-label={ariaLabel}
      onClick={onClick}
      disabled={disabled}
      sx={{
        color: active ? workspaceAccent.ink : workspaceText.muted,
        p: 0.625,
        '&:hover': {
          color: workspaceText.primary,
          backgroundColor: 'instrument.chipHoverBg',
        },
        '&.Mui-disabled': {
          color: 'rgba(0, 0, 0, 0.22)',
        },
      }}
    >
      {icon}
    </IconButton>
  )
  return (
    <Tooltip title={tooltip} enterDelay={400}>
      <span>{button}</span>
    </Tooltip>
  )
}
