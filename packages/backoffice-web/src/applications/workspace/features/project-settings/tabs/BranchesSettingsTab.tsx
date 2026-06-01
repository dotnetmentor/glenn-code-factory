import { useMemo, useState } from 'react'
import {
  Box,
  Chip,
  CircularProgress,
  IconButton,
  Skeleton,
  Stack,
  Switch,
  Tooltip,
  Typography,
} from '@mui/material'
import ArchiveOutlinedIcon from '@mui/icons-material/ArchiveOutlined'
import UnarchiveOutlinedIcon from '@mui/icons-material/UnarchiveOutlined'
import { useQueryClient } from '@tanstack/react-query'
import { formatDistanceToNow, parseISO } from 'date-fns'
import {
  getGetApiProjectsProjectIdBranchesQueryKey,
  getGetApiWorkspacesSlugProjectsQueryKey,
  type ProjectBranchDto,
  useGetApiProjectsProjectIdBranches,
  usePostApiProjectsProjectIdBranchesBranchIdArchive,
  usePostApiProjectsProjectIdBranchesBranchIdUnarchive,
} from '../../../../../api/queries-commands'
import { useNotification } from '../../../../shared/contexts/NotificationContext'
import {
  bodySx,
  captionSx,
  workspaceColors,
  workspaceText,
} from '../../../shared'

interface BranchesSettingsTabProps {
  projectId: string
  slug: string
}

/**
 * Branches tab — list every branch on the project and let the user archive or
 * unarchive them. The default branch can never be archived (the backend
 * enforces this; we render the row's archive button disabled with a tooltip so
 * the rule reads on screen too).
 *
 * <p>"Show archived" is OFF by default — most of the time the user only cares
 * about live branches. Flipping it on includes archived rows at the bottom of
 * the list, sorted by {@code archivedAt} desc so the most recently archived
 * sits closest to the live branches.</p>
 */
