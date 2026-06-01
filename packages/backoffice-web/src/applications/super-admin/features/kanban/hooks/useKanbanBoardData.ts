import { useQuery, type QueryKey } from '@tanstack/react-query'
import {
  getApiProjectsProjectIdKanbanBoard,
  getApiProjectsProjectIdKanbanColumnsStatusCards,
  ProjectKanbanCardStatus,
  type KanbanBoardColumnDto,
  type ProjectKanbanCardListItemDto,
} from '@/api/queries-commands'

/**
 * Composite board snapshot — one row per status column with the column
 * metadata (name + count from the board endpoint) and the actual cards
 * (from the column-cards endpoint). The page uses this single in-memory
 * shape for rendering and for drag-and-drop reordering. SignalR-driven
 * cache invalidation refetches the whole thing.
 */
export interface KanbanBoardSnapshot {
  columns: Array<{
    status: ProjectKanbanCardStatus
    name: string
    cards: ProjectKanbanCardListItemDto[]
  }>
}

/**
 * Stable query key for the project-scoped board snapshot. Exposed so
 * mutations can invalidate the cache from anywhere on the page. The
 * shape mirrors what the Orval-generated key helpers produce so a
 * board-level invalidation also catches the per-column REST queries
 * (they live under the same `/api/projects/{projectId}/kanban/...` prefix
 * and React Query does prefix-match invalidation).
 */
export function getKanbanBoardQueryKey(projectId: string): QueryKey {
  return ['/api/projects', projectId, 'kanban']
}

/**
 * Fetches the four-column board for a project in one round-trip-shaped
 * promise (board overview + per-column cards in parallel). Uses the REST
 * endpoints under `/api/projects/{projectId}/kanban` so the super-admin
 * browser session (user JWT) can hit them — the MCP equivalent requires
 * a runtime-token claim and isn't reachable from this UI.
 */
export function useKanbanBoardData(projectId: string | undefined) {
  return useQuery<KanbanBoardSnapshot>({
    queryKey: getKanbanBoardQueryKey(projectId ?? ''),
    enabled: !!projectId,
    queryFn: async ({ signal }) => {
      if (!projectId) {
        return { columns: [] }
      }
      const board: KanbanBoardColumnDto[] =
        await getApiProjectsProjectIdKanbanBoard(projectId, signal)

      // Always render the four canonical buckets in the same order, even
      // if the server omitted an empty one. This is the order the user
      // sees left-to-right; the board endpoint isn't strict about it.
      const canonicalOrder: ProjectKanbanCardStatus[] = [
        ProjectKanbanCardStatus.Backlog,
        ProjectKanbanCardStatus.Todo,
        ProjectKanbanCardStatus.InProgress,
        ProjectKanbanCardStatus.Done,
      ]

      const columnsByStatus = new Map<ProjectKanbanCardStatus, KanbanBoardColumnDto>()
      for (const c of board) columnsByStatus.set(c.status, c)

      const filled = await Promise.all(
        canonicalOrder.map(async (status) => {
          const meta = columnsByStatus.get(status)
          const cards = await getApiProjectsProjectIdKanbanColumnsStatusCards(
            projectId,
            status,
            signal,
          )
          const sorted = cards.slice().sort((a, b) => a.position - b.position)
          return {
            status,
            name: meta?.name ?? humanColumnName(status),
            cards: sorted,
          }
        }),
      )

      return { columns: filled }
    },
  })
}

function humanColumnName(status: ProjectKanbanCardStatus): string {
  switch (status) {
    case ProjectKanbanCardStatus.Backlog:
      return 'Backlog'
    case ProjectKanbanCardStatus.Todo:
      return 'Todo'
    case ProjectKanbanCardStatus.InProgress:
      return 'In Progress'
    case ProjectKanbanCardStatus.Done:
      return 'Done'
    default:
      return status
  }
}
