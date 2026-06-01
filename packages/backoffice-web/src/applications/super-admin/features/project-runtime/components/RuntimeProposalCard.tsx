import { useMemo, useState } from 'react'
import {
  Alert,
  Box,
  Button,
  Chip,
  CircularProgress,
  Collapse,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  Paper,
  Stack,
  Tooltip,
  Typography,
} from '@mui/material'
import CheckCircleOutlineIcon from '@mui/icons-material/CheckCircleOutline'
import CancelOutlinedIcon from '@mui/icons-material/CancelOutlined'
import ErrorOutlineIcon from '@mui/icons-material/ErrorOutline'
import EditIcon from '@mui/icons-material/Edit'
import ReplayIcon from '@mui/icons-material/Replay'
import { useQueryClient } from '@tanstack/react-query'
import {
  RuntimeProposalStatus,
  type RuntimeProposalDto,
  type RuntimeSpecV3,
  getGetApiProjectsProjectIdProposalsQueryKey,
  getGetApiProjectsProjectIdProposalsProposalIdQueryKey,
  getGetApiProjectsProjectIdRuntimeSpecQueryKey,
  usePostApiProjectsProjectIdProposalsProposalIdApprove,
  usePostApiProjectsProjectIdProposalsProposalIdEdit,
  usePostApiProjectsProjectIdProposalsProposalIdReject,
} from '@/api/queries-commands'
import {
  StackPickerFields,
  type StackPickerValue,
} from '../../project-onboarding'
import { parseProposedSpec } from '../utils/parseProposedSpec'

interface RuntimeProposalCardProps {
  proposal: RuntimeProposalDto
  projectId: string
  /** Optional: notify parent when an action commits (post-invalidate). */
  onActionComplete?: () => void
}

function formatTimestamp(value: string | null | undefined): string {
  if (!value) return ''
  try {
    return new Date(value).toLocaleString()
  } catch {
    return value
  }
}

/**
 * Status-aware confirmation card for a single
 * {@link RuntimeProposalDto}. The card morphs through the proposal lifecycle:
 *
 * <ul>
 *   <li><b>Pending</b> — full card with chips for languages / services /
 *       extras, the agent's reason, and Approve / Edit / Reject CTAs.</li>
 *   <li><b>Approved / Edited</b> — interim "Applying…" state while the
 *       daemon installs the delta.</li>
 *   <li><b>Applied</b> — collapsed one-liner with a "View" toggle that
 *       expands the original chip list.</li>
 *   <li><b>Rejected</b> — muted one-liner with the agent's reason.</li>
 *   <li><b>Failed</b> — red callout carrying the daemon's error message.
 *       The Retry CTA is shown but disabled with an explanatory tooltip
 *       because the Approve endpoint requires <c>Status = Pending</c>;
 *       a dedicated retry-apply path lives in a future card.</li>
 * </ul>
 *
 * <p>The Edit flow expands {@link StackPickerFields} inline so the user can
 * tweak the proposal without leaving the chat / workspace surface — same
 * picker as the manual onboarding flow (Card 7), so users see one stack
 * vocabulary across the product.</p>
 *
 * <p>Stand-alone by design: the card never assumes it's mounted inside a
 * chat panel. Card 8 mounts it on the workspace page; if a chat panel
 * arrives later, the same component can be the renderer for a
 * <c>runtime_proposal</c> structured message.</p>
 */
