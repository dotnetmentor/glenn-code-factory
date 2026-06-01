import {
  Box,
  Button,
  FormControl,
  IconButton,
  InputLabel,
  MenuItem,
  Paper,
  Select,
  type SelectChangeEvent,
  Stack,
  Switch,
  TextField,
  Tooltip,
  Typography,
} from '@mui/material'
import AddIcon from '@mui/icons-material/Add'
import DeleteOutlineIcon from '@mui/icons-material/DeleteOutline'
import { useEffect, useMemo, useState } from 'react'
import { formatDistanceToNow, parseISO } from 'date-fns'
import type {
  SystemSettingDefinitionDto,
  SystemSettingDto,
} from '../../../../../api/queries-commands'

export interface AgentPermissionFieldProps {
  /** Catalog metadata for this setting. */
  definition: SystemSettingDefinitionDto
  /** Current state from the list query. */
  state: SystemSettingDto | undefined
  /** Whether a save is in flight for this key. */
  isSaving: boolean
  /** Persist a new value. */
  onSave: (key: string, value: string) => void
}

/**
 * Allowed permission modes — `auto` is intentionally omitted per spec.
 * The labels mirror the SDK option names so the UI maps 1:1 to the spec.
 */
const PERMISSION_MODE_OPTIONS: ReadonlyArray<{
  value: string
  label: string
  hint: string
}> = [
  {
    value: 'default',
    label: 'default',
    hint: 'Prompts before file edits, shell commands, and other risky actions.',
  },
  {
    value: 'acceptEdits',
    label: 'acceptEdits',
    hint: 'Auto-approves file edits; prompts for other risky actions.',
  },
  {
    value: 'bypassPermissions',
    label: 'bypassPermissions',
    hint:
      'No prompts. Agent acts freely (only DisallowedTools are blocked).',
  },
  { value: 'plan', label: 'plan', hint: 'Agent plans without taking actions.' },
  {
    value: 'dontAsk',
    label: 'dontAsk',
    hint:
      'Like default but suppresses prompts the SDK would otherwise raise.',
  },
]

const PERMISSION_MODE_KEY = 'AgentPermissions:PermissionMode'
const ALLOW_DANGEROUS_KEY = 'AgentPermissions:AllowDangerouslySkipPermissions'

const STRING_LIST_KEYS = new Set([
  'AgentPermissions:AllowedTools',
  'AgentPermissions:DisallowedTools',
  'AgentPermissions:AdditionalDirectories',
])

const STRING_LIST_PLACEHOLDERS: Record<string, string> = {
  'AgentPermissions:AllowedTools': 'e.g. Read or Bash(npm test)',
  'AgentPermissions:DisallowedTools': 'e.g. Bash(rm -rf /*)',
  'AgentPermissions:AdditionalDirectories': 'e.g. /workspace/shared',
}

function formatRelative(iso: string | null | undefined): string {
  if (!iso) return ''
  try {
    return formatDistanceToNow(parseISO(iso), { addSuffix: true })
  } catch {
    return ''
  }
}

/**
 * Parse a JSON-encoded string array. Tolerates empty/null by returning [].
 * On parse failure returns null so callers can detect a corrupt value and
 * surface it to the user instead of silently truncating.
 */
function parseStringList(raw: string | null | undefined): string[] | null {
  if (!raw || raw.trim() === '') return []
  try {
    const parsed = JSON.parse(raw)
    if (!Array.isArray(parsed)) return null
    if (!parsed.every((x) => typeof x === 'string')) return null
    return parsed as string[]
  } catch {
    return null
  }
}

/**
 * Determines whether two string arrays differ. Order matters — the user can
 * reorder entries (they can't yet, but anyway) and we treat that as dirty.
 */
function listsEqual(a: string[], b: string[]): boolean {
  if (a.length !== b.length) return false
  for (let i = 0; i < a.length; i++) {
    if (a[i] !== b[i]) return false
  }
  return true
}

export function isAgentPermissionField(key: string): boolean {
  return (
    key === PERMISSION_MODE_KEY ||
    key === ALLOW_DANGEROUS_KEY ||
    STRING_LIST_KEYS.has(key)
  )
}

