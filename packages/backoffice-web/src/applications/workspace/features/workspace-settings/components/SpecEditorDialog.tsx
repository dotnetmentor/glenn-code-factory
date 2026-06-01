import { useEffect, useMemo, useState } from 'react'
import {
  Alert,
  Box,
  Button,
  CircularProgress,
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
  useGetApiWorkspacesWorkspaceIdSpecsSpecId,
  usePostApiWorkspacesWorkspaceIdSpecs,
  usePutApiWorkspacesWorkspaceIdSpecsSpecId,
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

/**
 * Resolves a server-side ProblemDetails into a single user-readable string.
 *
 * <p>The backend surfaces validation errors as kebab-cased codes on the
 * {@code detail} or {@code title} field (e.g. {@code service_command_required:
 * web}, {@code name_taken}). This helper expands the well-known codes into
 * sentences the user can act on, and falls back to the raw message for
 * anything we haven't taught it.</p>
 */
function humaniseProblem(problem: ProblemDetails | undefined | null): string {
  const raw = problem?.detail ?? problem?.title ?? null
  if (!raw) return 'Something went wrong saving this spec. Try again.'

  // The server may send {@code code: argument} pairs (e.g.
  // {@code service_command_required: web}). Split on the first colon so we can
  // keep the argument for the message.
  const colonIdx = raw.indexOf(':')
  const code = colonIdx >= 0 ? raw.slice(0, colonIdx).trim() : raw.trim()
  const arg = colonIdx >= 0 ? raw.slice(colonIdx + 1).trim() : null

  switch (code) {
    case 'name_taken':
      return 'A spec with this name already exists in this workspace.'
    case 'name_required':
      return 'Name is required.'
    case 'name_too_long':
      return 'Name must be at most 100 characters.'
    case 'description_too_long':
      return 'Description must be at most 500 characters.'
    case 'content_required':
      return 'Content is required.'
    case 'content_invalid_json':
      return 'Content must be valid JSON. Check for trailing commas, missing quotes, or unmatched brackets.'
    case 'content_invalid_spec':
      return arg
        ? `Content is not a valid V2 RuntimeSpec: ${arg}`
        : 'Content is not a valid V2 RuntimeSpec.'
    case 'service_name_required':
      return arg
        ? `Service \`${arg}\` is missing its \`name\` field.`
        : 'A service in the spec is missing its `name` field.'
    case 'service_command_required':
      return arg
        ? `Service \`${arg}\` is missing its \`command\` field.`
        : 'A service in the spec is missing its `command` field.'
    case 'service_duplicate_name':
      return arg
        ? `Two services share the same name \`${arg}\`. Service names must be unique.`
        : 'Two services share the same name. Service names must be unique.'
    case 'workspace_not_found':
      return 'This workspace no longer exists.'
    case 'spec_not_found':
      return 'This spec no longer exists. It may have been deleted by someone else.'
    case 'forbidden':
      return "You don't have permission to edit this spec."
    default:
      // Unknown code — pass the message through so we don't swallow detail
      // the user might recognise. The string still beats a silent failure.
      return raw
  }
}

function readProblem(err: unknown): ProblemDetails | null {
  const maybe = err as { response?: { data?: ProblemDetails } } | undefined
  return maybe?.response?.data ?? null
}

export type SpecEditorMode =
  | { kind: 'create' }
  | { kind: 'edit'; spec: WorkspaceSpecListItem }

interface SpecEditorDialogProps {
  open: boolean
  onClose: () => void
  workspaceId: string
  mode: SpecEditorMode
}

const PLACEHOLDER_CONTENT = `{
  "version": 2,
  "install": [],
  "services": [],
  "setup": []
}
`

/**
 * Create / edit dialog for a single workspace catalog spec.
 *
 * <p>Three fields: name, description, content. Content is a plain monospace
 * multiline {@code TextField} — no Monaco, no syntax highlighting. The server
 * is the source of truth for "is this valid V2 RuntimeSpec?", and surfaces
 * structured codes which {@link humaniseProblem} translates into one-line
 * messages above the form.</p>
 *
 * <p>In edit mode the dialog fetches the full {@link WorkspaceSpecDetail} via
 * {@code GET .../specs/{id}} so it has the content, which the list endpoint
 * intentionally omits. The list-item is passed in so we can paint the title
 * and prefilled name/description immediately while the content is in flight.</p>
 */
export function SpecEditorDialog({
  open,
  onClose,
  workspaceId,
  mode,
}: SpecEditorDialogProps) {
  const queryClient = useQueryClient()
  const { showSuccess } = useNotification()

  const isEdit = mode.kind === 'edit'
  const specId = isEdit ? mode.spec.id : null

  const detailQuery = useGetApiWorkspacesWorkspaceIdSpecsSpecId(
    workspaceId,
    specId ?? '',
    { query: { enabled: open && !!specId } },
  )

  const createMutation = usePostApiWorkspacesWorkspaceIdSpecs()
  const updateMutation = usePutApiWorkspacesWorkspaceIdSpecsSpecId()

  const [name, setName] = useState('')
  const [description, setDescription] = useState('')
  const [content, setContent] = useState('')
  const [error, setError] = useState<string | null>(null)

  // Seed the form whenever the dialog opens, and reseed in edit mode as soon
  // as the detail query lands so we replace the list-item's "no content" with
  // the real spec body.
  useEffect(() => {
    if (!open) return
    if (mode.kind === 'create') {
      setName('')
      setDescription('')
      setContent(PLACEHOLDER_CONTENT)
      setError(null)
      return
    }
    // Edit mode — start from the list-item so the dialog isn't empty while
    // the detail query is in flight.
    setName(mode.spec.name)
    setDescription(mode.spec.description ?? '')
    setError(null)
    if (detailQuery.data) {
      setContent(detailQuery.data.content)
    } else {
      setContent('')
    }
  }, [open, mode, detailQuery.data])

  const isPending = createMutation.isPending || updateMutation.isPending
  const isLoadingDetail = isEdit && detailQuery.isLoading

  const trimmedName = name.trim()
  const trimmedDescription = description.trim()
  const trimmedContent = content.trim()

  const canSubmit = useMemo(() => {
    if (isPending) return false
    if (trimmedName.length === 0 || trimmedName.length > 100) return false
    if (trimmedDescription.length > 500) return false
    if (trimmedContent.length === 0) return false
    return true
  }, [isPending, trimmedName, trimmedDescription, trimmedContent])

  const handleClose = () => {
    if (isPending) return
    onClose()
  }

  const handleSubmit = (event: React.FormEvent) => {
    event.preventDefault()
    if (!canSubmit) return
    setError(null)

    const payload = {
      name: trimmedName,
      description: trimmedDescription.length > 0 ? trimmedDescription : null,
      content,
    }

    const onSuccessCommon = (label: string) => {
      queryClient.invalidateQueries({
        queryKey: getGetApiWorkspacesWorkspaceIdSpecsQueryKey(workspaceId),
      })
      showSuccess(label)
      onClose()
    }

    const onError = (err: unknown) => {
      setError(humaniseProblem(readProblem(err)))
    }

    if (mode.kind === 'create') {
      createMutation.mutate(
        { workspaceId, data: payload },
        {
          onSuccess: (resp) => onSuccessCommon(`Spec '${resp.name}' created.`),
          onError,
        },
      )
    } else {
      updateMutation.mutate(
        { workspaceId, specId: mode.spec.id, data: payload },
        {
          onSuccess: (resp) => onSuccessCommon(`Spec '${resp.name}' saved.`),
          onError,
        },
      )
    }
  }

  const title = mode.kind === 'create' ? 'Create spec' : `Edit spec`
  const submitLabel =
    mode.kind === 'create'
      ? createMutation.isPending
        ? 'Creating…'
        : 'Create'
      : updateMutation.isPending
        ? 'Saving…'
        : 'Save'

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
      maxWidth="md"
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
          {title}
        </DialogTitle>
        <DialogContent>
          <Stack spacing={2.5}>
            <Typography sx={bodySx}>
              Reusable runtime spec for this workspace. Branches forked or
              created from this spec receive a copy of the content at fork
              time — later edits never touch existing branches.
            </Typography>

            {isLoadingDetail && (
              <Stack direction="row" spacing={1.5} alignItems="center">
                <CircularProgress size={14} sx={{ color: workspaceText.muted }} />
                <Typography sx={captionSx}>Loading spec content…</Typography>
              </Stack>
            )}

            <TextField
              size="small"
              fullWidth
              autoFocus={mode.kind === 'create'}
              value={name}
              onChange={(e) => setName(e.target.value)}
              placeholder="fullstack-dotnet-react"
              label="Name"
              required
              disabled={isPending}
              inputProps={{
                'aria-label': 'Spec name',
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
              placeholder="Standard backend + frontend for our customer projects."
              label="Description"
              disabled={isPending}
              inputProps={{
                'aria-label': 'Spec description',
                maxLength: 500,
              }}
              InputProps={{ sx: inputSxBase }}
              helperText={`${trimmedDescription.length}/500 — optional`}
              FormHelperTextProps={{
                sx: { ...captionSx, fontSize: '0.75rem', ml: 0.5 },
              }}
            />

            <TextField
              size="small"
              fullWidth
              multiline
              rows={18}
              value={content}
              onChange={(e) => setContent(e.target.value)}
              label="Content"
              required
              disabled={isPending || isLoadingDetail}
              inputProps={{ 'aria-label': 'Spec content (JSON)' }}
              InputProps={{
                sx: {
                  ...inputSxBase,
                  fontFamily: workspaceFontFamily.mono,
                  fontSize: '0.8125rem',
                  lineHeight: 1.5,
                  alignItems: 'flex-start',
                },
              }}
              helperText="Must be valid V2 RuntimeSpec JSON."
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
            disabled={isPending}
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
            {submitLabel}
          </Button>
        </DialogActions>
      </Box>
    </Dialog>
  )
}