export function RuntimeProposalCard({
  proposal,
  projectId,
  onActionComplete,
}: RuntimeProposalCardProps) {
  const queryClient = useQueryClient()

  const proposed = useMemo(
    () => parseProposedSpec(proposal.proposedSpec),
    [proposal.proposedSpec],
  )

  const [editing, setEditing] = useState(false)
  const [editValue, setEditValue] = useState<StackPickerValue>(proposed)
  const [extrasRaw, setExtrasRaw] = useState(proposed.extras.join(' '))
  const [confirmReject, setConfirmReject] = useState(false)
  const [actionError, setActionError] = useState<string | null>(null)
  const [appliedExpanded, setAppliedExpanded] = useState(false)

  const approveMutation =
    usePostApiProjectsProjectIdProposalsProposalIdApprove()
  const editMutation = usePostApiProjectsProjectIdProposalsProposalIdEdit()
  const rejectMutation =
    usePostApiProjectsProjectIdProposalsProposalIdReject()

  const isMutating =
    approveMutation.isPending ||
    editMutation.isPending ||
    rejectMutation.isPending

  const invalidate = () => {
    queryClient.invalidateQueries({
      queryKey: getGetApiProjectsProjectIdProposalsQueryKey(projectId),
    })
    queryClient.invalidateQueries({
      queryKey: getGetApiProjectsProjectIdProposalsProposalIdQueryKey(
        projectId,
        proposal.id,
      ),
    })
    queryClient.invalidateQueries({
      queryKey: getGetApiProjectsProjectIdRuntimeSpecQueryKey(projectId),
    })
    onActionComplete?.()
  }

  const handleError = (err: unknown) => {
    const msg =
      err instanceof Error
        ? err.message
        : 'Action failed. Please try again.'
    setActionError(msg)
  }

  const handleApprove = () => {
    setActionError(null)
    approveMutation.mutate(
      { projectId, proposalId: proposal.id },
      {
        onSuccess: () => invalidate(),
        onError: handleError,
      },
    )
  }

  const handleSubmitEdit = () => {
    setActionError(null)
    // V3 runtime spec is the source of truth — the V1 catalog fields
    // (languages / services / extras) no longer exist on EditProposalRequest.
    // The Phase 4 UI rebuild will replace the inline StackPickerFields edit
    // flow with a freeform spec editor; until then we ship the unchanged
    // proposed spec back so the request is at least well-typed.
    let editedSpec: RuntimeSpecV3
    try {
      const parsed = proposal.proposedSpec
        ? (JSON.parse(proposal.proposedSpec) as Partial<RuntimeSpecV3>)
        : {}
      editedSpec = { ...parsed, version: parsed.version ?? 3 }
    } catch {
      editedSpec = { version: 3 }
    }
    editMutation.mutate(
      {
        projectId,
        proposalId: proposal.id,
        data: { editedSpec },
      },
      {
        onSuccess: () => {
          setEditing(false)
          invalidate()
        },
        onError: handleError,
      },
    )
  }

  const handleConfirmReject = () => {
    setActionError(null)
    rejectMutation.mutate(
      { projectId, proposalId: proposal.id },
      {
        onSuccess: () => {
          setConfirmReject(false)
          invalidate()
        },
        onError: handleError,
      },
    )
  }

  const status = proposal.status

  // ---- Terminal one-liners ---------------------------------------------------

  if (status === RuntimeProposalStatus.Rejected) {
    return (
      <Paper variant="outlined" sx={{ p: 1.5, opacity: 0.75 }}>
        <Stack direction="row" spacing={1.5} alignItems="center">
          <CancelOutlinedIcon fontSize="small" color="disabled" />
          <Typography variant="body2" color="text.secondary">
            Rejected
            {proposal.decidedAt
              ? ` · ${formatTimestamp(proposal.decidedAt)}`
              : ''}
          </Typography>
          {proposal.reason && (
            <Typography
              variant="body2"
              color="text.disabled"
              sx={{ ml: 1, fontStyle: 'italic' }}
            >
              {proposal.reason}
            </Typography>
          )}
        </Stack>
      </Paper>
    )
  }

  if (status === RuntimeProposalStatus.Applied) {
    return (
      <Paper variant="outlined" sx={{ p: 1.5 }}>
        <Stack direction="row" spacing={1.5} alignItems="center">
          <CheckCircleOutlineIcon fontSize="small" color="success" />
          <Typography variant="body2">
            Live
            {proposal.decidedAt
              ? ` · ${formatTimestamp(proposal.decidedAt)}`
              : ''}
          </Typography>
          <Box sx={{ flex: 1 }} />
          <Button
            size="small"
            onClick={() => setAppliedExpanded((v) => !v)}
          >
            {appliedExpanded ? 'Hide' : 'View'}
          </Button>
        </Stack>
        <Collapse in={appliedExpanded} unmountOnExit>
          <Box sx={{ mt: 1.5 }}>
            <SpecChips spec={proposed} />
          </Box>
        </Collapse>
      </Paper>
    )
  }

  if (status === RuntimeProposalStatus.Failed) {
    return (
      <Paper
        variant="outlined"
        sx={(theme) => ({
          p: 2,
          borderColor: 'error.main',
          bgcolor: theme.palette.mode === 'dark'
            ? 'rgba(244, 67, 54, 0.08)'
            : 'rgba(244, 67, 54, 0.04)',
        })}
      >
        <Stack spacing={1.5}>
          <Stack direction="row" spacing={1} alignItems="center">
            <ErrorOutlineIcon fontSize="small" color="error" />
            <Typography variant="subtitle2" color="error">
              Apply failed
            </Typography>
          </Stack>
          {proposal.errorMessage && (
            <Typography variant="body2" color="text.secondary">
              {proposal.errorMessage}
            </Typography>
          )}
          <SpecChips spec={proposed} muted />
          <Box>
            <Tooltip title="Retry isn't available yet — the Approve endpoint requires a Pending proposal. Until a dedicated retry path ships, ask the agent to propose again.">
              <span>
                <Button
                  size="small"
                  startIcon={<ReplayIcon />}
                  disabled
                >
                  Retry
                </Button>
              </span>
            </Tooltip>
          </Box>
        </Stack>
      </Paper>
    )
  }

  // ---- Approved / Edited (waiting for daemon ack) ----------------------------

  if (
    status === RuntimeProposalStatus.Approved ||
    status === RuntimeProposalStatus.Edited
  ) {
    return (
      <Paper variant="outlined" sx={{ p: 2 }}>
        <Stack spacing={1.5}>
          <Stack direction="row" spacing={1} alignItems="center">
            <CircularProgress size={16} />
            <Typography variant="subtitle2">Applying…</Typography>
            <Typography variant="caption" color="text.secondary">
              {status === RuntimeProposalStatus.Edited
                ? 'Edited delta'
                : 'Approved'}
            </Typography>
          </Stack>
          <SpecChips spec={proposed} pending />
        </Stack>
      </Paper>
    )
  }

  // ---- Pending ---------------------------------------------------------------

  return (
    <>
      <Paper variant="outlined" sx={{ p: 2 }}>
        <Stack spacing={2}>
          <Box>
            <Typography variant="overline" color="text.secondary">
              Proposed runtime
            </Typography>
            <Typography variant="body2" sx={{ mt: 0.5 }}>
              The agent suggests installing the following stack:
            </Typography>
          </Box>

          <SpecChips spec={proposed} />

          {proposal.reason && (
            <Box
              sx={{
                p: 1.5,
                bgcolor: 'action.hover',
                borderRadius: 1,
                borderLeft: 3,
                borderLeftColor: 'primary.main',
              }}
            >
              <Typography variant="caption" color="text.secondary">
                Why
              </Typography>
              <Typography variant="body2">{proposal.reason}</Typography>
            </Box>
          )}

          {actionError && <Alert severity="error">{actionError}</Alert>}

          <Collapse in={editing} unmountOnExit>
            <Box sx={{ pt: 1 }}>
              <StackPickerFields
                value={editValue}
                onChange={setEditValue}
                extrasRaw={extrasRaw}
                onExtrasRawChange={setExtrasRaw}
                disabled={isMutating}
              />
              <Stack direction="row" spacing={1} sx={{ mt: 2 }}>
                <Button
                  variant="contained"
                  onClick={handleSubmitEdit}
                  disabled={isMutating}
                  startIcon={
                    editMutation.isPending ? (
                      <CircularProgress size={14} color="inherit" />
                    ) : null
                  }
                >
                  {editMutation.isPending ? 'Saving…' : 'Save edit'}
                </Button>
                <Button
                  onClick={() => setEditing(false)}
                  disabled={isMutating}
                >
                  Cancel
                </Button>
              </Stack>
            </Box>
          </Collapse>

          {!editing && (
            <Stack direction="row" spacing={1}>
              <Button
                variant="contained"
                color="primary"
                onClick={handleApprove}
                disabled={isMutating}
                startIcon={
                  approveMutation.isPending ? (
                    <CircularProgress size={14} color="inherit" />
                  ) : null
                }
              >
                {approveMutation.isPending ? 'Approving…' : 'Approve'}
              </Button>
              <Button
                variant="outlined"
                startIcon={<EditIcon />}
                onClick={() => {
                  setEditValue(proposed)
                  setExtrasRaw(proposed.extras.join(' '))
                  setEditing(true)
                }}
                disabled={isMutating}
              >
                Edit
              </Button>
              <Button
                color="inherit"
                onClick={() => setConfirmReject(true)}
                disabled={isMutating}
              >
                Reject
              </Button>
            </Stack>
          )}
        </Stack>
      </Paper>

      <Dialog
        open={confirmReject}
        onClose={
          rejectMutation.isPending ? undefined : () => setConfirmReject(false)
        }
        maxWidth="xs"
        fullWidth
      >
        <DialogTitle>Reject this proposal?</DialogTitle>
        <DialogContent>
          <Typography variant="body2">
            The agent can suggest a different stack. Rejecting only discards
            this specific proposal — the runtime keeps its current spec.
          </Typography>
        </DialogContent>
        <DialogActions>
          <Button
            onClick={() => setConfirmReject(false)}
            disabled={rejectMutation.isPending}
          >
            Cancel
          </Button>
          <Button
            color="error"
            variant="contained"
            onClick={handleConfirmReject}
            disabled={rejectMutation.isPending}
          >
            {rejectMutation.isPending ? 'Rejecting…' : 'Reject'}
          </Button>
        </DialogActions>
      </Dialog>
    </>
  )
}

