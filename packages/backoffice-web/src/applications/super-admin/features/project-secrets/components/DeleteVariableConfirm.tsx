import { useState } from 'react'
import {
  Alert,
  Button,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  Stack,
  Typography,
} from '@mui/material'

interface DeleteVariableConfirmProps {
  open: boolean
  variableKey: string | null
  onClose: () => void
  onConfirm: (key: string) => Promise<unknown>
  isDeleting: boolean
}

export function DeleteVariableConfirm({
  open,
  variableKey,
  onClose,
  onConfirm,
  isDeleting,
}: DeleteVariableConfirmProps) {
  const [serverError, setServerError] = useState<string | null>(null)

  const handleConfirm = async () => {
    if (!variableKey) return
    setServerError(null)
    try {
      await onConfirm(variableKey)
      onClose()
    } catch (err: unknown) {
      const msg =
        err instanceof Error ? err.message : 'Failed to delete variable'
      setServerError(msg)
    }
  }

  return (
    <Dialog open={open} onClose={isDeleting ? undefined : onClose} maxWidth="xs" fullWidth>
      <DialogTitle>Delete {variableKey ? <code>{variableKey}</code> : 'variable'}?</DialogTitle>
      <DialogContent>
        <Stack spacing={2}>
          <Typography variant="body2">
            This change is pushed to the runtime immediately. Any process that
            relies on this variable will lose access on next read.
          </Typography>
          {serverError && <Alert severity="error">{serverError}</Alert>}
        </Stack>
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose} disabled={isDeleting}>
          Cancel
        </Button>
        <Button onClick={handleConfirm} color="error" variant="contained" disabled={isDeleting}>
          {isDeleting ? 'Deleting...' : 'Delete'}
        </Button>
      </DialogActions>
    </Dialog>
  )
}
