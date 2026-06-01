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

const KEY_REGEX = /^[A-Z][A-Z0-9_]*$/

interface AddVariableDialogProps {
  open: boolean
  onClose: () => void
  onSubmit: (key: string, value: string) => Promise<unknown>
  isSubmitting: boolean
  /** keys already in the project, used for client-side duplicate guard */
  existingKeys: string[]
}

export function AddVariableDialog({
  open,
  onClose,
  onSubmit,
  isSubmitting,
  existingKeys,
}: AddVariableDialogProps) {
  const [key, setKey] = useState('')
  const [value, setValue] = useState('')
  const [keyError, setKeyError] = useState<string | null>(null)
  const [valueError, setValueError] = useState<string | null>(null)
  const [serverError, setServerError] = useState<string | null>(null)
  const [keyWarning, setKeyWarning] = useState<string | null>(null)

  useEffect(() => {
    if (!open) {
      setKey('')
      setValue('')
      setKeyError(null)
      setValueError(null)
      setServerError(null)
      setKeyWarning(null)
    }
  }, [open])

  const handleKeyChange = (raw: string) => {
    setKey(raw)
    setServerError(null)
    if (raw && raw !== raw.toUpperCase()) {
      setKeyWarning('Lowercase characters will be auto-uppercased on blur')
    } else {
      setKeyWarning(null)
    }
    if (raw && !KEY_REGEX.test(raw.toUpperCase())) {
      setKeyError('Must start with a letter and contain only A-Z, 0-9, _')
    } else {
      setKeyError(null)
    }
  }

  const handleKeyBlur = () => {
    const upper = key.trim().toUpperCase()
    setKey(upper)
    setKeyWarning(null)
    if (upper && !KEY_REGEX.test(upper)) {
      setKeyError('Must start with a letter and contain only A-Z, 0-9, _')
    } else if (upper && existingKeys.includes(upper)) {
      setKeyError(`Variable "${upper}" already exists`)
    } else {
      setKeyError(null)
    }
  }

  const handleValueChange = (raw: string) => {
    setValue(raw)
    setServerError(null)
    if (raw.includes('\n')) {
      setValueError('Multi-line values not yet supported')
    } else {
      setValueError(null)
    }
  }

  const canSubmit =
    !!key &&
    !!value &&
    !keyError &&
    !valueError &&
    KEY_REGEX.test(key) &&
    !existingKeys.includes(key) &&
    !value.includes('\n') &&
    !isSubmitting

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault()
    const upper = key.trim().toUpperCase()
    if (!KEY_REGEX.test(upper)) {
      setKeyError('Must start with a letter and contain only A-Z, 0-9, _')
      return
    }
    if (existingKeys.includes(upper)) {
      setKeyError(`Variable "${upper}" already exists`)
      return
    }
    if (value.includes('\n')) {
      setValueError('Multi-line values not yet supported')
      return
    }
    try {
      await onSubmit(upper, value)
      onClose()
    } catch (err: unknown) {
      const msg =
        err instanceof Error ? err.message : 'Failed to add variable'
      setServerError(msg)
    }
  }

  return (
    <Dialog open={open} onClose={isSubmitting ? undefined : onClose} maxWidth="sm" fullWidth>
      <form onSubmit={handleSubmit}>
        <DialogTitle>Add environment variable</DialogTitle>
        <DialogContent>
          <Stack spacing={2} sx={{ mt: 1 }}>
            <Typography variant="body2" color="text.secondary">
              The value is encrypted server-side. Once saved, you can re-paste a
              new value but you will not see the original here again.
            </Typography>

            <TextField
              label="Key"
              required
              autoFocus
              fullWidth
              value={key}
              onChange={(e) => handleKeyChange(e.target.value)}
              onBlur={handleKeyBlur}
              error={!!keyError}
              helperText={keyError ?? keyWarning ?? 'Format: A-Z, 0-9, _ (e.g. DATABASE_URL)'}
              disabled={isSubmitting}
              inputProps={{ spellCheck: 'false', autoComplete: 'off' }}
            />

            <TextField
              label="Value"
              required
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
            {isSubmitting ? 'Adding...' : 'Add variable'}
          </Button>
        </DialogActions>
      </form>
    </Dialog>
  )
}
