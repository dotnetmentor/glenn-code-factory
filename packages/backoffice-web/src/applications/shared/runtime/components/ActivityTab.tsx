import { Alert, Box, CircularProgress, Stack, Typography } from '@mui/material'
import { useGetApiProjectsProjectIdRuntimeSpec } from '@/api/queries-commands'
import { useAgentHub } from '@/lib/signalr'
import {
  useRuntimeEventStream,
  type UseRuntimeEventStreamReturn,
} from '@/applications/super-admin/features/project-runtime/hooks/useRuntimeEventStream'
import { TimelineTab } from '@/applications/super-admin/features/project-runtime/components/TimelineTab'
import { workspaceText, workspaceTokens } from '@/applications/workspace/shared/designTokens'

export interface ActivityTabProps {
  projectId: string
  /**
   * Pre-resolved runtime-event stream. When provided, the component skips
   * its own {@link useRuntimeEventStream} call entirely so the SignalR
   * subscription stays singular across sibling tabs that share a panel.
   * Pass this in from {@code RuntimeLogsPanel} where the panel itself owns
   * the stream; omit it from one-off mount points (e.g.
   * {@code ProjectSettingsDrawer}) and the component will provision its
   * own stream like before.
   */
  stream?: UseRuntimeEventStreamReturn
}

/**
 * Standalone wrapper that mounts the existing {@link TimelineTab} (plus its
 * boot-timing summary) for the customer-facing project settings drawer.
 *
 * <p>When the caller provides a {@code stream} prop, this component skips its
 * own SignalR + runtime-event subscription and renders directly off the
 * provided stream — used by {@code RuntimeLogsPanel} where the panel itself
 * already owns the subscription. Without a {@code stream} prop it falls back
 * to provisioning its own connection so other mount points (project settings
 * drawer) keep working unchanged.</p>
 */
export function ActivityTab({ projectId, stream: streamProp }: ActivityTabProps) {
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
        No runtime has been provisioned yet — activity will appear here once
        the daemon boots.
      </Typography>
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
      <TimelineTab
        events={stream.events}
        hasMore={stream.hasMore}
        loadingInitial={stream.loadingInitial}
        loadingMore={stream.loadingMore}
        error={stream.error}
        onLoadMore={stream.loadMore}
      />
    </Box>
  )
}
