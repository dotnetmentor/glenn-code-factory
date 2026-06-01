import { useMemo, useState } from 'react'
import {
  Box,
  Button,
  Chip,
  CircularProgress,
  Dialog,
  DialogActions,
  DialogContent,
  DialogContentText,
  DialogTitle,
  IconButton,
  Skeleton,
  Stack,
  Tooltip,
  Typography,
} from '@mui/material'
import ContentCopyOutlinedIcon from '@mui/icons-material/ContentCopyOutlined'
import { useQueryClient } from '@tanstack/react-query'
import { formatDistanceToNow, parseISO } from 'date-fns'
import {
  getGetApiAdminRuntimesQueryKey,
  RuntimeState,
  useGetApiAdminRuntimes,
  useGetApiProjectsProjectIdBranches,
  usePostApiAdminRuntimesIdForceRespawn,
  usePostApiAdminRuntimesIdForceStop,
  usePostApiAdminRuntimesIdForceSuspend,
  type ProjectRuntime,
} from '../../../../../api/queries-commands'
import { useNotification } from '../../../../shared/contexts/NotificationContext'
import {
  bodySx,
  captionSx,
  workspaceColors,
  workspaceRuntime,
  workspaceText,
} from '../../../shared'

interface RuntimesTabProps {
  projectId: string
}

/**
 * State buckets used by the row UI and the auto-poll heuristic.
 *
 * <p><b>Transient</b> states are the ones whose state is actively changing —
 * polling at 5s keeps the list reflecting reality without the user clicking
 * refresh. Once everything settles, polling stops to avoid hammering the API.
 *
 * <p><b>Respawnable</b> states are the ones where Force Respawn is a sensible
 * action. Respawning a {@code Deleted} or {@code Failed} runtime is either a
 * no-op or the wrong button — those rows show a disabled button with a tooltip.
 */
const TRANSIENT_STATES: ReadonlySet<RuntimeState> = new Set<RuntimeState>([
  RuntimeState.Pending,
  RuntimeState.Booting,
  RuntimeState.Bootstrapping,
  RuntimeState.Waking,
  RuntimeState.Suspending,
  RuntimeState.Crashed,
  RuntimeState.Deleting,
])

const RESPAWNABLE_STATES: ReadonlySet<RuntimeState> = new Set<RuntimeState>([
  RuntimeState.Online,
  RuntimeState.Bootstrapping,
  RuntimeState.Waking,
  RuntimeState.Booting,
  RuntimeState.Suspending,
])

/**
 * States from which a manual "Stop" is meaningful. The runtime state machine
 * only has a direct edge from {@code Online -> Suspending} — Booting /
 * Bootstrapping / Waking must reach Online (or Crashed) first. We surface a
 * disabled button with an explanatory tooltip outside this set so the
 * operator gets affordance + reason in one place.
 */
const STOPPABLE_STATES: ReadonlySet<RuntimeState> = new Set<RuntimeState>([
  RuntimeState.Online,
])

/**
 * States from which "Force Stop" (the operator escape hatch for stuck
 * runtimes) is legal. Mirrors the operator-override edges added to
 * {@code RuntimeStateMachine}: Online stays accepted for symmetry, plus the
 * mid-boot states where regular Stop is unavailable because the runtime
 * never reached Online. Crashed / Suspended / Pending / Failed / terminal
 * states are excluded — they're either already stopped or have nothing for
 * Fly to park.
 */
const FORCE_STOPPABLE_STATES: ReadonlySet<RuntimeState> = new Set<RuntimeState>([
  RuntimeState.Online,
  RuntimeState.Booting,
  RuntimeState.Bootstrapping,
  RuntimeState.Waking,
])

