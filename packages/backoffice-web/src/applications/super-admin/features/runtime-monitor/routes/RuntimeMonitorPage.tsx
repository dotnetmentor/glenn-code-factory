import { useEffect, useState } from 'react'
import {
  Alert,
  Box,
  Button,
  Chip,
  CircularProgress,
  Skeleton,
  Snackbar,
  Stack,
  Table,
  TableBody,
  TableCell,
  TableContainer,
  TableHead,
  TableRow,
  Typography,
} from '@mui/material'
import RefreshIcon from '@mui/icons-material/Refresh'
import { RuntimeDriftDto } from '@/api/queries-commands'
import { useDriftMonitor, DriftFilter } from '../hooks/useDriftMonitor'
import { DriftRow } from '../components/DriftRow'
import { DriftDetailDrawer } from '../components/DriftDetailDrawer'

type SnackState = { open: boolean; msg: string; severity: 'success' | 'error' }

const COLUMN_COUNT = 8

export function RuntimeMonitorPage() {
  const {
    items,
    totalCount,
    driftCount,
    generatedAt,
    isLoading,
    isFetching,
    error,
    filter,
    setFilter,
    refetch,
  } = useDriftMonitor()

  const [selected, setSelected] = useState<RuntimeDriftDto | null>(null)
  const [snack, setSnack] = useState<SnackState>({ open: false, msg: '', severity: 'success' })
  const [now, setNow] = useState(Date.now())

  // Refresh the "Updated Xs ago" label every second.
  useEffect(() => {
    const id = window.setInterval(() => setNow(Date.now()), 1000)
    return () => window.clearInterval(id)
  }, [])

  // If the selected row is no longer in the list (e.g. after a force-delete),
  // close the drawer.
  useEffect(() => {
    if (!selected) return
    const stillExists = items.find(
      (it) =>
        (it.runtimeId && it.runtimeId === selected.runtimeId) ||
        (!it.runtimeId && it.flyMachineId === selected.flyMachineId),
    )
    if (!stillExists) {
      setSelected(null)
    }
  }, [items, selected])

  const notify = (msg: string, severity: SnackState['severity']) =>
    setSnack({ open: true, msg, severity })

  const updatedAgo = generatedAt ? formatUpdatedAgo(generatedAt, now) : null

  return (
    <>
      <Stack spacing={4}>
            <Box
              sx={{
                display: 'flex',
                justifyContent: 'space-between',
                alignItems: 'flex-start',
                gap: 2,
              }}
            >
              <Box>
                <Typography variant="overline" color="text.secondary">
                  Super admin
                </Typography>
                <Typography variant="h4" component="h1" sx={{ mb: 0.5 }}>
                  Runtime Monitor
                </Typography>
                <Typography variant="body2" color="text.secondary">
                  DB vs Fly reality &mdash; operator drift view.
                </Typography>
              </Box>
              <Stack alignItems="flex-end" spacing={1}>
                <Stack direction="row" alignItems="center" spacing={1}>
                  {isFetching && <CircularProgress size={14} />}
                  <Typography variant="caption" color="text.secondary">
                    {updatedAgo ? `Updated ${updatedAgo}` : 'Loading\u2026'}
                  </Typography>
                  <Button
                    size="small"
                    variant="text"
                    startIcon={<RefreshIcon fontSize="small" />}
                    onClick={refetch}
                  >
                    Refresh
                  </Button>
                </Stack>
                {!isLoading && (
                  <Typography variant="body2" color="text.secondary">
                    {totalCount} runtime{totalCount === 1 ? '' : 's'} &middot; {driftCount} in drift
                  </Typography>
                )}
              </Stack>
            </Box>

            <FilterChips filter={filter} onChange={setFilter} driftCount={driftCount} />

            {error instanceof Error && (
              <Alert
                severity="error"
                action={
                  <Button color="inherit" size="small" onClick={refetch}>
                    Retry
                  </Button>
                }
              >
                Failed to load runtime drift: {error.message}
              </Alert>
            )}

            <TableContainer>
              <Table>
                <TableHead>
                  <TableRow>
                    <TableCell sx={{ pl: 2 }}>Severity</TableCell>
                    <TableCell>Project</TableCell>
                    <TableCell>Branch</TableCell>
                    <TableCell>DB state</TableCell>
                    <TableCell>Fly state</TableCell>
                    <TableCell>Heartbeat age</TableCell>
                    <TableCell>Region</TableCell>
                    <TableCell>Drift reasons</TableCell>
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

                  {!isLoading && !error && totalCount === 0 && (
                    <TableRow>
                      <TableCell colSpan={COLUMN_COUNT}>
                        <Box sx={{ textAlign: 'center', py: 6 }}>
                          <Typography variant="h6" color="text.secondary">
                            No runtimes in the system yet.
                          </Typography>
                        </Box>
                      </TableCell>
                    </TableRow>
                  )}

                  {!isLoading && !error && totalCount > 0 && items.length === 0 && (
                    <TableRow>
                      <TableCell colSpan={COLUMN_COUNT}>
                        <Box sx={{ textAlign: 'center', py: 6 }}>
                          <Typography variant="h6" color="text.secondary" gutterBottom>
                            No runtimes match this filter
                          </Typography>
                          <Button variant="outlined" onClick={() => setFilter('all')}>
                            Show all
                          </Button>
                        </Box>
                      </TableCell>
                    </TableRow>
                  )}

                  {!isLoading &&
                    items.map((row) => (
                      <DriftRow
                        key={
                          row.runtimeId ??
                          row.flyMachineId ??
                          `${row.projectId ?? 'unk'}-${row.branchId ?? 'unk'}`
                        }
                        row={row}
                        onClick={() => setSelected(row)}
                      />
                    ))}
                </TableBody>
              </Table>
            </TableContainer>
          </Stack>

      <DriftDetailDrawer
        open={!!selected}
        row={selected}
        onClose={() => setSelected(null)}
        onNotify={notify}
      />

      <Snackbar
        open={snack.open}
        autoHideDuration={2500}
        onClose={() => setSnack((s) => ({ ...s, open: false }))}
      >
        <Alert
          onClose={() => setSnack((s) => ({ ...s, open: false }))}
          severity={snack.severity}
          variant="filled"
          sx={{ width: '100%' }}
        >
          {snack.msg}
        </Alert>
      </Snackbar>
    </>
  )
}

