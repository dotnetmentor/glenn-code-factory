import { useCallback, useMemo, useState } from 'react'

/** Server-side cap for bulk-destroy requests. Mirrored here for client UX. */
export const BULK_DESTROY_LIMIT = 100

export type MachineStateFilter = 'Started' | 'Stopped' | 'Suspended' | 'Destroyed'
export type AgeFilter = 'all' | '1d' | '7d' | '30d'

export interface FilterState {
  /**
   * Selected machine states. Empty set = show all states (don't filter out
   * anything). Only consulted on the machines tab.
   */
  states: Set<MachineStateFilter>
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
  state?: string
  createdAt: string
  isOrphan: boolean
}

interface UseFlyCleanupSelectionOptions<T extends FilterableRow> {
  rows: T[]
  /** Machines: true. Volumes: false (state filter is hidden). */
  includeStateFilter: boolean
}

interface UseFlyCleanupSelectionResult<T extends FilterableRow> {
  filtered: T[]
  filter: FilterState
  setStates: (next: Set<MachineStateFilter>) => void
  toggleState: (state: MachineStateFilter) => void
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
 * Centralises selection + filter state for one tab of the Fly cleanup page.
 * Used twice — once by the machines tab, once by the volumes tab.
 */
export function useFlyCleanupSelection<T extends FilterableRow>({
  rows,
  includeStateFilter,
}: UseFlyCleanupSelectionOptions<T>): UseFlyCleanupSelectionResult<T> {
  const [states, setStatesRaw] = useState<Set<MachineStateFilter>>(new Set())
  const [orphansOnly, setOrphansOnly] = useState(false)
  const [age, setAge] = useState<AgeFilter>('all')
  const [selectedIds, setSelectedIds] = useState<Set<string>>(new Set())

  const filtered = useMemo(() => {
    const now = Date.now()
    return rows.filter((row) => {
      // Orphan filter
      if (orphansOnly && !row.isOrphan) return false

      // State filter (machines only — volumes pass states=empty)
      if (includeStateFilter && states.size > 0 && row.state) {
        if (!states.has(row.state as MachineStateFilter)) return false
      }

      // Age filter
      if (age !== 'all') {
        const created = Date.parse(row.createdAt)
        if (!Number.isNaN(created)) {
          const ageMs = now - created
          if (ageMs < AGE_THRESHOLD_MS[age]) return false
        }
      }

      return true
    })
  }, [rows, orphansOnly, states, age, includeStateFilter])

  const setStates = useCallback((next: Set<MachineStateFilter>) => {
    setStatesRaw(new Set(next))
  }, [])

  const toggleState = useCallback((state: MachineStateFilter) => {
    setStatesRaw((prev) => {
      const next = new Set(prev)
      if (next.has(state)) next.delete(state)
      else next.add(state)
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
    setSelectedIds(new Set(filtered.map((r) => r.id)))
  }, [filtered])

  const selectVisibleOrphans = useCallback(() => {
    setSelectedIds(
      new Set(filtered.filter((r) => r.isOrphan).map((r) => r.id)),
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
    filter: { states, orphansOnly, age },
    setStates,
    toggleState,
    setOrphansOnly,
    setAge,

    selectedIds,
    isSelected,
    toggleOne,
    selectAllVisible,
    selectVisibleOrphans,
    clearSelection,
    selectedCount: selectedIds.size,
    exceedsLimit: selectedIds.size > BULK_DESTROY_LIMIT,
  }
}
