import { Box, Card, CardContent, Chip, Stack, Tooltip, Typography } from '@mui/material'
import { useSortable } from '@dnd-kit/sortable'
import { CSS } from '@dnd-kit/utilities'
import {
  ProjectKanbanCardPriority,
  ProjectKanbanCardSource,
  type ProjectKanbanCardListItemDto,
} from '@/api/queries-commands'

interface KanbanCardProps {
  card: ProjectKanbanCardListItemDto
  onClick: () => void
}

/**
 * Compact draggable card chip for a single {@link ProjectKanbanCardListItemDto}.
 * Wraps {@link useSortable} from <c>@dnd-kit/sortable</c> — the column it
 * lives in mounts a <c>SortableContext</c> for the within-column reorder
 * UX, and the page-level <c>DndContext</c> handles cross-column moves.
 *
 * <p>Click anywhere on the card body (outside of the drag interaction)
 * opens the details dialog. The drag listeners are bound to the whole
 * card surface — dnd-kit's <c>PointerSensor</c> has an activation
 * distance threshold so a tap-to-click still feels native.</p>
 */
export function KanbanCard({ card, onClick }: KanbanCardProps) {
  const { attributes, listeners, setNodeRef, transform, transition, isDragging } =
    useSortable({ id: card.id })

  const style = {
    transform: CSS.Transform.toString(transform),
    transition,
    // Hide-but-keep-space while dragging so the drop indicator reads cleanly.
    opacity: isDragging ? 0.4 : 1,
  }

  return (
    <Box ref={setNodeRef} style={style} sx={{ touchAction: 'none' }}>
      <Card
        elevation={1}
        sx={{
          cursor: 'grab',
          '&:active': { cursor: 'grabbing' },
          '&:hover': { boxShadow: 3 },
          transition: 'box-shadow 120ms ease',
        }}
        {...attributes}
        {...listeners}
        onClick={(e) => {
          // Don't open the dialog mid-drag. dnd-kit suppresses click during
          // active drags; this is the belt-and-braces guard.
          if (isDragging) return
          e.stopPropagation()
          onClick()
        }}
      >
        <CardContent sx={{ p: 1.5, '&:last-child': { pb: 1.5 } }}>
          <Stack spacing={1}>
            <Typography
              variant="body2"
              sx={{ fontWeight: 500, lineHeight: 1.35 }}
            >
              {card.title}
            </Typography>

            <Stack
              direction="row"
              spacing={0.75}
              alignItems="center"
              flexWrap="wrap"
              sx={{ rowGap: 0.5 }}
            >
              <PriorityBadge priority={card.priority} />

              {card.source === ProjectKanbanCardSource.Agent && (
                <AgentProvenanceBadge branch={card.createdOnBranch ?? null} />
              )}

              {card.subtaskCount > 0 && (
                <Chip
                  size="small"
                  variant="outlined"
                  label={`${card.subtaskCompletedCount}/${card.subtaskCount}`}
                  sx={{ height: 20, fontSize: '0.6875rem' }}
                />
              )}

              {card.dueDate && (
                <Typography
                  variant="caption"
                  color="text.secondary"
                  sx={{ fontSize: '0.6875rem' }}
                >
                  {formatDueDate(card.dueDate)}
                </Typography>
              )}
            </Stack>
          </Stack>
        </CardContent>
      </Card>
    </Box>
  )
}

/**
 * Compact "this card was created by the agent" badge. The branch name (if
 * the daemon supplied it) is rendered alongside the robot glyph; missing
 * branch falls back to just the glyph. Human-source cards render NOTHING
 * — that's the default and we keep cards uncluttered.
 */
function AgentProvenanceBadge({ branch }: { branch: string | null }) {
  const label = branch ? `🤖 ${branch}` : '🤖'
  const tooltip = branch
    ? `Created by Agent on branch ${branch}`
    : 'Created by Agent'
  return (
    <Tooltip title={tooltip} arrow>
      <Chip
        size="small"
        variant="outlined"
        label={label}
        sx={{
          height: 20,
          fontSize: '0.6875rem',
          // Soft surface so the badge reads as informational rather than
          // an action target.
          bgcolor: 'action.hover',
        }}
      />
    </Tooltip>
  )
}

function PriorityBadge({ priority }: { priority: ProjectKanbanCardPriority }) {
  const { label, color } = priorityVisuals(priority)
  return (
    <Chip
      size="small"
      label={label}
      color={color}
      sx={{ height: 20, fontSize: '0.6875rem' }}
    />
  )
}

type ChipColor =
  | 'default'
  | 'primary'
  | 'secondary'
  | 'error'
  | 'info'
  | 'success'
  | 'warning'

function priorityVisuals(priority: ProjectKanbanCardPriority): {
  label: string
  color: ChipColor
} {
  switch (priority) {
    case ProjectKanbanCardPriority.Low:
      return { label: 'Low', color: 'default' }
    case ProjectKanbanCardPriority.Medium:
      return { label: 'Medium', color: 'primary' }
    case ProjectKanbanCardPriority.High:
      return { label: 'High', color: 'warning' }
    case ProjectKanbanCardPriority.Urgent:
      return { label: 'Urgent', color: 'error' }
    default:
      return { label: String(priority), color: 'default' }
  }
}

function formatDueDate(iso: string): string {
  try {
    const d = new Date(iso)
    if (Number.isNaN(d.getTime())) return iso
    return d.toLocaleDateString(undefined, {
      month: 'short',
      day: 'numeric',
    })
  } catch {
    return iso
  }
}
