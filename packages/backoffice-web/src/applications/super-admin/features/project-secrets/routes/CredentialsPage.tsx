import { useState } from 'react'
import { useParams } from 'react-router-dom'
import {
  Alert,
  Box,
  Button,
  Chip,
  CircularProgress,
  Skeleton,
  Snackbar,
  Stack,
  TextField,
  Typography,
} from '@mui/material'
import { useProjectByok } from '../hooks/useProjectByok'

type SnackState = { open: boolean; msg: string; severity: 'success' | 'error' | 'info' }

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
    <Box>
      <Stack
        direction="row"
        alignItems="center"
        spacing={1.5}
        sx={{ mb: 1 }}
      >
        <Typography variant="subtitle1" sx={{ fontWeight: 600 }}>
          {label}
        </Typography>
        {isConfigured ? (
          <Chip label="Configured" color="success" size="small" />
        ) : (
          <Chip label="Not set" size="small" />
        )}
      </Stack>
      <Typography variant="body2" color="text.secondary" sx={{ mb: 1.5 }}>
        {helper}
      </Typography>
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
          }}
        />
        <Stack direction="row" spacing={1} sx={{ flexShrink: 0 }}>
          <Button
            variant="contained"
            onClick={handleSave}
            disabled={!canSave}
            sx={{ minWidth: 96 }}
          >
            {isBusy ? <CircularProgress size={18} color="inherit" /> : 'Save'}
          </Button>
          {isConfigured && (
            <Button
              variant="outlined"
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
    </Box>
  )
}

export function CredentialsPage() {
  const { projectId = '' } = useParams<{ projectId: string }>()
  const [snack, setSnack] = useState<SnackState>({ open: false, msg: '', severity: 'success' })
  const [pending, setPending] = useState(false)

  const notify = (msg: string, severity: SnackState['severity']) =>
    setSnack({ open: true, msg, severity })

  const {
    status,
    isOwner,
    isLoadingStatus,
    isUpdating,
    saveCursorApiKey,
    clearCursorApiKey,
  } = useProjectByok({
    projectId,
    onSuccess: (msg) => notify(msg, 'success'),
    onError: (msg) => notify(msg, 'error'),
  })

  if (!projectId) {
    return <Alert severity="error">Missing project id in URL.</Alert>
  }

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

  return (
    <>
      <Stack spacing={4}>
        <Box>
          <Typography variant="overline" color="text.secondary">
            Project settings
          </Typography>
          <Typography variant="h4" component="h1" sx={{ mb: 0.5 }}>
            Credentials (BYOK)
          </Typography>
          <Typography variant="body2" color="text.secondary">
            Project {projectId}. Per-project Cursor credentials. Values are
            encrypted at rest and never sent back to the browser — once saved,
            you can only replace or clear them.
          </Typography>
        </Box>

        {isLoadingStatus && (
          <Stack spacing={3}>
            <Skeleton variant="rounded" height={96} />
          </Stack>
        )}

        {!isLoadingStatus && isOwner === false && (
          <Alert severity="info">
            Only the project owner can manage these credentials.
          </Alert>
        )}

        {!isLoadingStatus && isOwner === null && (
          <Alert severity="error">
            Could not load credential status. Reload the page to try again.
          </Alert>
        )}

        {!isLoadingStatus && isOwner === true && status && (
          <CredentialRow
            label="Cursor API key"
            helper="Cursor SDK API key used for agent turns on this project. Required for any Cursor-routed turns to authenticate."
            isConfigured={status.hasCursorApiKey}
            isUpdating={isUpdating}
            isPending={pending}
            onSave={(v) => runWith(() => saveCursorApiKey(v))}
            onClear={() => runWith(() => clearCursorApiKey())}
          />
        )}
      </Stack>

      <Snackbar
        open={snack.open}
        autoHideDuration={2500}
        onClose={() => setSnack((s) => ({ ...s, open: false }))}
      >
        <Alert
          onClose={() => setSnack((s) => ({ ...s, open: false }))}
          severity={snack.severity}
          variant="filled"
          sx={{ width: '100%' }}
        >
          {snack.msg}
        </Alert>
      </Snackbar>
    </>
  )
}
