import { Alert, Box } from '@mui/material'
import { ServicesTab } from '@/applications/super-admin/features/project-runtime/components/ServicesTab'
import { workspaceTokens } from '@/applications/workspace/shared/designTokens'
import type { RuntimeState, RuntimeStatusResponse } from '@/api/queries-commands'
import type { UseRuntimeEventStreamReturn } from '@/applications/super-admin/features/project-runtime/hooks/useRuntimeEventStream'
import { useRuntimeObservabilityContainer } from '../hooks/useRuntimeObservabilityContainer'
import { RuntimeObservabilityGate } from './RuntimeObservabilityGate'

export interface ServicesTabContainerProps {
  projectId: string
  branchId: string
  runtimeStatus?: RuntimeStatusResponse | null
  onViewLogs?: (serviceName: string) => void
  stream?: UseRuntimeEventStreamReturn
  runtimeState?: RuntimeState | string | null
  isLive?: boolean
}

export function ServicesTabContainer({
  projectId,
  branchId,
  runtimeStatus,
  onViewLogs,
  stream: streamProp,
  runtimeState = null,
  isLive: isLiveProp,
}: ServicesTabContainerProps) {
  const observability = useRuntimeObservabilityContainer({
    projectId,
    branchId,
    stream: streamProp,
    runtimeStatus,
    enabled: !!projectId && !!branchId,
  })

  const isLive = isLiveProp ?? observability.isLive
  const resolvedState = runtimeState ?? observability.status?.state

  return (
    <RuntimeObservabilityGate
      isLoading={!streamProp && observability.isLoading}
      isError={!streamProp && observability.isError}
      runtimeId={observability.runtimeId}
    >
      {observability.stream.error ? (
        <Alert severity="error">
          Failed to load runtime events. Refresh to try again.
        </Alert>
      ) : (
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
            events={observability.stream.events}
            supervisordSnapshot={observability.stream.supervisordSnapshot}
            heartbeatSnapshot={observability.stream.heartbeatSnapshot}
            runtimeState={resolvedState}
            isLive={isLive}
            onViewLogs={onViewLogs}
          />
        </Box>
      )}
    </RuntimeObservabilityGate>
  )
}
