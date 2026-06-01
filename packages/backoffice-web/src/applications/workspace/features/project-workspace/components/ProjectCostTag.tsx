import { Box, Tooltip } from '@mui/material'
import { useGetApiProjectsProjectIdCost } from '../../../../../api/queries-commands'
import { formatCostUsd } from './costFormat'

// Matches the muted-text token used throughout the workspace chrome (model
// picker, branch chip, runtime label). Inlined here so this tiny widget
// stays self-contained.
const COLOR_MUTED = 'rgba(0, 0, 0, 0.45)'

interface ProjectCostTagProps {
  projectId: string
}

/**
 * Quiet per-project lifetime-cost tag rendered alongside the project name in
 * the workspace sidebar.
 *
 * <p>Each instance fires its own {@link useGetApiProjectsProjectIdCost} query.
 * That's an N+1 in the sidebar, but a typical workspace renders single-digit
 * project rows and TanStack Query coalesces duplicate inflight requests, so
 * the cost is bounded. If the list grows large enough to feel it we'd batch
 * server-side (one rollup-all-projects endpoint) — but for v1 the per-row
 * hook keeps the wiring trivial.</p>
 *
 * <p>Returns {@code null} while loading, on error, or when the project's
 * lifetime total is exactly $0 — a brand-new project shouldn't sprout a
 * "$0.00" annotation next to its name.</p>
 */
export function ProjectCostTag({ projectId }: ProjectCostTagProps) {
  const costQuery = useGetApiProjectsProjectIdCost(projectId, {
    query: {
      enabled: !!projectId,
      staleTime: 60_000,
    },
  })
  const total = costQuery.data?.totalCostUsd ?? 0
  if (costQuery.isLoading || costQuery.isError) return null
  if (total <= 0) return null

  return (
    <Tooltip
      title={`Project lifetime spend across ${costQuery.data?.sessionCount ?? 0} session${
        (costQuery.data?.sessionCount ?? 0) === 1 ? '' : 's'
      }`}
      enterDelay={400}
      placement="right"
    >
      <Box
        component="span"
        sx={{
          flexShrink: 0,
          fontSize: '0.625rem',
          fontWeight: 500,
          color: COLOR_MUTED,
          letterSpacing: '-0.005em',
          lineHeight: 1.3,
          fontVariantNumeric: 'tabular-nums',
          cursor: 'default',
        }}
      >
        {formatCostUsd(total)}
      </Box>
    </Tooltip>
  )
}
