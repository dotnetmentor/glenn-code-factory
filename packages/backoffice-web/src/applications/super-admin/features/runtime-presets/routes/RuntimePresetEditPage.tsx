import { useEffect, useMemo, useState } from 'react'
import { useNavigate, useParams } from 'react-router-dom'
import {
  Alert,
  Box,
  Button,
  Chip,
  CircularProgress,
  FormControl,
  FormControlLabel,
  IconButton,
  InputLabel,
  MenuItem,
  Paper,
  Select,
  Snackbar,
  Stack,
  Switch,
  TextField,
  Tooltip,
  Typography,
  alpha,
} from '@mui/material'
import ArrowBackIcon from '@mui/icons-material/ArrowBack'
import SaveIcon from '@mui/icons-material/Save'
import SettingsApplicationsIcon from '@mui/icons-material/SettingsApplications'
import LockIcon from '@mui/icons-material/Lock'
import AddIcon from '@mui/icons-material/Add'
import DeleteOutlineIcon from '@mui/icons-material/DeleteOutline'
import ContentCopyIcon from '@mui/icons-material/ContentCopy'
import { useQueryClient } from '@tanstack/react-query'
import {
  getGetApiAdminRuntimePresetsQueryKey,
  useGetApiAdminRuntimePresets,
  usePostApiAdminRuntimePresets,
  usePostApiAdminRuntimePresetsIdClone,
  usePutApiAdminRuntimePresetsId,
  type CreatePresetRequest,
  type PresetParameter,
  type ServicePresetDto,
  type UpdatePresetRequest,
} from '@/api/queries-commands'
import { ParameterEditor } from '../components/ParameterEditor'
import { PresetPreview } from '../components/PresetPreview'

const SLUG_PATTERN = /^[a-z][a-z0-9-]+$/

const CATEGORIES = ['backend', 'frontend', 'database', 'worker', 'other'] as const

interface SnackState {
  open: boolean
  message: string
  severity: 'success' | 'error'
}

interface FormState {
  slug: string
  displayName: string
  description: string
  category: string
  iconName: string
  commandTemplate: string
  defaultUser: string
  autorestart: boolean
  healthcheckCommand: string
  healthcheckInterval: string // numeric input, kept as string for editing
  installContribution: string
  setupContribution: string
  installVerify: string
  envRows: EnvRow[]
  parameters: PresetParameter[]
}

interface EnvRow {
  key: string
  value: string
}

function emptyForm(): FormState {
  return {
    slug: '',
    displayName: '',
    description: '',
    category: 'backend',
    iconName: '',
    commandTemplate: '',
    defaultUser: '',
    autorestart: true,
    healthcheckCommand: '',
    healthcheckInterval: '',
    installContribution: '',
    setupContribution: '',
    installVerify: '',
    envRows: [],
    parameters: [],
  }
}

function formFromDto(dto: ServicePresetDto): FormState {
  const envRows: EnvRow[] = Object.entries(dto.envTemplate ?? {}).map(
    ([k, v]) => ({ key: k, value: v }),
  )
  return {
    slug: dto.slug,
    displayName: dto.displayName,
    description: dto.description,
    category: dto.category || 'other',
    iconName: dto.iconName ?? '',
    commandTemplate: dto.commandTemplate ?? '',
    defaultUser: dto.defaultUser ?? '',
    autorestart: dto.autorestart,
    healthcheckCommand: dto.healthcheckCommand ?? '',
    healthcheckInterval:
      dto.healthcheckInterval == null ? '' : String(dto.healthcheckInterval),
    installContribution: dto.installContribution ?? '',
    setupContribution: dto.setupContribution ?? '',
    installVerify: dto.installVerify ?? '',
    envRows,
    parameters: dto.parameters ?? [],
  }
}

function envRowsToMap(rows: EnvRow[]): Record<string, string> {
  const out: Record<string, string> = {}
  for (const r of rows) {
    const k = r.key.trim()
    if (k.length === 0) continue
    out[k] = r.value
  }
  return out
}