export function AgentPermissionField({
  definition,
  state,
  isSaving,
  onSave,
}: AgentPermissionFieldProps) {
  const { key, displayName, description } = definition
  const currentValue = state?.value ?? ''
  const labelId = `setting-${key.replace(/[^a-zA-Z0-9]/g, '-')}`

  // ---- Select (PermissionMode) -------------------------------------------
  if (key === PERMISSION_MODE_KEY) {
    return (
      <SelectField
        definition={definition}
        state={state}
        isSaving={isSaving}
        onSave={onSave}
        options={PERMISSION_MODE_OPTIONS}
      />
    )
  }

  // ---- Switch (AllowDangerouslySkipPermissions) --------------------------
  if (key === ALLOW_DANGEROUS_KEY) {
    return (
      <BoolField
        definition={definition}
        state={state}
        isSaving={isSaving}
        onSave={onSave}
      />
    )
  }

  // ---- String list (Allowed/Disallowed/AdditionalDirectories) ------------
  if (STRING_LIST_KEYS.has(key)) {
    return (
      <StringListField
        definition={definition}
        state={state}
        isSaving={isSaving}
        onSave={onSave}
        placeholder={STRING_LIST_PLACEHOLDERS[key]}
      />
    )
  }

  // Fallback — should never reach here when guarded by isAgentPermissionField.
  return (
    <Paper variant="outlined" sx={{ p: 3 }}>
      <Stack spacing={1}>
        <Typography variant="subtitle1" sx={{ fontWeight: 600 }} id={labelId}>
          {displayName}
        </Typography>
        <Typography variant="caption" color="text.secondary">
          {description}
        </Typography>
        <Typography variant="body2">
          {currentValue || '(no value)'}
        </Typography>
      </Stack>
    </Paper>
  )
}

// ---- Select ----------------------------------------------------------------

interface SelectFieldProps {
  definition: SystemSettingDefinitionDto
  state: SystemSettingDto | undefined
  isSaving: boolean
  onSave: (key: string, value: string) => void
  options: ReadonlyArray<{ value: string; label: string; hint: string }>
}

function SelectField({
  definition,
  state,
  isSaving,
  onSave,
  options,
}: SelectFieldProps) {
  const { key, displayName, description } = definition
  const currentValue = state?.value ?? ''
  const [localValue, setLocalValue] = useState<string>(currentValue)
  useEffect(() => {
    setLocalValue(currentValue)
  }, [currentValue])

  const dirty = localValue !== currentValue
  const labelId = `setting-${key.replace(/[^a-zA-Z0-9]/g, '-')}`

  const handleChange = (e: SelectChangeEvent<string>) => {
    setLocalValue(e.target.value)
  }

  const activeHint = options.find((o) => o.value === localValue)?.hint

  return (
    <Paper variant="outlined" sx={{ p: 3 }}>
      <Stack spacing={1.5}>
        <Box>
          <Typography variant="subtitle1" sx={{ fontWeight: 600 }} id={labelId}>
            {displayName}
          </Typography>
          <Typography variant="caption" color="text.secondary" component="div">
            {description}
          </Typography>
        </Box>

        <FormControl size="small" fullWidth>
          <InputLabel id={`${labelId}-label`}>{displayName}</InputLabel>
          <Select
            labelId={`${labelId}-label`}
            label={displayName}
            value={localValue}
            onChange={handleChange}
            inputProps={{ 'aria-labelledby': labelId }}
          >
            {options.map((opt) => (
              <MenuItem key={opt.value} value={opt.value}>
                {opt.label}
              </MenuItem>
            ))}
          </Select>
        </FormControl>

        {activeHint && (
          <Typography variant="caption" color="text.secondary">
            {activeHint}
          </Typography>
        )}

        <Box
          sx={{
            display: 'flex',
            alignItems: 'center',
            justifyContent: 'space-between',
            gap: 2,
          }}
        >
          <Typography variant="caption" color="text.secondary">
            {state?.updatedAt
              ? `Last updated ${formatRelative(state.updatedAt)}`
              : 'Never updated'}
          </Typography>
          <Button
            variant="contained"
            size="small"
            disabled={!dirty || isSaving}
            onClick={() => onSave(key, localValue)}
          >
            {isSaving ? 'Saving…' : 'Save'}
          </Button>
        </Box>
      </Stack>
    </Paper>
  )
}

// ---- Boolean (Switch) ------------------------------------------------------

interface BoolFieldProps {
  definition: SystemSettingDefinitionDto
  state: SystemSettingDto | undefined
  isSaving: boolean
  onSave: (key: string, value: string) => void
}