export function BranchesSettingsTab({
  projectId,
  slug,
}: BranchesSettingsTabProps) {
  const queryClient = useQueryClient()
  const { showSuccess, showError } = useNotification()

  const [showArchived, setShowArchived] = useState(false)
  const [pendingBranchId, setPendingBranchId] = useState<string | null>(null)

  const branchesQuery = useGetApiProjectsProjectIdBranches(
    projectId,
    { includeArchived: showArchived },
    {
      query: {
        enabled: !!projectId,
      },
    },
  )

  const archiveMut = usePostApiProjectsProjectIdBranchesBranchIdArchive()
  const unarchiveMut = usePostApiProjectsProjectIdBranchesBranchIdUnarchive()

  /**
   * Sorted view of the branches:
   *  1. Default branch first.
   *  2. Live (non-archived) branches sorted by name ascending.
   *  3. Archived branches at the bottom, sorted by {@code archivedAt} desc.
   */
  const sortedBranches = useMemo<ProjectBranchDto[]>(() => {
    const all = branchesQuery.data ?? []
    const defaultBranch = all.find((b) => b.isDefault) ?? null
    const live = all
      .filter((b) => !b.isDefault && !b.isArchived)
      .slice()
      .sort((a, b) => a.name.localeCompare(b.name))
    const archived = all
      .filter((b) => b.isArchived)
      .slice()
      .sort((a, b) => {
        // archivedAt desc — most recently archived first; null falls to bottom.
        const aT = a.archivedAt ? Date.parse(a.archivedAt) : 0
        const bT = b.archivedAt ? Date.parse(b.archivedAt) : 0
        return bT - aT
      })
    return [...(defaultBranch ? [defaultBranch] : []), ...live, ...archived]
  }, [branchesQuery.data])

  // Invalidate both the branches query (any params variant — prefix match) and
  // the workspace projects query so the sidebar refreshes the visible branch
  // set. Passing only the {projectId} into the branches key drops the params
  // segment, which makes it a prefix the router invalidator matches against
  // every variant.
  const invalidateAfterMutation = () => {
    queryClient.invalidateQueries({
      queryKey: getGetApiProjectsProjectIdBranchesQueryKey(projectId),
    })
    queryClient.invalidateQueries({
      queryKey: getGetApiWorkspacesSlugProjectsQueryKey(slug),
    })
  }

  const parseBackendError = (
    err: unknown,
  ): { error?: string; message?: string } => {
    const data = (err as {
      response?: { data?: { error?: string; message?: string } }
    })?.response?.data
    return {
      error: data?.error,
      message: data?.message,
    }
  }

  const handleArchive = (branch: ProjectBranchDto) => {
    setPendingBranchId(branch.id)
    archiveMut.mutate(
      { projectId, branchId: branch.id },
      {
        onSuccess: () => {
          showSuccess(`Archived branch "${branch.name}".`)
          invalidateAfterMutation()
        },
        onError: (err: unknown) => {
          const { error, message } = parseBackendError(err)
          if (error === 'has_running_session') {
            showError(
              'Stop the running turn first to archive this branch.',
            )
          } else if (error === 'is_default') {
            showError('The default branch cannot be archived.')
          } else {
            showError(
              `Failed to archive branch: ${message ?? 'unknown error'}`,
            )
          }
        },
        onSettled: () => setPendingBranchId(null),
      },
    )
  }

  const handleUnarchive = (branch: ProjectBranchDto) => {
    setPendingBranchId(branch.id)
    unarchiveMut.mutate(
      { projectId, branchId: branch.id },
      {
        onSuccess: () => {
          showSuccess(`Restored branch "${branch.name}".`)
          invalidateAfterMutation()
        },
        onError: (err: unknown) => {
          const { message } = parseBackendError(err)
          showError(
            `Failed to restore branch: ${message ?? 'unknown error'}`,
          )
        },
        onSettled: () => setPendingBranchId(null),
      },
    )
  }

  return (
    <Stack spacing={4}>
      {/* Heading */}
      <Stack
        direction="row"
        justifyContent="space-between"
        alignItems={{ xs: 'flex-start', sm: 'center' }}
        spacing={2}
      >
        <Box sx={{ flex: 1, minWidth: 0 }}>
          <Typography
            component="h3"
            sx={{
              fontSize: '1.25rem',
              fontWeight: 400,
              letterSpacing: '-0.01em',
              color: workspaceText.primary,
              mb: 0.5,
            }}
          >
            Branches
          </Typography>
          <Typography sx={bodySx}>
            Archive branches you're done with to keep the project sidebar
            focused. The default branch can't be archived, and archiving is
            blocked while a turn is running on the branch.
          </Typography>
        </Box>
        <Stack
          direction="row"
          alignItems="center"
          spacing={1}
          sx={{ flexShrink: 0 }}
        >
          <Typography sx={captionSx}>Show archived</Typography>
          <Switch
            size="small"
            checked={showArchived}
            onChange={(e) => setShowArchived(e.target.checked)}
            inputProps={{ 'aria-label': 'Show archived branches' }}
          />
        </Stack>
      </Stack>

      {/* List */}
      <Box
        sx={{
          border: `1px solid ${workspaceColors.hairline}`,
          borderRadius: 2,
          overflow: 'hidden',
        }}
      >
        {branchesQuery.isLoading ? (
          <Stack divider={<Box sx={{ borderTop: `1px solid ${workspaceColors.hairline}` }} />}>
            {[0, 1, 2].map((i) => (
              <Box key={i} sx={{ px: 2, py: 1.5 }}>
                <Skeleton variant="text" width="50%" height={22} />
                <Skeleton variant="text" width="30%" height={16} />
              </Box>
            ))}
          </Stack>
        ) : sortedBranches.length === 0 ? (
          <Box sx={{ px: 3, py: 4, textAlign: 'center' }}>
            <Typography sx={captionSx}>No branches</Typography>
          </Box>
        ) : (
          <Stack
            divider={
              <Box sx={{ borderTop: `1px solid ${workspaceColors.hairline}` }} />
            }
          >
            {sortedBranches.map((branch) => (
              <BranchRow
                key={branch.id}
                branch={branch}
                isPending={pendingBranchId === branch.id}
                onArchive={() => handleArchive(branch)}
                onUnarchive={() => handleUnarchive(branch)}
              />
            ))}
          </Stack>
        )}
      </Box>
    </Stack>
  )
}

