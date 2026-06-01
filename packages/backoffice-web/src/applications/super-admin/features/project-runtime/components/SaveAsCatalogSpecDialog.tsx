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
  usePostApiRuntimesRuntimeIdSaveAsCatalogSpec,
  type ProblemDetails,
} from '@/api/queries-commands'
import { useNotification } from '@/applications/shared/contexts/NotificationContext'
import {
  workspaceColors,
  workspaceFontFamily,
  workspaceText,
} from '@/applications/workspace/shared/designTokens'
import {
  bodySx,
  captionSx,
} from '@/applications/workspace/shared'

export interface SaveAsCatalogSpecDialogProps {
  open: boolean
  onClose: () => void
  /** Runtime whose currently-applied spec is being promoted to the catalog. */
  runtimeId: string
  /**
   * Workspace that owns the runtime. Used as the cache key for invalidating
   * the workspace specs list on success. May be empty while the surrounding
   * project lookup is loading — the dialog still works, but invalidation
   * is skipped.
   */
  workspaceId: string
}

/**
 * Reads a structured error code out of an Orval-thrown ProblemDetails and
 * translates it to a sentence the user can act on. Mirrors the pattern in
 * {@code DuplicateSpecDialog} so both surfaces speak the same language for
 * the catalog endpoints.
 */
function describeError(err: unknown, name: string): string {
  const maybe = err as { response?: { data?: ProblemDetails } } | undefined
  const detail =
    maybe?.response?.data?.detail ?? maybe?.response?.data?.title ?? ''
  const code = detail.trim().toLowerCase()

  if (code.startsWith('name_taken')) {
    return `A spec named "${name}" already exists in this workspace.`
  }
  if (code.startsWith('runtime_has_no_spec')) {
    return 'This runtime has no spec yet — there is nothing to save.'
  }
  if (code.startsWith('invalid_name')) {
    return 'Name must be between 1 and 100 characters.'
  }
  if (code.startsWith('invalid_spec_json') || code.startsWith('invalid_spec')) {
    return "The runtime's current spec failed validation and can't be saved."
  }
  if (code.startsWith('not_a_member')) {
    return "You don't have access to this workspace."
  }
  if (code.startsWith('runtime_not_found')) {
    return 'Runtime no longer exists. Try reopening the drawer.'
  }
  return detail || 'Could not save spec to catalog. Try again.'
}

/**
 * Dialog for the "Save as catalog spec" action on the runtime spec drawer.
 *
 * <p>Captures a name (1–100 chars, required) and an optional description
 * (0–500 chars), then promotes the runtime's currently-applied spec into a
 * named workspace catalog entry. On success it invalidates the workspace
 * specs list and shows a confirmation snackbar. 409 / `name_taken` keeps
 * the dialog open with an inline error so the user can pick a different
 * name without losing what they typed.</p>
 *
 * <p>Styling matches the existing workspace catalog dialogs
 * ({@link DuplicateSpecDialog}, {@link SpecEditorDialog}) — warm-paper
 * canvas, hairline border, bronze focus accents.</p>
 */
export function SaveAsCatalogSpecDialog({
  open,
  onClose,
  runtimeId,
  workspaceId,
}: SaveAsCatalogSpecDialogProps) {
  const queryClient = useQueryClient()
  const { showSuccess } = useNotification()
  const mutation = usePostApiRuntimesRuntimeIdSaveAsCatalogSpec()

  const [name, setName] = useState('')
  const [description, setDescription] = useState('')
  const [error, setError] = useState<string | null>(null)

  // Reset the form whenever the dialog is reopened so each save starts fresh.
  useEffect(() => {
    if (open) {
      setName('')
      setDescription('')
      setError(null)
    }
  }, [open])

  const trimmedName = name.trim()
  const trimmedDescription = description.trim()
  const canSubmit =
    !mutation.isPending &&
    !!runtimeId &&
    trimmedName.length > 0 &&
    trimmedName.length <= 100 &&
    trimmedDescription.length <= 500

  const handleClose = () => {
    if (mutation.isPending) return
    onClose()
  }

  const handleSubmit = (event: React.FormEvent) => {
    event.preventDefault()
    if (!canSubmit) return
    setError(null)
    mutation.mutate(
      {
        runtimeId,
        data: {
          name: trimmedName,
          description:
            trimmedDescription.length > 0 ? trimmedDescription : null,
        },
      },
      {
        onSuccess: (resp) => {
          if (workspaceId) {
            queryClient.invalidateQueries({
              queryKey: getGetApiWorkspacesWorkspaceIdSpecsQueryKey(workspaceId),
            })
          }
          showSuccess(`Saved to catalog as "${resp.name}"`)
          onClose()
        },
        onError: (err) => {
          setError(describeError(err, trimmedName))
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
          Save as catalog spec
        </DialogTitle>
        <DialogContent>
          <Stack spacing={2}>
            <Typography sx={bodySx}>
              Save this runtime's spec to your workspace catalog so you can
              stamp it on new branches and projects.
            </Typography>
            <TextField
              size="small"
              fullWidth
              autoFocus
              value={name}
              onChange={(e) => setName(e.target.value)}
              placeholder="fullstack-dotnet-react"
              label="Name"
              required
              disabled={mutation.isPending}
              inputProps={{
                'aria-label': 'Catalog spec name',
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
                'aria-label': 'Catalog spec description',
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
            {mutation.isPending ? 'Saving…' : 'Save'}
          </Button>
        </DialogActions>
      </Box>
    </Dialog>
  )
}
