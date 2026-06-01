import { useMemo, useState } from 'react'
import {
  Alert,
  Box,
  Button,
  Checkbox,
  Chip,
  CircularProgress,
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
  type BulkDestroyResponse,
  type FlyVolumeAdminRow,
  getGetApiAdminFlyVolumesQueryKey,
  useDeleteApiAdminFlyVolumesId,
  useGetApiAdminFlyVolumes,
  usePostApiAdminFlyVolumesBulkDestroy,
} from '@/api/queries-commands'
import { useNotification } from '../../../../shared/contexts/NotificationContext'
import {
  BULK_DESTROY_LIMIT,
  useFlyCleanupSelection,
} from '../hooks/useFlyCleanupSelection'
import { LinkageBadge } from './LinkageBadge'
import { BulkDestroyDialog, type BulkDestroyItem } from './BulkDestroyDialog'

const COLUMN_COUNT = 10
const DASH = '\u2014'

/**
 * Fly volume states that mean "already on its way out" — the DELETE call
 * succeeded, Fly has marked the volume for GC, but the list endpoint still
 * returns it until the sweeper picks it up. We hide these by default so the
 * "still alive, please delete me" set is unambiguous.
 */
const TRANSIENT_DESTROY_STATES = new Set(['pending_destroy', 'destroyed'])

function formatRelative(iso: string): string {
  try {
    return formatDistanceToNow(parseISO(iso), { addSuffix: true })
  } catch {
    return iso
  }
}

function shortId(id: string): string {
  return id.length > 8 ? id.slice(-8) : id
}

/**
 * Map a Fly volume state to a MUI Chip color. Live = neutral, transient =
 * warning (visually fading out), failed = error.
 */
function stateChipColor(
  state: string,
): 'default' | 'success' | 'warning' | 'error' {
  if (state === 'created') return 'success'
  if (state === 'failed') return 'error'
  if (TRANSIENT_DESTROY_STATES.has(state)) return 'warning'
  return 'default'
}

