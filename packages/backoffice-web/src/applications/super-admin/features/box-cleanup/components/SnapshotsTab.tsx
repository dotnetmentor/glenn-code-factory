import { useMemo } from 'react'
import {
  Alert,
  Box,
  Button,
  CircularProgress,
  Chip,
  IconButton,
  Skeleton,
  Stack,
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableRow,
  Tooltip,
  Typography,
} from '@mui/material'
import RefreshIcon from '@mui/icons-material/Refresh'
import { formatDistanceToNow, parseISO } from 'date-fns'
import { useQueryClient } from '@tanstack/react-query'
import {
  type BoxSnapshotAdminRow,
  getGetApiAdminBoxSnapshotsQueryKey,
  useGetApiAdminBoxSnapshots,
} from '@/api/queries-commands'
import { useBoxCleanupSelection } from '../hooks/useBoxCleanupSelection'
import { LinkageBadge } from './LinkageBadge'

const COLUMN_COUNT = 5
const DASH = '—'

function formatRelative(iso: string | null | undefined): string {
  if (!iso) return DASH
  try {
    return formatDistanceToNow(parseISO(iso), { addSuffix: true })
  } catch {
    return iso
  }
}

function shortId(id: string): string {
  return id.length > 8 ? id.slice(-8) : id
}

/** Humanize a byte count — "512 MB", "2.3 GB". Snapshot storage is the cost. */
function formatBytes(bytes: number | null | undefined): string {
  if (bytes == null || bytes <= 0) return DASH
  const gb = bytes / (1024 * 1024 * 1024)
  if (gb >= 1) return `${gb.toFixed(1)} GB`
  const mb = bytes / (1024 * 1024)
  if (mb >= 1) return `${Math.round(mb)} MB`
  return `${Math.round(bytes / 1024)} KB`
}

/**
 * Read-only snapshot inventory. The Box API has no snapshot-delete endpoint —
 * snapshots live and die with their box (deleting the box removes them), so
 * cleanup happens in the Boxes tab. This tab exists to see where snapshot
 * storage sits and to spot orphans whose box row we no longer track.
 */
export function SnapshotsTab() {
  const queryClient = useQueryClient()

  const query = useGetApiAdminBoxSnapshots()
  const rows = useMemo(() => query.data ?? [], [query.data])

  const { filtered, filter, setOrphansOnly, setAge } =
    useBoxCleanupSelection<BoxSnapshotAdminRow>({
      rows,
      includeStatusFilter: false,
    })

  const handleRefresh = () => {
    queryClient.invalidateQueries({ queryKey: getGetApiAdminBoxSnapshotsQueryKey() })
  }

  const hasRows = rows.length > 0
  const isLoading = query.isLoading
  const isFetching = query.isFetching
  const error = query.error

  return (
    <Stack spacing={3}>
      <Alert severity="info">
        Snapshots are managed by Box and cannot be deleted directly — they are
        removed together with their box. To reclaim snapshot storage, delete the
        owning box in the Boxes tab.
      </Alert>

      {/* Filter / toolbar row */}
      <Stack direction="row" spacing={1} flexWrap="wrap" useFlexGap alignItems="center">
        <Typography variant="caption" color="text.secondary" sx={{ mr: 0.5 }}>
          Age:
        </Typography>
        {([
          { key: 'all' as const, label: 'All' },
          { key: '1d' as const, label: '>1 day' },
          { key: '7d' as const, label: '>7 days' },
          { key: '30d' as const, label: '>30 days' },
        ]).map((opt) => (
          <Chip
            key={opt.key}
            size="small"
            label={opt.label}
            onClick={() => setAge(opt.key)}
            color={filter.age === opt.key ? 'primary' : 'default'}
            variant={filter.age === opt.key ? 'filled' : 'outlined'}
          />
        ))}
        <Box sx={{ width: 16 }} />
        <Chip
          size="small"
          label="Orphans only"
          onClick={() => setOrphansOnly(!filter.orphansOnly)}
          color={filter.orphansOnly ? 'primary' : 'default'}
          variant={filter.orphansOnly ? 'filled' : 'outlined'}
        />
        <Box sx={{ flexGrow: 1 }} />
        <Tooltip title="Refresh list">
          <span>
            <IconButton onClick={handleRefresh} disabled={isFetching}>
              {isFetching ? <CircularProgress size={18} /> : <RefreshIcon />}
            </IconButton>
          </span>
        </Tooltip>
      </Stack>

      {error instanceof Error && (
        <Alert
          severity="error"
          action={
            <Button color="inherit" size="small" onClick={handleRefresh}>
              Retry
            </Button>
          }
        >
          Failed to load snapshots: {error.message}
        </Alert>
      )}

      <Table size="small">
        <TableHead>
          <TableRow>
            <TableCell>Linkage</TableCell>
            <TableCell>ID</TableCell>
            <TableCell>Box</TableCell>
            <TableCell>Size</TableCell>
            <TableCell>Created</TableCell>
          </TableRow>
        </TableHead>
        <TableBody>
          {isLoading &&
            Array.from({ length: 4 }).map((_, i) => (
              <TableRow key={`skel-${i}`}>
                {Array.from({ length: COLUMN_COUNT }).map((__, j) => (
                  <TableCell key={j}>
                    <Skeleton width="80%" />
                  </TableCell>
                ))}
              </TableRow>
            ))}

          {!isLoading && !error && !hasRows && (
            <TableRow>
              <TableCell colSpan={COLUMN_COUNT}>
                <Box sx={{ textAlign: 'center', py: 6 }}>
                  <Typography variant="h6" color="text.secondary">
                    No snapshots yet
                  </Typography>
                </Box>
              </TableCell>
            </TableRow>
          )}

          {!isLoading && !error && hasRows && filtered.length === 0 && (
            <TableRow>
              <TableCell colSpan={COLUMN_COUNT}>
                <Box sx={{ textAlign: 'center', py: 6 }}>
                  <Typography variant="body1" color="text.secondary">
                    No snapshots match current filters
                  </Typography>
                </Box>
              </TableCell>
            </TableRow>
          )}

          {!isLoading &&
            filtered.map((row) => (
              <TableRow key={row.id} hover>
                <TableCell>
                  <LinkageBadge
                    isOrphan={row.isOrphan}
                    projectName={row.linkedRuntimeId}
                  />
                </TableCell>
                <TableCell>
                  <Tooltip title={row.id} arrow>
                    <Typography
                      variant="body2"
                      sx={{ fontFamily: 'monospace', fontSize: '0.78rem' }}
                    >
                      …{shortId(row.id)}
                    </Typography>
                  </Tooltip>
                </TableCell>
                <TableCell>
                  {row.boxId ? (
                    <Tooltip title={row.boxId} arrow>
                      <Typography
                        variant="body2"
                        sx={{ fontFamily: 'monospace', fontSize: '0.78rem', color: 'text.secondary' }}
                      >
                        …{shortId(row.boxId)}
                      </Typography>
                    </Tooltip>
                  ) : (
                    <Typography variant="body2" color="text.disabled">
                      {DASH}
                    </Typography>
                  )}
                </TableCell>
                <TableCell>
                  <Typography variant="body2">{formatBytes(row.sizeBytes)}</Typography>
                </TableCell>
                <TableCell>
                  <Tooltip title={row.createdAt ?? ''} arrow>
                    <Typography variant="body2" color="text.secondary">
                      {formatRelative(row.createdAt)}
                    </Typography>
                  </Tooltip>
                </TableCell>
              </TableRow>
            ))}
        </TableBody>
      </Table>
    </Stack>
  )
}