function BoolField({ definition, state, isSaving, onSave }: BoolFieldProps) {
  const { key, displayName, description } = definition
  const currentValue = state?.value ?? 'false'
  const currentBool = currentValue.toLowerCase() === 'true'
  const [localBool, setLocalBool] = useState<boolean>(currentBool)
  useEffect(() => {
    setLocalBool(currentBool)
  }, [currentBool])

  const dirty = localBool !== currentBool
  const labelId = `setting-${key.replace(/[^a-zA-Z0-9]/g, '-')}`

  return (
    <Paper variant="outlined" sx={{ p: 3 }}>
      <Stack spacing={1.5}>
        <Box
          sx={{
            display: 'flex',
            alignItems: 'flex-start',
            justifyContent: 'space-between',
            gap: 2,
          }}
        >
          <Box sx={{ flexGrow: 1 }}>
            <Typography
              variant="subtitle1"
              sx={{ fontWeight: 600 }}
              id={labelId}
            >
              {displayName}
            </Typography>
            <Typography
              variant="caption"
              color="text.secondary"
              component="div"
            >
              {description}
            </Typography>
          </Box>
          <Switch
            checked={localBool}
            onChange={(_, checked) => setLocalBool(checked)}
            inputProps={{ 'aria-labelledby': labelId }}
          />
        </Box>

        <Box
          sx={{
            display: 'flex',
            alignItems: 'center',
            justifyContent: 'space-between',
            gap: 2,
          }}
        >
          <Typography variant="caption" color="text.secondary">
            {state?.updatedAt
              ? `Last updated ${formatRelative(state.updatedAt)}`
              : 'Never updated'}
          </Typography>
          <Button
            variant="contained"
            size="small"
            disabled={!dirty || isSaving}
            onClick={() => onSave(key, localBool ? 'true' : 'false')}
          >
            {isSaving ? 'Saving…' : 'Save'}
          </Button>
        </Box>
      </Stack>
    </Paper>
  )
}

// ---- String list -----------------------------------------------------------

interface StringListFieldProps {
  definition: SystemSettingDefinitionDto
  state: SystemSettingDto | undefined
  isSaving: boolean
  onSave: (key: string, value: string) => void
  placeholder?: string
}

function StringListField({
  definition,
  state,
  isSaving,
  onSave,
  placeholder,
}: StringListFieldProps) {
  const { key, displayName, description } = definition
  const rawValue = state?.value ?? '[]'
  const parsedFromServer = useMemo(
    () => parseStringList(rawValue),
    [rawValue],
  )
  const corrupt = parsedFromServer === null
  const serverList = useMemo(
    () => parsedFromServer ?? [],
    [parsedFromServer],
  )

  // Local working copy — rows the user can edit/add/remove.
  const [items, setItems] = useState<string[]>(serverList)
  useEffect(() => {
    setItems(serverList)
  }, [serverList])

  const dirty = !listsEqual(items, serverList)
  const labelId = `setting-${key.replace(/[^a-zA-Z0-9]/g, '-')}`

  const updateAt = (index: number, value: string) => {
    setItems((prev) => {
      const next = prev.slice()
      next[index] = value
      return next
    })
  }

  const removeAt = (index: number) => {
    setItems((prev) => prev.filter((_, i) => i !== index))
  }

  const addRow = () => {
    setItems((prev) => [...prev, ''])
  }

  const handleSave = () => {
    // Trim each item and drop fully-empty rows so we don't persist whitespace.
    const cleaned = items.map((s) => s.trim()).filter((s) => s.length > 0)
    onSave(key, JSON.stringify(cleaned))
  }

  return (
    <Paper variant="outlined" sx={{ p: 3 }}>
      <Stack spacing={1.5}>
        <Box>
          <Typography
            variant="subtitle1"
            sx={{ fontWeight: 600 }}
            id={labelId}
          >
            {displayName}
          </Typography>
          <Typography variant="caption" color="text.secondary" component="div">
            {description}
          </Typography>
        </Box>

        {corrupt && (
          <Typography variant="caption" color="error">
            Stored value is not a valid JSON array. Editing will replace it.
          </Typography>
        )}

        <Stack spacing={1}>
          {items.length === 0 && (
            <Typography variant="body2" color="text.secondary">
              (empty)
            </Typography>
          )}
          {items.map((item, index) => (
            <Box
              key={index}
              sx={{ display: 'flex', alignItems: 'center', gap: 1 }}
            >
              <TextField
                value={item}
                onChange={(e) => updateAt(index, e.target.value)}
                placeholder={placeholder}
                size="small"
                fullWidth
                inputProps={{
                  'aria-label': `${displayName} entry ${index + 1}`,
                }}
              />
              <Tooltip title="Remove">
                <IconButton
                  size="small"
                  onClick={() => removeAt(index)}
                  aria-label={`Remove ${displayName} entry ${index + 1}`}
                >
                  <DeleteOutlineIcon fontSize="small" />
                </IconButton>
              </Tooltip>
            </Box>
          ))}
        </Stack>

        <Box>
          <Button
            size="small"
            variant="outlined"
            startIcon={<AddIcon />}
            onClick={addRow}
          >
            Add
          </Button>
        </Box>

        <Box
          sx={{
            display: 'flex',
            alignItems: 'center',
            justifyContent: 'space-between',
            gap: 2,
          }}
        >
          <Typography variant="caption" color="text.secondary">
            {state?.updatedAt
              ? `Last updated ${formatRelative(state.updatedAt)}`
              : 'Never updated'}
          </Typography>
          <Button
            variant="contained"
            size="small"
            disabled={!dirty || isSaving}
            onClick={handleSave}
          >
            {isSaving ? 'Saving…' : 'Save'}
          </Button>
        </Box>
      </Stack>
    </Paper>
  )
}
