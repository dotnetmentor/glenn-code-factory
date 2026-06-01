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
  getGetApiWorkspacesWorkspaceIdSpecsQueryKey,
  usePostApiWorkspacesWorkspaceIdSpecsSpecIdDuplicate,
} from '../../../../../api/queries-commands'
import type {
  ProblemDetails,
  WorkspaceSpecListItem,
} from '../../../../../api/queries-commands'
import { useNotification } from '../../../../shared/contexts/NotificationContext'
import {
  bodySx,
  captionSx,
  workspaceColors,
  workspaceFontFamily,
  workspaceText,
} from '../../../shared'

function readErrorDetail(err: unknown): string | null {
  const maybe = err as { response?: { data?: ProblemDetails } } | undefined
  const raw = maybe?.response?.data?.detail ?? maybe?.response?.data?.title ?? null
  if (!raw) return null
  if (raw.startsWith('name_taken')) {
    return 'A spec with this name already exists in this workspace.'
  }
  return raw
}

interface DuplicateSpecDialogProps {
  open: boolean
  onClose: () => void
  workspaceId: string
  source: WorkspaceSpecListItem | null
}

/**
 * Tiny dialog for duplicating a workspace catalog spec.
 *
 * <p>One field — the name for the new entry, prefilled with
 * {@code "{sourceName} (copy)"}. Description is optional and starts empty; the
 * server copies the source's content verbatim, so we don't surface that here.
 * Hits {@link usePostApiWorkspacesWorkspaceIdSpecsSpecIdDuplicate} on submit
 * and invalidates the workspace specs list on success.</p>
 */
export function DuplicateSpecDialog({
  open,
  onClose,
  workspaceId,
  source,
}: DuplicateSpecDialogProps) {
  const queryClient = useQueryClient()
  const { showSuccess } = useNotification()
  const mutation = usePostApiWorkspacesWorkspaceIdSpecsSpecIdDuplicate()

  const [name, setName] = useState('')
  const [description, setDescription] = useState('')
  const [error, setError] = useState<string | null>(null)

  // Seed the name from the source whenever the dialog opens. We intentionally
  // don't carry the source's description forward — the duplicate is a starting
  // point for a variant, the description usually needs to be different.
  useEffect(() => {
    if (open && source) {
      setName(`${source.name} (copy)`)
      setDescription('')
      setError(null)
    }
  }, [open, source])

  const trimmedName = name.trim()
  const trimmedDescription = description.trim()
  const canSubmit =
    !mutation.isPending &&
    trimmedName.length > 0 &&
    trimmedName.length <= 100 &&
    trimmedDescription.length <= 500 &&
    !!source

  const handleClose = () => {
    if (mutation.isPending) return
    onClose()
  }

  const handleSubmit = (event: React.FormEvent) => {
    event.preventDefault()
    if (!canSubmit || !source) return
    setError(null)
    mutation.mutate(
      {
        workspaceId,
        specId: source.id,
        data: {
          name: trimmedName,
          description:
            trimmedDescription.length > 0 ? trimmedDescription : null,
        },
      },
      {
        onSuccess: (resp) => {
          queryClient.invalidateQueries({
            queryKey: getGetApiWorkspacesWorkspaceIdSpecsQueryKey(workspaceId),
          })
          showSuccess(`Spec '${resp.name}' created.`)
          onClose()
        },
        onError: (err) => {
          setError(
            readErrorDetail(err) ?? 'Could not duplicate the spec. Try again.',
          )
        },
      },
    )
  }

  const inputSxBase = {
    backgroundColor: workspaceColors.inputBg,
    fontFamily: workspaceFontFamily.sans,
    fontSize: '0.875rem',
    color: workspaceText.primary,
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
          Duplicate spec
        </DialogTitle>
        <DialogContent>
          <Stack spacing={2}>
            <Typography sx={bodySx}>
              Duplicate{' '}
              <Box
                component="span"
                sx={{
                  fontFamily: workspaceFontFamily.mono,
                  color: workspaceText.primary,
                }}
              >
                {source?.name ?? ''}
              </Box>{' '}
              as a new independent spec. The content is copied verbatim — you
              can edit the new entry after it's created.
            </Typography>
            <TextField
              size="small"
              fullWidth
              autoFocus
              value={name}
              onChange={(e) => setName(e.target.value)}
              placeholder={`${source?.name ?? ''} (copy)`}
              label="New spec name"
              required
              disabled={mutation.isPending}
              inputProps={{
                'aria-label': 'New spec name',
                maxLength: 100,
                autoComplete: 'off',
              }}
              InputProps={{ sx: inputSxBase }}
              helperText={`${trimmedName.length}/100`}
              FormHelperTextProps={{
                sx: { ...captionSx, fontSize: '0.75rem', ml: 0.5 },
              }}
            />
            <TextField
              size="small"
              fullWidth
              multiline
              minRows={2}
              maxRows={4}
              value={description}
              onChange={(e) => setDescription(e.target.value)}
              label="Description"
              disabled={mutation.isPending}
              inputProps={{
                'aria-label': 'New spec description',
                maxLength: 500,
              }}
              InputProps={{ sx: inputSxBase }}
              helperText={`${trimmedDescription.length}/500 — optional`}
              FormHelperTextProps={{
                sx: { ...captionSx, fontSize: '0.75rem', ml: 0.5 },
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
            type="submit"
            variant="pill" color="primary"
            disabled={!canSubmit}
          >
            {mutation.isPending ? 'Duplicating…' : 'Duplicate'}
          </Button>
        </DialogActions>
      </Box>
    </Dialog>
  )
}
