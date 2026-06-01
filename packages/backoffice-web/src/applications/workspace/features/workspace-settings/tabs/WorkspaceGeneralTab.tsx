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
  useDeleteApiWorkspacesSlug,
  usePutApiWorkspacesSlug,
} from '../../../../../api/queries-commands'
import { useNotification } from '../../../../shared/contexts/NotificationContext'
import { useWorkspace } from '../../../../shared/contexts/WorkspaceContext'
import {
  bodySx,
  captionSx,
  pageCardPaddedSx,
  sectionTitleSx,
  workspaceFontFamily,
  workspaceText,
} from '../../../shared'

const MAX_NAME_LENGTH = 120

interface WorkspaceGeneralTabProps {
  /** Called when the workspace is deleted (so the parent can close the drawer). */
  onDeleted: () => void
}

/**
 * General settings tab for a workspace — rename and (hard) delete.
 *
 * <p>Sits inside {@code WorkspaceSettingsDrawer}'s left-rail Tabs. The delete
 * action uses a typed-to-confirm dialog matching the project-level danger
 * zone so both surfaces feel identical.</p>
 */
export function WorkspaceGeneralTab({ onDeleted }: WorkspaceGeneralTabProps) {
  const queryClient = useQueryClient()
  const navigate = useNavigate()
  const { showSuccess, showError } = useNotification()
  const { currentWorkspace, currentSlug } = useWorkspace()

  const slug = currentSlug ?? ''
  const workspaceName = currentWorkspace?.name ?? ''

  // ── Rename ───────────────────────────────────────────────────────────────
  const [nameDraft, setNameDraft] = useState(workspaceName)
  useEffect(() => setNameDraft(workspaceName), [workspaceName])

  const renameMutation = usePutApiWorkspacesSlug()
  const trimmed = nameDraft.trim()
  const renameDirty =
    trimmed.length > 0 &&
    trimmed.length <= MAX_NAME_LENGTH &&
    trimmed !== workspaceName
  const canRename = renameDirty && !renameMutation.isPending

  const handleRename = () => {
    if (!canRename || !slug) return
    renameMutation.mutate(
      { slug, data: { name: trimmed } },
      {
        onSuccess: () => {
          showSuccess('Workspace renamed.')
          queryClient.invalidateQueries({ queryKey: getGetApiMeWorkspacesQueryKey() })
        },
        onError: () => {
          showError('Could not rename the workspace.')
        },
      },
    )
  }

  // ── Delete ───────────────────────────────────────────────────────────────
  const [confirmOpen, setConfirmOpen] = useState(false)
  const [confirmText, setConfirmText] = useState('')
  const deleteMutation = useDeleteApiWorkspacesSlug()
  const canDelete = confirmText.trim() === workspaceName && !deleteMutation.isPending

  const openConfirm = () => {
    setConfirmText('')
    setConfirmOpen(true)
  }
  const closeConfirm = () => {
    if (deleteMutation.isPending) return
    setConfirmOpen(false)
  }

  const handleDelete = () => {
    if (!canDelete || !slug) return
    deleteMutation.mutate(
      { slug },
      {
        onSuccess: () => {
          showSuccess('Workspace deleted.')
          queryClient.invalidateQueries({ queryKey: getGetApiMeWorkspacesQueryKey() })
          setConfirmOpen(false)
          onDeleted()
          navigate('/')
        },
        onError: () => {
          showError('Could not delete the workspace.')
        },
      },
    )
  }

  if (!slug) {
    return <Alert severity="error">No workspace selected.</Alert>
  }

  return (
    <Stack spacing={4}>
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
          Rename or remove this workspace. Deleting hides it for everyone.
        </Typography>
      </Box>

      {/* Rename */}
      <Box sx={pageCardPaddedSx}>
        <Stack spacing={2}>
          <Box>
            <Typography sx={sectionTitleSx}>Workspace name</Typography>
            <Typography sx={{ ...captionSx, mt: 0.5 }}>
              Visible to everyone in this workspace.
            </Typography>
          </Box>
          <TextField
            value={nameDraft}
            onChange={(e) => setNameDraft(e.target.value)}
            size="small"
            fullWidth
            inputProps={{ 'aria-label': 'Workspace name', maxLength: MAX_NAME_LENGTH }}
            InputProps={{
              sx: {
                backgroundColor: 'instrument.inputBg',
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

      {/* Danger zone */}
      <Box sx={pageCardPaddedSx}>
        <Stack spacing={2}>
          <Box>
            <Typography sx={sectionTitleSx}>Danger zone</Typography>
            <Typography sx={{ ...captionSx, mt: 0.5 }}>
              Deleting the workspace removes access for every member. This
              action cannot be undone from the UI.
            </Typography>
          </Box>
          <Box>
            <Button
              variant="pillOutlined" color="error"
              onClick={openConfirm}
              
            >
              Delete workspace
            </Button>
          </Box>
        </Stack>
      </Box>

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
          Delete this workspace?
        </DialogTitle>
        <DialogContent>
          <Stack spacing={2}>
            <Alert
              severity="warning"
              variant="quiet"
            >
              Every member loses access immediately. Projects, conversations,
              and integrations are retained on the server but no longer
              reachable from the UI.
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
                  backgroundColor: 'instrument.codeBg',
                  borderRadius: 0.5,
                }}
              >
                {workspaceName}
              </Box>{' '}
              to confirm.
            </Typography>
            <TextField
              size="small"
              fullWidth
              autoFocus
              value={confirmText}
              onChange={(e) => setConfirmText(e.target.value)}
              placeholder={workspaceName}
              inputProps={{
                'aria-label': 'Confirm workspace name to delete',
                autoComplete: 'off',
                spellCheck: 'false',
              }}
              InputProps={{
                sx: {
                  backgroundColor: 'instrument.inputBg',
                  fontFamily: workspaceFontFamily.sans,
                },
              }}
            />
          </Stack>
        </DialogContent>
        <DialogActions sx={{ px: 3, pb: 2 }}>
          <Button
            variant="quietText"
            onClick={closeConfirm}
          >
            Cancel
          </Button>
          <Button
            variant="pill" color="error"
            onClick={handleDelete}
            disabled={!canDelete}
            
          >
            {deleteMutation.isPending ? 'Deleting…' : 'Delete workspace'}
          </Button>
        </DialogActions>
      </Dialog>
    </Stack>
  )
}
