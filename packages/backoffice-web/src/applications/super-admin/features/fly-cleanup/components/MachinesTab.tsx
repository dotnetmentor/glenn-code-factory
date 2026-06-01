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
  type FlyMachineAdminRow,
  getGetApiAdminFlyMachinesQueryKey,
  useDeleteApiAdminFlyMachinesId,
  useGetApiAdminFlyMachines,
  usePostApiAdminFlyMachinesBulkDestroy,
} from '@/api/queries-commands'
import { useNotification } from '../../../../shared/contexts/NotificationContext'
import { workspaceRuntime } from '@/applications/workspace/shared/designTokens'
import {
  BULK_DESTROY_LIMIT,
  type MachineStateFilter,
  useFlyCleanupSelection,
} from '../hooks/useFlyCleanupSelection'
import { LinkageBadge } from './LinkageBadge'
import { BulkDestroyDialog, type BulkDestroyItem } from './BulkDestroyDialog'

const COLUMN_COUNT = 8
const MACHINE_STATES: MachineStateFilter[] = ['Started', 'Stopped', 'Suspended', 'Destroyed']

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

/** Lightweight coloured state chip — palette matches runtime-monitor's. */
function StateChip({ state }: { state: string }) {
  const palette: Record<string, string> = {
    Started: workspaceRuntime.online,
    Stopped: workspaceRuntime.suspended,
    Suspended: workspaceRuntime.booting,
    Destroyed: workspaceRuntime.failed,
  }
  const color = palette[state] ?? workspaceRuntime.suspended
  return (
    <Chip
      size="small"
      label={state}
      sx={{
        bgcolor: color,
        color: '#fff',
        fontWeight: 500,
        fontSize: '0.7rem',
      }}
    />
  )
}

export function MachinesTab() {
  const queryClient = useQueryClient()
  const { showSuccess, showError } = useNotification()

  const query = useGetApiAdminFlyMachines()
  const bulkDestroy = usePostApiAdminFlyMachinesBulkDestroy()
  const singleDestroy = useDeleteApiAdminFlyMachinesId()

  const rows = useMemo(() => query.data ?? [], [query.data])

  const {
    filtered,
    filter,
    toggleState,
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
  } = useFlyCleanupSelection<FlyMachineAdminRow>({
    rows,
    includeStateFilter: true,
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
    queryClient.invalidateQueries({ queryKey: getGetApiAdminFlyMachinesQueryKey() })
  }

  const openBulkDialog = () => {
    if (selectedCount === 0 || exceedsLimit) return
    setLastBulkResult(null)
    setBulkOpen(true)
  }

  const handleBulkConfirm = async (force: boolean) => {
    try {
      const result = await bulkDestroy.mutateAsync({
        data: { ids: selectedItems.map((it) => it.id), force },
      })
      setLastBulkResult(result)
      await queryClient.invalidateQueries({
        queryKey: getGetApiAdminFlyMachinesQueryKey(),
      })
      if (result.failed.length === 0) {
        showSuccess(`Destroyed ${result.succeeded} machines.`)
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

  const handleSingleDelete = async (row: FlyMachineAdminRow) => {
    const label = row.isOrphan
      ? `Destroy orphan machine ${row.name}?`
      : `Destroy LINKED machine ${row.name}? It maps to ${row.linkedProjectName ?? '?'} / ${row.linkedBranchName ?? '?'} — that runtime will break.`
    if (!window.confirm(label)) return
    try {
      await singleDestroy.mutateAsync({ id: row.id })
      await queryClient.invalidateQueries({
        queryKey: getGetApiAdminFlyMachinesQueryKey(),
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
            State:
          </Typography>
          {MACHINE_STATES.map((state) => (
            <Chip
              key={state}
              size="small"
              label={state}
              onClick={() => toggleState(state)}
              color={filter.states.has(state) ? 'primary' : 'default'}
              variant={filter.states.has(state) ? 'filled' : 'outlined'}
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
          Failed to load machines: {error.message}
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
            <TableCell>Name</TableCell>
            <TableCell>ID</TableCell>
            <TableCell>Region</TableCell>
            <TableCell>State</TableCell>
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
                    No machines yet
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
                    No machines match current filters
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
                  <Typography
                    variant="body2"
                    sx={{ fontFamily: 'monospace', fontSize: '0.78rem' }}
                  >
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
                  <StateChip state={row.state} />
                </TableCell>
                <TableCell>
                  <Tooltip title={row.createdAt} arrow>
                    <Typography variant="body2" color="text.secondary">
                      {formatRelative(row.createdAt)}
                    </Typography>
                  </Tooltip>
                </TableCell>
                <TableCell align="right">
                  <Tooltip title="Destroy this machine">
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
        resourceKind="machines"
        items={selectedItems}
        showForce
        isSubmitting={bulkDestroy.isPending}
        lastResult={lastBulkResult}
        onConfirm={handleBulkConfirm}
      />
    </Stack>
  )
}
