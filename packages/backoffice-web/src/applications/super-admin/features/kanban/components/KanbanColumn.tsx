import { useCallback, useState } from 'react'
import {
  Alert,
  Box,
  Button,
  Paper,
  Stack,
  TextField,
  Typography,
} from '@mui/material'
import AddIcon from '@mui/icons-material/Add'
import { useDroppable } from '@dnd-kit/core'
import {
  SortableContext,
  verticalListSortingStrategy,
} from '@dnd-kit/sortable'
import { useQueryClient } from '@tanstack/react-query'
import {
  ProjectKanbanCardPriority,
  usePostApiProjectsProjectIdKanbanCards,
  type ProjectKanbanCardListItemDto,
  type ProjectKanbanCardStatus,
} from '@/api/queries-commands'
import { KanbanCard } from './KanbanCard'
import { getKanbanBoardQueryKey } from '../hooks/useKanbanBoardData'
import { useNotification } from '@/applications/shared/contexts/NotificationContext'

interface KanbanColumnProps {
  status: ProjectKanbanCardStatus
  name: string
  cards: ProjectKanbanCardListItemDto[]
  projectId: string
  onCardClick: (cardId: string) => void
}

/**
 * Droppable column wrapper. The page mounts one of these per status; the
 * column id passed to <c>useDroppable</c> is the status enum value, which
 * the page-level <c>onDragEnd</c> handler reads to decide cross-column vs
 * within-column drops.
 *
 * <p>Cards inside the column live in a <c>SortableContext</c> with the
 * vertical-list strategy so dnd-kit handles the within-column reorder UX
 * gestures.</p>
 *
 * <p>The footer hosts an inline "+ Add card" affordance: collapsed it's a
 * low-emphasis text button; expanded it's a single-line title TextField
 * that submits on Enter (Escape cancels). This keeps card creation in
 * place — no Dialog hop — and matches the inline subtask-add UX in
 * {@link CardDetailsDialog}.</p>
 */
export function KanbanColumn({
  status,
  name,
  cards,
  projectId,
  onCardClick,
}: KanbanColumnProps) {
  const { setNodeRef, isOver } = useDroppable({ id: status })
  const queryClient = useQueryClient()
  const { showError } = useNotification()

  const [adding, setAdding] = useState(false)
  const [draftTitle, setDraftTitle] = useState('')

  const createCard = usePostApiProjectsProjectIdKanbanCards({
    mutation: {
      onSuccess: () => {
        queryClient.invalidateQueries({
          queryKey: getKanbanBoardQueryKey(projectId),
        })
        setDraftTitle('')
        // Stay in "adding" mode so the user can fire off several in a row;
        // the explicit Cancel / Escape closes the row.
      },
      onError: () => {
        showError('Could not create card — try again.')
      },
    },
  })

  const submit = useCallback(() => {
    const trimmed = draftTitle.trim()
    if (!trimmed) return
    createCard.mutate({
      projectId,
      data: {
        title: trimmed,
        status,
        priority: ProjectKanbanCardPriority.Medium,
      },
    })
  }, [createCard, draftTitle, projectId, status])

  const cancel = useCallback(() => {
    setAdding(false)
    setDraftTitle('')
    createCard.reset()
  }, [createCard])

  return (
    <Paper
      variant="outlined"
      sx={{
        display: 'flex',
        flexDirection: 'column',
        flex: '0 0 280px',
        minWidth: 280,
        maxHeight: 'calc(100vh - 220px)',
        bgcolor: 'background.default',
        // Subtle highlight when a card is hovered over the column. Useful
        // signal for the cross-column drop, especially when the column is
        // empty (no surrounding cards to react).
        outline: isOver ? '2px solid' : '2px solid transparent',
        outlineColor: isOver ? 'primary.main' : 'transparent',
        transition: 'outline-color 120ms ease',
      }}
    >
      <Box
        sx={{
          px: 2,
          py: 1.5,
          borderBottom: 1,
          borderColor: 'divider',
        }}
      >
        <Stack direction="row" alignItems="center" spacing={1}>
          <Typography variant="subtitle2" sx={{ fontWeight: 600 }}>
            {name}
          </Typography>
          <Typography variant="caption" color="text.secondary">
            ({cards.length})
          </Typography>
        </Stack>
      </Box>

      <Box
        ref={setNodeRef}
        sx={{
          flex: 1,
          overflowY: 'auto',
          p: 1.5,
          minHeight: 80,
        }}
      >
        <SortableContext
          items={cards.map((c) => c.id)}
          strategy={verticalListSortingStrategy}
        >
          <Stack spacing={1}>
            {cards.map((card) => (
              <KanbanCard
                key={card.id}
                card={card}
                onClick={() => onCardClick(card.id)}
              />
            ))}
            {cards.length === 0 && (
              <Box
                sx={{
                  py: 4,
                  textAlign: 'center',
                  border: '1px dashed',
                  borderColor: 'divider',
                  borderRadius: 1,
                }}
              >
                <Typography variant="caption" color="text.secondary">
                  No cards
                </Typography>
              </Box>
            )}
          </Stack>
        </SortableContext>
      </Box>

      <Box
        sx={{
          px: 1.5,
          py: 1,
          borderTop: 1,
          borderColor: 'divider',
        }}
      >
        {adding ? (
          <Stack spacing={1}>
            <TextField
              value={draftTitle}
              onChange={(e) => setDraftTitle(e.target.value)}
              onKeyDown={(e) => {
                if (e.key === 'Enter') {
                  e.preventDefault()
                  submit()
                } else if (e.key === 'Escape') {
                  e.preventDefault()
                  cancel()
                }
              }}
              placeholder="Card title"
              size="small"
              fullWidth
              autoFocus
              disabled={createCard.isPending}
            />
            <Stack direction="row" spacing={1}>
              <Button
                size="small"
                variant="contained"
                onClick={submit}
                disabled={!draftTitle.trim() || createCard.isPending}
              >
                Add
              </Button>
              <Button
                size="small"
                variant="text"
                onClick={cancel}
                disabled={createCard.isPending}
              >
                Cancel
              </Button>
            </Stack>
            {createCard.error && (
              <Alert severity="error" sx={{ py: 0, px: 1 }}>
                Failed to add card. Please try again.
              </Alert>
            )}
          </Stack>
        ) : (
          <Button
            fullWidth
            size="small"
            startIcon={<AddIcon fontSize="small" />}
            onClick={() => setAdding(true)}
            sx={{
              justifyContent: 'flex-start',
              color: 'text.secondary',
              textTransform: 'none',
              fontWeight: 400,
            }}
          >
            Add card
          </Button>
        )}
      </Box>
    </Paper>
  )
}