function extractErrorMessage(err: unknown): string {
  if (!err) return 'Unknown error'
  if (typeof err === 'string') return err
  if (typeof err === 'object') {
    const maybe = err as {
      message?: unknown
      title?: unknown
      detail?: unknown
      response?: { data?: unknown }
    }
    const data = maybe.response?.data
    if (data && typeof data === 'object') {
      const d = data as {
        detail?: unknown
        title?: unknown
        message?: unknown
        error?: unknown
      }
      if (typeof d.detail === 'string') return d.detail
      if (typeof d.title === 'string') return d.title
      if (typeof d.message === 'string') return d.message
      if (typeof d.error === 'string') return d.error
    }
    if (typeof maybe.detail === 'string') return maybe.detail
    if (typeof maybe.title === 'string') return maybe.title
    if (typeof maybe.message === 'string') return maybe.message
  }
  return 'Something went wrong'
}

/**
 * Dual-mode Service Preset editor. Creates a new preset when the URL is
 * <c>/super-admin/runtime-presets/new</c>; edits an existing one when the
 * URL is <c>/super-admin/runtime-presets/:id</c>. Built-in presets are
 * rendered in read-only mode with a "Clone it to make changes" banner —
 * the backend enforces the same rule with a 409 if a write slips through.
 *
 * <p>Live preview talks to <c>/api/admin/runtime-presets/{id}/preview</c>,
 * which only exists for saved presets, so the Preview panel renders a
 * placeholder until the preset is saved.</p>
 */
