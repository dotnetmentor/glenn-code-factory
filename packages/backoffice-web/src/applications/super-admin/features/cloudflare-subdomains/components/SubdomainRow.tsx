import { Box, Chip, Stack, TableCell, TableRow, Tooltip, Typography } from '@mui/material'
import { formatDistanceToNow, parseISO } from 'date-fns'
import {
  SubdomainStatus,
  type SubdomainAssignmentDto,
} from '../../../../../api/queries-commands'

function formatRelative(iso: string | null | undefined): string {
  if (!iso) return ''
  try {
    return formatDistanceToNow(parseISO(iso), { addSuffix: true })
  } catch {
    return ''
  }
}

interface StatusChipProps {
  status: SubdomainAssignmentDto['status']
}

function StatusChip({ status }: StatusChipProps) {
  let color: 'default' | 'success' | 'warning' = 'default'
  let label: string = status
  if (status === SubdomainStatus.Available) {
    color = 'warning'
    label = 'Available'
  } else if (status === SubdomainStatus.Assigned) {
    color = 'success'
    label = 'Assigned'
  } else if (status === SubdomainStatus.Releasing) {
    color = 'default'
    label = 'Releasing'
  }
  return <Chip size="small" label={label} color={color} variant="outlined" />
}

export interface SubdomainRowProps {
  subdomain: SubdomainAssignmentDto
}

export function SubdomainRow({ subdomain }: SubdomainRowProps) {
  const createdLabel = formatRelative(subdomain.createdAt)
  const projectLabel =
    subdomain.assignedToProjectName && subdomain.assignedToBranchName
      ? `${subdomain.assignedToProjectName} / ${subdomain.assignedToBranchName}`
      : null

  return (
    <TableRow hover>
      <TableCell sx={{ maxWidth: 360 }}>
        <Box sx={{ minWidth: 0 }}>
          <Typography
            component="div"
            sx={{
              fontFamily: 'monospace',
              fontSize: '0.875rem',
              overflow: 'hidden',
              textOverflow: 'ellipsis',
              whiteSpace: 'nowrap',
            }}
            title={subdomain.hostname}
          >
            {subdomain.hostname}
          </Typography>
          <Tooltip title={subdomain.createdAt}>
            <Typography variant="caption" color="text.secondary">
              Created {createdLabel}
            </Typography>
          </Tooltip>
        </Box>
      </TableCell>
      <TableCell>
        <StatusChip status={subdomain.status} />
      </TableCell>
      <TableCell>
        {projectLabel ? (
          <Stack direction="row" spacing={1} alignItems="center">
            <Typography
              variant="body2"
              color="text.secondary"
              sx={{
                maxWidth: 320,
                overflow: 'hidden',
                textOverflow: 'ellipsis',
                whiteSpace: 'nowrap',
              }}
              title={projectLabel}
            >
              {projectLabel}
            </Typography>
          </Stack>
        ) : (
          <Typography variant="body2" color="text.disabled">
            —
          </Typography>
        )}
      </TableCell>
    </TableRow>
  )
}
