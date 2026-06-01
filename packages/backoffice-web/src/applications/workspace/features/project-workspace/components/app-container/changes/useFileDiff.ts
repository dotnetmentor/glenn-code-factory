import { useMemo } from 'react'
import {
  useGetApiRuntimesRuntimeIdDiffFile,
  type FileDiffResponse,
} from '../../../../../../../api/queries-commands'
import { scopeToWireParams, type CompareScope } from './types'

interface UseFileDiffOptions {
  runtimeId: string
  scope: CompareScope
  /** When null, the query stays parked (no auto-fetch on mount). */
  path: string | null
  /** Master switch from the parent — false hides the tab. */
  enabled: boolean
}

interface UseFileDiffResult {
  data: FileDiffResponse | undefined
  isLoading: boolean
  isFetching: boolean
  isError: boolean
  refetch: () => void
}

/**
 * Wrapper around the generated Orval query for
 * {@code GET /api/runtimes/{runtimeId}/diff/file}.
 *
 * <p>Disabled until {@code path} is set so we never fetch on tab open —
 * the diff pane shows the {@code NoSelectionState} until the user picks
 * a row. Working-tree fetches use {@code staleTime: 0} (the contents of
 * the working tree mutate continuously); committed-range fetches use a
 * long {@code staleTime} since they're immutable.</p>
 */
export function useFileDiff({
  runtimeId,
  scope,
  path,
  enabled,
}: UseFileDiffOptions): UseFileDiffResult {
  const params = useMemo(() => {
    const wire = scopeToWireParams(scope)
    return path ? { ...wire, path } : wire
  }, [scope, path])

  const query = useGetApiRuntimesRuntimeIdDiffFile(runtimeId, params, {
    query: {
      enabled: enabled && !!runtimeId && !!path,
      staleTime: scope.kind === 'workingTree' ? 0 : 5 * 60 * 1000,
      gcTime: 30_000,
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
