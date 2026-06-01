import { useEffect, useState } from 'react'
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
  usePostApiWorkspaces,
} from '../../../../../api/queries-commands'
import { useNotification } from '../../../../shared/contexts/NotificationContext'
import {
  bodySx,
  workspaceColors,
  workspaceFontFamily,
  workspaceText,
} from '../../../shared'

interface CreateWorkspaceDialogProps {
  open: boolean
  onClose: () => void
  /**
   * Called after the workspace is created on the server and the workspaces
   * list cache has been invalidated. The caller decides where to navigate
   * (different surfaces want different post-create behaviour).
   */
  onCreated: (slug: string) => void
}

/**
 * Modal for creating a new workspace.
 *
 * <p>Single-field form (name) — the slug is derived server-side. Submits
 * through {@link usePostApiWorkspaces} and invalidates the user's workspace
 * list so the picker and tab bar pick up the new entry. Routing is delegated
 * to the parent via {@code onCreated} so this dialog can be reused from the
 * header CTA, the workspace picker dropdown, and any future entry point.</p>
 */
export function CreateWorkspaceDialog({
  open,
  onClose,
  onCreated,
}: CreateWorkspaceDialogProps) {
  const queryClient = useQueryClient()
  const { showError, showSuccess } = useNotification()
  const createMutation = usePostApiWorkspaces()

  const [name, setName] = useState('')
  const [error, setError] = useState<string | null>(null)

  // Reset state whenever the dialog reopens — leaving stale "Creating…"
  // labels or half-typed names around between sessions reads as broken.
  useEffect(() => {
    if (open) {
      setName('')
      setError(null)
    }
  }, [open])

  const trimmed = name.trim()
  const canSubmit = trimmed.length > 0 && !createMutation.isPending

  const handleClose = () => {
    if (createMutation.isPending) return
    onClose()
  }

  const handleSubmit = (event: React.FormEvent) => {
    event.preventDefault()
    if (!canSubmit) return
    setError(null)
    createMutation.mutate(
      { data: { name: trimmed } },
      {
        onSuccess: (response) => {
          queryClient.invalidateQueries({ queryKey: getGetApiMeWorkspacesQueryKey() })
          showSuccess(`Workspace '${response.name}' created.`)
          onCreated(response.slug)
          onClose()
        },
        onError: (err) => {
          // Surface the server's problem detail when we have one, fall back to
          // a generic toast otherwise. Keep the dialog open so the user can
          // correct the input or retry without retyping.
          const problem =
            (err as { response?: { data?: { detail?: string | null; title?: string | null } } })
              ?.response?.data
          const message =
            problem?.detail ?? problem?.title ?? 'Could not create the workspace.'
          setError(message)
          showError(message)
        },
      },
    )
  }

  return (
    <Dialog
      open={open}
      onClose={handleClose}
      maxWidth="xs"
      fullWidth
    >
      <Box component="form" onSubmit={handleSubmit} noValidate>
        <DialogTitle
          sx={{
            fontFamily: workspaceFontFamily.sans,
            fontWeight: 500,
            fontSize: '1.0625rem',
            letterSpacing: '-0.01em',
            color: workspaceText.primary,
          }}
        >
          Create workspace
        </DialogTitle>
        <DialogContent>
          <Stack spacing={2}>
            <Typography sx={bodySx}>
              Workspaces hold your projects, conversations, and members. You
              can rename or delete it later from settings.
            </Typography>
            <TextField
              size="small"
              fullWidth
              autoFocus
              value={name}
              onChange={(e) => setName(e.target.value)}
              placeholder="Acme Inc."
              label="Workspace name"
              disabled={createMutation.isPending}
              inputProps={{
                'aria-label': 'Workspace name',
                maxLength: 120,
                autoComplete: 'off',
              }}
              InputProps={{
                sx: {
                  backgroundColor: workspaceColors.inputBg,
                  fontFamily: workspaceFontFamily.sans,
                  fontSize: '0.875rem',
                  color: workspaceText.primary,
                },
              }}
            />
            {error && (
              <Alert
                severity="error"
                variant="quiet"
              >
                {error}
              </Alert>
            )}
          </Stack>
        </DialogContent>
        <DialogActions sx={{ px: 3, pb: 2 }}>
          <Button
            type="button"
            onClick={handleClose}
            disabled={createMutation.isPending}
            sx={{
              textTransform: 'none',
              color: workspaceText.muted,
              '&:hover': { color: workspaceText.primary, backgroundColor: 'transparent' },
            }}
          >
            Cancel
          </Button>
          <Button
            type="submit"
            variant="pill" color="primary"
            disabled={!canSubmit}
          >
            {createMutation.isPending ? 'Creating…' : 'Create'}
          </Button>
        </DialogActions>
      </Box>
    </Dialog>
  )
}
