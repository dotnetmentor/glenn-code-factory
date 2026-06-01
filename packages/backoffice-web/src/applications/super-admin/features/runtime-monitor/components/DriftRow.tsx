import { Box, Chip, Stack, TableCell, TableRow, Typography } from '@mui/material'
import { Link as RouterLink } from 'react-router-dom'
import { RuntimeDriftDto } from '@/api/queries-commands'
import { SEVERITY_COLORS, SeverityBadge } from './SeverityBadge'
import { HeartbeatAge } from './HeartbeatAge'

const DASH = '\u2014'

interface DriftRowProps {
  row: RuntimeDriftDto
  onClick: () => void
}

export function DriftRow({ row, onClick }: DriftRowProps) {
  const accentColor = SEVERITY_COLORS[row.driftSeverity]
  const isOrphan = !row.runtimeId

  return (
    <TableRow
      hover
      onClick={onClick}
      sx={{
        cursor: 'pointer',
        '& > td': { borderLeft: 'none' },
        '& > td:first-of-type': {
          borderLeft: `4px solid ${accentColor}`,
        },
      }}
    >
      <TableCell sx={{ pl: 2 }}>
        <SeverityBadge severity={row.driftSeverity} />
      </TableCell>
      <TableCell>
        {row.projectId && row.projectName ? (
          <RouterLink
            to={`/super-admin/projects/${row.projectId}/runtime`}
            onClick={(e) => e.stopPropagation()}
            style={{ color: 'inherit', textDecoration: 'none' }}
          >
            <Typography variant="body2" sx={{ '&:hover': { textDecoration: 'underline' } }}>
              {row.projectName}
            </Typography>
          </RouterLink>
        ) : (
          <Typography variant="body2" color="text.secondary">
            {isOrphan ? 'Orphan Fly machine' : DASH}
          </Typography>
        )}
      </TableCell>
      <TableCell>
        <Typography variant="body2" color={row.branchName ? 'text.primary' : 'text.secondary'}>
          {row.branchName ?? DASH}
        </Typography>
      </TableCell>
      <TableCell>
        <Typography variant="body2" sx={{ fontFamily: 'monospace' }}>
          {row.dbState ?? DASH}
        </Typography>
      </TableCell>
      <TableCell>
        <Typography
          variant="body2"
          sx={{ fontFamily: 'monospace' }}
          color={row.flyState ? 'text.primary' : 'text.secondary'}
        >
          {row.flyState ?? DASH}
        </Typography>
      </TableCell>
      <TableCell>
        <HeartbeatAge seconds={row.secondsSinceHeartbeat} />
      </TableCell>
      <TableCell>
        <Typography variant="body2" color={row.region ? 'text.primary' : 'text.secondary'}>
          {row.region ?? DASH}
        </Typography>
      </TableCell>
      <TableCell>
        {row.driftReasons.length > 0 ? (
          <Stack direction="row" spacing={0.5} flexWrap="wrap" useFlexGap>
            {row.driftReasons.map((reason) => (
              <Chip
                key={reason}
                label={reason}
                size="small"
                sx={{ fontFamily: 'monospace', fontSize: '0.7rem' }}
              />
            ))}
          </Stack>
        ) : (
          <Box>
            <Typography variant="body2" color="text.disabled">
              {DASH}
            </Typography>
          </Box>
        )}
      </TableCell>
    </TableRow>
  )
}
