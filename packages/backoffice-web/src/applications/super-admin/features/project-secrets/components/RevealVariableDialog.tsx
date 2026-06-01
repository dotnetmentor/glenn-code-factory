import { useEffect, useRef, useState } from 'react'
import {
  Alert,
  Box,
  Button,
  CircularProgress,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  IconButton,
  Stack,
  TextField,
  Typography,
} from '@mui/material'
import ContentCopyIcon from '@mui/icons-material/ContentCopy'
import VisibilityOffIcon from '@mui/icons-material/VisibilityOff'
import { useGetApiProjectsProjectIdSecretsKeyReveal } from '@/api/queries-commands'

const AUTO_DISMISS_SECONDS = 15

interface RevealVariableDialogProps {
  open: boolean
  projectId: string
  variableKey: string | null
  onClose: (reason?: 'auto' | 'manual' | 'error') => void
}

/**
 * Fetches the plaintext for a single secret, displays it for AUTO_DISMISS_SECONDS,
 * then closes and wipes from local React state.
 *
 * Plaintext lives ONLY in this component's local state. Never logged, never persisted.
 */
export function RevealVariableDialog({
  open,
  projectId,
  variableKey,
  onClose,
}: RevealVariableDialogProps) {
  // Local-only plaintext mirror so we can wipe it on dismiss.
  const [plaintext, setPlaintext] = useState<string>('')
  const [secondsLeft, setSecondsLeft] = useState<number>(AUTO_DISMISS_SECONDS)
  const [copyToast, setCopyToast] = useState<string | null>(null)
  const closeRef = useRef(onClose)
  closeRef.current = onClose

  // Only enable the query when the dialog is actually open with a key.
  const enabled = open && !!projectId && !!variableKey
  const revealQuery = useGetApiProjectsProjectIdSecretsKeyReveal(
    projectId,
    variableKey ?? '',
    {
      query: {
        enabled,
        // Never cache the plaintext beyond the lifetime of this dialog.
        staleTime: 0,
        gcTime: 0,
        refetchOnWindowFocus: false,
        refetchOnReconnect: false,
        refetchOnMount: 'always',
      },
    },
  )

  // Mirror server response into local state so we can wipe it.
  useEffect(() => {
    if (open && revealQuery.data?.plaintext) {
      setPlaintext(revealQuery.data.plaintext)
    }
  }, [open, revealQuery.data])

  // Reset timer + state when dialog opens.
  useEffect(() => {
    if (open) {
      setSecondsLeft(AUTO_DISMISS_SECONDS)
      setCopyToast(null)
    } else {
      // Wipe on close — explicit, non-negotiable.
      setPlaintext('')
      setCopyToast(null)
    }
  }, [open])

  // Auto-dismiss countdown — only ticks once we actually have plaintext to show.
  useEffect(() => {
    if (!open || !plaintext) return
    if (secondsLeft <= 0) {
      setPlaintext('')
      closeRef.current('auto')
      return
    }
    const t = window.setTimeout(() => setSecondsLeft((s) => s - 1), 1000)
    return () => window.clearTimeout(t)
  }, [open, plaintext, secondsLeft])

  const handleCopy = async () => {
    if (!plaintext) return
    try {
      await navigator.clipboard.writeText(plaintext)
      setCopyToast('Copied to clipboard')
      window.setTimeout(() => setCopyToast(null), 2000)
    } catch {
      setCopyToast('Copy failed — select and copy manually')
    }
  }

  const handleManualClose = () => {
    setPlaintext('')
    closeRef.current('manual')
  }

  const error = revealQuery.error
  const isLoading = enabled && revealQuery.isLoading

  return (
    <Dialog open={open} onClose={handleManualClose} maxWidth="sm" fullWidth>
      <DialogTitle>
        <Stack direction="row" alignItems="center" justifyContent="space-between">
          <Box>
            <Typography variant="h6" component="span">
              {variableKey ?? 'Variable'}
            </Typography>
          </Box>
          {plaintext && (
            <Typography variant="caption" color="text.secondary">
              auto-closes in {secondsLeft}s
            </Typography>
          )}
        </Stack>
      </DialogTitle>
      <DialogContent>
        <Stack spacing={2} sx={{ mt: 1 }}>
          <Typography variant="body2" color="text.secondary">
            This action is audit-logged on the server. The value will be cleared
            from this view in {AUTO_DISMISS_SECONDS} seconds.
          </Typography>

          {isLoading && (
            <Box sx={{ display: 'flex', justifyContent: 'center', py: 4 }}>
              <CircularProgress size={24} />
            </Box>
          )}

          {error && (
            <Alert severity="error">
              Failed to reveal variable.{' '}
              {error instanceof Error ? error.message : ''}
            </Alert>
          )}

          {plaintext && !isLoading && !error && (
            <Box sx={{ position: 'relative' }}>
              <TextField
                label="Value"
                fullWidth
                multiline={false}
                value={plaintext}
                InputProps={{
                  readOnly: true,
                  sx: { fontFamily: 'monospace', pr: 6 },
                }}
                inputProps={{ spellCheck: 'false' }}
              />
              <IconButton
                aria-label="Copy value"
                onClick={handleCopy}
                size="small"
                sx={{ position: 'absolute', top: 18, right: 8 }}
              >
                <ContentCopyIcon fontSize="small" />
              </IconButton>
            </Box>
          )}

          {copyToast && (
            <Alert severity="info" variant="outlined">
              {copyToast}
            </Alert>
          )}
        </Stack>
      </DialogContent>
      <DialogActions>
        <Button
          onClick={handleManualClose}
          startIcon={<VisibilityOffIcon />}
          variant="contained"
        >
          Hide
        </Button>
      </DialogActions>
    </Dialog>
  )
}
