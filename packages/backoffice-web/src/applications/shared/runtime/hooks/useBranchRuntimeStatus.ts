import {
  useGetApiProjectsProjectIdBranchesBranchIdRuntimeStatus,
  type RuntimeStatusResponse,
} from '@/api/queries-commands'

export interface UseBranchRuntimeStatusOptions {
  enabled?: boolean
  refetchInterval?: number | false
  /** Skip fetch when a parent already resolved branch status. */
  status?: RuntimeStatusResponse | null
}

/**
 * Branch-scoped runtime identity + status. Prefer this over
 * {@code GET /runtime/spec}'s project-level {@code runtimeId} whenever the UI
 * is pinned to a branch (debug panel, services tab, diff endpoints, etc.).
 */
export function useBranchRuntimeStatus(
  projectId: string,
  branchId: string,
  options: UseBranchRuntimeStatusOptions = {},
) {
  const { enabled = true, refetchInterval, status: statusProp } = options
  const shouldFetch =
    enabled && !!projectId && !!branchId && statusProp === undefined

  const query = useGetApiProjectsProjectIdBranchesBranchIdRuntimeStatus(
    projectId,
    branchId,
    {
      query: {
        enabled: shouldFetch,
        refetchInterval,
        refetchIntervalInBackground: false,
      },
    },
  )

  const status = statusProp ?? query.data
  const runtimeId = status?.runtimeId ?? undefined

  return {
    status,
    runtimeId,
    isLoading: shouldFetch && query.isLoading,
    isError: shouldFetch && query.isError,
    isFetching: shouldFetch && query.isFetching,
    query,
  }
}
