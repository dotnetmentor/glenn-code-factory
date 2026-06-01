import { Box } from '@mui/material'
import { workspaceTokens } from '@/applications/workspace/shared/designTokens'
import { SpecTab } from '@/applications/super-admin/features/project-runtime/components/SpecTab'

export interface RuntimeTabProps {
  projectId: string
  /**
   * Branch the runtime is pinned to. Threaded down to {@link SpecTab} so the
   * Apply History section can fetch the branch-scoped proposals endpoint —
   * without this, the section is suppressed entirely (see SpecTab line ~36).
   * Optional so super-admin-drawer callers that resolve branch differently
   * (or not at all) keep compiling.
   */
  branchId?: string
}

/**
 * Workspace-mount wrapper around the read-only / edit Spec surface. The
 * underlying {@link SpecTab} already resolves its own runtime id from the
 * spec endpoint, so this wrapper exists purely to provide a workspace-toned
 * canvas (warm paper background, hairline-bordered scroll container) around
 * the original MUI-default tab.
 */
export function RuntimeTab({ projectId, branchId }: RuntimeTabProps) {
  return (
    <Box
      sx={{
        flex: 1,
        minHeight: 0,
        display: 'flex',
        flexDirection: 'column',
        backgroundColor: workspaceTokens.canvasBg,
      }}
    >
      <SpecTab projectId={projectId} branchId={branchId} />
    </Box>
  )
}
