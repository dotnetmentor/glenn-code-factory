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
import StopIcon from '@mui/icons-material/Stop'
import PlayArrowIcon from '@mui/icons-material/PlayArrow'
import { formatDistanceToNow, parseISO } from 'date-fns'
import { useQueryClient } from '@tanstack/react-query'
import {
  type BoxAdminRow,
  type BulkDeleteResponse,
  getGetApiAdminBoxBoxesQueryKey,
  useDeleteApiAdminBoxBoxesId,
  useGetApiAdminBoxBoxes,
  usePostApiAdminBoxBoxesBulkDelete,
  usePostApiAdminBoxBoxesIdResume,
  usePostApiAdminBoxBoxesIdStop,
} from '@/api/queries-commands'
import { useNotification } from '../../../../shared/contexts/NotificationContext'
import { workspaceRuntime } from '@/applications/workspace/shared/designTokens'
import {
  BULK_DELETE_LIMIT,
  type BoxStatusFilter,
  useBoxCleanupSelection,
} from '../hooks/useBoxCleanupSelection'
import { LinkageBadge } from './LinkageBadge'
import { BulkDeleteDialog, type BulkDeleteItem } from './BulkDeleteDialog'

const COLUMN_COUNT = 10
const BOX_STATUSES: BoxStatusFilter[] = [
  'ready',
  'running',
  'idle',
  'provisioning',
  'archived',
  'error',
]
const DASH = '—'

/** Statuses where the box VM is alive and a Stop makes sense. */
const STOPPABLE_STATUSES = new Set(['ready', 'running', 'idle'])
/** Statuses where the box is parked and a Resume makes sense. */
const RESUMABLE_STATUSES = new Set(['archived', 'stopped', 'suspended'])

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

/**
 * Humanize a TTL in seconds — "45m", "5.8h", "3.2d". Boxes self-archive at
 * TTL, so this is "time until the box parks itself", not a deadline we have
 * to beat.
 */
function formatTtl(seconds: number | null | undefined): string {
  if (seconds == null || seconds <= 0) return DASH
  if (seconds < 3600) return `${Math.round(seconds / 60)}m`
  if (seconds < 48 * 3600) return `${(seconds / 3600).toFixed(1)}h`
  return `${(seconds / 86400).toFixed(1)}d`
}

/** Lightweight coloured status chip — palette matches runtime-monitor's. */
function StatusChip({ status }: { status: string }) {
  const palette: Record<string, string> = {
    // Alive and serving — success-ish.
    ready: workspaceRuntime.online,
    running: workspaceRuntime.online,
    idle: workspaceRuntime.online,
    // Still coming up — info.
    provisioning: workspaceRuntime.booting,
    // Parked at TTL — neutral (cost already stopped).
    archived: workspaceRuntime.suspended,
    error: workspaceRuntime.failed,
  }
  const color = palette[status.toLowerCase()] ?? workspaceRuntime.suspended
  return (
    <Chip
      size="small"
      label={status}
      sx={{
        bgcolor: color,
        color: '#fff',
        fontWeight: 500,
        fontSize: '0.7rem',
      }}
    />
  )
}