export function RuntimePresetEditPage() {
  const params = useParams<{ id: string }>()
  const navigate = useNavigate()
  const queryClient = useQueryClient()

  const id = params.id
  const isCreate = !id || id === 'new'

  const listQuery = useGetApiAdminRuntimePresets({
    query: { staleTime: 30_000 },
  })

  const presetDto = useMemo<ServicePresetDto | undefined>(() => {
    if (isCreate) return undefined
    return (listQuery.data ?? []).find((p) => p.id === id)
  }, [isCreate, id, listQuery.data])

  const [form, setForm] = useState<FormState>(() => emptyForm())
  const [hasHydrated, setHasHydrated] = useState(false)
  const [parameterValues, setParameterValues] = useState<
    Record<string, string>
  >({})
  const [snack, setSnack] = useState<SnackState>({
    open: false,
    message: '',
    severity: 'success',
  })
  const [validationError, setValidationError] = useState<string | null>(null)
  const [cloneOpen, setCloneOpen] = useState(false)
  const [cloneSlug, setCloneSlug] = useState('')
  const [cloneError, setCloneError] = useState<string | null>(null)

  // Hydrate the form once the matching preset arrives in the list query.
  useEffect(() => {
    if (isCreate) {
      if (!hasHydrated) {
        setForm(emptyForm())
        setHasHydrated(true)
      }
      return
    }
    if (presetDto && !hasHydrated) {
      setForm(formFromDto(presetDto))
      setHasHydrated(true)
    }
  }, [isCreate, presetDto, hasHydrated])

  const isBuiltIn = !!presetDto?.isBuiltIn
  const readOnly = isBuiltIn

  const createMutation = usePostApiAdminRuntimePresets()
  const updateMutation = usePutApiAdminRuntimePresetsId()
  const cloneMutation = usePostApiAdminRuntimePresetsIdClone()

  const invalidate = () => {
    queryClient.invalidateQueries({
      queryKey: getGetApiAdminRuntimePresetsQueryKey(),
    })
  }

  const showSnack = (message: string, severity: 'success' | 'error') =>
    setSnack({ open: true, message, severity })

  const setFormField = <K extends keyof FormState>(
    key: K,
    value: FormState[K],
  ) => {
    setForm((prev) => ({ ...prev, [key]: value }))
  }

  const validate = (): string | null => {
    if (isCreate) {
      if (!SLUG_PATTERN.test(form.slug.trim())) {
        return 'Slug must start with a lowercase letter and contain only lowercase letters, digits, and hyphens.'
      }
    }
    if (form.displayName.trim().length === 0) {
      return 'Display name is required.'
    }
    if (form.description.trim().length === 0) {
      return 'Description is required.'
    }
    if (form.category.trim().length === 0) {
      return 'Category is required.'
    }
    if (form.commandTemplate.trim().length === 0) {
      return 'Command template is required.'
    }
    if (
      form.healthcheckInterval.trim().length > 0 &&
      Number.isNaN(Number(form.healthcheckInterval))
    ) {
      return 'Healthcheck interval must be a number (seconds).'
    }
    for (const p of form.parameters) {
      if (!p.key || !SLUG_PATTERN.test(p.key)) {
        return `Parameter key "${p.key || '(empty)'}" is invalid. Use lowercase letters, digits, and hyphens.`
      }
      if (!p.label) {
        return `Parameter "${p.key}" needs a label.`
      }
    }
    return null
  }

  const buildCreatePayload = (): CreatePresetRequest => ({
    slug: form.slug.trim(),
    displayName: form.displayName.trim(),
    description: form.description.trim(),
    category: form.category.trim(),
    iconName: form.iconName.trim() || null,
    commandTemplate: form.commandTemplate,
    envTemplate:
      form.envRows.length === 0 ? null : envRowsToMap(form.envRows),
    healthcheckCommand: form.healthcheckCommand.trim() || null,
    healthcheckInterval:
      form.healthcheckInterval.trim().length === 0
        ? null
        : Number(form.healthcheckInterval),
    defaultUser: form.defaultUser.trim() || null,
    autorestart: form.autorestart,
    installContribution: form.installContribution.trim() || null,
    setupContribution: form.setupContribution.trim() || null,
    installVerify: form.installVerify.trim() || null,
    parameters: form.parameters,
  })

  const buildUpdatePayload = (): UpdatePresetRequest => ({
    displayName: form.displayName.trim(),
    description: form.description.trim(),
    category: form.category.trim(),
    iconName: form.iconName.trim() || null,
    commandTemplate: form.commandTemplate,
    envTemplate:
      form.envRows.length === 0 ? null : envRowsToMap(form.envRows),
    healthcheckCommand: form.healthcheckCommand.trim() || null,
    healthcheckInterval:
      form.healthcheckInterval.trim().length === 0
        ? null
        : Number(form.healthcheckInterval),
    defaultUser: form.defaultUser.trim() || null,
    autorestart: form.autorestart,
    installContribution: form.installContribution.trim() || null,
    setupContribution: form.setupContribution.trim() || null,
    installVerify: form.installVerify.trim() || null,
    parameters: form.parameters,
  })

  const handleSave = () => {
    const err = validate()
    if (err) {
      setValidationError(err)
      return
    }
    setValidationError(null)

    if (isCreate) {
      createMutation.mutate(
        { data: buildCreatePayload() },
        {
          onSuccess: (created) => {
            invalidate()
            showSnack(`Created '${created.slug}'.`, 'success')
            navigate(`/super-admin/runtime-presets/${created.id}`)
          },
          onError: (e) => {
            showSnack(extractErrorMessage(e), 'error')
          },
        },
      )
      return
    }

    if (!presetDto) return
    updateMutation.mutate(
      { id: presetDto.id, data: buildUpdatePayload() },
      {
        onSuccess: () => {
          invalidate()
          showSnack(`Saved '${presetDto.slug}'.`, 'success')
        },
        onError: (e) => {
          showSnack(extractErrorMessage(e), 'error')
        },
      },
    )
  }

  const handleCloneSubmit = () => {
    if (!presetDto) return
    const slug = cloneSlug.trim()
    if (!SLUG_PATTERN.test(slug)) {
      setCloneError(
        'Slug must start with a lowercase letter and contain only lowercase letters, digits, and hyphens.',
      )
      return
    }
    setCloneError(null)
    cloneMutation.mutate(
      {
        id: presetDto.id,
        data: { newSlug: slug, newDisplayName: null },
      },
      {
        onSuccess: (created) => {
          invalidate()
          setCloneOpen(false)
          showSnack(
            `Cloned '${presetDto.slug}' to '${created.slug}'.`,
            'success',
          )
          navigate(`/super-admin/runtime-presets/${created.id}`)
        },
        onError: (e) => {
          setCloneError(extractErrorMessage(e))
        },
      },
    )
  }

  // Loading state for edit mode.
  if (!isCreate && (listQuery.isLoading || !hasHydrated)) {
    return (
      <Box
        sx={{
          display: 'flex',
          alignItems: 'center',
          justifyContent: 'center',
          minHeight: 240,
        }}
      >
        {listQuery.isError ? (
          <Alert severity="error">
            {extractErrorMessage(listQuery.error)}
          </Alert>
        ) : !presetDto && !listQuery.isLoading ? (
          <Alert severity="warning">Preset not found.</Alert>
        ) : (
          <CircularProgress />
        )}
      </Box>
    )
  }

  // Not-found after load completed.
  if (!isCreate && !presetDto) {
    return (
      <Box sx={{ p: 4 }}>
        <Alert severity="warning">
          Preset not found. It may have been deleted.
        </Alert>
        <Button
          sx={{ mt: 2, textTransform: 'none' }}
          startIcon={<ArrowBackIcon />}
          onClick={() => navigate('/super-admin/runtime-presets')}
        >
          Back to list
        </Button>
      </Box>
    )
  }

  const saving = createMutation.isPending || updateMutation.isPending

  return (
    <>
      {/* Header */}
      <Box sx={{ mb: 3 }}>
        <Box sx={{ display: 'flex', alignItems: 'center', mb: 1 }}>
          <Button
            size="small"
            startIcon={<ArrowBackIcon />}
            onClick={() => navigate('/super-admin/runtime-presets')}
            sx={{ textTransform: 'none', color: 'text.secondary' }}
          >
            Back to presets
          </Button>
        </Box>
        <Box
          sx={{
            display: 'flex',
            alignItems: 'center',
            justifyContent: 'space-between',
            gap: 2,
            flexWrap: 'wrap',
          }}
        >
          <Box sx={{ display: 'flex', alignItems: 'center', gap: 1.5 }}>
            <Box
              sx={{
                width: 36,
                height: 36,
                borderRadius: 2,
                bgcolor: (theme) => alpha(theme.palette.primary.main, 0.06),
                border: '1px solid',
                borderColor: 'divider',
                display: 'flex',
                alignItems: 'center',
                justifyContent: 'center',
              }}
            >
              {readOnly ? (
                <LockIcon sx={{ fontSize: 18, color: 'text.secondary' }} />
              ) : (
                <SettingsApplicationsIcon
                  sx={{ fontSize: 18, color: 'text.secondary' }}
                />
              )}
            </Box>
            <Box>
              <Typography
                variant="h4"
                component="h1"
                sx={{ lineHeight: 1.2, display: 'flex', alignItems: 'center', gap: 1 }}
              >
                {isCreate
                  ? 'New Preset'
                  : presetDto?.displayName || presetDto?.slug}
                {readOnly && (
                  <Chip
                    size="small"
                    label="Built-in"
                    color="default"
                    variant="outlined"
                  />
                )}
              </Typography>
              <Typography variant="caption" color="text.secondary">
                {isCreate
                  ? 'Create a new service preset the runtime spec editor can pick from.'
                  : `${presetDto?.slug ?? ''} — service template metadata, command, env, and parameters.`}
              </Typography>
            </Box>
          </Box>
          <Stack direction="row" spacing={1}>
            {!isCreate && (
              <Button
                variant="outlined"
                startIcon={<ContentCopyIcon />}
                onClick={() => {
                  setCloneSlug(`${presetDto?.slug ?? 'preset'}-copy`)
                  setCloneError(null)
                  setCloneOpen(true)
                }}
                sx={{ textTransform: 'none' }}
              >
                Clone
              </Button>
            )}
            {!readOnly && (
              <Button
                variant="contained"
                startIcon={
                  saving ? (
                    <CircularProgress size={14} color="inherit" />
                  ) : (
                    <SaveIcon />
                  )
                }
                onClick={handleSave}
                disabled={saving}
                sx={{
                  textTransform: 'none',
                  boxShadow: 'none',
                  '&:hover': { boxShadow: 'none' },
                }}
              >
                {saving ? 'Saving…' : isCreate ? 'Create preset' : 'Save'}
              </Button>
            )}
          </Stack>
        </Box>
      </Box>

      {readOnly && (
        <Alert severity="info" sx={{ mb: 3 }} icon={<LockIcon />}>
          <Typography variant="body2" sx={{ fontWeight: 500 }}>
            This is a built-in preset. Clone it to make changes.
          </Typography>
          <Typography variant="caption" color="text.secondary">
            Built-ins are seeded by the runtime migration and shared by every
            workspace, so they're locked. The clone keeps the same template
            but lives as a user-owned preset you can edit freely.
          </Typography>
        </Alert>
      )}

      {validationError && (
        <Alert
          severity="error"
          sx={{ mb: 3 }}
          onClose={() => setValidationError(null)}
        >
          {validationError}
        </Alert>
      )}

      <Stack spacing={3}>
        {/* Identity */}
        <Section title="Identity" subtitle="Slug, name, and category metadata.">
          <Stack spacing={2}>
            <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2}>
              <TextField
                label="Slug"
                value={form.slug}
                onChange={(e) => setFormField('slug', e.target.value)}
                size="small"
                fullWidth
                required
                disabled={readOnly || !isCreate}
                helperText={
                  !isCreate
                    ? 'Slug is immutable after creation.'
                    : 'lowercase, digits, hyphens. e.g. node-vite'
                }
                slotProps={{
                  input: { sx: { fontFamily: 'monospace', fontSize: 13 } },
                }}
              />
              <TextField
                label="Display name"
                value={form.displayName}
                onChange={(e) => setFormField('displayName', e.target.value)}
                size="small"
                fullWidth
                required
                disabled={readOnly}
              />
            </Stack>
            <TextField
              label="Description"
              value={form.description}
              onChange={(e) => setFormField('description', e.target.value)}
              size="small"
              fullWidth
              multiline
              minRows={2}
              required
              disabled={readOnly}
            />
            <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2}>
              <FormControl size="small" fullWidth>
                <InputLabel>Category</InputLabel>
                <Select
                  label="Category"
                  value={form.category}
                  disabled={readOnly}
                  onChange={(e) =>
                    setFormField('category', e.target.value as string)
                  }
                >
                  {CATEGORIES.map((c) => (
                    <MenuItem key={c} value={c}>
                      {c}
                    </MenuItem>
                  ))}
                </Select>
              </FormControl>
              <TextField
                label="Icon name (optional)"
                value={form.iconName}
                onChange={(e) => setFormField('iconName', e.target.value)}
                size="small"
                fullWidth
                disabled={readOnly}
                placeholder="e.g. node, postgres"
                helperText="Free-form hint the picker uses for icon lookup."
              />
            </Stack>
          </Stack>
        </Section>

        {/* Command */}
        <Section
          title="Command"
          subtitle="Supervisord command + runtime user. Use {{handlebars}} for parameters."
        >
          <Stack spacing={2}>
            <TextField
              label="Command template"
              value={form.commandTemplate}
              onChange={(e) => setFormField('commandTemplate', e.target.value)}
              fullWidth
              required
              multiline
              minRows={2}
              maxRows={10}
              disabled={readOnly}
              placeholder="dotnet run --project {{project}} --urls http://0.0.0.0:{{port}}"
              slotProps={{
                input: { sx: { fontFamily: 'monospace', fontSize: 13 } },
              }}
            />
            <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2}>
              <TextField
                label="Default user"
                value={form.defaultUser}
                onChange={(e) => setFormField('defaultUser', e.target.value)}
                size="small"
                fullWidth
                disabled={readOnly}
                placeholder="root"
                slotProps={{
                  input: { sx: { fontFamily: 'monospace', fontSize: 13 } },
                }}
              />
              <FormControlLabel
                control={
                  <Switch
                    checked={form.autorestart}
                    disabled={readOnly}
                    onChange={(e) =>
                      setFormField('autorestart', e.target.checked)
                    }
                  />
                }
                label="Auto-restart on crash"
              />
            </Stack>
          </Stack>
        </Section>

        {/* Environment */}
        <Section
          title="Environment"
          subtitle="Env vars seeded into the service. Values support {{handlebars}}."
        >
          <EnvRowsEditor
            rows={form.envRows}
            onChange={(next) => setFormField('envRows', next)}
            readOnly={readOnly}
          />
        </Section>

        {/* Healthcheck */}
        <Section
          title="Healthcheck"
          subtitle="Probe the service before declaring it healthy. Optional."
        >
          <Stack spacing={2}>
            <TextField
              label="Healthcheck command"
              value={form.healthcheckCommand}
              onChange={(e) =>
                setFormField('healthcheckCommand', e.target.value)
              }
              fullWidth
              multiline
              minRows={2}
              maxRows={6}
              disabled={readOnly}
              placeholder="curl -sf http://localhost:{{port}}/health"
              slotProps={{
                input: { sx: { fontFamily: 'monospace', fontSize: 13 } },
              }}
            />
            <TextField
              label="Healthcheck interval (seconds)"
              value={form.healthcheckInterval}
              onChange={(e) =>
                setFormField('healthcheckInterval', e.target.value)
              }
              size="small"
              fullWidth
              type="number"
              disabled={readOnly}
              placeholder="5"
            />
          </Stack>
        </Section>

        {/* Lifecycle */}
        <Section
          title="Lifecycle hooks"
          subtitle="Snippets the spec expander stitches into the project's install / setup / verify scripts."
        >
          <Stack spacing={2}>
            <TextField
              label="Install contribution"
              value={form.installContribution}
              onChange={(e) =>
                setFormField('installContribution', e.target.value)
              }
              fullWidth
              multiline
              minRows={3}
              maxRows={12}
              disabled={readOnly}
              placeholder="mise install"
              slotProps={{
                input: { sx: { fontFamily: 'monospace', fontSize: 13 } },
              }}
            />
            <TextField
              label="Setup contribution"
              value={form.setupContribution}
              onChange={(e) =>
                setFormField('setupContribution', e.target.value)
              }
              fullWidth
              multiline
              minRows={3}
              maxRows={12}
              disabled={readOnly}
              placeholder="dotnet ef database update"
              slotProps={{
                input: { sx: { fontFamily: 'monospace', fontSize: 13 } },
              }}
            />
            <TextField
              label="Install verify"
              value={form.installVerify}
              onChange={(e) => setFormField('installVerify', e.target.value)}
              fullWidth
              multiline
              minRows={2}
              maxRows={8}
              disabled={readOnly}
              placeholder="dotnet --version"
              slotProps={{
                input: { sx: { fontFamily: 'monospace', fontSize: 13 } },
              }}
            />
          </Stack>
        </Section>

        {/* Parameters */}
        <Section
          title="Parameters"
          subtitle="Knobs the runtime-spec editor exposes when adding this service. Type-aware defaults + optional mise version lookup."
        >
          <ParameterEditor
            value={form.parameters}
            onChange={(next) => setFormField('parameters', next)}
            readOnly={readOnly}
          />
        </Section>

        {/* Live preview */}
        <Section
          title="Live preview"
          subtitle="Render the template with sample values against the saved preset."
        >
          <PresetPreview
            presetId={isCreate ? undefined : presetDto?.id}
            parameters={form.parameters}
            parameterValues={parameterValues}
            onParameterValuesChange={setParameterValues}
          />
        </Section>

        {/* Footer save button mirrors header for long pages. */}
        {!readOnly && (
          <Box
            sx={{
              display: 'flex',
              justifyContent: 'flex-end',
              gap: 1,
              pt: 1,
            }}
          >
            <Button
              variant="text"
              onClick={() => navigate('/super-admin/runtime-presets')}
              disabled={saving}
              sx={{ textTransform: 'none' }}
            >
              Cancel
            </Button>
            <Button
              variant="contained"
              startIcon={
                saving ? (
                  <CircularProgress size={14} color="inherit" />
                ) : (
                  <SaveIcon />
                )
              }
              onClick={handleSave}
              disabled={saving}
              sx={{
                textTransform: 'none',
                boxShadow: 'none',
                '&:hover': { boxShadow: 'none' },
              }}
            >
              {saving ? 'Saving…' : isCreate ? 'Create preset' : 'Save'}
            </Button>
          </Box>
        )}
      </Stack>

      {/* Clone dialog (only used in edit mode for built-ins or quick fork) */}
      <Snackbar
        open={snack.open}
        autoHideDuration={4000}
        onClose={() => setSnack((prev) => ({ ...prev, open: false }))}
        anchorOrigin={{ vertical: 'bottom', horizontal: 'center' }}
      >
        <Alert
          onClose={() => setSnack((prev) => ({ ...prev, open: false }))}
          severity={snack.severity}
          variant="filled"
          sx={{ borderRadius: 2, fontWeight: 500 }}
        >
          {snack.message}
        </Alert>
      </Snackbar>

      {/* Clone modal -- using a controlled state pattern rather than a separate Dialog */}
      {cloneOpen && (
        <CloneDialog
          slug={cloneSlug}
          onSlugChange={setCloneSlug}
          error={cloneError}
          submitting={cloneMutation.isPending}
          onCancel={() => setCloneOpen(false)}
          onSubmit={handleCloneSubmit}
        />
      )}
    </>
  )
}

