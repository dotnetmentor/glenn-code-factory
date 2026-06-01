import { Alert, Box, CircularProgress, Stack, Typography } from '@mui/material'
import {
  useGetApiProjectsProjectIdRuntimeSpec,
  type RuntimeStatusResponse,
} from '@/api/queries-commands'
import { useAgentHub } from '@/lib/signalr'
import {
  useRuntimeEventStream,
  type UseRuntimeEventStreamReturn,
} from '@/applications/super-admin/features/project-runtime/hooks/useRuntimeEventStream'
import { ServicesTab } from '@/applications/super-admin/features/project-runtime/components/ServicesTab'
import { workspaceText, workspaceTokens } from '@/applications/workspace/shared/designTokens'

export interface ServicesTabContainerProps {
  projectId: string
  /** Optional cached runtime status — used as a hint for the runtime id. */
  runtimeStatus?: RuntimeStatusResponse | null
  /**
   * Called when the user clicks "View logs" for a row. The shell wires this
   * to: (1) close the settings drawer, (2) open the bottom log panel with
   * that service pre-selected.
   */
  onViewLogs?: (serviceName: string) => void
  /**
   * Pre-resolved runtime-event stream. When provided, the component skips
   * its own {@link useRuntimeEventStream} call so the SignalR subscription
   * stays singular across sibling tabs that share a parent panel. Pass it
   * in from {@code RuntimeLogsPanel} where the panel owns the stream; omit
   * it from one-off mount points and the component will provision its own
   * stream like before.
   */
  stream?: UseRuntimeEventStreamReturn
}

/**
 * Standalone wrapper that owns its own SignalR connection and runtime-event
 * stream so the original {@link ServicesTab} (designed to live inside the
 * super-admin runtime drawer, which lifts both upstream) can be mounted from
 * the project settings drawer without rewiring its props.
 *
 * <p>The runtime id is resolved from the {@code runtimeSpec} endpoint — the
 * same source the super-admin drawer uses for the Logs tab's service
 * picker. We deliberately fetch it here (rather than threading from
 * {@code ProjectWorkspaceRoute}) so this tab continues to render correctly
 * if the project settings drawer is opened from a different surface in the
 * future.</p>
 */
export function ServicesTabContainer({ projectId, onViewLogs, stream: streamProp }: ServicesTabContainerProps) {
  const specQuery = useGetApiProjectsProjectIdRuntimeSpec(projectId, {
    query: { enabled: !!projectId },
  })
  // DTO field is nullable now (project-level spec — no live runtime means
  // null). Collapse to undefined so downstream string|undefined props are
  // happy without spreading ?? undefined everywhere.
  const runtimeId = specQuery.data?.runtimeId ?? undefined

  // Self-provisioned fallback path — only runs when the parent didn't pass
  // a stream prop. The pooled hub means even this path won't cause an extra
  // negotiate when another consumer already opened one.
  const { connection } = useAgentHub({
    projectId: projectId || undefined,
    enabled: !!projectId && !streamProp,
  })

  const fallbackStream = useRuntimeEventStream({
    connection,
    runtimeId,
    enabled: !!runtimeId && !streamProp,
  })

  const stream = streamProp ?? fallbackStream

  if (specQuery.isLoading) {
    return (
      <Stack
        direction="row"
        spacing={1.25}
        alignItems="center"
        sx={{ py: 4, justifyContent: 'center' }}
      >
        <CircularProgress size={16} sx={{ color: workspaceText.muted }} />
        <Typography variant="body2" sx={{ color: workspaceText.muted }}>
          Loading runtime…
        </Typography>
      </Stack>
    )
  }

  if (specQuery.isError) {
    return (
      <Alert severity="error">
        Failed to load runtime. Try reopening the settings drawer.
      </Alert>
    )
  }

  if (!runtimeId) {
    return (
      <Typography variant="body2" sx={{ color: workspaceText.muted }}>
        No runtime has been provisioned yet — services will appear here once
        the daemon boots.
      </Typography>
    )
  }

  if (stream.error) {
    return (
      <Alert severity="error">
        Failed to load runtime events. Refresh to try again.
      </Alert>
    )
  }

  return (
    <Box
      sx={{
        flex: 1,
        minHeight: 0,
        display: 'flex',
        flexDirection: 'column',
        backgroundColor: workspaceTokens.canvasBg,
      }}
    >
      <ServicesTab
        events={stream.events}
        supervisordSnapshot={stream.supervisordSnapshot}
        heartbeatSnapshot={stream.heartbeatSnapshot}
        onViewLogs={onViewLogs}
      />
    </Box>
  )
}
