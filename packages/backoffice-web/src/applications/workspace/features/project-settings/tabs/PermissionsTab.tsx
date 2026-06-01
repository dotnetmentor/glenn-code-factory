import { useEffect, useMemo, useState } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import {
  Alert,
  Box,
  Button,
  CircularProgress,
  FormControl,
  FormControlLabel,
  InputLabel,
  MenuItem,
  Paper,
  Select,
  Stack,
  Switch,
  Tooltip,
  Typography,
} from '@mui/material'
import {
  getGetApiProjectsProjectIdAgentPermissionsQueryKey,
  useDeleteApiProjectsProjectIdAgentPermissions,
  useGetApiProjectsProjectIdAgentPermissions,
  useGetApiSystemSettings,
  usePutApiProjectsProjectIdAgentPermissions,
  type ProjectAgentPermissionsDto,
  type SystemSettingDto,
} from '../../../../../api/queries-commands'
import { useNotification } from '../../../../shared/contexts/NotificationContext'
import { StringListEditor } from '../components/StringListEditor'
import {
  bodySx,
  captionSx,
  sectionTitleSx,
  workspaceColors,
  workspaceText,
} from '../../../shared'

/**
 * Available agent permission modes. We intentionally omit
 * {@code auto} (the model-classifier mode). The order roughly tracks
 * "strictest → loosest" so a cautious team scans top-to-bottom.
 */
const PERMISSION_MODES = [
  {
    value: 'default',
    label: 'default — prompts before file edits, shell commands, and other risky actions',
  },
  {
    value: 'acceptEdits',
    label: 'acceptEdits — auto-approves file edits; prompts for other risky actions',
  },
  { value: 'plan', label: 'plan — agent plans without taking actions' },
  {
    value: 'dontAsk',
    label: 'dontAsk — like default but suppresses prompts the SDK would otherwise raise',
  },
  {
    value: 'bypassPermissions',
    label:
      'bypassPermissions — no prompts; agent acts freely (only DisallowedTools are blocked)',
  },
] as const

const SS_KEYS = {
  PermissionMode: 'AgentPermissions:PermissionMode',
  AllowDangerouslySkipPermissions: 'AgentPermissions:AllowDangerouslySkipPermissions',
  AllowedTools: 'AgentPermissions:AllowedTools',
  DisallowedTools: 'AgentPermissions:DisallowedTools',
  AdditionalDirectories: 'AgentPermissions:AdditionalDirectories',
} as const

interface FormState {
  permissionMode: string
  allowDangerouslySkipPermissions: boolean
  allowedTools: string[]
  disallowedTools: string[]
  additionalDirectories: string[]
}

function findSystemSetting(
  settings: SystemSettingDto[] | undefined,
  key: string,
): { value: string; hasValue: boolean } {
  if (!settings) return { value: '', hasValue: false }
  const match = settings.find((s) => s.key === key)
  if (!match) return { value: '', hasValue: false }
  return { value: match.value ?? '', hasValue: match.hasValue }
}

function parseStringArray(raw: string): string[] {
  if (!raw) return []
  try {
    const parsed = JSON.parse(raw)
    if (Array.isArray(parsed)) {
      return parsed.filter((x): x is string => typeof x === 'string')
    }
    return []
  } catch {
    return []
  }
}

function parseBool(raw: string): boolean {
  return raw === 'true'
}

function buildDefaultFromSystem(settings: SystemSettingDto[] | undefined): FormState {
  return {
    permissionMode: findSystemSetting(settings, SS_KEYS.PermissionMode).value || 'default',
    allowDangerouslySkipPermissions: parseBool(
      findSystemSetting(settings, SS_KEYS.AllowDangerouslySkipPermissions).value,
    ),
    allowedTools: parseStringArray(findSystemSetting(settings, SS_KEYS.AllowedTools).value),
    disallowedTools: parseStringArray(findSystemSetting(settings, SS_KEYS.DisallowedTools).value),
    additionalDirectories: parseStringArray(
      findSystemSetting(settings, SS_KEYS.AdditionalDirectories).value,
    ),
  }
}

