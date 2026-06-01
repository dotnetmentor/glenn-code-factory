import { useMemo } from 'react'
import {
  RuntimeState,
  useGetApiRuntimesRuntimeIdDiffChangedFiles,
  type ChangedFilesResponse,
} from '../../../../../../../api/queries-commands'
import { scopeToWireParams, type CompareScope } from './types'

interface UseChangedFilesOptions {
  /** Runtime to query — the diff endpoints are runtime-scoped. */
  runtimeId: string
  /** Which slice of history (or working tree) to read changed files for. */
  scope: CompareScope
  /** When false (tab hidden or runtime offline) the query stays parked. */
  enabled: boolean
  /** Current runtime state — gates {@code enabled} on Online. */
  runtimeState: RuntimeState | string | undefined
}

interface UseChangedFilesResult {
  data: ChangedFilesResponse | undefined
  isLoading: boolean
  isFetching: boolean
  isError: boolean
  refetch: () => void
}

/**
 * Thin wrapper around the generated Orval query for
 * {@code GET /api/runtimes/{runtimeId}/diff/changed-files}.
 *
 * <p>Behaviour highlights:
 * <ul>
 *   <li>Suspended via {@code enabled: false} when the tab is hidden, the
 *       runtime isn't Online, or the runtime id hasn't been threaded
 *       through yet — the Diff endpoint requires a live runtime to query
 *       the daemon over the hub.</li>
 *   <li>Working-tree queries use {@code staleTime: 0} so a refresh-button
 *       click (or a future SignalR push) is honoured immediately. Phase 1
 *       has no live push — the user drives invalidation via Refresh.</li>
 *   <li>Returns a {@code refetch} thunk the chrome can call on the refresh
 *       button.</li>
 * </ul></p>
 */
export function useChangedFiles({
  runtimeId,
  scope,
  enabled,
  runtimeState,
}: UseChangedFilesOptions): UseChangedFilesResult {
  const params = useMemo(() => scopeToWireParams(scope), [scope])
  const isOnline = runtimeState === RuntimeState.Online
  const queryEnabled = enabled && isOnline && !!runtimeId

  const query = useGetApiRuntimesRuntimeIdDiffChangedFiles(runtimeId, params, {
    query: {
      enabled: queryEnabled,
      // Working-tree is the only scope that can change without a commit;
      // for committed ranges the underlying data is immutable, so we let
      // React Query keep the cached value for the default staleTime. We
      // bias to "always recheck" for the working tree so Refresh is
      // honoured even within React Query's debounce window.
      staleTime: scope.kind === 'workingTree' ? 0 : 5 * 60 * 1000,
    },
  })

  return {
    data: query.data,
    isLoading: query.isLoading,
    isFetching: query.isFetching,
    isError: query.isError,
    refetch: () => {
      query.refetch()
    },
  }
}
