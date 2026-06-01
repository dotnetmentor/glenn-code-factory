import { useState } from 'react'
import { AxiosError } from 'axios'
import { useQueryClient } from '@tanstack/react-query'
import {
  Alert,
  Box,
  Button,
  Dialog,
  DialogActions,
  DialogContent,
  DialogContentText,
  DialogTitle,
  Divider,
  Drawer,
  IconButton,
  Stack,
  Typography,
} from '@mui/material'
import CloseIcon from '@mui/icons-material/Close'
import RestartAltIcon from '@mui/icons-material/RestartAlt'
import PauseCircleOutlineIcon from '@mui/icons-material/PauseCircleOutline'
import DeleteForeverIcon from '@mui/icons-material/DeleteForever'
import { Link as RouterLink } from 'react-router-dom'
import {
  ProblemDetails,
  RuntimeDriftDto,
  getGetApiAdminRuntimesDriftQueryKey,
  usePostApiAdminRuntimesIdForceDelete,
  usePostApiAdminRuntimesIdForceSuspend,
  usePostApiAdminRuntimesIdReset,
} from '@/api/queries-commands'
import { getErrorMessage } from '@/applications/shared/utils/errorUtils'
import { SeverityBadge } from './SeverityBadge'
import { HeartbeatAge } from './HeartbeatAge'

const DASH = '\u2014'

const DRIFT_REASON_LABELS: Record<string, string> = {
  MachineVanished: 'Fly machine has been deleted but DB still tracks it',
  OrphanFlyMachine: 'Fly machine exists with no matching DB row',
  StateMismatch_OnlineButStopped: 'DB thinks online but Fly says stopped',
  StateMismatch_SuspendedButStarted: 'DB thinks suspended but Fly is running',
  StateMismatch_OnlineButNotStarted: 'DB thinks online but Fly is not started',
  StuckInTransition: 'Stuck in a transitional state for over 5 minutes',
  StaleHeartbeat: 'Daemon heartbeat is stale (>60s)',
}

interface DriftDetailDrawerProps {
  open: boolean
  row: RuntimeDriftDto | null
  onClose: () => void
  onNotify: (msg: string, severity: 'success' | 'error') => void
}

