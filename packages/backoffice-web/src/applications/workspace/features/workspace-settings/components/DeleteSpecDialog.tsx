import { useState } from 'react'
import {
  Alert,
  Box,
  Button,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  Stack,
  Typography,
} from '@mui/material'
import { useQueryClient } from '@tanstack/react-query'
import {
  getGetApiWorkspacesWorkspaceIdSpecsQueryKey,
  useDeleteApiWorkspacesWorkspaceIdSpecsSpecId,
} from '../../../../../api/queries-commands'
import type {
  ProblemDetails,
  WorkspaceSpecListItem,
} from '../../../../../api/queries-commands'
import { useNotification } from '../../../../shared/contexts/NotificationContext'
import {
  bodySx,
  workspaceAccent,
  workspaceFontFamily,
  workspaceText,
} from '../../../shared'

function readErrorDetail(err: unknown): string | null {
  const maybe = err as { response?: { data?: ProblemDetails } } | undefined
  return maybe?.response?.data?.detail ?? maybe?.response?.data?.title ?? null
}

interface DeleteSpecDialogProps {
  open: boolean
  onClose: () => void
  workspaceId: string
  spec: WorkspaceSpecListItem | null
}

/**
 * Confirmation dialog for deleting a workspace catalog spec.
 *
 * <p>Spells out the snapshot semantic in plain language: existing branches
 * forked from this spec are unaffected, because the spec was copied at fork
 * time rather than linked. This reassurance is the whole point of the dialog —
 * the user shouldn't worry about half their projects restarting.</p>
 */
export function DeleteSpecDialog({
  open,
  onClose,
  workspaceId,
  spec,
}: DeleteSpecDialogProps) {
  const queryClient = useQueryClient()
  const { showSuccess } = useNotification()
  const mutation = useDeleteApiWorkspacesWorkspaceIdSpecsSpecId()

  const [error, setError] = useState<string | null>(null)

  const handleClose = () => {
    if (mutation.isPending) return
    setError(null)
    onClose()
  }

  const handleConfirm = () => {
    if (!spec) return
    setError(null)
    mutation.mutate(
      { workspaceId, specId: spec.id },
      {
        onSuccess: () => {
          queryClient.invalidateQueries({
            queryKey: getGetApiWorkspacesWorkspaceIdSpecsQueryKey(workspaceId),
          })
          showSuccess(`Spec '${spec.name}' deleted.`)
          onClose()
        },
        onError: (err) => {
          setError(
            readErrorDetail(err) ?? 'Could not delete the spec. Try again.',
          )
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
      <DialogTitle
        sx={{
          fontFamily: workspaceFontFamily.sans,
          fontWeight: 500,
          fontSize: '1.0625rem',
          letterSpacing: '-0.01em',
          color: workspaceText.primary,
        }}
      >
        Delete spec
      </DialogTitle>
      <DialogContent>
        <Stack spacing={2}>
          <Typography sx={bodySx}>
            Delete{' '}
            <Box
              component="span"
              sx={{
                fontFamily: workspaceFontFamily.mono,
                color: workspaceText.primary,
              }}
            >
              {spec?.name ?? ''}
            </Box>
            ?
          </Typography>
          <Typography sx={bodySx}>
            Existing branches forked from this spec are not affected — the
            spec is copied at fork time, not linked. New branches won't be
            able to pick it any more.
          </Typography>
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
          disabled={mutation.isPending}
          sx={{
            textTransform: 'none',
            color: workspaceText.muted,
            '&:hover': {
              color: workspaceText.primary,
              backgroundColor: 'transparent',
            },
          }}
        >
          Cancel
        </Button>
        <Button
          type="button"
          variant="outlined"
          onClick={handleConfirm}
          disabled={mutation.isPending || !spec}
          sx={{
            textTransform: 'none',
            borderRadius: 999,
            borderColor: workspaceAccent.ink,
            color: workspaceAccent.ink,
            fontFamily: workspaceFontFamily.sans,
            fontWeight: 500,
            fontSize: '0.8125rem',
            px: 2,
            py: 0.5,
            '&:hover': {
              borderColor: workspaceAccent.ink,
              color: workspaceAccent.ink,
              backgroundColor: workspaceAccent.faint,
            },
          }}
        >
          {mutation.isPending ? 'Deleting…' : 'Delete'}
        </Button>
      </DialogActions>
    </Dialog>
  )
}