function Section({
  title,
  subtitle,
  children,
}: {
  title: string
  subtitle?: string
  children: React.ReactNode
}) {
  return (
    <Paper
      variant="outlined"
      sx={{
        p: { xs: 2.5, sm: 3 },
        borderRadius: 3,
      }}
    >
      <Box sx={{ mb: 2 }}>
        <Typography
          variant="caption"
          sx={{
            textTransform: 'uppercase',
            letterSpacing: '0.08em',
            fontWeight: 600,
            color: 'text.secondary',
            display: 'block',
          }}
        >
          {title}
        </Typography>
        {subtitle && (
          <Typography
            variant="body2"
            color="text.secondary"
            sx={{ mt: 0.25, fontSize: 12.5 }}
          >
            {subtitle}
          </Typography>
        )}
      </Box>
      {children}
    </Paper>
  )
}

function EnvRowsEditor({
  rows,
  onChange,
  readOnly,
}: {
  rows: EnvRow[]
  onChange: (next: EnvRow[]) => void
  readOnly: boolean
}) {
  const updateAt = (index: number, mutate: (r: EnvRow) => EnvRow) => {
    const next = rows.slice()
    next[index] = mutate(next[index])
    onChange(next)
  }
  const removeAt = (index: number) => {
    const next = rows.slice()
    next.splice(index, 1)
    onChange(next)
  }
  const add = () => {
    onChange([...rows, { key: '', value: '' }])
  }

  return (
    <Stack spacing={1.5}>
      {rows.length === 0 && (
        <Typography variant="body2" color="text.secondary">
          No env vars yet. Add one to seed the service environment.
        </Typography>
      )}
      {rows.map((row, index) => (
        <Stack
          key={index}
          direction={{ xs: 'column', sm: 'row' }}
          spacing={1.5}
          alignItems={{ xs: 'stretch', sm: 'center' }}
        >
          <TextField
            label="Key"
            value={row.key}
            onChange={(e) =>
              updateAt(index, (r) => ({ ...r, key: e.target.value }))
            }
            size="small"
            sx={{ flex: 1 }}
            disabled={readOnly}
            slotProps={{
              input: { sx: { fontFamily: 'monospace', fontSize: 13 } },
            }}
          />
          <TextField
            label="Value"
            value={row.value}
            onChange={(e) =>
              updateAt(index, (r) => ({ ...r, value: e.target.value }))
            }
            size="small"
            sx={{ flex: 2 }}
            disabled={readOnly}
            slotProps={{
              input: { sx: { fontFamily: 'monospace', fontSize: 13 } },
            }}
          />
          {!readOnly && (
            <Tooltip title="Remove">
              <IconButton
                size="small"
                color="error"
                onClick={() => removeAt(index)}
              >
                <DeleteOutlineIcon fontSize="small" />
              </IconButton>
            </Tooltip>
          )}
        </Stack>
      ))}
      {!readOnly && (
        <Box>
          <Button
            variant="outlined"
            size="small"
            startIcon={<AddIcon />}
            onClick={add}
            sx={{ textTransform: 'none' }}
          >
            Add env var
          </Button>
        </Box>
      )}
    </Stack>
  )
}

