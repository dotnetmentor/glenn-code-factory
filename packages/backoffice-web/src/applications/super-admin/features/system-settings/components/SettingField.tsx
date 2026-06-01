import {
  Box,
  Button,
  Chip,
  Link,
  Paper,
  Stack,
  TextField,
  Typography,
} from '@mui/material'
import OpenInNewIcon from '@mui/icons-material/OpenInNew'
import { useEffect, useState } from 'react'
import { formatDistanceToNow, parseISO } from 'date-fns'
import type {
  SystemSettingDefinitionDto,
  SystemSettingDto,
} from '../../../../../api/queries-commands'
import {
  AgentPermissionField,
  isAgentPermissionField,
} from './AgentPermissionField'
import { GITHUB_FIELD_HELP } from './githubFieldHelp'

export interface SettingFieldProps {
  /** Catalog metadata for this setting (label, description, isSecret). */
  definition: SystemSettingDefinitionDto
  /** Current state from the list query (hasValue, value for non-secrets, updatedAt). */
  state: SystemSettingDto | undefined
  /** Whether a save is in flight for this key. */
  isSaving: boolean
  /** Persist a new value. `null` would clear; we never send null from this UI. */
  onSave: (key: string, value: string) => void
}

const PEM_KEY = 'GitHub:PrivateKeyPem'

function formatRelative(iso: string | null | undefined): string {
  if (!iso) return ''
  try {
    return formatDistanceToNow(parseISO(iso), { addSuffix: true })
  } catch {
    return ''
  }
}

