import { useMemo } from 'react'
import { Box, Tooltip } from '@mui/material'
import FilterListIcon from '@mui/icons-material/FilterList'
import {
  KanbanBoardPage,
  useKanbanBoardData,
} from '@/applications/super-admin/features/kanban'
import { ProjectKanbanCardStatus } from '@/api/queries-commands'
import { appContainerTokens } from './tokens'
import { SecondaryHeader } from './SecondaryHeader'

interface KanbanTabProps {
  /** Project the board belongs to. */
  projectId: string
  /** When false, the tab is hidden via {@code display: none} but stays mounted. */
  active: boolean
}

/**
 * Kanban tab — embeds the project's Kanban board (four canonical
 * status columns + drag-and-drop) inside the workspace AppContainer.
 *
 * <p>The board manages its own selection state (the
 * {@code CardDetailsDialog} portal is rendered internally), so this
 * wrapper is little more than the active-toggle envelope every tab in
 * the AppContainer follows.</p>
 *
 * <p>Chrome: renders a {@link SecondaryHeader} above the board so the
 * Kanban tab matches the Specs and Changes tabs' "label · sub · right"
 * recipe. Sub-text summarises in-progress + done totals (cheap read off
 * the same query the board mounts, so no extra fetch). The right slot
 * holds a disabled "Filter" stub mirroring the reference design —
 * filtering is on the roadmap but not wired yet.</p>
 */
export function KanbanTab({ projectId, active }: KanbanTabProps) {
  // Piggy-back on the same query the embedded board mounts — React
  // Query dedupes by key so this read is free.
  const boardQuery = useKanbanBoardData(projectId || undefined)

  const sub = useMemo(() => {
    if (!boardQuery.data) return null
    let inProgress = 0
    let done = 0
    for (const column of boardQuery.data.columns) {
      if (column.status === ProjectKanbanCardStatus.InProgress) {
        inProgress = column.cards.length
      } else if (column.status === ProjectKanbanCardStatus.Done) {
        done = column.cards.length
      }
    }
    return `${inProgress} in progress · ${done} done`
  }, [boardQuery.data])

  return (
    <Box
      sx={{
        flex: 1,
        minHeight: 0,
        display: active ? 'flex' : 'none',
        flexDirection: 'column',
        backgroundColor: appContainerTokens.canvasBg,
      }}
      aria-hidden={!active}
    >
      <SecondaryHeader
        label="Kanban"
        sub={sub}
        right={<FilterStub />}
      />

      <Box sx={{ flex: 1, minHeight: 0, overflow: 'auto' }}>
        <KanbanBoardPage projectId={projectId} embedded />
      </Box>
    </Box>
  )
}

/**
 * Disabled "Filter" stub. Reference design includes a ghost button on
 * this slot ("Filter") — placeholder for a future query/filter popover.
 * We render it disabled so the right cluster still reads as balanced
 * while signalling that filtering isn't wired yet.
 */
function FilterStub() {
  return (
    <Tooltip title="Not implemented yet" enterDelay={400}>
      <Box
        component="span"
        sx={{
          display: 'inline-flex',
          alignItems: 'center',
          gap: '5px',
          height: 26,
          px: '10px',
          border: `1px solid ${appContainerTokens.hairline}`,
          borderRadius: '7px',
          background: appContainerTokens.chipBg,
          color: appContainerTokens.textFaint,
          fontSize: '0.75rem',
          fontFamily: 'inherit',
          fontWeight: 500,
          letterSpacing: '-0.005em',
          cursor: 'default',
          userSelect: 'none',
        }}
      >
        <FilterListIcon sx={{ fontSize: 12 }} />
        <span>Filter</span>
      </Box>
    </Tooltip>
  )
}