export function DriftDetailDrawer({ open, row, onClose, onNotify }: DriftDetailDrawerProps) {
  const queryClient = useQueryClient()
  const [confirmDelete, setConfirmDelete] = useState(false)

  const invalidate = () =>
    queryClient.invalidateQueries({ queryKey: getGetApiAdminRuntimesDriftQueryKey() })

  const resetMutation = usePostApiAdminRuntimesIdReset({
    mutation: {
      onSuccess: () => {
        invalidate()
        onNotify('Runtime reset to Pending', 'success')
      },
      onError: (error: AxiosError<ProblemDetails>) => onNotify(getErrorMessage(error), 'error'),
    },
  })

  const suspendMutation = usePostApiAdminRuntimesIdForceSuspend({
    mutation: {
      onSuccess: () => {
        invalidate()
        onNotify('Runtime force-suspended', 'success')
      },
      onError: (error: AxiosError<ProblemDetails>) => onNotify(getErrorMessage(error), 'error'),
    },
  })

  const deleteMutation = usePostApiAdminRuntimesIdForceDelete({
    mutation: {
      onSuccess: () => {
        invalidate()
        onNotify('Runtime force-deleted', 'success')
        setConfirmDelete(false)
        onClose()
      },
      onError: (error: AxiosError<ProblemDetails>) => {
        onNotify(getErrorMessage(error), 'error')
        setConfirmDelete(false)
      },
    },
  })

  const isOrphan = !row?.runtimeId
  const anyPending =
    resetMutation.isPending || suspendMutation.isPending || deleteMutation.isPending

  const handleReset = () => {
    if (!row?.runtimeId) return
    resetMutation.mutate({ id: row.runtimeId })
  }
  const handleSuspend = () => {
    if (!row?.runtimeId) return
    suspendMutation.mutate({ id: row.runtimeId })
  }
  const handleDelete = () => {
    if (!row?.runtimeId) return
    deleteMutation.mutate({ id: row.runtimeId })
  }

  return (
    <>
      <Drawer
        anchor="right"
        open={open}
        onClose={onClose}
        slotProps={{ paper: { sx: { width: 480, maxWidth: '100vw' } } }}
      >
        {row && (
          <Box sx={{ p: 3 }}>
            <Box sx={{ display: 'flex', justifyContent: 'space-between', alignItems: 'flex-start' }}>
              <Box>
                <Typography variant="overline" color="text.secondary">
                  Runtime detail
                </Typography>
                <Typography variant="h5" sx={{ mb: 0.5 }}>
                  {row.projectName ?? (isOrphan ? 'Orphan Fly machine' : 'Unknown project')}
                </Typography>
                {row.branchName && (
                  <Typography variant="body2" color="text.secondary">
                    Branch: {row.branchName}
                  </Typography>
                )}
                {row.runtimeId && (
                  <Typography
                    variant="caption"
                    color="text.secondary"
                    sx={{ fontFamily: 'monospace', display: 'block', mt: 0.5 }}
                  >
                    {row.runtimeId}
                  </Typography>
                )}
              </Box>
              <IconButton size="small" onClick={onClose}>
                <CloseIcon fontSize="small" />
              </IconButton>
            </Box>

            <Box sx={{ mt: 3 }}>
              <SeverityBadge severity={row.driftSeverity} size="large" />
              {row.driftReasons.length > 0 && (
                <Box component="ul" sx={{ pl: 2.5, mt: 1, mb: 0 }}>
                  {row.driftReasons.map((reason) => (
                    <Box component="li" key={reason} sx={{ mb: 0.5 }}>
                      <Typography variant="body2">
                        {DRIFT_REASON_LABELS[reason] ?? reason}
                      </Typography>
                      <Typography
                        variant="caption"
                        color="text.secondary"
                        sx={{ fontFamily: 'monospace' }}
                      >
                        {reason}
                      </Typography>
                    </Box>
                  ))}
                </Box>
              )}
            </Box>

            <Divider sx={{ my: 3 }} />

            <Typography variant="overline" color="text.secondary">
              State
            </Typography>
            <Stack spacing={1.5} sx={{ mt: 1 }}>
              <FieldRow label="DB state" value={row.dbState ?? DASH} />
              <FieldRow
                label="State changed"
                value={
                  row.stateChangedAt
                    ? `${formatRelativeSecs(row.secondsSinceStateChange)} ago (${row.stateChangedAt})`
                    : DASH
                }
              />
              <FieldRow label="Fly state" value={row.flyState ?? DASH} />
              <FieldRow label="Region" value={row.region ?? DASH} />
              <FieldRow
                label="Fly machine id"
                value={row.flyMachineId ?? DASH}
                mono
              />
              <Stack direction="row" spacing={2}>
                <Box sx={{ minWidth: 140 }}>
                  <Typography variant="caption" color="text.secondary">
                    Last heartbeat
                  </Typography>
                </Box>
                <Box sx={{ flex: 1 }}>
                  <HeartbeatAge seconds={row.secondsSinceHeartbeat} />
                  {row.lastHeartbeatAt && (
                    <Typography variant="caption" color="text.secondary" sx={{ display: 'block' }}>
                      {row.lastHeartbeatAt}
                    </Typography>
                  )}
                </Box>
              </Stack>
            </Stack>

            {row.projectId && (
              <Box sx={{ mt: 2 }}>
                <RouterLink
                  to={`/super-admin/projects/${row.projectId}/runtime`}
                  style={{ color: 'inherit' }}
                >
                  <Typography variant="body2" sx={{ textDecoration: 'underline' }}>
                    Open runtime workspace
                  </Typography>
                </RouterLink>
              </Box>
            )}

            {!isOrphan && (
              <>
                <Divider sx={{ my: 3 }} />
                <Typography variant="overline" color="text.secondary">
                  Actions
                </Typography>
                <Stack spacing={1} sx={{ mt: 1 }}>
                  <Button
                    fullWidth
                    variant="outlined"
                    startIcon={<RestartAltIcon />}
                    onClick={handleReset}
                    disabled={anyPending}
                  >
                    Reset to Pending
                  </Button>
                  <Button
                    fullWidth
                    variant="outlined"
                    color="warning"
                    startIcon={<PauseCircleOutlineIcon />}
                    onClick={handleSuspend}
                    disabled={anyPending}
                  >
                    Force Suspend
                  </Button>
                  <Button
                    fullWidth
                    variant="outlined"
                    color="error"
                    startIcon={<DeleteForeverIcon />}
                    onClick={() => setConfirmDelete(true)}
                    disabled={anyPending}
                  >
                    Force Delete
                  </Button>
                </Stack>
              </>
            )}

            {isOrphan && (
              <>
                <Divider sx={{ my: 3 }} />
                <Alert severity="info" variant="outlined">
                  No DB row backs this Fly machine, so no recovery action is available from this
                  view. Investigate the machine directly on Fly.
                </Alert>
              </>
            )}
          </Box>
        )}
      </Drawer>

      <Dialog open={confirmDelete} onClose={() => setConfirmDelete(false)}>
        <DialogTitle>Force delete this runtime?</DialogTitle>
        <DialogContent>
          <DialogContentText>
            This will mark the runtime row as Deleted and tear down the Fly machine if one exists.
            This action cannot be undone.
          </DialogContentText>
        </DialogContent>
        <DialogActions>
          <Button onClick={() => setConfirmDelete(false)} disabled={deleteMutation.isPending}>
            Cancel
          </Button>
          <Button color="error" onClick={handleDelete} disabled={deleteMutation.isPending}>
            Force Delete
          </Button>
        </DialogActions>
      </Dialog>
    </>
  )
}

function FieldRow({
  label,
  value,
  mono,
}: {
  label: string
  value: string
  mono?: boolean
}) {
  return (
    <Stack direction="row" spacing={2}>
      <Box sx={{ minWidth: 140 }}>
        <Typography variant="caption" color="text.secondary">
          {label}
        </Typography>
      </Box>
      <Box sx={{ flex: 1 }}>
        <Typography
          variant="body2"
          sx={{ fontFamily: mono ? 'monospace' : undefined, wordBreak: 'break-all' }}
        >
          {value}
        </Typography>
      </Box>
    </Stack>
  )
}

function formatRelativeSecs(secs: number | null | undefined): string {
  if (secs === null || secs === undefined) return DASH
  if (secs < 60) return `${Math.round(secs)}s`
  const m = Math.floor(secs / 60)
  if (m < 60) return `${m}m`
  const h = Math.floor(m / 60)
  if (h < 24) return `${h}h`
  return `${Math.floor(h / 24)}d`
}