export function SettingField({
  definition,
  state,
  isSaving,
  onSave,
}: SettingFieldProps) {
  const { key, displayName, description, isSecret } = definition

  // AgentPermissions fields use specialized inputs (select / switch / list)
  // — dispatched by key since the catalog metadata does not carry type info.
  if (isAgentPermissionField(key)) {
    return (
      <AgentPermissionField
        definition={definition}
        state={state}
        isSaving={isSaving}
        onSave={onSave}
      />
    )
  }

  const help = GITHUB_FIELD_HELP[key]
  const helpText = help?.help ?? description
  const docUrl = help?.docUrl
  const isPem = key === PEM_KEY
  const hasValue = state?.hasValue ?? false
  const currentValue = state?.value ?? ''

  // For non-secret fields, the input is bound to a local copy of the
  // server-side value so the user can edit and we can detect "no change".
  const [localValue, setLocalValue] = useState<string>(currentValue)
  // For secret fields, the input is hidden until the user clicks "Replace value".
  const [replaceMode, setReplaceMode] = useState<boolean>(false)
  const [secretDraft, setSecretDraft] = useState<string>('')

  // Re-sync local state when the underlying value changes (e.g. after a save
  // round-trips, or when the list query refetches).
  useEffect(() => {
    setLocalValue(currentValue)
  }, [currentValue])

  const labelId = `setting-${key.replace(/[^a-zA-Z0-9]/g, '-')}`

  // ---- Non-secret rendering ------------------------------------------------
  if (!isSecret) {
    const dirty = localValue !== currentValue

    return (
      <Paper variant="outlined" sx={{ p: 3 }}>
        <Stack spacing={1.5}>
          <Box>
            <Typography variant="subtitle1" sx={{ fontWeight: 600 }} id={labelId}>
              {displayName}
            </Typography>
            <Typography variant="caption" color="text.secondary" component="div">
              {helpText}
              {docUrl && (
                <>
                  {' '}
                  <Link
                    href={docUrl}
                    target="_blank"
                    rel="noreferrer"
                    sx={{ display: 'inline-flex', alignItems: 'center', gap: 0.25 }}
                  >
                    Docs <OpenInNewIcon sx={{ fontSize: 12 }} />
                  </Link>
                </>
              )}
            </Typography>
          </Box>

          <TextField
            inputProps={{ 'aria-labelledby': labelId }}
            value={localValue}
            onChange={(e) => setLocalValue(e.target.value)}
            placeholder={hasValue ? '' : 'Not configured'}
            size="small"
            fullWidth
          />

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

  // ---- Secret rendering ----------------------------------------------------
  const handleSecretSave = () => {
    // Empty string while in replace-mode is treated by the backend as
    // "keep existing", per SS.2 contract. We still call onSave so the
    // user gets feedback; the field collapses on success.
    onSave(key, secretDraft)
  }

  const handleCancelReplace = () => {
    setReplaceMode(false)
    setSecretDraft('')
  }

  // Collapse the input automatically once the save round-trip lands and
  // updatedAt advances — the parent re-renders us with a fresh `state`.
  // We use a simple effect: when isSaving flips false AND we were in
  // replace mode AND the secretDraft was non-empty, reset.
  // Done via parent-driven prop changes; we expose a tiny reset on success
  // by watching `state?.updatedAt`.
  const lastUpdatedRef = state?.updatedAt
  useEffect(() => {
    if (!replaceMode) return
    if (!isSaving) {
      // After a save lands, lastUpdatedRef changes. Collapse only when
      // we've actually completed a save attempt (secretDraft was set).
      // This effect re-runs on every updatedAt change.
      setReplaceMode(false)
      setSecretDraft('')
    }
    // We intentionally only react to updatedAt + saving transitions.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [lastUpdatedRef])

  return (
    <Paper variant="outlined" sx={{ p: 3 }}>
      <Stack spacing={1.5}>
        <Box>
          <Box sx={{ display: 'flex', alignItems: 'center', gap: 1, flexWrap: 'wrap' }}>
            <Typography variant="subtitle1" sx={{ fontWeight: 600 }} id={labelId}>
              {displayName}
            </Typography>
            {hasValue ? (
              <Chip size="small" color="success" label="Set" />
            ) : (
              <Chip size="small" color="warning" label="Not configured" />
            )}
            {hasValue && state?.updatedAt && (
              <Typography variant="caption" color="text.secondary">
                Last updated {formatRelative(state.updatedAt)}
              </Typography>
            )}
          </Box>
          <Typography variant="caption" color="text.secondary" component="div">
            {helpText}
            {docUrl && (
              <>
                {' '}
                <Link
                  href={docUrl}
                  target="_blank"
                  rel="noreferrer"
                  sx={{ display: 'inline-flex', alignItems: 'center', gap: 0.25 }}
                >
                  Docs <OpenInNewIcon sx={{ fontSize: 12 }} />
                </Link>
              </>
            )}
          </Typography>
        </Box>

        {!replaceMode ? (
          <Box>
            <Button
              variant="text"
              size="small"
              onClick={() => setReplaceMode(true)}
            >
              {hasValue ? 'Replace value' : 'Set value'}
            </Button>
          </Box>
        ) : (
          <Stack spacing={1.5}>
            <TextField
              inputProps={{ 'aria-labelledby': labelId }}
              type={isPem ? 'text' : 'password'}
              multiline={isPem}
              rows={isPem ? 8 : undefined}
              label={`New ${displayName}`}
              placeholder={
                isPem
                  ? '-----BEGIN PRIVATE KEY-----\n…\n-----END PRIVATE KEY-----'
                  : 'Enter new value'
              }
              value={secretDraft}
              onChange={(e) => setSecretDraft(e.target.value)}
              size="small"
              fullWidth
              autoFocus
            />
            <Typography variant="caption" color="text.secondary">
              {hasValue
                ? 'Leave blank to keep the current value.'
                : 'Paste the new value and click Save.'}
            </Typography>
            <Box sx={{ display: 'flex', gap: 1, justifyContent: 'flex-end' }}>
              <Button
                size="small"
                variant="text"
                onClick={handleCancelReplace}
                disabled={isSaving}
              >
                Cancel
              </Button>
              <Button
                size="small"
                variant="contained"
                onClick={handleSecretSave}
                disabled={isSaving}
              >
                {isSaving ? 'Saving…' : 'Save'}
              </Button>
            </Box>
          </Stack>
        )}
      </Stack>
    </Paper>
  )
}
