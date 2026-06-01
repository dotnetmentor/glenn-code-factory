import { useCallback, useMemo, useState } from 'react'
import { useParams } from 'react-router-dom'
import { useQueryClient } from '@tanstack/react-query'
import {
  Alert,
  Box,
  CircularProgress,
  Container,
  Stack,
  Typography,
} from '@mui/material'
import {
  DndContext,
  PointerSensor,
  useSensor,
  useSensors,
  type DragEndEvent,
} from '@dnd-kit/core'
import {
  usePutApiProjectsProjectIdKanbanCardsCardIdMove,
  ProjectKanbanCardStatus,
  type ProjectKanbanCardListItemDto,
} from '@/api/queries-commands'
import { usePlanningSignalR } from '@/applications/super-admin/features/specifications'
import { KanbanColumn } from './KanbanColumn'
import { CardDetailsDialog } from './CardDetailsDialog'
import {
  getKanbanBoardQueryKey,
  useKanbanBoardData,
  type KanbanBoardSnapshot,
} from '../hooks/useKanbanBoardData'
import { useNotification } from '@/applications/shared/contexts/NotificationContext'

interface KanbanBoardPageProps {
  /**
   * Overrides the URL <c>projectId</c> param. Used when the page is
   * embedded inside the workspace AppContainer's Kanban tab — there's
   * no react-router match at that level, so the parent threads the id
   * down by hand.
   */
  projectId?: string
  /**
   * When {@code true} the page suppresses its own title block + page
   * gutter — the surrounding chrome (a workspace SecondaryHeader) is
   * already labelled "Kanban", and the AppContainer owns the outer
   * padding. Defaults to {@code false} so the standalone /kanban route
   * keeps its current standalone look.
   */
  embedded?: boolean
}

/**
 * Top-level Kanban surface for one project. Renders the four canonical
 * status columns side-by-side (Backlog / Todo / InProgress / Done),
 * powered by the project-scoped MCP kanban endpoints.
 *
 * <p>Realtime: mounts the shared {@link usePlanningSignalR} hook to
 * subscribe to <c>cardChanged</c> + <c>subtaskChanged</c> notifications
 * for the current project. Each one invalidates the board query, so the
 * agent (and any concurrent operator) edits show up live.</p>
 *
 * <p>Drag and drop: a single {@link DndContext} wraps the four columns
 * and routes both within-column reorder + cross-column move events to
 * the <c>moveCard</c> MCP endpoint. We invalidate the board on settled
 * (no optimistic update yet — correctness over smoothness for this
 * card, per the brief).</p>
 *
 * <p>Also reusable inside the workspace IDE's Kanban tab — pass
 * <c>projectId</c> to bypass the URL params lookup.</p>
 */