export function BoxesTab() {
  const queryClient = useQueryClient()
  const { showSuccess, showError } = useNotification()

  const query = useGetApiAdminBoxBoxes()
  const bulkDelete = usePostApiAdminBoxBoxesBulkDelete()
  const singleDelete = useDeleteApiAdminBoxBoxesId()
  const stopBox = usePostApiAdminBoxBoxesIdStop()
  const resumeBox = usePostApiAdminBoxBoxesIdResume()

  const rows = useMemo(() => query.data ?? [], [query.data])

  const {
    filtered,
    filter,
    toggleStatus,
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
  } = useBoxCleanupSelection<BoxAdminRow>({
    rows,
    includeStatusFilter: true,
  })

  const [bulkOpen, setBulkOpen] = useState(false)
  const [lastBulkResult, setLastBulkResult] = useState<BulkDeleteResponse | null>(null)

  const selectedItems: BulkDeleteItem[] = useMemo(
    () =>
      filtered
        .filter((r) => selectedIds.has(r.id))
        .map((r) => ({
          id: r.id,
          name: r.name ?? r.id,
          isOrphan: r.isOrphan,
          linkedProjectName: r.linkedProjectName,
          linkedBranchName: r.linkedBranchName,
        })),
    [filtered, selectedIds],
  )

  // Template boxes never participate in bulk selection (the backend refuses
  // to delete them anyway), so "all selected" is judged against the rest.
  const selectableRows = useMemo(
    () => filtered.filter((r) => !r.isTemplate),
    [filtered],
  )
  const allVisibleSelected =
    selectableRows.length > 0 && selectableRows.every((r) => selectedIds.has(r.id))

  const handleRefresh = () => {
    queryClient.invalidateQueries({ queryKey: getGetApiAdminBoxBoxesQueryKey() })
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
        queryKey: getGetApiAdminBoxBoxesQueryKey(),
      })
      if (result.failed.length === 0) {
        showSuccess(`Deleted ${result.succeeded} boxes.`)
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

  const handleSingleDelete = async (row: BoxAdminRow) => {
    const name = row.name ?? row.id
    const label = row.isOrphan
      ? `Delete orphan box ${name}?`
      : `Delete LINKED box ${name}? It maps to ${row.linkedProjectName ?? '?'} / ${row.linkedBranchName ?? '?'} — that runtime will break.`
    if (!window.confirm(label)) return
    try {
      await singleDelete.mutateAsync({ id: row.id })
      await queryClient.invalidateQueries({
        queryKey: getGetApiAdminBoxBoxesQueryKey(),
      })
      showSuccess(`Deleted ${name}.`)
    } catch {
      showError(`Could not delete ${name}.`)
    }
  }

  const handleStop = async (row: BoxAdminRow) => {
    const name = row.name ?? row.id
    try {
      await stopBox.mutateAsync({ id: row.id })
      await queryClient.invalidateQueries({
        queryKey: getGetApiAdminBoxBoxesQueryKey(),
      })
      showSuccess(`Stopped ${name}.`)
    } catch {
      showError(`Could not stop ${name}.`)
    }
  }

  const handleResume = async (row: BoxAdminRow) => {
    const name = row.name ?? row.id
    try {
      await resumeBox.mutateAsync({ id: row.id })
      await queryClient.invalidateQueries({
        queryKey: getGetApiAdminBoxBoxesQueryKey(),
      })
      showSuccess(`Resumed ${name}.`)
    } catch {
      showError(`Could not resume ${name}.`)
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
            Status:
          </Typography>
          {BOX_STATUSES.map((status) => (
            <Chip
              key={status}
              size="small"
              label={status}
              onClick={() => toggleStatus(status)}
              color={filter.statuses.has(status) ? 'primary' : 'default'}
              variant={filter.statuses.has(status) ? 'filled' : 'outlined'}
            />
          ))}
          <Box sx={{ width: 16 }} />
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
          Failed to load boxes: {error.message}
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
                disabled={selectableRows.length === 0}
              />
            </TableCell>
            <TableCell>Linkage</TableCell>
            <TableCell>Name</TableCell>
            <TableCell>ID</TableCell>
            <TableCell>Status</TableCell>
            <TableCell>Size</TableCell>
            <TableCell>Region</TableCell>
            <TableCell>TTL</TableCell>
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
                    No boxes yet
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
                    No boxes match current filters
                  </Typography>
                </Box>
              </TableCell>
            </TableRow>
          )}

          {!isLoading &&
            filtered.map((row) => {
              const status = row.status.toLowerCase()
              return (
                <TableRow key={row.id} hover selected={isSelected(row.id)}>
                  <TableCell padding="checkbox">
                    <Tooltip
                      title={row.isTemplate ? 'Template boxes cannot be deleted' : ''}
                      arrow
                    >
                      <span>
                        <Checkbox
                          size="small"
                          checked={isSelected(row.id)}
                          onChange={() => toggleOne(row.id)}
                          disabled={row.isTemplate}
                        />
                      </span>
                    </Tooltip>
                  </TableCell>
                  <TableCell>
                    <Stack direction="row" spacing={0.5} alignItems="center">
                      <LinkageBadge
                        isOrphan={row.isOrphan}
                        projectName={row.linkedProjectName}
                        branchName={row.linkedBranchName}
                      />
                      {row.isTemplate && (
                        <Tooltip title="Golden template box — fork source for new runtimes" arrow>
                          <Chip
                            size="small"
                            label="template"
                            variant="outlined"
                            sx={{ fontWeight: 600, fontSize: '0.7rem', letterSpacing: 0.2 }}
                          />
                        </Tooltip>
                      )}
                    </Stack>
                  </TableCell>
                  <TableCell>
                    <Typography
                      variant="body2"
                      sx={{ fontFamily: 'monospace', fontSize: '0.78rem' }}
                    >
                      {row.name ?? DASH}
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
                    <StatusChip status={row.status} />
                  </TableCell>
                  <TableCell>
                    <Typography variant="body2">{row.size ?? DASH}</Typography>
                  </TableCell>
                  <TableCell>
                    <Typography variant="body2">{row.region ?? DASH}</Typography>
                  </TableCell>
                  <TableCell>
                    <Typography variant="body2" color="text.secondary">
                      {formatTtl(row.ttlSeconds)}
                    </Typography>
                  </TableCell>
                  <TableCell>
                    <Tooltip title={row.createdAt ?? ''} arrow>
                      <Typography variant="body2" color="text.secondary">
                        {formatRelative(row.createdAt)}
                      </Typography>
                    </Tooltip>
                  </TableCell>
                  <TableCell align="right">
                    <Stack direction="row" spacing={0.5} justifyContent="flex-end">
                      {STOPPABLE_STATUSES.has(status) && (
                        <Tooltip title="Stop this box">
                          <span>
                            <IconButton
                              size="small"
                              onClick={() => handleStop(row)}
                              disabled={stopBox.isPending}
                            >
                              <StopIcon fontSize="small" />
                            </IconButton>
                          </span>
                        </Tooltip>
                      )}
                      {RESUMABLE_STATUSES.has(status) && (
                        <Tooltip title="Resume this box">
                          <span>
                            <IconButton
                              size="small"
                              onClick={() => handleResume(row)}
                              disabled={resumeBox.isPending}
                            >
                              <PlayArrowIcon fontSize="small" />
                            </IconButton>
                          </span>
                        </Tooltip>
                      )}
                      <Tooltip
                        title={
                          row.isTemplate
                            ? 'Template boxes cannot be deleted'
                            : 'Delete this box'
                        }
                      >
                        <span>
                          <IconButton
                            size="small"
                            color="error"
                            onClick={() => handleSingleDelete(row)}
                            disabled={singleDelete.isPending || row.isTemplate}
                          >
                            <DeleteIcon fontSize="small" />
                          </IconButton>
                        </span>
                      </Tooltip>
                    </Stack>
                  </TableCell>
                </TableRow>
              )
            })}
        </TableBody>
      </Table>

      <BulkDeleteDialog
        open={bulkOpen}
        onClose={() => setBulkOpen(false)}
        resourceKind="boxes"
        items={selectedItems}
        isSubmitting={bulkDelete.isPending}
        lastResult={lastBulkResult}
        onConfirm={handleBulkConfirm}
      />
    </Stack>
  )
}
