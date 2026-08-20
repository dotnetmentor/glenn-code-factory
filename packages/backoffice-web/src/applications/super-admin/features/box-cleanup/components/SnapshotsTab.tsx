import { useMemo, useState } from 'react'
import {
  Alert,
  Box,
  Button,
  Checkbox,
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
import DeleteIcon from '@mui/icons-material/Delete'
import DeleteForeverIcon from '@mui/icons-material/DeleteForever'
import { formatDistanceToNow, parseISO } from 'date-fns'
import { useQueryClient } from '@tanstack/react-query'
import {
  type BoxSnapshotAdminRow,
  type BulkDeleteResponse,
  getGetApiAdminBoxSnapshotsQueryKey,
  useDeleteApiAdminBoxSnapshotsId,
  useGetApiAdminBoxSnapshots,
  usePostApiAdminBoxSnapshotsBulkDelete,
} from '@/api/queries-commands'
import { useNotification } from '../../../../shared/contexts/NotificationContext'
import {
  BULK_DELETE_LIMIT,
  useBoxCleanupSelection,
} from '../hooks/useBoxCleanupSelection'
import { LinkageBadge } from './LinkageBadge'
import { BulkDeleteDialog, type BulkDeleteItem } from './BulkDeleteDialog'

const COLUMN_COUNT = 7
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

export function SnapshotsTab() {
  const queryClient = useQueryClient()
  const { showSuccess, showError } = useNotification()

  const query = useGetApiAdminBoxSnapshots()
  const bulkDelete = usePostApiAdminBoxSnapshotsBulkDelete()
  const singleDelete = useDeleteApiAdminBoxSnapshotsId()

  const rows = useMemo(() => query.data ?? [], [query.data])

  const {
    filtered,
    filter,
    setOrphansOnly,
    setAge,
    selectedIds,
    isSelected,
    toggleOne,
    selectAllVisible,
    selectVisibleOrphans,
    clearSelection,
    selectedCount,
    exceedsLimit,
  } = useBoxCleanupSelection<BoxSnapshotAdminRow>({
    rows,
    includeStatusFilter: false,
  })

  const [bulkOpen, setBulkOpen] = useState(false)
  const [lastBulkResult, setLastBulkResult] = useState<BulkDeleteResponse | null>(null)

  const selectedItems: BulkDeleteItem[] = useMemo(
    () =>
      filtered
        .filter((r) => selectedIds.has(r.id))
        .map((r) => ({
          id: r.id,
          name: r.id,
          isOrphan: r.isOrphan,
        })),
    [filtered, selectedIds],
  )

  const allVisibleSelected =
    filtered.length > 0 && filtered.every((r) => selectedIds.has(r.id))

  const handleRefresh = () => {
    queryClient.invalidateQueries({ queryKey: getGetApiAdminBoxSnapshotsQueryKey() })
  }

  const openBulkDialog = () => {
    if (selectedCount === 0 || exceedsLimit) return
    setLastBulkResult(null)
    setBulkOpen(true)
  }

  const handleBulkConfirm = async () => {
    try {
      const result = await bulkDelete.mutateAsync({
        data: { ids: selectedItems.map((it) => it.id) },
      })
      setLastBulkResult(result)
      await queryClient.invalidateQueries({
        queryKey: getGetApiAdminBoxSnapshotsQueryKey(),
      })
      if (result.failed.length === 0) {
        showSuccess(`Deleted ${result.succeeded} snapshots.`)
        clearSelection()
        setBulkOpen(false)
      } else {
        showError(
          `Deleted ${result.succeeded}. ${result.failed.length} failed.`,
        )
      }
    } catch {
      showError('Bulk delete request failed.')
    }
  }

  const handleSingleDelete = async (row: BoxSnapshotAdminRow) => {
    const label = row.isOrphan
      ? `Delete orphan snapshot ${shortId(row.id)}?`
      : `Delete LINKED snapshot ${shortId(row.id)}? Runtime ${row.linkedRuntimeId ?? '?'} still references it — resuming that runtime will break.`
    if (!window.confirm(label)) return
    try {
      await singleDelete.mutateAsync({ id: row.id })
      await queryClient.invalidateQueries({
        queryKey: getGetApiAdminBoxSnapshotsQueryKey(),
      })
      showSuccess(`Deleted snapshot ${shortId(row.id)}.`)
    } catch {
      showError(`Could not delete snapshot ${shortId(row.id)}.`)
    }
  }

  const handleToggleAllVisible = () => {
    if (allVisibleSelected) clearSelection()
    else selectAllVisible()
  }

  const hasRows = rows.length > 0
  const isLoading = query.isLoading
  const isFetching = query.isFetching
  const error = query.error

  return (
    <Stack spacing={3}>
      {/* Filter / toolbar row */}
      <Stack spacing={1.5}>
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
        </Stack>

        <Stack direction="row" spacing={1.5} flexWrap="wrap" useFlexGap alignItems="center">
          <Button size="small" variant="outlined" onClick={selectAllVisible}>
            Select all visible
          </Button>
          <Button size="small" variant="outlined" onClick={selectVisibleOrphans}>
            Select orphans
          </Button>
          <Button size="small" variant="text" onClick={clearSelection} disabled={selectedCount === 0}>
            Clear selection
          </Button>
          <Box sx={{ flexGrow: 1 }} />
          {exceedsLimit && (
            <Typography variant="caption" color="error">
              Select up to {BULK_DELETE_LIMIT} at a time
            </Typography>
          )}
          <Button
            variant="contained"
            color="error"
            startIcon={<DeleteForeverIcon />}
            disabled={selectedCount === 0 || exceedsLimit || bulkDelete.isPending}
            onClick={openBulkDialog}
          >
            Delete {selectedCount} selected
          </Button>
          <Tooltip title="Refresh list">
            <span>
              <IconButton onClick={handleRefresh} disabled={isFetching}>
                {isFetching ? <CircularProgress size={18} /> : <RefreshIcon />}
              </IconButton>
            </span>
          </Tooltip>
        </Stack>
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
            <TableCell padding="checkbox">
              <Checkbox
                size="small"
                checked={allVisibleSelected}
                indeterminate={!allVisibleSelected && selectedCount > 0}
                onChange={handleToggleAllVisible}
                disabled={filtered.length === 0}
              />
            </TableCell>
            <TableCell>Linkage</TableCell>
            <TableCell>ID</TableCell>
            <TableCell>Box</TableCell>
            <TableCell>Size</TableCell>
            <TableCell>Created</TableCell>
            <TableCell align="right">Actions</TableCell>
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
              <TableRow key={row.id} hover selected={isSelected(row.id)}>
                <TableCell padding="checkbox">
                  <Checkbox
                    size="small"
                    checked={isSelected(row.id)}
                    onChange={() => toggleOne(row.id)}
                  />
                </TableCell>
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
                <TableCell align="right">
                  <Tooltip title="Delete this snapshot">
                    <span>
                      <IconButton
                        size="small"
                        color="error"
                        onClick={() => handleSingleDelete(row)}
                        disabled={singleDelete.isPending}
                      >
                        <DeleteIcon fontSize="small" />
                      </IconButton>
                    </span>
                  </Tooltip>
                </TableCell>
              </TableRow>
            ))}
        </TableBody>
      </Table>

      <BulkDeleteDialog
        open={bulkOpen}
        onClose={() => setBulkOpen(false)}
        resourceKind="snapshots"
        items={selectedItems}
        isSubmitting={bulkDelete.isPending}
        lastResult={lastBulkResult}
        onConfirm={handleBulkConfirm}
      />
    </Stack>
  )
}