/** Map a runtime state to a small color/label pair for the state chip. */
function stateAppearance(state: RuntimeState): {
  color: string
  label: string
} {
  switch (state) {
    case RuntimeState.Online:
      return { color: workspaceRuntime.online, label: 'Online' }
    case RuntimeState.Booting:
    case RuntimeState.Bootstrapping:
    case RuntimeState.Pending:
    case RuntimeState.Waking:
      return { color: workspaceRuntime.booting, label: state }
    case RuntimeState.Crashed:
    case RuntimeState.Failed:
      return { color: workspaceRuntime.failed, label: state }
    case RuntimeState.Suspending:
    case RuntimeState.Suspended:
    case RuntimeState.Deleting:
    case RuntimeState.Deleted:
      return { color: workspaceRuntime.suspended, label: state }
    default:
      return { color: workspaceRuntime.unknown, label: state }
  }
}

export function RuntimesTab({ projectId }: RuntimesTabProps) {
  const queryClient = useQueryClient()
  const { showSuccess, showError } = useNotification()

  // List runtimes for this project. We pass the projectId param so the admin
  // endpoint scopes to just this project's runtimes.
  const runtimesQuery = useGetApiAdminRuntimes(
    { projectId },
    {
      query: {
        enabled: !!projectId,
        // Compute the refetch interval from the current data: while any
        // runtime is in a transient state, poll every 5s. Once everything is
        // stable, returning false stops the polling. This is the cleanest
        // TanStack Query pattern — the runtime decides, not a useEffect.
        refetchInterval: (query) => {
          const data = query.state.data
          const items = data?.items ?? []
          const anyTransient = items.some((r) => TRANSIENT_STATES.has(r.state))
          return anyTransient ? 5000 : false
        },
      },
    },
  )

  // Branches list lets us turn a branchId GUID into a human-readable branch
  // name. Match the BranchesSettingsTab call signature exactly.
  const branchesQuery = useGetApiProjectsProjectIdBranches(
    projectId,
    { includeArchived: true },
    { query: { enabled: !!projectId } },
  )

  const branchNameById = useMemo<Record<string, string>>(() => {
    const map: Record<string, string> = {}
    for (const b of branchesQuery.data ?? []) {
      map[b.id] = b.name
    }
    return map
  }, [branchesQuery.data])

  /**
   * Set of branch IDs that are archived. We still ask the API to include
   * archived branches (so we have their names for any lingering rows), but we
   * use this set to hide their runtimes from the operator view — archived
   * means "this branch is gone from my dashboard," and the runtimes
   * underneath it shouldn't keep cluttering the list.
   */
  const archivedBranchIds = useMemo<ReadonlySet<string>>(() => {
    const ids = new Set<string>()
    for (const b of branchesQuery.data ?? []) {
      if (b.isArchived) ids.add(b.id)
    }
    return ids
  }, [branchesQuery.data])

  const respawnMut = usePostApiAdminRuntimesIdForceRespawn()
  const suspendMut = usePostApiAdminRuntimesIdForceSuspend()
  const forceStopMut = usePostApiAdminRuntimesIdForceStop()

  const [confirmRuntime, setConfirmRuntime] = useState<ProjectRuntime | null>(
    null,
  )
  const [confirmSuspendRuntime, setConfirmSuspendRuntime] =
    useState<ProjectRuntime | null>(null)
  const [confirmForceStopRuntime, setConfirmForceStopRuntime] =
    useState<ProjectRuntime | null>(null)
  const [pendingRespawnId, setPendingRespawnId] = useState<string | null>(null)
  const [pendingSuspendId, setPendingSuspendId] = useState<string | null>(null)
  const [pendingForceStopId, setPendingForceStopId] = useState<string | null>(
    null,
  )

  const allRuntimes = runtimesQuery.data?.items ?? []
  const runtimes = useMemo(
    () => allRuntimes.filter((r) => !archivedBranchIds.has(r.branchId)),
    [allRuntimes, archivedBranchIds],
  )

  const parseBackendError = (err: unknown): string | undefined => {
    const data = (err as {
      response?: { data?: { message?: string; error?: string; title?: string } }
    })?.response?.data
    return data?.message ?? data?.error ?? data?.title
  }

  const handleConfirmRespawn = () => {
    if (!confirmRuntime) return
    const runtime = confirmRuntime
    setPendingRespawnId(runtime.id)
    respawnMut.mutate(
      { id: runtime.id },
      {
        onSuccess: () => {
          showSuccess(
            'Respawn scheduled — runtime will be back in ~60s',
          )
          // Invalidate by the bare runtimes key (no params) so every variant
          // — including this project-scoped one — flushes and refetches.
          queryClient.invalidateQueries({
            queryKey: getGetApiAdminRuntimesQueryKey(),
          })
          setConfirmRuntime(null)
        },
        onError: (err: unknown) => {
          const message = parseBackendError(err) ?? 'unknown error'
          showError(`Failed to respawn runtime: ${message}`)
        },
        onSettled: () => setPendingRespawnId(null),
      },
    )
  }

  const handleConfirmSuspend = () => {
    if (!confirmSuspendRuntime) return
    const runtime = confirmSuspendRuntime
    setPendingSuspendId(runtime.id)
    suspendMut.mutate(
      { id: runtime.id },
      {
        onSuccess: () => {
          // Controller transitions DB to Suspending + fires Fly.StopMachine
          // synchronously, so by the time we land here the user already saved
          // resources. The webhook handler (or RuntimeReconcilerJob's stuck-
          // Suspending retry) closes Suspending -> Suspended within seconds.
          showSuccess('Runtime stopping — machine will park in a few seconds')
          queryClient.invalidateQueries({
            queryKey: getGetApiAdminRuntimesQueryKey(),
          })
          setConfirmSuspendRuntime(null)
        },
        onError: (err: unknown) => {
          const message = parseBackendError(err) ?? 'unknown error'
          showError(`Failed to stop runtime: ${message}`)
        },
        onSettled: () => setPendingSuspendId(null),
      },
    )
  }

  /**
   * Force Stop — operator escape hatch. Same target state (Suspending) as
   * Stop, but routes through the {@code force-stop} endpoint which accepts
   * mid-boot source states (Booting / Bootstrapping / Waking) where regular
   * Stop is forbidden by the state graph. Used when a runtime is hung
   * partway through bootstrap and the operator wants to park it without
   * waiting for it to reach Online.
   */
  const handleConfirmForceStop = () => {
    if (!confirmForceStopRuntime) return
    const runtime = confirmForceStopRuntime
    setPendingForceStopId(runtime.id)
    forceStopMut.mutate(
      { id: runtime.id },
      {
        onSuccess: () => {
          showSuccess(
            'Force-stop sent — machine parking, runtime will be Suspended shortly',
          )
          queryClient.invalidateQueries({
            queryKey: getGetApiAdminRuntimesQueryKey(),
          })
          setConfirmForceStopRuntime(null)
        },
        onError: (err: unknown) => {
          const message = parseBackendError(err) ?? 'unknown error'
          showError(`Failed to force-stop runtime: ${message}`)
        },
        onSettled: () => setPendingForceStopId(null),
      },
    )
  }

  return (
    <Stack spacing={4}>
      {/* Heading */}
      <Box>
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
          Runtimes
        </Typography>
        <Typography sx={bodySx}>
          Every container running for this project. Force Respawn destroys
          and recreates the Fly machine — useful after publishing a new
          daemon bundle. Active sessions will be interrupted briefly.
        </Typography>
      </Box>

      {/* List */}
      <Box
        sx={{
          border: `1px solid ${workspaceColors.hairline}`,
          borderRadius: 2,
          overflow: 'hidden',
        }}
      >
        {runtimesQuery.isLoading ? (
          <Stack
            divider={
              <Box sx={{ borderTop: `1px solid ${workspaceColors.hairline}` }} />
            }
          >
            {[0, 1, 2].map((i) => (
              <Box key={i} sx={{ px: 2, py: 1.5 }}>
                <Skeleton variant="text" width="50%" height={22} />
                <Skeleton variant="text" width="30%" height={16} />
              </Box>
            ))}
          </Stack>
        ) : runtimes.length === 0 ? (
          <Box sx={{ px: 3, py: 4, textAlign: 'center' }}>
            <Typography sx={captionSx}>
              No runtimes provisioned for this project yet.
            </Typography>
          </Box>
        ) : (
          <Stack
            divider={
              <Box sx={{ borderTop: `1px solid ${workspaceColors.hairline}` }} />
            }
          >
            {runtimes.map((runtime) => (
              <RuntimeRow
                key={runtime.id}
                runtime={runtime}
                branchName={
                  branchNameById[runtime.branchId] ??
                  shortId(runtime.branchId)
                }
                isPending={pendingRespawnId === runtime.id}
                isStopPending={pendingSuspendId === runtime.id}
                isForceStopPending={pendingForceStopId === runtime.id}
                stoppable={STOPPABLE_STATES.has(runtime.state)}
                forceStoppable={FORCE_STOPPABLE_STATES.has(runtime.state)}
                onForceRespawn={() => setConfirmRuntime(runtime)}
                onStop={() => setConfirmSuspendRuntime(runtime)}
                onForceStop={() => setConfirmForceStopRuntime(runtime)}
                onCopyMachineId={(machineId) => {
                  void navigator.clipboard
                    .writeText(machineId)
                    .then(() => showSuccess('Fly machine ID copied'))
                    .catch(() => showError('Failed to copy machine ID'))
                }}
              />
            ))}
          </Stack>
        )}
      </Box>

      {/* Confirmation dialog */}
      <Dialog
        open={confirmRuntime !== null}
        onClose={() => {
          if (pendingRespawnId === null) setConfirmRuntime(null)
        }}
        maxWidth="sm"
        fullWidth
      >
        <DialogTitle>Force respawn runtime?</DialogTitle>
        <DialogContent>
          <DialogContentText>
            The Fly machine will be destroyed and recreated. Your active
            session on branch{' '}
            <Box component="strong" sx={{ color: workspaceText.primary }}>
              {confirmRuntime
                ? branchNameById[confirmRuntime.branchId] ??
                  shortId(confirmRuntime.branchId)
                : ''}
            </Box>{' '}
            will be interrupted for ~60 seconds. The runtime will reboot with
            the latest daemon bundle from the stable channel.
          </DialogContentText>
        </DialogContent>
        <DialogActions sx={{ px: 3, pb: 2 }}>
          <Button
            onClick={() => setConfirmRuntime(null)}
            disabled={pendingRespawnId !== null}
            sx={{ textTransform: 'none' }}
          >
            Cancel
          </Button>
          <Button
            variant="contained"
            color="error"
            onClick={handleConfirmRespawn}
            disabled={pendingRespawnId !== null}
            startIcon={
              pendingRespawnId !== null ? (
                <CircularProgress size={14} color="inherit" />
              ) : undefined
            }
            sx={{ textTransform: 'none' }}
          >
            Force Respawn
          </Button>
        </DialogActions>
      </Dialog>

      {/* Stop / Suspend confirmation dialog */}
      <Dialog
        open={confirmSuspendRuntime !== null}
        onClose={() => {
          if (pendingSuspendId === null) setConfirmSuspendRuntime(null)
        }}
        maxWidth="sm"
        fullWidth
      >
        <DialogTitle>Stop runtime?</DialogTitle>
        <DialogContent>
          <DialogContentText>
            The Fly machine for branch{' '}
            <Box component="strong" sx={{ color: workspaceText.primary }}>
              {confirmSuspendRuntime
                ? branchNameById[confirmSuspendRuntime.branchId] ??
                  shortId(confirmSuspendRuntime.branchId)
                : ''}
            </Box>{' '}
            will be stopped to save resources. Any active session will be
            interrupted, but the runtime will wake automatically the next time
            someone opens the branch.
          </DialogContentText>
        </DialogContent>
        <DialogActions sx={{ px: 3, pb: 2 }}>
          <Button
            onClick={() => setConfirmSuspendRuntime(null)}
            disabled={pendingSuspendId !== null}
            sx={{ textTransform: 'none' }}
          >
            Cancel
          </Button>
          <Button
            variant="contained"
            color="warning"
            onClick={handleConfirmSuspend}
            disabled={pendingSuspendId !== null}
            startIcon={
              pendingSuspendId !== null ? (
                <CircularProgress size={14} color="inherit" />
              ) : undefined
            }
            sx={{ textTransform: 'none' }}
          >
            Stop Runtime
          </Button>
        </DialogActions>
      </Dialog>

      {/* Force Stop confirmation dialog */}
      <Dialog
        open={confirmForceStopRuntime !== null}
        onClose={() => {
          if (pendingForceStopId === null) setConfirmForceStopRuntime(null)
        }}
        maxWidth="sm"
        fullWidth
      >
        <DialogTitle>Force stop runtime?</DialogTitle>
        <DialogContent>
          <DialogContentText>
            Park the Fly machine for branch{' '}
            <Box component="strong" sx={{ color: workspaceText.primary }}>
              {confirmForceStopRuntime
                ? branchNameById[confirmForceStopRuntime.branchId] ??
                  shortId(confirmForceStopRuntime.branchId)
                : ''}
            </Box>{' '}
            regardless of its current state. Use this when the runtime is
            stuck mid-bootstrap and regular Stop is unavailable — it bypasses
            the "must be Online first" guard and forces the machine to stop
            now. The runtime will wake automatically the next time someone
            opens the branch.
          </DialogContentText>
        </DialogContent>
        <DialogActions sx={{ px: 3, pb: 2 }}>
          <Button
            onClick={() => setConfirmForceStopRuntime(null)}
            disabled={pendingForceStopId !== null}
            sx={{ textTransform: 'none' }}
          >
            Cancel
          </Button>
          <Button
            variant="contained"
            color="error"
            onClick={handleConfirmForceStop}
            disabled={pendingForceStopId !== null}
            startIcon={
              pendingForceStopId !== null ? (
                <CircularProgress size={14} color="inherit" />
              ) : undefined
            }
            sx={{ textTransform: 'none' }}
          >
            Force Stop
          </Button>
        </DialogActions>
      </Dialog>
    </Stack>
  )
}