export function KanbanBoardPage({
  projectId: projectIdProp,
  embedded = false,
}: KanbanBoardPageProps = {}) {
  const params = useParams<{ projectId: string }>()
  const projectId = projectIdProp ?? params.projectId ?? ''
  const queryClient = useQueryClient()
  const { showError } = useNotification()
  const [activeCardId, setActiveCardId] = useState<string | null>(null)

  const boardQuery = useKanbanBoardData(projectId || undefined)

  const invalidateBoard = useCallback(() => {
    if (!projectId) return
    queryClient.invalidateQueries({
      queryKey: getKanbanBoardQueryKey(projectId),
    })
  }, [projectId, queryClient])

  const onCardChanged = useCallback(() => invalidateBoard(), [invalidateBoard])
  const onSubtaskChanged = useCallback(
    () => invalidateBoard(),
    [invalidateBoard],
  )

  usePlanningSignalR(projectId || undefined, {
    onCardChanged,
    onSubtaskChanged,
  })

  const moveCard = usePutApiProjectsProjectIdKanbanCardsCardIdMove({
    mutation: {
      // The board query is refetched on settled so a failed move snaps
      // back to the authoritative server state.
      onSettled: () => invalidateBoard(),
      onError: () => showError('Could not move card — try again.'),
    },
  })

  // dnd-kit pointer sensor with a small activation distance so a click
  // (to open the dialog) doesn't accidentally trigger a drag.
  const sensors = useSensors(
    useSensor(PointerSensor, { activationConstraint: { distance: 4 } }),
  )

  const board: KanbanBoardSnapshot | undefined = boardQuery.data

  // Lookup: cardId -> { status, position } so onDragEnd can read the
  // source column without re-walking the columns. Memoized for perf.
  const cardIndex = useMemo(() => {
    const map = new Map<
      string,
      {
        card: ProjectKanbanCardListItemDto
        status: ProjectKanbanCardStatus
        index: number
      }
    >()
    if (!board) return map
    for (const column of board.columns) {
      column.cards.forEach((card, index) => {
        map.set(card.id, { card, status: column.status, index })
      })
    }
    return map
  }, [board])

  const handleDragEnd = useCallback(
    (event: DragEndEvent) => {
      const { active, over } = event
      if (!over || !board) return

      const activeId = String(active.id)
      const source = cardIndex.get(activeId)
      if (!source) return

      const overId = String(over.id)

      // Two cases: dropping on a column container (id === status enum)
      // vs dropping on another card (id === cardId). Figure out the
      // destination status + position for both.
      let destStatus: ProjectKanbanCardStatus
      let destPosition: number

      if (isStatusValue(overId)) {
        destStatus = overId
        const destColumn = board.columns.find((c) => c.status === destStatus)
        // Dropping on the column shell appends to the end.
        destPosition = destColumn ? destColumn.cards.length : 0
      } else {
        const target = cardIndex.get(overId)
        if (!target) return
        destStatus = target.status
        destPosition = target.index
      }

      // No-op drop (same status, same position) — skip the round trip.
      if (source.status === destStatus && source.index === destPosition) {
        return
      }

      moveCard.mutate({
        projectId,
        cardId: activeId,
        data: {
          newStatus: destStatus,
          newPosition: destPosition,
        },
      })
    },
    [board, cardIndex, moveCard, projectId],
  )

  if (!projectId) {
    return (
      <Container maxWidth="md" sx={{ py: 4 }}>
        <Alert severity="error">No project id in URL.</Alert>
      </Container>
    )
  }

  return (
    <Container
      maxWidth={false}
      disableGutters={embedded}
      sx={{
        py: embedded ? 2 : 4,
        // Embedded inside the AppContainer: the workspace SecondaryHeader
        // already labels this tab "Kanban" and the surrounding column owns
        // the outer gutter — so we collapse to a tight 16px x-pad. The
        // standalone /kanban route keeps the default Container gutter.
        px: embedded ? 2 : undefined,
      }}
    >
      <Stack spacing={embedded ? 2 : 3}>
        {!embedded && (
          <Box>
            <Typography variant="h4" component="h1" sx={{ mb: 1 }}>
              Kanban
            </Typography>
            <Typography variant="body1" color="text.secondary">
              Project plan board. Drag a card between columns to update its
              status; the agent reads and writes the same cards via MCP.
            </Typography>
          </Box>
        )}

        {boardQuery.isPending ? (
          <Stack direction="row" spacing={1.5} alignItems="center">
            <CircularProgress size={16} />
            <Typography variant="body2" color="text.secondary">
              Loading board…
            </Typography>
          </Stack>
        ) : boardQuery.error ? (
          <Alert severity="error">
            Failed to load board. Please refresh and try again.
          </Alert>
        ) : board ? (
          <DndContext sensors={sensors} onDragEnd={handleDragEnd}>
            <Box
              sx={{
                display: 'flex',
                gap: 2,
                overflowX: 'auto',
                pb: 1,
              }}
            >
              {board.columns.map((column) => (
                <KanbanColumn
                  key={column.status}
                  status={column.status}
                  name={column.name}
                  cards={column.cards}
                  projectId={projectId}
                  onCardClick={(id) => setActiveCardId(id)}
                />
              ))}
            </Box>
          </DndContext>
        ) : null}
      </Stack>

      {activeCardId && (
        <CardDetailsDialog
          cardId={activeCardId}
          projectId={projectId}
          open={!!activeCardId}
          onClose={() => setActiveCardId(null)}
        />
      )}
    </Container>
  )
}

const STATUS_VALUES = new Set<string>(Object.values(ProjectKanbanCardStatus))
function isStatusValue(value: string): value is ProjectKanbanCardStatus {
  return STATUS_VALUES.has(value)
}
