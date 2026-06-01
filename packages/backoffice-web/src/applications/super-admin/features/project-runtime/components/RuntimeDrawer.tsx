import { useCallback, useEffect, useMemo, useState } from 'react'
import {
  Box,
  Drawer,
  Tab,
  Tabs,
} from '@mui/material'
import { useQueryClient } from '@tanstack/react-query'
import {
  getGetApiProjectsProjectIdBranchesBranchIdRuntimeStatusQueryKey,
  useGetApiProjectsProjectIdBranchesBranchIdRuntimeStatus,
  useGetApiProjectsProjectIdRuntimeSpec,
  usePostApiRuntimesRuntimeIdRepair,
} from '@/api/queries-commands'
import type { AgentHubConnection } from '@/lib/signalr'
import { RuntimeStatusHeader } from '@/applications/shared/runtime/components/RuntimeStatusHeader'
import { useRuntimeEventStream } from '../hooks/useRuntimeEventStream'
import { TimelineTab } from './TimelineTab'
import { ServicesTab } from './ServicesTab'
import { LogsTab } from './LogsTab'
import { SpecTab } from './SpecTab'
import { SysstatsPanel } from './SysstatsPanel'

/**
 * The four tabs the runtime drawer exposes. Phase 4 shipped Timeline +
 * Services, Phase 5 wires Logs to a live tail, and Spec (P6) still renders
 * a placeholder.
 */
const TABS = ['timeline', 'services', 'logs', 'spec'] as const
type RuntimeDrawerTab = (typeof TABS)[number]

const DRAWER_WIDTH = 720

export interface RuntimeDrawerProps {
  open: boolean
  onClose: () => void
  projectId: string
  /**
   * Branch id the runtime is pinned to. The branch-scoped runtime/status
   * endpoint requires both ids; without {@code branchId} the header falls
   * back to the live event stream only (state chip shows "Unknown" until
   * a state-bearing event arrives).
   */
  branchId: string | undefined
  /** Current runtime id for the project, if known. */
  runtimeId: string | undefined
  /** Live AgentHub connection (may be null while connecting). */
  connection: AgentHubConnection | null
}

/**
 * Right-anchored MUI Drawer that surfaces structured runtime observability
 * for the current project's runtime. Owns the tab state, the per-runtime
 * event stream hook, and the header status chip.
 *
 * <p>The drawer is mounted unconditionally by its parent (so it can animate
 * open / closed cleanly) but the underlying event-stream hook stays inert
 * while {@code open} is false to avoid background network noise.</p>
 */