interface FilterChipsProps {
  filter: DriftFilter
  onChange: (next: DriftFilter) => void
  driftCount: number
}

function FilterChips({ filter, onChange, driftCount }: FilterChipsProps) {
  const options: Array<{ key: DriftFilter; label: string }> = [
    { key: 'all', label: 'All' },
    { key: 'drift', label: `Drift only${driftCount > 0 ? ` (${driftCount})` : ''}` },
    { key: 'critical', label: 'Critical only' },
  ]

  return (
    <Stack direction="row" spacing={1}>
      {options.map((opt) => (
        <Chip
          key={opt.key}
          label={opt.label}
          onClick={() => onChange(opt.key)}
          color={filter === opt.key ? 'primary' : 'default'}
          variant={filter === opt.key ? 'filled' : 'outlined'}
        />
      ))}
    </Stack>
  )
}

function formatUpdatedAgo(generatedAt: string, nowMs: number): string {
  const then = Date.parse(generatedAt)
  if (Number.isNaN(then)) return generatedAt
  const diffSecs = Math.max(0, Math.round((nowMs - then) / 1000))
  if (diffSecs < 60) return `${diffSecs}s ago`
  const m = Math.floor(diffSecs / 60)
  if (m < 60) return `${m}m ago`
  const h = Math.floor(m / 60)
  return `${h}h ago`
}