interface RuntimeRowProps {
  runtime: ProjectRuntime
  branchName: string
  isPending: boolean
  isStopPending: boolean
  isForceStopPending: boolean
  stoppable: boolean
  forceStoppable: boolean
  onForceRespawn: () => void
  onStop: () => void
  onForceStop: () => void
  onCopyMachineId: (machineId: string) => void
}

function RuntimeRow({
  runtime,
  branchName,
  isPending,
  isStopPending,
  isForceStopPending,
  stoppable,
  forceStoppable,
  onForceRespawn,
  onStop,
  onForceStop,
  onCopyMachineId,
}: RuntimeRowProps) {
  const appearance = stateAppearance(runtime.state)
  const heartbeatLabel = formatHeartbeatAge(runtime.lastHeartbeatAt)
  const respawnable = RESPAWNABLE_STATES.has(runtime.state)
  const machineId = runtime.flyMachineId ?? null
  const machineIdShort = machineId
    ? machineId.slice(-6)
    : null

  return (
    <Stack
      direction={{ xs: 'column', sm: 'row' }}
      alignItems={{ xs: 'flex-start', sm: 'center' }}
      spacing={2}
      sx={{ px: 2, py: 1.5 }}
    >
      {/* Branch name + state */}
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
            title={branchName}
          >
            {branchName}
          </Typography>
          <Chip
            label={appearance.label}
            size="small"
            sx={{
              height: 20,
              fontSize: '0.6875rem',
              fontWeight: 500,
              letterSpacing: '0.02em',
              backgroundColor: `${appearance.color}1A`,
              color: appearance.color,
              border: `1px solid ${appearance.color}33`,
              '& .MuiChip-label': { px: 0.75 },
            }}
          />
        </Stack>
        <Stack
          direction="row"
          spacing={1.5}
          sx={{ mt: 0.5, flexWrap: 'wrap', rowGap: 0.25 }}
        >
          <Typography sx={{ ...captionSx, color: workspaceText.faint }}>
            {heartbeatLabel}
          </Typography>
          <Typography sx={{ ...captionSx, color: workspaceText.faint }}>
            {runtime.region}
          </Typography>
          {machineId && machineIdShort && (
            <Stack
              direction="row"
              alignItems="center"
              spacing={0.25}
              sx={{ minWidth: 0 }}
            >
              <Typography
                sx={{
                  ...captionSx,
                  color: workspaceText.faint,
                  fontFamily:
                    'ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace',
                }}
                title={machineId}
              >
                …{machineIdShort}
              </Typography>
              <Tooltip title="Copy full Fly machine ID">
                <IconButton
                  size="small"
                  onClick={() => onCopyMachineId(machineId)}
                  aria-label="Copy Fly machine ID"
                  sx={{ p: 0.25, color: workspaceText.faint }}
                >
                  <ContentCopyOutlinedIcon sx={{ fontSize: 14 }} />
                </IconButton>
              </Tooltip>
            </Stack>
          )}
          <Typography sx={{ ...captionSx, color: workspaceText.faint }}>
            {runtime.respawnRetries} respawn
            {runtime.respawnRetries === 1 ? '' : 's'}
          </Typography>
        </Stack>
      </Box>

      {/* Action buttons: Stop + Force Stop + Force Respawn */}
      <Stack direction="row" spacing={1} sx={{ flexShrink: 0 }}>
        {stoppable ? (
          <Button
            size="small"
            variant="outlined"
            onClick={onStop}
            disabled={isStopPending}
            startIcon={
              isStopPending ? (
                <CircularProgress size={12} color="inherit" />
              ) : undefined
            }
            sx={{
              textTransform: 'none',
              borderColor: workspaceColors.hairline,
              color: workspaceText.primary,
              '&:hover': {
                borderColor: workspaceText.muted,
                backgroundColor: workspaceColors.chipHoverBg,
              },
            }}
          >
            Stop
          </Button>
        ) : (
          <Tooltip title="Can only stop Online runtimes.">
            <span>
              <Button
                size="small"
                variant="outlined"
                disabled
                sx={{ textTransform: 'none' }}
              >
                Stop
              </Button>
            </span>
          </Tooltip>
        )}
        {forceStoppable ? (
          <Tooltip title="Park the runtime regardless of state. Use when stuck mid-bootstrap.">
            <Button
              size="small"
              variant="outlined"
              onClick={onForceStop}
              disabled={isForceStopPending}
              startIcon={
                isForceStopPending ? (
                  <CircularProgress size={12} color="inherit" />
                ) : undefined
              }
              sx={{
                textTransform: 'none',
                borderColor: workspaceColors.hairline,
                color: workspaceText.primary,
                '&:hover': {
                  borderColor: workspaceText.muted,
                  backgroundColor: workspaceColors.chipHoverBg,
                },
              }}
            >
              Force Stop
            </Button>
          </Tooltip>
        ) : (
          <Tooltip title="Force Stop only works for runtimes with a live Fly machine (Online, Booting, Bootstrapping, Waking).">
            <span>
              <Button
                size="small"
                variant="outlined"
                disabled
                sx={{ textTransform: 'none' }}
              >
                Force Stop
              </Button>
            </span>
          </Tooltip>
        )}
        {respawnable ? (
          <Button
            size="small"
            variant="outlined"
            onClick={onForceRespawn}
            disabled={isPending}
            sx={{
              textTransform: 'none',
              borderColor: workspaceColors.hairline,
              color: workspaceText.primary,
              '&:hover': {
                borderColor: workspaceText.muted,
                backgroundColor: workspaceColors.chipHoverBg,
              },
            }}
          >
            Force Respawn
          </Button>
        ) : (
          <Tooltip title="Can only respawn runtimes in active states.">
            <span>
              <Button
                size="small"
                variant="outlined"
                disabled
                sx={{ textTransform: 'none' }}
              >
                Force Respawn
              </Button>
            </span>
          </Tooltip>
        )}
      </Stack>
    </Stack>
  )
}

function shortId(id: string): string {
  return id.length > 8 ? `${id.slice(0, 8)}…` : id
}

function formatHeartbeatAge(iso: string | null | undefined): string {
  if (!iso) return 'never'
  try {
    return `${formatDistanceToNow(parseISO(iso))} ago`
  } catch {
    return 'never'
  }
}
