import { useState } from 'react'
import {
  Alert,
  Box,
  Button,
  Chip,
  CircularProgress,
  FormControlLabel,
  Skeleton,
  Stack,
  Switch,
  TextField,
  Typography,
} from '@mui/material'
import { useNotification } from '../../../../shared/contexts/NotificationContext'
import { useWorkspace } from '../../../../shared/contexts/WorkspaceContext'
import {
  bodySx,
  captionSx,
  sectionTitleSx,
  workspaceColors,
  workspaceFontFamily,
  workspaceText,
} from '../../../shared'
import { useWorkspaceByok } from '../hooks/useWorkspaceByok'

export function CredentialsTab() {
  const { currentSlug } = useWorkspace()
  const { showSuccess, showError } = useNotification()
  const [pending, setPending] = useState(false)

  const {
    status,
    canManage,
    isLoadingStatus,
    isUpdating,
    saveCursorApiKey,
    clearCursorApiKey,
    setAllowProjectOverride,
  } = useWorkspaceByok({
    workspaceSlug: currentSlug,
    onSuccess: (msg) => showSuccess(msg),
    onError: (msg) => showError(msg),
  })

  const runWith = async (fn: () => Promise<unknown>) => {
    setPending(true)
    try {
      await fn()
    } catch {
      // hook surfaces toast
    } finally {
      setPending(false)
    }
  }

  const isBusy = isUpdating && pending

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
          Workspace-wide Cursor agent credentials. All projects inherit this key
          unless a project sets its own override. Values are encrypted at rest and
          never sent back to the browser.
        </Typography>
      </Box>

      {isLoadingStatus && (
        <Stack spacing={3}>
          <Skeleton variant="rounded" height={120} />
        </Stack>
      )}

      {!isLoadingStatus && canManage === false && (
        <Alert severity="info" variant="quiet">
          Only workspace owners and admins can manage credentials.
        </Alert>
      )}

      {!isLoadingStatus && canManage === null && (
        <Alert severity="error" variant="quiet">
          Could not load credential status. Reload the page to try again.
        </Alert>
      )}

      {!isLoadingStatus && canManage === true && status && (
        <Stack spacing={3}>
          <CredentialRow
            label="Cursor API key"
            helper="Default Cursor SDK API key for every project in this workspace. Individual projects can override when allowed below."
            isConfigured={status.hasCursorApiKey}
            isBusy={isBusy}
            onSave={(v) => runWith(() => saveCursorApiKey(v))}
            onClear={() => runWith(() => clearCursorApiKey())}
          />

          <Box
            sx={{
              border: `1px solid ${workspaceColors.hairline}`,
              borderRadius: 2,
              p: { xs: 2.5, md: 3 },
            }}
          >
            <FormControlLabel
              control={
                <Switch
                  checked={status.allowProjectCursorApiKeyOverride}
                  disabled={isBusy}
                  onChange={(_e, checked) =>
                    runWith(() => setAllowProjectOverride(checked))
                  }
                />
              }
              label={
                <Box>
                  <Typography sx={sectionTitleSx}>Allow per-project keys</Typography>
                  <Typography sx={captionSx}>
                    When off, project owners cannot set a project-specific Cursor key;
                    every project uses the workspace key (or the host env fallback).
                  </Typography>
                </Box>
              }
              sx={{ alignItems: 'flex-start', m: 0 }}
            />
          </Box>
        </Stack>
      )}
    </Stack>
  )
}

interface CredentialRowProps {
  label: string
  helper: string
  isConfigured: boolean
  isBusy: boolean
  onSave: (value: string) => Promise<unknown>
  onClear: () => Promise<unknown>
}

function CredentialRow({
  label,
  helper,
  isConfigured,
  isBusy,
  onSave,
  onClear,
}: CredentialRowProps) {
  const [value, setValue] = useState('')
  const trimmed = value.trim()
  const canSave = trimmed.length > 0 && !isBusy

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
              onClick={async () => {
                if (!canSave) return
                await onSave(trimmed)
                setValue('')
              }}
              disabled={!canSave}
              sx={{ minWidth: 96 }}
            >
              {isBusy ? <CircularProgress size={16} color="inherit" /> : 'Save'}
            </Button>
            {isConfigured && (
              <Button
                variant="pillOutlined"
                color="error"
                onClick={async () => {
                  await onClear()
                  setValue('')
                }}
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
