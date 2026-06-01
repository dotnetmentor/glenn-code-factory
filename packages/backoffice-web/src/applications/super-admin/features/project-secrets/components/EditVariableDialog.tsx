import { useEffect, useState } from 'react'
import {
  Alert,
  Button,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  Stack,
  TextField,
  Typography,
} from '@mui/material'

interface EditVariableDialogProps {
  open: boolean
  variableKey: string | null
  onClose: () => void
  onSubmit: (key: string, value: string) => Promise<unknown>
  isSubmitting: boolean
}

export function EditVariableDialog({
  open,
  variableKey,
  onClose,
  onSubmit,
  isSubmitting,
}: EditVariableDialogProps) {
  const [value, setValue] = useState('')
  const [valueError, setValueError] = useState<string | null>(null)
  const [serverError, setServerError] = useState<string | null>(null)

  useEffect(() => {
    if (!open) {
      setValue('')
      setValueError(null)
      setServerError(null)
    }
  }, [open])

  const handleValueChange = (raw: string) => {
    setValue(raw)
    setServerError(null)
    if (raw.includes('\n')) {
      setValueError('Multi-line values not yet supported')
    } else {
      setValueError(null)
    }
  }

  const canSubmit = !!value && !valueError && !value.includes('\n') && !isSubmitting

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault()
    if (!variableKey) return
    if (value.includes('\n')) {
      setValueError('Multi-line values not yet supported')
      return
    }
    try {
      await onSubmit(variableKey, value)
      onClose()
    } catch (err: unknown) {
      const msg =
        err instanceof Error ? err.message : 'Failed to update variable'
      setServerError(msg)
    }
  }

  return (
    <Dialog open={open} onClose={isSubmitting ? undefined : onClose} maxWidth="sm" fullWidth>
      <form onSubmit={handleSubmit}>
        <DialogTitle>Edit environment variable</DialogTitle>
        <DialogContent>
          <Stack spacing={2} sx={{ mt: 1 }}>
            <Typography variant="body2" color="text.secondary">
              For safety, the existing value is not pre-filled. Paste the new value below.
            </Typography>

            <TextField
              label="Key"
              fullWidth
              value={variableKey ?? ''}
              disabled
              InputProps={{ readOnly: true }}
              helperText="Key cannot be changed. Delete and re-add to rename."
            />

            <TextField
              label="New value"
              required
              autoFocus
              fullWidth
              value={value}
              onChange={(e) => handleValueChange(e.target.value)}
              error={!!valueError}
              helperText={valueError ?? 'Single-line value. Multi-line not yet supported.'}
              disabled={isSubmitting}
              inputProps={{ spellCheck: 'false', autoComplete: 'off' }}
            />

            {serverError && <Alert severity="error">{serverError}</Alert>}
          </Stack>
        </DialogContent>
        <DialogActions>
          <Button type="button" onClick={onClose} disabled={isSubmitting}>
            Cancel
          </Button>
          <Button type="submit" variant="contained" disabled={!canSubmit}>
            {isSubmitting ? 'Saving...' : 'Save'}
          </Button>
        </DialogActions>
      </form>
    </Dialog>
  )
}
