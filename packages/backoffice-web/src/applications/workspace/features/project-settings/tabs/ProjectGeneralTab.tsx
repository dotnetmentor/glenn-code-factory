import { useEffect, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import {
  Alert,
  Box,
  Button,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  Stack,
  TextField,
  Typography,
} from '@mui/material'
import { useQueryClient } from '@tanstack/react-query'
import {
  getGetApiMeWorkspacesQueryKey,
  getGetApiProjectsProjectIdConversationsQueryKey,
  getGetApiProjectsProjectIdQueryKey,
  getGetApiWorkspacesSlugProjectsQueryKey,
  useDeleteApiProjectsProjectId,
  usePatchApiProjectsProjectId,
  usePostApiProjectsProjectIdBranchesBranchIdReset,
} from '../../../../../api/queries-commands'
import { useNotification } from '../../../../shared/contexts/NotificationContext'
import {
  bodySx,
  captionSx,
  sectionTitleSx,
  workspaceColors,
  workspaceFontFamily,
  workspaceText,
} from '../../../shared'

const MAX_NAME_LENGTH = 100

interface ProjectGeneralTabProps {
  projectId: string
  /**
   * Current branch ID — when present the "Cancel everything on this branch"
   * affordance is rendered. Optional so callers that open the drawer without
   * a branch context (e.g. admin / list views) just don't see the action.
   */
  branchId?: string
  slug: string
  projectName: string
  onDeleted: () => void
}

/**
 * General settings tab for a project — rename and (soft) delete. Mirrors the
 * workspace settings General tab so the same affordances live in the same
 * left-rail slot regardless of which scope the user is in.
 */
export function ProjectGeneralTab({
  projectId,
  branchId,
  slug,
  projectName,
  onDeleted,
}: ProjectGeneralTabProps) {
  const queryClient = useQueryClient()
  const navigate = useNavigate()
  const { showSuccess, showError } = useNotification()

  // ── Rename ───────────────────────────────────────────────────────────────
  const [nameDraft, setNameDraft] = useState(projectName)
  useEffect(() => setNameDraft(projectName), [projectName])

  const renameMutation = usePatchApiProjectsProjectId()
  const trimmed = nameDraft.trim()
  const renameDirty =
    trimmed.length > 0 &&
    trimmed.length <= MAX_NAME_LENGTH &&
    trimmed !== projectName
  const canRename = renameDirty && !renameMutation.isPending

  const handleRename = () => {
    if (!canRename) return
    renameMutation.mutate(
      { projectId, data: { name: trimmed } },
      {
        onSuccess: () => {
          showSuccess('Project renamed.')
          queryClient.invalidateQueries({
            queryKey: getGetApiProjectsProjectIdQueryKey(projectId),
          })
          queryClient.invalidateQueries({
            queryKey: getGetApiWorkspacesSlugProjectsQueryKey(slug),
          })
          queryClient.invalidateQueries({ queryKey: getGetApiMeWorkspacesQueryKey() })
        },
        onError: () => {
          showError('Could not rename the project.')
        },
      },
    )
  }

  // ── Cancel everything on this branch ─────────────────────────────────────
  // Calls the branch-reset endpoint which cancels every AgentSession with a
  // non-terminal status (Pending / Running / Canceling). Pending → Canceled,
  // Running → Canceling (the daemon then completes the cancellation when it
  // hears CancelTurn). The runtime is NOT torn down and the working tree is
  // NOT touched — only in-flight work is stopped. Audit trail is preserved
  // (the cancellation is logged), so this is recoverable for forensics even
  // though the actual turn output is lost.
  const [resetConfirmOpen, setResetConfirmOpen] = useState(false)
  const resetMutation = usePostApiProjectsProjectIdBranchesBranchIdReset()
  const canReset = !!branchId && !resetMutation.isPending

  const openResetConfirm = () => setResetConfirmOpen(true)
  const closeResetConfirm = () => {
    if (resetMutation.isPending) return
    setResetConfirmOpen(false)
  }

  const handleReset = () => {
    if (!branchId) return
    resetMutation.mutate(
      { projectId, branchId },
      {
        onSuccess: (data) => {
          const total =
            (data?.canceledRunning ?? 0) + (data?.canceledPending ?? 0)
          showSuccess(
            `Branch reset — ${total} turn${total === 1 ? '' : 's'} canceled`,
          )
          setResetConfirmOpen(false)
          // The conversation detail query renders the per-session status —
          // invalidate every cached variant by prefix so the UI re-syncs
          // immediately rather than waiting for the next poll cycle.
          queryClient.invalidateQueries({
            predicate: (query) =>
              typeof query.queryKey[0] === 'string' &&
              query.queryKey[0].startsWith('/api/conversations/'),
          })
          queryClient.invalidateQueries({
            queryKey: getGetApiProjectsProjectIdConversationsQueryKey(projectId),
          })
        },
        onError: () => {
          showError("Couldn't reset branch — try again")
        },
      },
    )
  }

  // ── Delete ───────────────────────────────────────────────────────────────
  const [confirmOpen, setConfirmOpen] = useState(false)
  const [confirmText, setConfirmText] = useState('')
  const deleteMutation = useDeleteApiProjectsProjectId()
  const canDelete = confirmText.trim() === projectName && !deleteMutation.isPending

  const openConfirm = () => {
    setConfirmText('')
    setConfirmOpen(true)
  }
  const closeConfirm = () => {
    if (deleteMutation.isPending) return
    setConfirmOpen(false)
  }

  const handleDelete = () => {
    if (!canDelete) return
    deleteMutation.mutate(
      { projectId },
      {
        onSuccess: () => {
          showSuccess('Project deleted.')
          queryClient.invalidateQueries({
            queryKey: getGetApiWorkspacesSlugProjectsQueryKey(slug),
          })
          queryClient.invalidateQueries({ queryKey: getGetApiMeWorkspacesQueryKey() })
          setConfirmOpen(false)
          onDeleted()
          navigate(`/w/${slug}`)
        },
        onError: () => {
          showError('Could not delete the project.')
        },
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
          General
        </Typography>
        <Typography sx={bodySx}>
          Rename or remove this project. Removing keeps the data on file but
          hides it from the workspace.
        </Typography>
      </Box>

      {/* Rename */}
      <Box
        sx={{
          border: `1px solid ${workspaceColors.hairline}`,
          borderRadius: 2,
          p: { xs: 2.5, md: 3 },
        }}
      >
        <Stack spacing={2}>
          <Box>
            <Typography sx={sectionTitleSx}>Project name</Typography>
            <Typography sx={{ ...captionSx, mt: 0.5 }}>
              Visible to everyone in the workspace.
            </Typography>
          </Box>
          <TextField
            value={nameDraft}
            onChange={(e) => setNameDraft(e.target.value)}
            size="small"
            fullWidth
            inputProps={{ 'aria-label': 'Project name', maxLength: MAX_NAME_LENGTH }}
            InputProps={{
              sx: {
                backgroundColor: workspaceColors.inputBg,
                fontFamily: workspaceFontFamily.sans,
                fontSize: '0.875rem',
                color: workspaceText.primary,
              },
            }}
            sx={{ maxWidth: 420 }}
          />
          <Box>
            <Button
              variant="pill" color="primary"
              onClick={handleRename}
              disabled={!canRename}
            >
              {renameMutation.isPending ? 'Saving…' : 'Save name'}
            </Button>
          </Box>
        </Stack>
      </Box>

      {/* Danger zone — branch-scoped actions first (less destructive), then
          project-scoped delete at the bottom (irreversible from the UI). Each
          action sits in its own block, separated by the card's hairline
          rhythm so the labels stay readable. */}
      <Box
        sx={{
          border: `1px solid ${workspaceColors.hairline}`,
          borderRadius: 2,
          p: { xs: 2.5, md: 3 },
        }}
      >
        <Stack spacing={3}>
          <Box>
            <Typography sx={sectionTitleSx}>Danger zone</Typography>
            <Typography sx={{ ...captionSx, mt: 0.5 }}>
              Destructive actions. Take a breath before you click.
            </Typography>
          </Box>

          {/* Cancel everything on this branch — branch-scoped. Suppressed
              entirely when there is no branch context to act on. */}
          {branchId && (
            <Stack spacing={1.25}>
              <Box>
                <Typography
                  sx={{
                    ...captionSx,
                    color: workspaceText.primary,
                    fontWeight: 500,
                  }}
                >
                  Cancel everything on this branch
                </Typography>
                <Typography sx={{ ...captionSx, mt: 0.25 }}>
                  Stops every running and queued turn on this branch. Does NOT
                  restart the runtime or touch your files.
                </Typography>
              </Box>
              <Box>
                <Button
                  variant="pillOutlined" color="error"
                  onClick={openResetConfirm}
                  disabled={!canReset}
                  
                >
                  {resetMutation.isPending
                    ? 'Canceling…'
                    : 'Cancel everything on this branch'}
                </Button>
              </Box>
            </Stack>
          )}

          {/* Hairline divider between the two scopes — only when both are
              present (otherwise the divider sits orphaned at the top). */}
          {branchId && (
            <Box
              aria-hidden
              sx={{
                height: '1px',
                width: '100%',
                backgroundColor: workspaceColors.hairline,
              }}
            />
          )}

          {/* Delete project — project-scoped, soft delete. */}
          <Stack spacing={1.25}>
            <Box>
              <Typography
                sx={{
                  ...captionSx,
                  color: workspaceText.primary,
                  fontWeight: 500,
                }}
              >
                Delete project
              </Typography>
              <Typography sx={{ ...captionSx, mt: 0.25 }}>
                Removing the project hides it from the workspace. Data is kept
                for recovery.
              </Typography>
            </Box>
            <Box>
              <Button
                variant="pillOutlined" color="error"
                onClick={openConfirm}
                
              >
                Delete project
              </Button>
            </Box>
          </Stack>
        </Stack>
      </Box>

      {/* Reset-branch confirmation dialog. Mirrors the delete dialog's chrome
          (warm-paper Paper, hairline border, near-black/rust button pair) so
          both destructive flows read as the same surface. */}
      <Dialog
        open={resetConfirmOpen}
        onClose={closeResetConfirm}
        maxWidth="xs"
        fullWidth
      >
        <DialogTitle
          sx={{
            fontFamily: workspaceFontFamily.sans,
            fontWeight: 500,
            fontSize: '1.0625rem',
            letterSpacing: '-0.01em',
            color: workspaceText.primary,
          }}
        >
          Cancel everything?
        </DialogTitle>
        <DialogContent>
          <Typography sx={bodySx}>
            This will stop every running and queued turn on this branch. The
            runtime stays online and your files are untouched. Continue?
          </Typography>
        </DialogContent>
        <DialogActions sx={{ px: 3, pb: 2 }}>
          <Button
            onClick={closeResetConfirm}
            variant="quietText"
            disabled={resetMutation.isPending}
          >
            Keep going
          </Button>
          <Button
            variant="pill" color="error"
            onClick={handleReset}
            disabled={!canReset}
            
          >
            {resetMutation.isPending ? 'Canceling…' : 'Cancel all turns'}
          </Button>
        </DialogActions>
      </Dialog>

      <Dialog
        open={confirmOpen}
        onClose={closeConfirm}
        maxWidth="xs"
        fullWidth
      >
        <DialogTitle
          sx={{
            fontFamily: workspaceFontFamily.sans,
            fontWeight: 500,
            fontSize: '1.0625rem',
            letterSpacing: '-0.01em',
            color: workspaceText.primary,
          }}
        >
          Delete this project?
        </DialogTitle>
        <DialogContent>
          <Stack spacing={2}>
            <Alert
              severity="warning"
              variant="quiet"
            >
              The project will be hidden from the workspace immediately.
              Conversations, branches, and runtimes are preserved for recovery
              but no longer reachable from the UI.
            </Alert>
            <Typography sx={bodySx}>
              Type{' '}
              <Box
                component="span"
                sx={{
                  fontFamily: workspaceFontFamily.mono,
                  color: workspaceText.primary,
                  px: 0.5,
                  py: 0.125,
                  backgroundColor: workspaceColors.codeBg,
                  borderRadius: 0.5,
                }}
              >
                {projectName}
              </Box>{' '}
              to confirm.
            </Typography>
            <TextField
              size="small"
              fullWidth
              autoFocus
              value={confirmText}
              onChange={(e) => setConfirmText(e.target.value)}
              placeholder={projectName}
              inputProps={{
                'aria-label': 'Confirm project name to delete',
                autoComplete: 'off',
                spellCheck: 'false',
              }}
              InputProps={{
                sx: {
                  backgroundColor: workspaceColors.inputBg,
                  fontFamily: workspaceFontFamily.sans,
                },
              }}
            />
          </Stack>
        </DialogContent>
        <DialogActions sx={{ px: 3, pb: 2 }}>
          <Button
            onClick={closeConfirm}
            variant="quietText"
            disabled={deleteMutation.isPending}
          >
            Cancel
          </Button>
          <Button
            variant="pill" color="error"
            onClick={handleDelete}
            disabled={!canDelete}
            
          >
            {deleteMutation.isPending ? 'Deleting…' : 'Delete project'}
          </Button>
        </DialogActions>
      </Dialog>
    </Stack>
  )
}