function dtoToForm(dto: ProjectAgentPermissionsDto): FormState {
  return {
    permissionMode: dto.permissionMode,
    allowDangerouslySkipPermissions: dto.allowDangerouslySkipPermissions,
    allowedTools: dto.allowedTools ?? [],
    disallowedTools: dto.disallowedTools ?? [],
    additionalDirectories: dto.additionalDirectories ?? [],
  }
}

interface PermissionsTabProps {
  projectId: string
}

/**
 * Agent permissions override settings for a project, rendered as a drawer tab.
 *
 * <p>Logic ported from {@code ProjectAgentPermissionsPage} (the standalone
 * routed page) — the breadcrumb / page-shell chrome is dropped because the
 * drawer header already provides the framing. The form fields and the
 * "override on / system defaults read-only" switching behavior are preserved
 * 1:1 so the contract with the backend doesn't drift.</p>
 */
export function PermissionsTab({ projectId }: PermissionsTabProps) {
  const queryClient = useQueryClient()
  const { showSuccess, showError } = useNotification()

  const overrideQuery = useGetApiProjectsProjectIdAgentPermissions(projectId, {
    query: { enabled: !!projectId },
  })
  const systemSettingsQuery = useGetApiSystemSettings()

  const overrideRow: ProjectAgentPermissionsDto | null | undefined = overrideQuery.data ?? null
  const overrideExists = !!overrideRow

  const systemDefaultSettings = useMemo(
    () =>
      (systemSettingsQuery.data ?? []).filter((s) => s.category === 'AgentPermissions'),
    [systemSettingsQuery.data],
  )
  const systemDefaultForm = useMemo(
    () => buildDefaultFromSystem(systemDefaultSettings),
    [systemDefaultSettings],
  )

  const [overrideEnabled, setOverrideEnabled] = useState<boolean>(false)
  const [form, setForm] = useState<FormState | null>(null)
  const [hydrated, setHydrated] = useState(false)

  useEffect(() => {
    if (hydrated) return
    if (overrideQuery.isLoading || systemSettingsQuery.isLoading) return
    if (overrideRow) {
      setOverrideEnabled(true)
      setForm(dtoToForm(overrideRow))
    } else {
      setOverrideEnabled(false)
      setForm(null)
    }
    setHydrated(true)
  }, [hydrated, overrideQuery.isLoading, systemSettingsQuery.isLoading, overrideRow])

  useEffect(() => {
    if (!overrideEnabled) return
    if (form) return
    if (overrideRow) {
      setForm(dtoToForm(overrideRow))
    } else {
      setForm(systemDefaultForm)
    }
  }, [overrideEnabled, form, overrideRow, systemDefaultForm])

  useEffect(() => {
    if (!overrideEnabled && form !== null) {
      setForm(null)
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [overrideEnabled])

  const isBypassMode = form?.permissionMode === 'bypassPermissions'
  useEffect(() => {
    if (!form) return
    if (isBypassMode && !form.allowDangerouslySkipPermissions) {
      setForm({ ...form, allowDangerouslySkipPermissions: true })
    }
  }, [isBypassMode, form])

  const putMutation = usePutApiProjectsProjectIdAgentPermissions()
  const deleteMutation = useDeleteApiProjectsProjectIdAgentPermissions()
  const isSaving = putMutation.isPending || deleteMutation.isPending

  const handleSave = async () => {
    if (!projectId) return
    const queryKey = getGetApiProjectsProjectIdAgentPermissionsQueryKey(projectId)
    try {
      if (overrideEnabled) {
        if (!form) return
        await putMutation.mutateAsync({
          projectId,
          data: {
            permissionMode: form.permissionMode,
            allowDangerouslySkipPermissions: form.allowDangerouslySkipPermissions,
            allowedTools: form.allowedTools,
            disallowedTools: form.disallowedTools,
            additionalDirectories: form.additionalDirectories,
          },
        })
        await queryClient.invalidateQueries({ queryKey })
        showSuccess('Project override saved.')
      } else {
        if (overrideExists) {
          await deleteMutation.mutateAsync({ projectId })
          await queryClient.invalidateQueries({ queryKey })
          showSuccess('Project override removed — system defaults apply.')
        } else {
          showSuccess('No override to remove — system defaults already apply.')
        }
      }
    } catch (err) {
      const message = err instanceof Error ? err.message : 'Unknown error'
      showError(`Save failed: ${message}`)
    }
  }

  if (!projectId) {
    return <Alert severity="error">Missing project id.</Alert>
  }

  const isLoading = !hydrated || overrideQuery.isLoading || systemSettingsQuery.isLoading
  if (isLoading) {
    return (
      <Box sx={{ display: 'flex', justifyContent: 'center', py: 6 }}>
        <Stack spacing={1.5} alignItems="center">
          <CircularProgress size={24} />
          <Typography sx={captionSx}>Loading agent permissions…</Typography>
        </Stack>
      </Box>
    )
  }

  if (overrideQuery.isError) {
    return <Alert severity="error">Could not load agent permissions.</Alert>
  }

  const saveDisabled = isSaving || (!overrideEnabled && !overrideExists)

  return (
    <Stack spacing={3}>
      {/* Header */}
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
          Agent permissions
        </Typography>
        <Typography sx={bodySx}>
          Override is complete. When enabled, this project ignores all
          system-level changes.
        </Typography>
      </Box>

      {/* Toggle */}
      <Paper
        variant="outlined"
        sx={{
          p: 2.5,
          backgroundColor: 'transparent',
          borderColor: workspaceColors.hairline,
          boxShadow: 'none',
        }}
      >
        <Stack
          direction={{ xs: 'column', sm: 'row' }}
          spacing={2}
          alignItems={{ xs: 'flex-start', sm: 'center' }}
          justifyContent="space-between"
        >
          <Box>
            <Typography sx={sectionTitleSx}>Override system defaults</Typography>
            <Typography sx={{ ...captionSx, mt: 0.5 }}>
              {overrideEnabled
                ? 'Active — the fields below are the only rules in effect for this project.'
                : overrideExists
                  ? 'Currently active. Turn off and Save to drop the override and re-inherit the system defaults.'
                  : 'Inactive — this project inherits the system defaults shown below.'}
            </Typography>
          </Box>
          <FormControlLabel
            control={
              <Switch
                checked={overrideEnabled}
                onChange={(e) => setOverrideEnabled(e.target.checked)}
                inputProps={{ 'aria-label': 'Override system defaults' }}
              />
            }
            label={overrideEnabled ? 'On' : 'Off'}
            labelPlacement="start"
          />
        </Stack>
      </Paper>

      {/* Body */}
      {overrideEnabled && form ? (
        <EditableForm form={form} setForm={setForm} isBypassMode={isBypassMode} />
      ) : (
        <ReadOnlyDefaultsPreview form={systemDefaultForm} />
      )}

      {/* Save */}
      <Box sx={{ display: 'flex', justifyContent: 'flex-end' }}>
        <Button
          variant="pill" color="primary"
          onClick={handleSave}
          disabled={saveDisabled}
        >
          {isSaving ? 'Saving…' : 'Save'}
        </Button>
      </Box>
    </Stack>
  )
}

interface EditableFormProps {
  form: FormState
  setForm: (next: FormState) => void
  isBypassMode: boolean
}

function EditableForm({ form, setForm, isBypassMode }: EditableFormProps) {
  const paperSx = {
    p: 2.5,
    backgroundColor: 'transparent',
    borderColor: workspaceColors.hairline,
    boxShadow: 'none',
  } as const

  return (
    <Stack spacing={3}>
      <Alert
        severity="warning"
        variant="quiet"
      >
        These rules fully replace the system defaults.{' '}
        <strong>Disallowed Tools</strong> hides tools entirely (no approval
        prompt is ever raised). The approval prompt under{' '}
        <strong>default</strong> / <strong>acceptEdits</strong> only fires for
        actions the SDK classifies as risky.
      </Alert>

      <Paper variant="outlined" sx={paperSx}>
        <Stack spacing={2.5}>
          <FormControl fullWidth size="small">
            <InputLabel id="permission-mode-label">Permission Mode</InputLabel>
            <Select
              labelId="permission-mode-label"
              label="Permission Mode"
              value={form.permissionMode}
              onChange={(e) => setForm({ ...form, permissionMode: e.target.value })}
            >
              {PERMISSION_MODES.map((mode) => (
                <MenuItem key={mode.value} value={mode.value}>
                  {mode.label}
                </MenuItem>
              ))}
            </Select>
          </FormControl>

          <Tooltip
            title={isBypassMode ? 'Required true when Permission Mode is bypassPermissions.' : ''}
            placement="right"
            arrow
          >
            <FormControlLabel
              control={
                <Switch
                  checked={form.allowDangerouslySkipPermissions}
                  disabled={isBypassMode}
                  onChange={(e) =>
                    setForm({
                      ...form,
                      allowDangerouslySkipPermissions: e.target.checked,
                    })
                  }
                />
              }
              label="Allow Dangerously Skip Permissions"
            />
          </Tooltip>
        </Stack>
      </Paper>

      <Paper variant="outlined" sx={paperSx}>
        <StringListEditor
          label="Allowed Tools"
          helperText="Tools the agent can always use without prompting (e.g. Read, Bash(npm test))."
          value={form.allowedTools}
          onChange={(next) => setForm({ ...form, allowedTools: next })}
          placeholder="Read or Bash(npm test)"
          addLabel="Add allowed tool"
        />
      </Paper>

      <Paper variant="outlined" sx={paperSx}>
        <StringListEditor
          label="Disallowed Tools"
          helperText="Tools the agent cannot use — hidden entirely (no approval prompt). Wins over bypassPermissions."
          value={form.disallowedTools}
          onChange={(next) => setForm({ ...form, disallowedTools: next })}
          placeholder="Bash(rm -rf *) or Bash(sudo *)"
          addLabel="Add disallowed tool"
        />
      </Paper>

      <Paper variant="outlined" sx={paperSx}>
        <StringListEditor
          label="Additional Directories"
          helperText="Absolute paths the agent may read / write beyond its cwd."
          value={form.additionalDirectories}
          onChange={(next) => setForm({ ...form, additionalDirectories: next })}
          placeholder="/absolute/path"
          addLabel="Add directory"
        />
      </Paper>
    </Stack>
  )
}

interface ReadOnlyDefaultsPreviewProps {
  form: FormState
}

function ReadOnlyDefaultsPreview({ form }: ReadOnlyDefaultsPreviewProps) {
  const modeLabel =
    PERMISSION_MODES.find((m) => m.value === form.permissionMode)?.label ??
    (form.permissionMode || '(not set)')
  const paperSx = {
    p: 2.5,
    backgroundColor: 'transparent',
    borderColor: workspaceColors.hairline,
    boxShadow: 'none',
  } as const
  return (
    <Stack spacing={3}>
      <Alert
        severity="info"
        variant="quiet"
      >
        This project currently inherits the system defaults shown below. Turn
        on <strong>Override system defaults</strong> above to set
        project-specific rules.
      </Alert>

      <Paper variant="outlined" sx={paperSx}>
        <Stack spacing={2}>
          <Box>
            <Typography sx={captionSx}>Permission Mode</Typography>
            <Typography sx={bodySx}>{modeLabel}</Typography>
          </Box>
          <Box>
            <Typography sx={captionSx}>Allow Dangerously Skip Permissions</Typography>
            <Typography sx={bodySx}>
              {form.allowDangerouslySkipPermissions ? 'On' : 'Off'}
            </Typography>
          </Box>
        </Stack>
      </Paper>

      <Paper variant="outlined" sx={paperSx}>
        <StringListEditor
          label="Allowed Tools"
          helperText="Inherited from system defaults."
          value={form.allowedTools}
          onChange={() => {}}
          disabled
        />
      </Paper>

      <Paper variant="outlined" sx={paperSx}>
        <StringListEditor
          label="Disallowed Tools"
          helperText="Inherited from system defaults."
          value={form.disallowedTools}
          onChange={() => {}}
          disabled
        />
      </Paper>

      <Paper variant="outlined" sx={paperSx}>
        <StringListEditor
          label="Additional Directories"
          helperText="Inherited from system defaults."
          value={form.additionalDirectories}
          onChange={() => {}}
          disabled
        />
      </Paper>
    </Stack>
  )
}