function CloneDialog({
  slug,
  onSlugChange,
  error,
  submitting,
  onCancel,
  onSubmit,
}: {
  slug: string
  onSlugChange: (next: string) => void
  error: string | null
  submitting: boolean
  onCancel: () => void
  onSubmit: () => void
}) {
  return (
    <Box
      sx={{
        position: 'fixed',
        inset: 0,
        bgcolor: 'rgba(0,0,0,0.4)',
        display: 'flex',
        alignItems: 'center',
        justifyContent: 'center',
        zIndex: (theme) => theme.zIndex.modal,
        p: 2,
      }}
      onClick={() => {
        if (!submitting) onCancel()
      }}
    >
      <Paper
        sx={{ p: 3, maxWidth: 420, width: '100%', borderRadius: 3 }}
        onClick={(e) => e.stopPropagation()}
      >
        <Typography variant="h6" sx={{ mb: 1 }}>
          Clone preset
        </Typography>
        <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
          Creates a writable copy you can edit. Pick a new slug — lowercase
          letters, digits, and hyphens, starting with a letter.
        </Typography>
        <Stack spacing={2}>
          <TextField
            label="New slug"
            value={slug}
            onChange={(e) => onSlugChange(e.target.value)}
            size="small"
            fullWidth
            autoFocus
            slotProps={{
              input: { sx: { fontFamily: 'monospace', fontSize: 13 } },
            }}
          />
          {error && <Alert severity="error">{error}</Alert>}
        </Stack>
        <Stack
          direction="row"
          spacing={1}
          justifyContent="flex-end"
          sx={{ mt: 2 }}
        >
          <Button
            onClick={onCancel}
            disabled={submitting}
            sx={{ textTransform: 'none' }}
          >
            Cancel
          </Button>
          <Button
            variant="contained"
            onClick={onSubmit}
            disabled={submitting}
            sx={{
              textTransform: 'none',
              boxShadow: 'none',
              '&:hover': { boxShadow: 'none' },
            }}
          >
            {submitting ? 'Cloning…' : 'Clone'}
          </Button>
        </Stack>
      </Paper>
    </Box>
  )
}
