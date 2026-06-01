import { useMemo, useState } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import {
  DriftSeverity,
  RuntimeDriftDto,
  getGetApiAdminRuntimesDriftQueryKey,
  useGetApiAdminRuntimesDrift,
} from '@/api/queries-commands'

export type DriftFilter = 'all' | 'drift' | 'critical'

const SEVERITY_RANK: Record<DriftSeverity, number> = {
  Ok: 0,
  Low: 1,
  Medium: 2,
  High: 3,
  Critical: 4,
}

interface UseDriftMonitorResult {
  items: RuntimeDriftDto[]
  totalCount: number
  driftCount: number
  generatedAt: string | null
  isLoading: boolean
  isFetching: boolean
  error: unknown
  filter: DriftFilter
  setFilter: (filter: DriftFilter) => void
  refetch: () => void
}

/**
 * Wrapper around the generated Orval hook for runtime drift monitoring.
 * Polls every 10s, manages local filter state, and exposes a refetch helper.
 */
export function useDriftMonitor(): UseDriftMonitorResult {
  const queryClient = useQueryClient()
  const [filter, setFilterState] = useState<DriftFilter | null>(null)

  const query = useGetApiAdminRuntimesDrift({
    query: { refetchInterval: 10_000 },
  })

  const totalCount = query.data?.totalCount ?? 0
  const driftCount = query.data?.driftCount ?? 0
  const allItems = useMemo(() => query.data?.items ?? [], [query.data])

  // Default: "drift" if anything is drifting, else "all". Only applied on
  // first load (i.e. while user hasn't picked a filter explicitly).
  const effectiveFilter: DriftFilter =
    filter ?? (driftCount > 0 ? 'drift' : 'all')

  const filteredItems = useMemo(() => {
    if (effectiveFilter === 'all') return allItems
    if (effectiveFilter === 'critical') {
      return allItems.filter((it) => it.driftSeverity === DriftSeverity.Critical)
    }
    // 'drift' = severity > Ok
    return allItems.filter(
      (it) => SEVERITY_RANK[it.driftSeverity] > SEVERITY_RANK[DriftSeverity.Ok],
    )
  }, [allItems, effectiveFilter])

  return {
    items: filteredItems,
    totalCount,
    driftCount,
    generatedAt: query.data?.generatedAt ?? null,
    isLoading: query.isLoading,
    isFetching: query.isFetching,
    error: query.error,
    filter: effectiveFilter,
    setFilter: (next: DriftFilter) => setFilterState(next),
    refetch: () => {
      queryClient.invalidateQueries({ queryKey: getGetApiAdminRuntimesDriftQueryKey() })
    },
  }
}
