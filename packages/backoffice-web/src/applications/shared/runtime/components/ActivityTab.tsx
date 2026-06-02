import { Box } from '@mui/material'
import { TimelineTab } from '@/applications/super-admin/features/project-runtime/components/TimelineTab'
import { workspaceTokens } from '@/applications/workspace/shared/designTokens'
import type { UseRuntimeEventStreamReturn } from '@/applications/super-admin/features/project-runtime/hooks/useRuntimeEventStream'
import { useRuntimeObservabilityContainer } from '../hooks/useRuntimeObservabilityContainer'
import { RuntimeObservabilityGate } from './RuntimeObservabilityGate'

export interface ActivityTabProps {
  projectId: string
  branchId: string
  stream?: UseRuntimeEventStreamReturn
}

export function ActivityTab({ projectId, branchId, stream: streamProp }: ActivityTabProps) {
  const observability = useRuntimeObservabilityContainer({
    projectId,
    branchId,
    stream: streamProp,
    enabled: !!projectId && !!branchId,
  })

  const timeline = (
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
        events={observability.stream.events}
        hasMore={observability.stream.hasMore}
        loadingInitial={observability.stream.loadingInitial}
        loadingMore={observability.stream.loadingMore}
        error={observability.stream.error}
        onLoadMore={observability.stream.loadMore}
      />
    </Box>
  )

  if (streamProp) {
    return timeline
  }

  return (
    <RuntimeObservabilityGate
      isLoading={observability.isLoading}
      isError={observability.isError}
      runtimeId={observability.runtimeId}
    >
      {timeline}
    </RuntimeObservabilityGate>
  )
}
