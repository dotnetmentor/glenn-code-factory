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
  FormControlLabel,
  Stack,
  Switch,
  TextField,
  Typography,
} from '@mui/material'
import { useQueryClient } from '@tanstack/react-query'
import {
  getGetApiAdminProjectTemplatesQueryKey,
  useGetApiAdminProjectTemplatesTemplateId,
  usePostApiAdminProjectTemplates,
  usePutApiAdminProjectTemplatesTemplateId,
} from '../../../../../api/queries-commands'
import type {
  CreateProjectTemplateRequest,
  ProblemDetails,
  ProjectTemplateListItem,
  UpdateProjectTemplateRequest,
} from '../../../../../api/queries-commands'

/**
 * Wire shape returned by the ProjectTemplates controller's error mapper —
 * a flat <c>{ error: "stable_code[: arg]" }</c> envelope. Distinct from
 * ASP.NET's <see cref="ProblemDetails"/> shape (which we historically also
 * read here), so we accept either to be resilient to future controller
 * changes.
 */
type ProjectTemplateError = { error?: string }

/**
 * Translates a server-side error envelope into a user-facing message.
 * Accepts BOTH wire shapes:
 *  - {@code { error: "slug_taken" }} — what ProjectTemplatesController
 *    actually emits via its <c>MapFailure</c> helper.
 *  - {@code ProblemDetails} ({@code detail}/{@code title}) — what the rest
 *    of the API tends to emit; kept for resilience.
 */
function humaniseProblem(
  body: ProblemDetails | ProjectTemplateError | undefined | null,
): { field: 'name' | 'slug' | 'runtimeSpec' | null; message: string } {
  const projectTemplateError = (body as ProjectTemplateError | null)?.error
  const problemDetail = (body as ProblemDetails | null)?.detail
  const problemTitle = (body as ProblemDetails | null)?.title
  const raw = projectTemplateError ?? problemDetail ?? problemTitle ?? null
  if (!raw)
    return { field: null, message: 'Could not save this Starter. Try again.' }

  const colonIdx = raw.indexOf(':')
  const code = colonIdx >= 0 ? raw.slice(0, colonIdx).trim() : raw.trim()
  const arg = colonIdx >= 0 ? raw.slice(colonIdx + 1).trim() : null

  switch (code) {
    case 'name_taken':
      return {
        field: 'name',
        message: 'A Starter with this name already exists.',
      }
    case 'slug_taken':
      return {
        field: 'slug',
        message: 'A Starter with this slug already exists.',
      }
    case 'slug_invalid':
      return {
        field: 'slug',
        message: 'Slug must be lowercase letters, digits, and hyphens.',
      }
    case 'name_required':
      return { field: 'name', message: 'Name is required.' }
    case 'name_too_long':
      return { field: 'name', message: 'Name must be at most 100 characters.' }
    case 'invalid_name':
      return { field: 'name', message: 'Name is required and must be at most 100 characters.' }
    case 'slug_required':
      return { field: 'slug', message: 'Slug is required.' }
    case 'slug_too_long':
      return { field: 'slug', message: 'Slug must be at most 100 characters.' }
    case 'invalid_slug':
      return { field: 'slug', message: 'Slug is required and must be at most 100 characters.' }
    case 'invalid_description':
      return { field: null, message: 'Description must be at most 500 characters.' }
    case 'invalid_icon_key':
      return { field: null, message: 'Icon key must be at most 50 characters.' }
    case 'invalid_source_repo_owner':
      return { field: null, message: 'Repo owner is required and must be at most 120 characters.' }
    case 'invalid_source_repo_name':
      return { field: null, message: 'Repo name is required and must be at most 120 characters.' }
    case 'spec_invalid':
    case 'runtime_spec_invalid':
    case 'content_invalid_spec':
      return {
        field: 'runtimeSpec',
        message: arg
          ? `Runtime Spec is not a valid V2 RuntimeSpec: ${arg}`
          : 'Runtime Spec is not a valid V2 RuntimeSpec.',
      }
    case 'spec_invalid_json':
    case 'runtime_spec_invalid_json':
    case 'content_invalid_json':
      return {
        field: 'runtimeSpec',
        message:
          'Runtime Spec must be valid JSON. Check for trailing commas or unmatched brackets.',
      }
    case 'forbidden':
    case 'not_authorized':
      return {
        field: null,
        message: "You don't have permission to manage Starters.",
      }
    case 'template_not_found':
      return {
        field: null,
        message: 'This Starter no longer exists — it may have been archived.',
      }
    default:
      return { field: null, message: raw }
  }
}