export function VolumesTab() {
  const queryClient = useQueryClient()
  const { showSuccess, showError } = useNotification()

  const query = useGetApiAdminFlyVolumes()
  const bulkDestroy = usePostApiAdminFlyVolumesBulkDestroy()
  const singleDestroy = useDeleteApiAdminFlyVolumesId()

  const rows = useMemo(() => query.data ?? [], [query.data])

  // Hide `pending_destroy` + `destroyed` by default. Those are volumes Fly has
  // already accepted the DELETE for — they just haven't been GC'd out of the
  // LIST response yet. Without this filter the user keeps re-selecting and
  // re-deleting the same already-destroyed volumes (see incident: 86/100 of a
  // second bulk batch were re-deletes of the first batch).
  const [hideTransient, setHideTransient] = useState(true)

  const visibleRows = useMemo(
    () =>
      hideTransient
        ? rows.filter((r) => !TRANSIENT_DESTROY_STATES.has(r.state))
        : rows,
    [rows, hideTransient],
  )

  const hiddenCount = rows.length - visibleRows.length

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
  } = useFlyCleanupSelection<FlyVolumeAdminRow>({
    rows: visibleRows,
    includeStateFilter: false,
  })

  const [bulkOpen, setBulkOpen] = useState(false)
  const [lastBulkResult, setLastBulkResult] = useState<BulkDestroyResponse | null>(null)

  const selectedItems: BulkDestroyItem[] = useMemo(
    () =>
      filtered
        .filter((r) => selectedIds.has(r.id))
        .map((r) => ({
          id: r.id,
          name: r.name,
          isOrphan: r.isOrphan,
          linkedProjectName: r.linkedProjectName,
          linkedBranchName: r.linkedBranchName,
        })),
    [filtered, selectedIds],
  )

  const allVisibleSelected =
    filtered.length > 0 && filtered.every((r) => selectedIds.has(r.id))

  const handleRefresh = () => {
    queryClient.invalidateQueries({ queryKey: getGetApiAdminFlyVolumesQueryKey() })
  }

  const openBulkDialog = () => {
    if (selectedCount === 0 || exceedsLimit) return
    setLastBulkResult(null)
    setBulkOpen(true)
  }

  const handleBulkConfirm = async (_force: boolean) => {
    try {
      // Server ignores force for volumes — pass false to match.
      const result = await bulkDestroy.mutateAsync({
        data: { ids: selectedItems.map((it) => it.id), force: false },
      })
      setLastBulkResult(result)
      await queryClient.invalidateQueries({
        queryKey: getGetApiAdminFlyVolumesQueryKey(),
      })
      if (result.failed.length === 0) {
        showSuccess(`Destroyed ${result.succeeded} volumes.`)
        clearSelection()
        setBulkOpen(false)
      } else {
        showError(
          `Destroyed ${result.succeeded}. ${result.failed.length} failed.`,
        )
      }
    } catch {
      showError('Bulk destroy request failed.')
    }
  }

  const handleSingleDelete = async (row: FlyVolumeAdminRow) => {
    const label = row.isOrphan
      ? `Destroy orphan volume ${row.name}?`
      : `Destroy LINKED volume ${row.name}? It maps to ${row.linkedProjectName ?? '?'} / ${row.linkedBranchName ?? '?'} — that runtime will break.`
    if (!window.confirm(label)) return
    try {
      await singleDestroy.mutateAsync({ id: row.id })
      await queryClient.invalidateQueries({
        queryKey: getGetApiAdminFlyVolumesQueryKey(),
      })
      showSuccess(`Destroyed ${row.name}.`)
    } catch {
      showError(`Could not destroy ${row.name}.`)
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
          <Tooltip
            title={
              hideTransient
                ? `Hiding ${hiddenCount} volume${hiddenCount === 1 ? '' : 's'} already pending destroy (Fly is reaping them)`
                : 'Showing all volumes including those Fly is already reaping'
            }
            arrow
          >
            <Chip
              size="small"
              label={
                hideTransient
                  ? `Hide pending destroy${hiddenCount > 0 ? ` (${hiddenCount})` : ''}`
                  : `Showing pending destroy${hiddenCount > 0 ? ` (${hiddenCount})` : ''}`
              }
              onClick={() => setHideTransient(!hideTransient)}
              color={hideTransient ? 'primary' : 'warning'}
              variant={hideTransient ? 'filled' : 'filled'}
            />
          </Tooltip>
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
              Select up to {BULK_DESTROY_LIMIT} at a time
            </Typography>
          )}
          <Button
            variant="contained"
            color="error"
            startIcon={<DeleteForeverIcon />}
            disabled={selectedCount === 0 || exceedsLimit || bulkDestroy.isPending}
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
          Failed to load volumes: {error.message}
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
            <TableCell>State</TableCell>
            <TableCell>Name</TableCell>
            <TableCell>ID</TableCell>
            <TableCell>Region</TableCell>
            <TableCell>Size</TableCell>
            <TableCell>Attached to</TableCell>
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
                    No volumes yet
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
                    No volumes match current filters
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
                    projectName={row.linkedProjectName}
                    branchName={row.linkedBranchName}
                  />
                </TableCell>
                <TableCell>
                  <Chip
                    size="small"
                    label={row.state}
                    color={stateChipColor(row.state)}
                    variant={
                      TRANSIENT_DESTROY_STATES.has(row.state)
                        ? 'outlined'
                        : 'filled'
                    }
                    sx={{ fontFamily: 'monospace', fontSize: '0.72rem' }}
                  />
                </TableCell>
                <TableCell>
                  <Typography variant="body2" sx={{ fontFamily: 'monospace', fontSize: '0.78rem' }}>
                    {row.name}
                  </Typography>
                </TableCell>
                <TableCell>
                  <Tooltip title={row.id} arrow>
                    <Typography
                      variant="body2"
                      sx={{ fontFamily: 'monospace', fontSize: '0.78rem', color: 'text.secondary' }}
                    >
                      …{shortId(row.id)}
                    </Typography>
                  </Tooltip>
                </TableCell>
                <TableCell>
                  <Typography variant="body2">{row.region}</Typography>
                </TableCell>
                <TableCell>
                  <Typography variant="body2">{row.sizeGb} GB</Typography>
                </TableCell>
                <TableCell>
                  {row.attachedMachineId ? (
                    <Tooltip title={row.attachedMachineId} arrow>
                      <Typography
                        variant="body2"
                        sx={{ fontFamily: 'monospace', fontSize: '0.78rem', color: 'text.secondary' }}
                      >
                        …{shortId(row.attachedMachineId)}
                      </Typography>
                    </Tooltip>
                  ) : (
                    <Typography variant="body2" color="text.disabled">
                      {DASH}
                    </Typography>
                  )}
                </TableCell>
                <TableCell>
                  <Tooltip title={row.createdAt} arrow>
                    <Typography variant="body2" color="text.secondary">
                      {formatRelative(row.createdAt)}
                    </Typography>
                  </Tooltip>
                </TableCell>
                <TableCell align="right">
                  <Tooltip title="Destroy this volume">
                    <span>
                      <IconButton
                        size="small"
                        color="error"
                        onClick={() => handleSingleDelete(row)}
                        disabled={singleDestroy.isPending}
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

      <BulkDestroyDialog
        open={bulkOpen}
        onClose={() => setBulkOpen(false)}
        resourceKind="volumes"
        items={selectedItems}
        showForce={false}
        isSubmitting={bulkDestroy.isPending}
        lastResult={lastBulkResult}
        onConfirm={handleBulkConfirm}
      />
    </Stack>
  )
}
