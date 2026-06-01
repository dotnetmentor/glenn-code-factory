import { Chip, Tooltip } from '@mui/material'
import { workspaceRuntime } from '@/applications/workspace/shared/designTokens'

/**
 * Shared linkage indicator for the Fly cleanup tables.
 *
 *  - {@code Orphan} (rust): the Fly resource is not referenced by any runtime
 *    row in our DB. Safe to destroy.
 *  - {@code Linked} (olive): the resource maps to a live runtime. The tooltip
 *    shows {@code projectName / branchName} so operators can sanity-check
 *    before nuking it.
 */
export interface LinkageBadgeProps {
  isOrphan: boolean
  projectName?: string | null
  branchName?: string | null
}

const ORPHAN_BG = workspaceRuntime.failed
const LINKED_BG = workspaceRuntime.online

export function LinkageBadge({ isOrphan, projectName, branchName }: LinkageBadgeProps) {
  if (isOrphan) {
    return (
      <Chip
        size="small"
        label="Orphan"
        sx={{
          bgcolor: ORPHAN_BG,
          color: '#fff',
          fontWeight: 600,
          fontSize: '0.7rem',
          letterSpacing: 0.2,
        }}
      />
    )
  }

  const tooltipLabel =
    projectName && branchName
      ? `${projectName} / ${branchName}`
      : projectName ?? branchName ?? 'Linked runtime'

  return (
    <Tooltip title={tooltipLabel} arrow>
      <Chip
        size="small"
        label="Linked"
        sx={{
          bgcolor: LINKED_BG,
          color: '#fff',
          fontWeight: 600,
          fontSize: '0.7rem',
          letterSpacing: 0.2,
        }}
      />
    </Tooltip>
  )
}