/** Chip row for the proposed languages / services / extras. */
function SpecChips({
  spec,
  muted,
  pending,
}: {
  spec: { languages: string[]; services: string[]; extras: string[] }
  muted?: boolean
  pending?: boolean
}) {
  const variant = muted ? 'outlined' : 'filled'
  const color: 'default' | 'warning' = pending ? 'warning' : 'default'
  const empty =
    spec.languages.length === 0 &&
    spec.services.length === 0 &&
    spec.extras.length === 0
  if (empty) {
    return (
      <Typography variant="body2" color="text.secondary">
        No additions.
      </Typography>
    )
  }
  return (
    <Stack spacing={1}>
      {spec.languages.length > 0 && (
        <Stack direction="row" spacing={1} alignItems="center" flexWrap="wrap">
          <Typography variant="caption" color="text.secondary" sx={{ minWidth: 72 }}>
            Languages
          </Typography>
          {spec.languages.map((lang) => (
            <Chip
              key={lang}
              size="small"
              label={lang}
              variant={variant}
              color={color}
            />
          ))}
        </Stack>
      )}
      {spec.services.length > 0 && (
        <Stack direction="row" spacing={1} alignItems="center" flexWrap="wrap">
          <Typography variant="caption" color="text.secondary" sx={{ minWidth: 72 }}>
            Services
          </Typography>
          {spec.services.map((svc) => (
            <Chip
              key={svc}
              size="small"
              label={svc}
              variant={variant}
              color={color}
            />
          ))}
        </Stack>
      )}
      {spec.extras.length > 0 && (
        <Stack direction="row" spacing={1} alignItems="center" flexWrap="wrap">
          <Typography variant="caption" color="text.secondary" sx={{ minWidth: 72 }}>
            Extras
          </Typography>
          {spec.extras.map((ex) => (
            <Chip
              key={ex}
              size="small"
              label={ex}
              variant={variant}
              color={color}
            />
          ))}
        </Stack>
      )}
    </Stack>
  )
}
