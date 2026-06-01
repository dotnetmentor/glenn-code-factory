import { useCallback, useEffect, useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import {
  Alert,
  Box,
  Checkbox,
  CircularProgress,
  Dialog,
  DialogContent,
  DialogTitle,
  Divider,
  FormControl,
  IconButton,
  InputLabel,
  MenuItem,
  Select,
  Stack,
  TextField,
  Typography,
} from '@mui/material'
import DeleteOutlineIcon from '@mui/icons-material/DeleteOutline'
import AddIcon from '@mui/icons-material/Add'
import { formatDistanceToNow } from 'date-fns'
import {
  getApiProjectsProjectIdKanbanCardsCardId,
  usePutApiProjectsProjectIdKanbanCardsCardId,
  usePostApiProjectsProjectIdKanbanCardsCardIdSubtasks,
  usePutApiProjectsProjectIdKanbanCardsCardIdSubtasksSubtaskIdToggle,
  useDeleteApiProjectsProjectIdKanbanCardsCardIdSubtasksSubtaskId,
  ProjectKanbanCardPriority,
  ProjectKanbanCardSource,
  type ProjectKanbanCardDto,
  type ProjectKanbanCardSubtaskDto,
} from '@/api/queries-commands'
import { MarkdownContent } from '@/applications/shared/components/MarkdownContent'
import { getKanbanBoardQueryKey } from '../hooks/useKanbanBoardData'
import { useNotification } from '@/applications/shared/contexts/NotificationContext'

interface CardDetailsDialogProps {
  cardId: string
  projectId: string
  open: boolean
  onClose: () => void
}

/**
 * Modal editor for one {@link ProjectKanbanCardDto}. Mounts when a
 * card on the board is clicked; pulls the full card detail (including
 * subtasks) via <c>getCardDetails</c> and lets the user edit title,
 * description, priority, due date, and the subtask checklist inline.
 *
 * <p>Mutations are fire-and-forget — every successful response triggers
 * a board-query invalidation so the kanban view re-syncs. Edits to
 * title/description save on blur; the priority dropdown and due-date
 * picker save on change.</p>
 */
export function CardDetailsDialog({
  cardId,
  projectId,
  open,
  onClose,
}: CardDetailsDialogProps) {
  const queryClient = useQueryClient()
  const { showError } = useNotification()

  const detailQueryKey = ['kanban', 'card-details', projectId, cardId]
  const detail = useQuery<ProjectKanbanCardDto>({
    queryKey: detailQueryKey,
    enabled: open && !!cardId,
    queryFn: async ({ signal }) =>
      await getApiProjectsProjectIdKanbanCardsCardId(projectId, cardId, signal),
  })

  const invalidate = useCallback(() => {
    queryClient.invalidateQueries({ queryKey: detailQueryKey })
    queryClient.invalidateQueries({ queryKey: getKanbanBoardQueryKey(projectId) })
  }, [queryClient, detailQueryKey, projectId])

  const updateCard = usePutApiProjectsProjectIdKanbanCardsCardId({
    mutation: {
      onSettled: invalidate,
      onError: () => showError('Could not save card changes.'),
    },
  })
  const createSubtask = usePostApiProjectsProjectIdKanbanCardsCardIdSubtasks({
    mutation: {
      onSettled: invalidate,
      onError: () => showError('Could not add subtask.'),
    },
  })
  const toggleSubtask = usePutApiProjectsProjectIdKanbanCardsCardIdSubtasksSubtaskIdToggle({
    mutation: {
      onSettled: invalidate,
      onError: () => showError('Could not update subtask.'),
    },
  })
  const deleteSubtask = useDeleteApiProjectsProjectIdKanbanCardsCardIdSubtasksSubtaskId({
    mutation: {
      onSettled: invalidate,
      onError: () => showError('Could not delete subtask.'),
    },
  })

  // Local editor state. Initialized from the fetched card, kept in sync
  // when the server payload changes (e.g. SignalR pushed a remote edit).
  const [title, setTitle] = useState('')
  const [description, setDescription] = useState('')
  const [descriptionFocused, setDescriptionFocused] = useState(false)
  const [priority, setPriority] = useState<ProjectKanbanCardPriority>(
    ProjectKanbanCardPriority.Medium,
  )
  const [dueDate, setDueDate] = useState<string>('')
  const [newSubtaskTitle, setNewSubtaskTitle] = useState('')

  useEffect(() => {
    const card = detail.data
    if (!card) return
    setTitle(card.title)
    setDescription(card.description ?? '')
    setPriority(card.priority)
    setDueDate(card.dueDate ? toDateInputValue(card.dueDate) : '')
  }, [detail.data])

  const persistMetadata = useCallback(
    (overrides?: {
      title?: string
      description?: string | null
      priority?: ProjectKanbanCardPriority
      dueDate?: string | null
      clearDueDate?: boolean
    }) => {
      if (!detail.data) return
      const nextTitle = overrides?.title ?? title
      const nextDescription =
        overrides?.description !== undefined
          ? overrides.description
          : description
      const nextPriority = overrides?.priority ?? priority
      // dueDate handling: a "clear" intent takes precedence; otherwise
      // an empty string maps to clearDueDate=true.
      let nextDueDate: string | null = null
      let clear = false
      if (overrides?.clearDueDate) {
        clear = true
      } else if (overrides?.dueDate !== undefined) {
        if (overrides.dueDate) {
          nextDueDate = overrides.dueDate
        } else {
          clear = true
        }
      } else if (dueDate) {
        nextDueDate = new Date(dueDate).toISOString()
      } else {
        clear = true
      }

      updateCard.mutate({
        projectId,
        cardId,
        data: {
          title: nextTitle,
          description: nextDescription ?? null,
          priority: nextPriority,
          dueDate: nextDueDate,
          clearDueDate: clear,
        },
      })
    },
    [
      cardId,
      projectId,
      detail.data,
      title,
      description,
      priority,
      dueDate,
      updateCard,
    ],
  )

  const handleAddSubtask = useCallback(() => {
    const trimmed = newSubtaskTitle.trim()
    if (!trimmed) return
    createSubtask.mutate({
      projectId,
      cardId,
      data: { title: trimmed },
    })
    setNewSubtaskTitle('')
  }, [cardId, projectId, createSubtask, newSubtaskTitle])

  return (
    <Dialog open={open} onClose={onClose} fullWidth maxWidth="md">
      <DialogTitle sx={{ pb: 1 }}>Card details</DialogTitle>
      <DialogContent dividers>
        {detail.isPending ? (
          <Stack direction="row" spacing={1.5} alignItems="center" sx={{ py: 4 }}>
            <CircularProgress size={16} />
            <Typography variant="body2" color="text.secondary">
              Loading card…
            </Typography>
          </Stack>
        ) : detail.error ? (
          <Alert severity="error">
            Failed to load card. It might have been deleted.
          </Alert>
        ) : detail.data ? (
          <Stack spacing={3} sx={{ py: 1 }}>
            <TextField
              label="Title"
              value={title}
              onChange={(e) => setTitle(e.target.value)}
              onBlur={() => {
                if (title !== detail.data?.title) persistMetadata()
              }}
              fullWidth
              variant="outlined"
              size="small"
            />

            <Box>
              <Typography
                variant="caption"
                color="text.secondary"
                sx={{ display: 'block', mb: 0.5 }}
              >
                Description
              </Typography>
              {descriptionFocused ? (
                <TextField
                  value={description}
                  onChange={(e) => setDescription(e.target.value)}
                  onBlur={() => {
                    setDescriptionFocused(false)
                    if (description !== (detail.data?.description ?? '')) {
                      persistMetadata()
                    }
                  }}
                  multiline
                  minRows={4}
                  fullWidth
                  variant="outlined"
                  size="small"
                  autoFocus
                  placeholder="Markdown supported"
                />
              ) : (
                <Box
                  onClick={() => setDescriptionFocused(true)}
                  sx={{
                    border: 1,
                    borderColor: 'divider',
                    borderRadius: 1,
                    p: 1.5,
                    minHeight: 100,
                    cursor: 'text',
                    bgcolor: 'background.default',
                  }}
                >
                  {description.trim() ? (
                    <MarkdownContent content={description} />
                  ) : (
                    <Typography variant="body2" color="text.disabled">
                      Click to add a description (markdown supported)
                    </Typography>
                  )}
                </Box>
              )}
            </Box>

            <Stack direction="row" spacing={2}>
              <FormControl size="small" sx={{ minWidth: 160 }}>
                <InputLabel id="priority-label">Priority</InputLabel>
                <Select
                  labelId="priority-label"
                  label="Priority"
                  value={priority}
                  onChange={(e) => {
                    const next = e.target.value as ProjectKanbanCardPriority
                    setPriority(next)
                    persistMetadata({ priority: next })
                  }}
                >
                  {Object.values(ProjectKanbanCardPriority).map((p) => (
                    <MenuItem key={p} value={p}>
                      {p}
                    </MenuItem>
                  ))}
                </Select>
              </FormControl>

              <Box>
                <Typography
                  variant="caption"
                  color="text.secondary"
                  sx={{ display: 'block', mb: 0.5 }}
                >
                  Due date
                </Typography>
                <input
                  type="date"
                  value={dueDate}
                  onChange={(e) => {
                    const next = e.target.value
                    setDueDate(next)
                    if (next) {
                      persistMetadata({
                        dueDate: new Date(next).toISOString(),
                      })
                    } else {
                      persistMetadata({ clearDueDate: true })
                    }
                  }}
                  style={{
                    padding: '8.5px 14px',
                    fontSize: '0.875rem',
                    border: '1px solid rgba(0,0,0,0.23)',
                    borderRadius: 4,
                    fontFamily: 'inherit',
                  }}
                />
              </Box>
            </Stack>

            <Divider />

            <Box>
              <Typography variant="subtitle2" sx={{ mb: 1, fontWeight: 600 }}>
                Subtasks ({detail.data.subtasks.length})
              </Typography>

              <Stack spacing={0.5}>
                {detail.data.subtasks
                  .slice()
                  .sort((a, b) => a.position - b.position)
                  .map((subtask) => (
                    <SubtaskRow
                      key={subtask.id}
                      subtask={subtask}
                      onToggle={() =>
                        toggleSubtask.mutate({
                          projectId,
                          cardId,
                          subtaskId: subtask.id,
                        })
                      }
                      onDelete={() =>
                        deleteSubtask.mutate({
                          projectId,
                          cardId,
                          subtaskId: subtask.id,
                        })
                      }
                    />
                  ))}

                <Stack
                  direction="row"
                  spacing={1}
                  alignItems="center"
                  sx={{ pt: 1 }}
                >
                  <AddIcon fontSize="small" color="action" />
                  <TextField
                    value={newSubtaskTitle}
                    onChange={(e) => setNewSubtaskTitle(e.target.value)}
                    onKeyDown={(e) => {
                      if (e.key === 'Enter') {
                        e.preventDefault()
                        handleAddSubtask()
                      }
                    }}
                    placeholder="Add a subtask and press Enter"
                    size="small"
                    fullWidth
                    variant="standard"
                  />
                </Stack>
              </Stack>
            </Box>

            <ProvenanceFooter card={detail.data} />
          </Stack>
        ) : null}
      </DialogContent>
    </Dialog>
  )
}

/**
 * Small caption row at the bottom of the details dialog: who/where the
 * card came from + how long ago. We don't surface a human's display name
 * yet (the card payload doesn't carry it), so a Human card just reads
 * "Created &lt;relative time&gt;" — that's spec-aligned and a separate
 * card will layer the human's identity later.
 */
function ProvenanceFooter({ card }: { card: ProjectKanbanCardDto }) {
  const relative = formatRelative(card.createdAt)

  let text: string
  if (card.source === ProjectKanbanCardSource.Agent) {
    text = card.createdOnBranch
      ? `Created by Agent on branch ${card.createdOnBranch} · ${relative}`
      : `Created by Agent · ${relative}`
  } else {
    text = `Created ${relative}`
  }

  return (
    <Typography variant="caption" color="text.secondary">
      {text}
    </Typography>
  )
}

function formatRelative(iso: string): string {
  try {
    const d = new Date(iso)
    if (Number.isNaN(d.getTime())) return iso
    return formatDistanceToNow(d, { addSuffix: true })
  } catch {
    return iso
  }
}

interface SubtaskRowProps {
  subtask: ProjectKanbanCardSubtaskDto
  onToggle: () => void
  onDelete: () => void
}

function SubtaskRow({ subtask, onToggle, onDelete }: SubtaskRowProps) {
  return (
    <Stack direction="row" alignItems="center" spacing={0.5}>
      <Checkbox
        checked={subtask.isCompleted}
        onChange={onToggle}
        size="small"
        sx={{ p: 0.5 }}
      />
      <Typography
        variant="body2"
        sx={{
          flex: 1,
          textDecoration: subtask.isCompleted ? 'line-through' : 'none',
          color: subtask.isCompleted ? 'text.disabled' : 'text.primary',
        }}
      >
        {subtask.title}
      </Typography>
      <IconButton size="small" onClick={onDelete} aria-label="Delete subtask">
        <DeleteOutlineIcon fontSize="small" />
      </IconButton>
    </Stack>
  )
}

/**
 * Server gives an ISO timestamp; the native date input wants
 * <c>YYYY-MM-DD</c>. We slice the ISO string rather than format with
 * date-fns to avoid a timezone shift on display.
 */
function toDateInputValue(iso: string): string {
  if (!iso) return ''
  // Accept both full ISO and bare date strings.
  const datePart = iso.split('T')[0]
  return datePart ?? ''
}