export function RuntimeDrawer(props: RuntimeDrawerProps) {
  const { open, onClose, projectId, branchId, runtimeId, connection } = props
  const [tab, setTab] = useState<RuntimeDrawerTab>('timeline')
  // Selected service name for the Logs tab. Lives at the drawer level so
  // the cross-link from the Services tab can pre-seed it before flipping
  // the active tab. Cleared on drawer close to avoid a stale selection
  // showing up next time the user opens it.
  const [logsService, setLogsService] = useState<string | undefined>(undefined)

  // Reset to Timeline whenever the drawer is reopened so deep-links via the
  // header button always land on the most-useful first surface. Mid-session
  // tab changes are preserved while open.
  useEffect(() => {
    if (open) {
      setTab('timeline')
      setLogsService(undefined)
    }
  }, [open])

  // Branch-scoped runtime status drives the header (state chip, time-in-state,
  // last heartbeat caption, respawn pill, spec version). Refetches every 5s
  // while the drawer is open so the chip ticks without depending solely on the
  // event stream — the stream covers transitions, but a healthy idle runtime
  // emits no events and the poll keeps "Last seen Xs ago" honest. Also feeds
  // the heartbeat sysstats snapshot to useRuntimeEventStream so SysstatsPanel
  // / ServicesTab consumers see the latest disk + per-process numbers.
  const statusQuery = useGetApiProjectsProjectIdBranchesBranchIdRuntimeStatus(
    projectId,
    branchId ?? '',
    {
      query: {
        enabled: open && !!projectId && !!branchId,
        refetchInterval: 5_000,
        refetchIntervalInBackground: false,
      },
    },
  )

  const specQuery = useGetApiProjectsProjectIdRuntimeSpec(projectId, {
    query: {
      // Only fetch while the drawer is open — the Logs tab needs the
      // current spec's service list to populate its picker, and the
      // header doesn't depend on the spec at all.
      enabled: !!projectId && open,
    },
  })
  const services = useMemo(
    () => specQuery.data?.spec?.services ?? [],
    [specQuery.data],
  )

  const stream = useRuntimeEventStream({
    connection,
    runtimeId,
    status: statusQuery.data,
    enabled: open && !!runtimeId,
  })

  // "Let agent fix it" — dispatches the self-heal repair turn for a runtime
  // whose spec only partially applied (specHealth === 'Degraded'). On success
  // we invalidate the branch-scoped status query so the next poll observes the
  // (eventually) Healthy specHealth and the degraded banner unmounts itself.
  const queryClient = useQueryClient()
  const repairMutation = usePostApiRuntimesRuntimeIdRepair({
    mutation: {
      onSuccess: () => {
        if (projectId && branchId) {
          queryClient.invalidateQueries({
            queryKey: getGetApiProjectsProjectIdBranchesBranchIdRuntimeStatusQueryKey(
              projectId,
              branchId,
            ),
          })
        }
      },
    },
  })

  const handleRepair = useCallback(() => {
    if (!runtimeId) return
    repairMutation.mutate({ runtimeId })
  }, [runtimeId, repairMutation])

  // Cross-link target from the Services tab's "View logs" button — set the
  // selection first so LogsTab picks it up immediately on mount, then
  // switch tabs.
  const handleViewLogs = useCallback((serviceName: string) => {
    setLogsService(serviceName)
    setTab('logs')
  }, [])

  return (
    <Drawer
      anchor="right"
      open={open}
      onClose={onClose}
      ModalProps={{ keepMounted: false }}
      PaperProps={{
        sx: {
          width: { xs: '100%', sm: DRAWER_WIDTH },
          maxWidth: '100vw',
        },
      }}
    >
      <Box
        role="dialog"
        aria-label="Project runtime"
        sx={{
          height: '100%',
          display: 'flex',
          flexDirection: 'column',
          overflow: 'hidden',
        }}
      >
        <RuntimeStatusHeader
          status={statusQuery.data}
          runtimeId={runtimeId}
          supervisordSnapshot={stream.supervisordSnapshot}
          events={stream.events}
          isLive={stream.isLive}
          onClose={onClose}
          onRepair={handleRepair}
          isRepairing={repairMutation.isPending}
        />

        {/*
         * Sysstats panel sits between the header and the tab strip so it's
         * the first thing an operator sees on a stuck runtime. Default
         * collapsed (and {@code flexShrink: 0} inside the panel) so its
         * presence doesn't change the drawer's scroll geometry until the
         * operator opens it.
         */}
        <SysstatsPanel heartbeatSnapshot={stream.heartbeatSnapshot} />

        <Tabs
          value={tab}
          onChange={(_e, value: RuntimeDrawerTab) => setTab(value)}
          sx={{ borderBottom: 1, borderColor: 'divider', px: 2 }}
        >
          <Tab value="timeline" label="Timeline" />
          <Tab value="services" label="Services" />
          <Tab value="logs" label="Logs" />
          <Tab value="spec" label="Spec" />
        </Tabs>

        <Box
          sx={{
            flex: 1,
            minHeight: 0,
            overflow: 'hidden',
            display: 'flex',
            flexDirection: 'column',
            p: 2,
          }}
        >
          {tab === 'timeline' && (
            <TimelineTab
              events={stream.events}
              hasMore={stream.hasMore}
              loadingInitial={stream.loadingInitial}
              loadingMore={stream.loadingMore}
              error={stream.error}
              onLoadMore={stream.loadMore}
            />
          )}
          {tab === 'services' && (
            <ServicesTab
              events={stream.events}
              supervisordSnapshot={stream.supervisordSnapshot}
              heartbeatSnapshot={stream.heartbeatSnapshot}
              onViewLogs={handleViewLogs}
            />
          )}
          {tab === 'logs' && (
            <LogsTab
              connection={connection}
              runtimeId={runtimeId}
              services={services}
              supervisordSnapshot={stream.supervisordSnapshot}
              initialServiceName={logsService}
            />
          )}
          {tab === 'spec' && (
            <SpecTab projectId={projectId} branchId={branchId} />
          )}
        </Box>
      </Box>
    </Drawer>
  )
}
