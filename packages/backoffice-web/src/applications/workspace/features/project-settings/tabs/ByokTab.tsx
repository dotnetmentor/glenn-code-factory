import { useState } from 'react'
import {
  Alert,
  Box,
  Button,
  Chip,
  CircularProgress,
  Skeleton,
  Stack,
  TextField,
  Typography,
} from '@mui/material'
import { useProjectByok } from '../../../../super-admin/features/project-secrets/hooks/useProjectByok'
import { useNotification } from '../../../../shared/contexts/NotificationContext'
import {
  bodySx,
  captionSx,
  sectionTitleSx,
  workspaceColors,
  workspaceFontFamily,
  workspaceText,
} from '../../../shared'

interface ByokTabProps {
  projectId: string
}

export function ByokTab({ projectId }: ByokTabProps) {
  const { showSuccess, showError } = useNotification()
  const [pending, setPending] = useState(false)

  const {
    status,
    isOwner,
    isLoadingStatus,
    isUpdating,
    saveCursorApiKey,
    clearCursorApiKey,
  } = useProjectByok({
    projectId,
    onSuccess: (msg) => showSuccess(msg),
    onError: (msg) => showError(msg),
  })

  const runWith = async (fn: () => Promise<unknown>) => {
    setPending(true)
    try {
      await fn()
    } catch {
      // error toast already surfaced via hook's onError
    } finally {
      setPending(false)
    }
  }

  const projectOverrideAllowed = status?.allowProjectCursorApiKeyOverride !== false
  const usingWorkspaceDefault =
    status &&
    !status.hasCursorApiKey &&
    status.hasWorkspaceCursorApiKey &&
    status.hasEffectiveCursorApiKey

  return (
    <Stack spacing={3}>
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
          Credentials
        </Typography>
        <Typography sx={bodySx}>
          Optional per-project Cursor key. When unset, this project uses the
          workspace default from workspace settings. Values are encrypted at rest
          and never sent back to the browser.
        </Typography>
      </Box>

      {isLoadingStatus && (
        <Stack spacing={3}>
          <Skeleton variant="rounded" height={120} />
        </Stack>
      )}

      {!isLoadingStatus && isOwner === false && (
        <Alert severity="info" variant="quiet">
          Only the project owner can manage these credentials.
        </Alert>
      )}

      {!isLoadingStatus && isOwner === null && (
        <Alert severity="error" variant="quiet">
          Could not load credential status. Reload the page to try again.
        </Alert>
      )}

      {!isLoadingStatus && isOwner === true && status && !projectOverrideAllowed && (
        <Alert severity="info" variant="quiet">
          This workspace uses a shared Cursor key only. Per-project overrides are
          disabled — configure the key under workspace settings → Credentials.
        </Alert>
      )}

      {!isLoadingStatus && isOwner === true && status && usingWorkspaceDefault && (
        <Alert severity="success" variant="quiet">
          Using the workspace Cursor API key (no project override).
        </Alert>
      )}

      {!isLoadingStatus && isOwner === true && status && projectOverrideAllowed && (
        <CredentialRow
          label="Cursor API key (project override)"
          helper="Overrides the workspace key for this project only. Clear to inherit the workspace default again."
          isConfigured={status.hasCursorApiKey}
          isUpdating={isUpdating}
          isPending={pending}
          onSave={(v) => runWith(() => saveCursorApiKey(v))}
          onClear={() => runWith(() => clearCursorApiKey())}
        />
      )}
    </Stack>
  )
}

interface CredentialRowProps {
  label: string
  helper: string
  isConfigured: boolean
  isUpdating: boolean
  isPending: boolean
  onSave: (value: string) => Promise<unknown>
  onClear: () => Promise<unknown>
}

function CredentialRow({
  label,
  helper,
  isConfigured,
  isUpdating,
  isPending,
  onSave,
  onClear,
}: CredentialRowProps) {
  const [value, setValue] = useState('')
  const isBusy = isUpdating && isPending
  const trimmed = value.trim()
  const canSave = trimmed.length > 0 && !isBusy

  const handleSave = async () => {
    if (!canSave) return
    await onSave(trimmed)
    setValue('')
  }

  const handleClear = async () => {
    await onClear()
    setValue('')
  }

  return (
    <Box
      sx={{
        border: `1px solid ${workspaceColors.hairline}`,
        borderRadius: 2,
        p: { xs: 2.5, md: 3 },
      }}
    >
      <Stack spacing={2}>
        <Box>
          <Stack direction="row" alignItems="center" spacing={1.5} sx={{ mb: 0.5 }}>
            <Typography sx={sectionTitleSx}>{label}</Typography>
            {isConfigured ? (
              <Chip label="Configured" size="small" color="success" variant="outlined" />
            ) : (
              <Chip label="Not set" size="small" variant="outlined" />
            )}
          </Stack>
          <Typography sx={captionSx}>{helper}</Typography>
        </Box>

        <Stack direction={{ xs: 'column', sm: 'row' }} spacing={1.5} alignItems="stretch">
          <TextField
            type="password"
            fullWidth
            size="small"
            placeholder={isConfigured ? 'Paste new value to replace' : 'Paste value'}
            value={value}
            onChange={(e) => setValue(e.target.value)}
            disabled={isBusy}
            inputProps={{
              autoComplete: 'off',
              spellCheck: 'false',
              'aria-label': `${label} value`,
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
          <Stack direction="row" spacing={1} sx={{ flexShrink: 0 }}>
            <Button
              variant="pill"
              color="primary"
              onClick={handleSave}
              disabled={!canSave}
              sx={{ minWidth: 96 }}
            >
              {isBusy ? <CircularProgress size={16} color="inherit" /> : 'Save'}
            </Button>
            {isConfigured && (
              <Button
                variant="pillOutlined"
                color="error"
                onClick={handleClear}
                disabled={isBusy}
                sx={{ minWidth: 96 }}
              >
                Clear
              </Button>
            )}
          </Stack>
        </Stack>
      </Stack>
    </Box>
  )
}
