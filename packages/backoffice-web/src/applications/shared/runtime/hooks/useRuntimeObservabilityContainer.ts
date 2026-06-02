import { useAgentHub } from '@/lib/signalr'
import {
  useRuntimeEventStream,
  type UseRuntimeEventStreamReturn,
} from '@/applications/super-admin/features/project-runtime/hooks/useRuntimeEventStream'
import type { RuntimeStatusResponse } from '@/api/queries-commands'
import { useBranchRuntimeStatus } from './useBranchRuntimeStatus'

export interface UseRuntimeObservabilityContainerParams {
  projectId: string
  branchId: string
  /** Parent-owned stream — skips hub + event subscription when set. */
  stream?: UseRuntimeEventStreamReturn
  /** Parent-resolved status — skips the branch status fetch when set. */
  runtimeStatus?: RuntimeStatusResponse | null
  refetchInterval?: number | false
  enabled?: boolean
}

/**
 * Shared wiring for runtime observability tabs (Services, Activity): branch
 * status → runtime id → optional AgentHub + event stream.
 */
export function useRuntimeObservabilityContainer(
  params: UseRuntimeObservabilityContainerParams,
) {
  const {
    projectId,
    branchId,
    stream: streamProp,
    runtimeStatus,
    refetchInterval,
    enabled = true,
  } = params

  const branchRuntime = useBranchRuntimeStatus(projectId, branchId, {
    enabled,
    refetchInterval,
    status: runtimeStatus,
  })

  const { connection } = useAgentHub({
    projectId: projectId || undefined,
    branchId: branchId || undefined,
    enabled: enabled && !!projectId && !!branchId && !streamProp,
  })

  const fallbackStream = useRuntimeEventStream({
    connection,
    runtimeId: branchRuntime.runtimeId,
    status: branchRuntime.status,
    enabled: enabled && !!branchRuntime.runtimeId && !streamProp,
  })

  const stream = streamProp ?? fallbackStream

  return {
    ...branchRuntime,
    connection,
    stream,
    isLive: stream.isLive,
  }
}
