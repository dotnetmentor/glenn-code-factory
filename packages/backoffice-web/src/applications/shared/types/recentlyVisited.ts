export interface RecentlyVisitedItem {
  id: string
  name: string
  visitedAt: string
  url: string
}

export type EntityType = 'users'

export interface UseRecentlyVisitedOptions {
  entityType: EntityType
  maxItems?: number
  storageKey?: string
}

export interface UseRecentlyVisitedReturn {
  items: RecentlyVisitedItem[]
  addItem: (item: Omit<RecentlyVisitedItem, 'visitedAt'>) => void
  removeItem: (id: string) => void
  clearAll: () => void
}
