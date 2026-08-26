import { useCallback, useMemo, useState } from 'react'

/** Server-side cap for bulk-delete requests. Mirrored here for client UX. */
export const BULK_DELETE_LIMIT = 100

export type BoxStatusFilter =
  | 'ready'
  | 'running'
  | 'idle'
  | 'provisioning'
  | 'archived'
  | 'error'
export type AgeFilter = 'all' | '1d' | '7d' | '30d'

export interface FilterState {
  /**
   * Selected box statuses. Empty set = show all statuses (don't filter out
   * anything). Only consulted on the boxes tab.
   */
  statuses: Set<BoxStatusFilter>
  /** When on, only rows where {@code isOrphan === true} are visible. */
  orphansOnly: boolean
  /** Age threshold based on {@code createdAt}. */
  age: AgeFilter
}

const AGE_THRESHOLD_MS: Record<Exclude<AgeFilter, 'all'>, number> = {
  '1d': 1 * 24 * 60 * 60 * 1000,
  '7d': 7 * 24 * 60 * 60 * 1000,
  '30d': 30 * 24 * 60 * 60 * 1000,
}

/** Minimum row shape we need for filtering. Both tabs supply this. */
interface FilterableRow {
  id: string
  status?: string
  createdAt?: string | null
  isOrphan: boolean
  /**
   * Golden template boxes must never enter a bulk-delete selection (the
   * backend refuses them anyway). Snapshot rows simply don't set this.
   */
  isTemplate?: boolean
}

interface UseBoxCleanupSelectionOptions<T extends FilterableRow> {
  rows: T[]
  /** Boxes: true. Snapshots: false (status filter is hidden). */
  includeStatusFilter: boolean
}

interface UseBoxCleanupSelectionResult<T extends FilterableRow> {
  filtered: T[]
  filter: FilterState
  setStatuses: (next: Set<BoxStatusFilter>) => void
  toggleStatus: (status: BoxStatusFilter) => void
  setOrphansOnly: (next: boolean) => void
  setAge: (next: AgeFilter) => void

  selectedIds: Set<string>
  isSelected: (id: string) => boolean
  toggleOne: (id: string) => void
  selectAllVisible: () => void
  selectVisibleOrphans: () => void
  clearSelection: () => void
  /** Selection count derived from selectedIds. */
  selectedCount: number
  /** True when the user has selected more than the server cap. */
  exceedsLimit: boolean
}

/**
 * Centralises selection + filter state for one tab of the Box cleanup page.
 * Used twice — once by the boxes tab, once by the snapshots tab.
 */
export function useBoxCleanupSelection<T extends FilterableRow>({
  rows,
  includeStatusFilter,
}: UseBoxCleanupSelectionOptions<T>): UseBoxCleanupSelectionResult<T> {
  const [statuses, setStatusesRaw] = useState<Set<BoxStatusFilter>>(new Set())
  const [orphansOnly, setOrphansOnly] = useState(false)
  const [age, setAge] = useState<AgeFilter>('all')
  const [selectedIds, setSelectedIds] = useState<Set<string>>(new Set())

  const filtered = useMemo(() => {
    const now = Date.now()
    return rows.filter((row) => {
      // Orphan filter
      if (orphansOnly && !row.isOrphan) return false

      // Status filter (boxes only — snapshots pass statuses=empty)
      if (includeStatusFilter && statuses.size > 0 && row.status) {
        if (!statuses.has(row.status.toLowerCase() as BoxStatusFilter)) return false
      }

      // Age filter
      if (age !== 'all' && row.createdAt) {
        const created = Date.parse(row.createdAt)
        if (!Number.isNaN(created)) {
          const ageMs = now - created
          if (ageMs < AGE_THRESHOLD_MS[age]) return false
        }
      }

      return true
    })
  }, [rows, orphansOnly, statuses, age, includeStatusFilter])

  const setStatuses = useCallback((next: Set<BoxStatusFilter>) => {
    setStatusesRaw(new Set(next))
  }, [])

  const toggleStatus = useCallback((status: BoxStatusFilter) => {
    setStatusesRaw((prev) => {
      const next = new Set(prev)
      if (next.has(status)) next.delete(status)
      else next.add(status)
      return next
    })
  }, [])

  const toggleOne = useCallback((id: string) => {
    setSelectedIds((prev) => {
      const next = new Set(prev)
      if (next.has(id)) next.delete(id)
      else next.add(id)
      return next
    })
  }, [])

  const selectAllVisible = useCallback(() => {
    setSelectedIds(new Set(filtered.filter((r) => !r.isTemplate).map((r) => r.id)))
  }, [filtered])

  const selectVisibleOrphans = useCallback(() => {
    setSelectedIds(
      new Set(
        filtered.filter((r) => r.isOrphan && !r.isTemplate).map((r) => r.id),
      ),
    )
  }, [filtered])

  const clearSelection = useCallback(() => {
    setSelectedIds(new Set())
  }, [])

  const isSelected = useCallback(
    (id: string) => selectedIds.has(id),
    [selectedIds],
  )

  return {
    filtered,
    filter: { statuses, orphansOnly, age },
    setStatuses,
    toggleStatus,
    setOrphansOnly,
    setAge,

    selectedIds,
    isSelected,
    toggleOne,
    selectAllVisible,
    selectVisibleOrphans,
    clearSelection,
    selectedCount: selectedIds.size,
    exceedsLimit: selectedIds.size > BULK_DELETE_LIMIT,
  }
}