interface BranchRowProps {
  branch: ProjectBranchDto
  isPending: boolean
  onArchive: () => void
  onUnarchive: () => void
}

function BranchRow({
  branch,
  isPending,
  onArchive,
  onUnarchive,
}: BranchRowProps) {
  const archivedRelative = (() => {
    if (!branch.archivedAt) return null
    try {
      return formatDistanceToNow(parseISO(branch.archivedAt), {
        addSuffix: true,
      })
    } catch {
      return null
    }
  })()

  return (
    <Stack
      direction="row"
      alignItems="center"
      spacing={2}
      sx={{
        px: 2,
        py: 1.5,
        opacity: branch.isArchived ? 0.7 : 1,
      }}
    >
      <Box sx={{ flex: 1, minWidth: 0 }}>
        <Stack direction="row" alignItems="center" spacing={1}>
          <Typography
            sx={{
              fontSize: '0.9375rem',
              fontWeight: 500,
              letterSpacing: '-0.005em',
              color: workspaceText.primary,
              overflow: 'hidden',
              textOverflow: 'ellipsis',
              whiteSpace: 'nowrap',
              minWidth: 0,
            }}
            title={branch.name}
          >
            {branch.name}
          </Typography>
          {branch.isDefault && (
            <Chip
              label="default"
              size="small"
              sx={{
                height: 18,
                fontSize: '0.6875rem',
                fontWeight: 500,
                letterSpacing: '0.02em',
                backgroundColor: workspaceColors.chipBg,
                color: workspaceText.muted,
                '& .MuiChip-label': { px: 0.75 },
              }}
            />
          )}
          {branch.isArchived && (
            <Chip
              label="archived"
              size="small"
              sx={{
                height: 18,
                fontSize: '0.6875rem',
                fontWeight: 500,
                letterSpacing: '0.02em',
                backgroundColor: workspaceColors.chipBg,
                color: workspaceText.muted,
                '& .MuiChip-label': { px: 0.75 },
              }}
            />
          )}
        </Stack>
        {archivedRelative && (
          <Typography sx={{ ...captionSx, mt: 0.25, color: workspaceText.faint }}>
            Archived {archivedRelative}
          </Typography>
        )}
      </Box>

      <Box sx={{ flexShrink: 0, display: 'flex', alignItems: 'center' }}>
        {isPending ? (
          <Box sx={{ width: 32, height: 32, display: 'inline-flex', alignItems: 'center', justifyContent: 'center' }}>
            <CircularProgress size={16} />
          </Box>
        ) : branch.isArchived ? (
          <Tooltip title="Restore branch">
            <IconButton
              size="small"
              aria-label={`Restore branch ${branch.name}`}
              onClick={onUnarchive}
            >
              <UnarchiveOutlinedIcon fontSize="small" />
            </IconButton>
          </Tooltip>
        ) : branch.isDefault ? (
          <Tooltip title="The default branch cannot be archived">
            {/* MUI tooltips need a non-disabled child — wrap in a span. */}
            <span>
              <IconButton
                size="small"
                aria-label={`Archive branch ${branch.name}`}
                disabled
              >
                <ArchiveOutlinedIcon fontSize="small" />
              </IconButton>
            </span>
          </Tooltip>
        ) : (
          <Tooltip title="Archive branch">
            <IconButton
              size="small"
              aria-label={`Archive branch ${branch.name}`}
              onClick={onArchive}
            >
              <ArchiveOutlinedIcon fontSize="small" />
            </IconButton>
          </Tooltip>
        )}
      </Box>
    </Stack>
  )
}