function readProblem(
  err: unknown,
): ProblemDetails | ProjectTemplateError | null {
  const maybe = err as
    | { response?: { data?: ProblemDetails | ProjectTemplateError } }
    | undefined
  return maybe?.response?.data ?? null
}

const SLUG_PATTERN = /^[a-z0-9]+(?:-[a-z0-9]+)*$/

const PLACEHOLDER_SPEC = `{
  "version": 2,
  "install": [],
  "services": [],
  "setup": []
}
`

export type StarterEditorMode =
  | { kind: 'create' }
  | { kind: 'edit'; starter: ProjectTemplateListItem }

interface StarterEditorDialogProps {
  open: boolean
  onClose: () => void
  mode: StarterEditorMode
  onSaved?: (message: string) => void
  onError?: (message: string) => void
}

/**
 * Create / edit dialog for a single Starter (ProjectTemplate). The Runtime
 * Spec field is an optional inline JSON editor — null means "no spec, just
 * clone the repo" (used by the Empty and Rails-8 starters). The dialog mirrors
 * the spirit of the workspace SpecEditorDialog so the two surfaces feel
 * related, but lives natively under the super-admin chrome.
 */
export function StarterEditorDialog({
  open,
  onClose,
  mode,
  onSaved,
  onError,
}: StarterEditorDialogProps) {
  const queryClient = useQueryClient()

  const isEdit = mode.kind === 'edit'
  const templateId = isEdit ? mode.starter.id : null

  // In edit mode we need the runtimeSpec body, which the list endpoint
  // intentionally omits. Fetch the detail when the dialog opens.
  const detailQuery = useGetApiAdminProjectTemplatesTemplateId(
    templateId ?? '',
    { query: { enabled: open && !!templateId } },
  )

  const createMutation = usePostApiAdminProjectTemplates()
  const updateMutation = usePutApiAdminProjectTemplatesTemplateId()

  const [name, setName] = useState('')
  const [slug, setSlug] = useState('')
  const [description, setDescription] = useState('')
  const [iconKey, setIconKey] = useState('')
  const [repoOwner, setRepoOwner] = useState('')
  const [repoName, setRepoName] = useState('')
  const [runtimeSpec, setRuntimeSpec] = useState('')
  const [isActive, setIsActive] = useState(true)
  const [isDefault, setIsDefault] = useState(false)
  const [sortOrder, setSortOrder] = useState<string>('0')

  const [serverError, setServerError] = useState<string | null>(null)
  const [fieldError, setFieldError] = useState<
    'name' | 'slug' | 'runtimeSpec' | null
  >(null)

  // Seed the form whenever the dialog opens, and reseed in edit mode when the
  // detail query lands so we replace the list-item's "no spec" placeholder
  // with the real Runtime Spec body.
  useEffect(() => {
    if (!open) return
    if (mode.kind === 'create') {
      setName('')
      setSlug('')
      setDescription('')
      setIconKey('')
      setRepoOwner('')
      setRepoName('')
      setRuntimeSpec('')
      setIsActive(true)
      setIsDefault(false)
      setSortOrder('0')
      setServerError(null)
      setFieldError(null)
      return
    }
    // Edit — start from list-item so the dialog isn't blank while detail is
    // in flight; reseed Runtime Spec from the detail response when it lands.
    const s = mode.starter
    setName(s.name)
    setSlug(s.slug)
    setDescription(s.description ?? '')
    setIconKey(s.iconKey ?? '')
    setRepoOwner(s.sourceRepoOwner)
    setRepoName(s.sourceRepoName)
    setIsActive(s.isActive)
    setIsDefault(s.isDefault)
    setSortOrder(String(s.sortOrder))
    setServerError(null)
    setFieldError(null)
    if (detailQuery.data) {
      setRuntimeSpec(detailQuery.data.runtimeSpec ?? '')
    } else {
      setRuntimeSpec('')
    }
  }, [open, mode, detailQuery.data])

  const isPending = createMutation.isPending || updateMutation.isPending
  const isLoadingDetail = isEdit && detailQuery.isLoading

  const trimmedName = name.trim()
  const trimmedSlug = slug.trim()
  const trimmedDescription = description.trim()
  const trimmedIconKey = iconKey.trim()
  const trimmedOwner = repoOwner.trim()
  const trimmedRepoName = repoName.trim()
  const trimmedSpec = runtimeSpec.trim()

  const slugLooksValid = trimmedSlug.length === 0 || SLUG_PATTERN.test(trimmedSlug)

  const parsedSortOrder = Number(sortOrder)
  const sortOrderIsInteger =
    sortOrder.trim() !== '' && Number.isInteger(parsedSortOrder)

  const canSubmit = useMemo(() => {
    if (isPending) return false
    if (trimmedName.length === 0 || trimmedName.length > 100) return false
    if (trimmedSlug.length === 0 || trimmedSlug.length > 100) return false
    if (!slugLooksValid) return false
    if (trimmedOwner.length === 0 || trimmedOwner.length > 120) return false
    if (trimmedRepoName.length === 0 || trimmedRepoName.length > 120)
      return false
    if (!sortOrderIsInteger) return false
    return true
  }, [
    isPending,
    trimmedName,
    trimmedSlug,
    slugLooksValid,
    trimmedOwner,
    trimmedRepoName,
    sortOrderIsInteger,
  ])

  const handleClose = () => {
    if (isPending) return
    onClose()
  }

  const handleFormatJson = () => {
    if (trimmedSpec.length === 0) return
    try {
      const parsed = JSON.parse(runtimeSpec)
      setRuntimeSpec(JSON.stringify(parsed, null, 2))
      if (fieldError === 'runtimeSpec') setFieldError(null)
    } catch {
      setFieldError('runtimeSpec')
      setServerError(
        'Runtime Spec is not valid JSON — could not format. Check for trailing commas or unmatched brackets.',
      )
    }
  }

  const handleSubmit = (event: React.FormEvent) => {
    event.preventDefault()
    if (!canSubmit) return
    setServerError(null)
    setFieldError(null)

    const payload: CreateProjectTemplateRequest & UpdateProjectTemplateRequest = {
      name: trimmedName,
      slug: trimmedSlug,
      description: trimmedDescription.length > 0 ? trimmedDescription : null,
      iconKey: trimmedIconKey.length > 0 ? trimmedIconKey : null,
      sourceRepoOwner: trimmedOwner,
      sourceRepoName: trimmedRepoName,
      runtimeSpec: trimmedSpec.length > 0 ? runtimeSpec : null,
      isActive,
      isDefault,
      sortOrder: parsedSortOrder,
    }

    const onSuccessCommon = (label: string) => {
      queryClient.invalidateQueries({
        queryKey: getGetApiAdminProjectTemplatesQueryKey(),
      })
      onSaved?.(label)
      onClose()
    }

    const handleError = (err: unknown) => {
      const { field, message } = humaniseProblem(readProblem(err))
      setFieldError(field)
      setServerError(message)
      if (!field) {
        onError?.(message)
      }
    }

    if (mode.kind === 'create') {
      createMutation.mutate(
        { data: payload },
        {
          onSuccess: (resp) =>
            onSuccessCommon(`Starter '${resp.name}' created.`),
          onError: handleError,
        },
      )
    } else {
      updateMutation.mutate(
        { templateId: mode.starter.id, data: payload },
        {
          onSuccess: (resp) =>
            onSuccessCommon(`Starter '${resp.name}' saved.`),
          onError: handleError,
        },
      )
    }
  }

  const title = mode.kind === 'create' ? 'New Starter' : 'Edit Starter'
  const submitLabel =
    mode.kind === 'create'
      ? createMutation.isPending
        ? 'Creating…'
        : 'Create'
      : updateMutation.isPending
        ? 'Saving…'
        : 'Save'

  return (
    <Dialog
      open={open}
      onClose={handleClose}
      maxWidth="md"
      fullWidth
      PaperProps={{ sx: { borderRadius: 2 } }}
    >
      <Box component="form" onSubmit={handleSubmit} noValidate>
        <DialogTitle sx={{ fontWeight: 600 }}>{title}</DialogTitle>
        <DialogContent dividers>
          <Stack spacing={2.5} sx={{ pt: 1 }}>
            <Typography variant="body2" color="text.secondary">
              Starters bundle a GitHub template repo with an optional Runtime
              Spec so new projects come up with their dependencies installed
              and services running.
            </Typography>

            {isLoadingDetail ? (
              <Stack
                direction="row"
                spacing={1.5}
                alignItems="center"
                sx={{ color: 'text.secondary' }}
              >
                <CircularProgress size={14} />
                <Typography variant="caption">
                  Loading Runtime Spec…
                </Typography>
              </Stack>
            ) : null}

            <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2}>
              <TextField
                label="Name"
                value={name}
                onChange={(e) => setName(e.target.value)}
                placeholder="React + Vite + TS"
                required
                fullWidth
                size="small"
                autoFocus={mode.kind === 'create'}
                disabled={isPending}
                inputProps={{ 'aria-label': 'Starter name', maxLength: 100 }}
                error={fieldError === 'name'}
                helperText={
                  fieldError === 'name'
                    ? serverError ?? undefined
                    : `${trimmedName.length}/100`
                }
              />
              <TextField
                label="Slug"
                value={slug}
                onChange={(e) => setSlug(e.target.value)}
                placeholder="react-vite-ts"
                required
                fullWidth
                size="small"
                disabled={isPending}
                inputProps={{ 'aria-label': 'Starter slug', maxLength: 100 }}
                error={fieldError === 'slug' || !slugLooksValid}
                helperText={
                  fieldError === 'slug'
                    ? serverError ?? undefined
                    : !slugLooksValid
                      ? 'Lowercase letters, digits, and hyphens only.'
                      : 'Stable identifier — used in URLs and analytics.'
                }
              />
            </Stack>

            <TextField
              label="Description"
              value={description}
              onChange={(e) => setDescription(e.target.value)}
              placeholder="One-line summary shown in the picker."
              fullWidth
              multiline
              minRows={2}
              maxRows={4}
              size="small"
              disabled={isPending}
              inputProps={{
                'aria-label': 'Starter description',
                maxLength: 500,
              }}
              helperText={`${trimmedDescription.length}/500 — optional`}
            />

            <TextField
              label="Icon key (optional)"
              value={iconKey}
              onChange={(e) => setIconKey(e.target.value)}
              placeholder="rails, react, empty"
              fullWidth
              size="small"
              disabled={isPending}
              inputProps={{ 'aria-label': 'Icon key', maxLength: 50 }}
              helperText="Short identifier for the icon rendered next to this Starter."
            />

            <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2}>
              <TextField
                label="GitHub repo owner"
                value={repoOwner}
                onChange={(e) => setRepoOwner(e.target.value)}
                placeholder="our-org"
                required
                fullWidth
                size="small"
                disabled={isPending}
                inputProps={{ 'aria-label': 'Source repo owner', maxLength: 120 }}
              />
              <TextField
                label="GitHub repo name"
                value={repoName}
                onChange={(e) => setRepoName(e.target.value)}
                placeholder="rails-8-template"
                required
                fullWidth
                size="small"
                disabled={isPending}
                inputProps={{
                  'aria-label': 'Source repo name',
                  maxLength: 120,
                }}
                helperText="Repos must be a GitHub template the install can fork from."
              />
            </Stack>

            <Box>
              <Stack
                direction="row"
                alignItems="center"
                justifyContent="space-between"
                sx={{ mb: 0.5 }}
              >
                <Typography variant="body2" sx={{ fontWeight: 500 }}>
                  Runtime Spec (optional)
                </Typography>
                <Button
                  type="button"
                  size="small"
                  variant="text"
                  onClick={handleFormatJson}
                  disabled={isPending || trimmedSpec.length === 0}
                  sx={{ textTransform: 'none' }}
                >
                  Format JSON
                </Button>
              </Stack>
              <TextField
                value={runtimeSpec}
                onChange={(e) => setRuntimeSpec(e.target.value)}
                placeholder={PLACEHOLDER_SPEC}
                fullWidth
                multiline
                rows={12}
                size="small"
                disabled={isPending || isLoadingDetail}
                inputProps={{ 'aria-label': 'Runtime Spec JSON' }}
                error={fieldError === 'runtimeSpec'}
                helperText={
                  fieldError === 'runtimeSpec'
                    ? serverError ?? undefined
                    : 'Inline RuntimeSpecV2 JSON. Empty = no spec — runtime comes up empty after clone.'
                }
                InputProps={{
                  sx: {
                    fontFamily: '"SF Mono", "Fira Code", "Consolas", monospace',
                    fontSize: '0.8125rem',
                    lineHeight: 1.5,
                    alignItems: 'flex-start',
                  },
                }}
              />
            </Box>

            <Stack
              direction={{ xs: 'column', sm: 'row' }}
              spacing={2}
              alignItems={{ xs: 'flex-start', sm: 'center' }}
              sx={{ pt: 0.5 }}
            >
              <FormControlLabel
                control={
                  <Switch
                    checked={isActive}
                    onChange={(e) => setIsActive(e.target.checked)}
                    disabled={isPending}
                    inputProps={{ 'aria-label': 'Active' }}
                  />
                }
                label={
                  <Box>
                    <Typography variant="body2" sx={{ fontWeight: 500 }}>
                      Active
                    </Typography>
                    <Typography variant="caption" color="text.secondary">
                      Show in the new-project picker.
                    </Typography>
                  </Box>
                }
              />
              <FormControlLabel
                control={
                  <Switch
                    checked={isDefault}
                    onChange={(e) => setIsDefault(e.target.checked)}
                    disabled={isPending}
                    inputProps={{ 'aria-label': 'Default' }}
                  />
                }
                label={
                  <Box>
                    <Typography variant="body2" sx={{ fontWeight: 500 }}>
                      Default
                    </Typography>
                    <Typography variant="caption" color="text.secondary">
                      Pre-selected when the picker opens.
                    </Typography>
                  </Box>
                }
              />
              <TextField
                label="Sort order"
                type="number"
                value={sortOrder}
                onChange={(e) => setSortOrder(e.target.value)}
                helperText="Lower numbers appear first."
                size="small"
                required
                disabled={isPending}
                inputProps={{
                  'aria-label': 'Sort order',
                  inputMode: 'numeric',
                  step: 1,
                }}
                sx={{ width: 160 }}
                error={!sortOrderIsInteger}
              />
            </Stack>

            {serverError && fieldError === null ? (
              <Alert severity="error" variant="outlined">
                {serverError}
              </Alert>
            ) : null}
          </Stack>
        </DialogContent>
        <DialogActions sx={{ px: 3, py: 2 }}>
          <Button
            type="button"
            onClick={handleClose}
            disabled={isPending}
            sx={{ textTransform: 'none' }}
          >
            Cancel
          </Button>
          <Button
            type="submit"
            variant="contained"
            disabled={!canSubmit}
            sx={{
              textTransform: 'none',
              boxShadow: 'none',
              '&:hover': { boxShadow: 'none' },
            }}
          >
            {submitLabel}
          </Button>
        </DialogActions>
      </Box>
    </Dialog>
  )
}
