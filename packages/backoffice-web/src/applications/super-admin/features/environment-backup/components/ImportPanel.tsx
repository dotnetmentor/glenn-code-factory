import { useState } from 'react'
import {
  Alert,
  AlertTitle,
  Box,
  Button,
  Dialog,
  DialogActions,
  DialogContent,
  DialogContentText,
  DialogTitle,
  Paper,
  Stack,
  TextField,
  Typography,
} from '@mui/material'
import UploadIcon from '@mui/icons-material/Upload'
import type { EnvironmentSnapshotDto } from '../../../../../api/queries-commands'
import { parseSnapshot, useEnvironmentBackup } from '../hooks/useEnvironmentBackup'
import { ImportSummaryView } from './ImportSummaryView'
import { PostImportCaveats } from './PostImportCaveats'

type Props = {
  backup: ReturnType<typeof useEnvironmentBackup>
}

export function ImportPanel({ backup }: Props) {
  const { importing, importError, importSummary, runImport } = backup
  const [raw, setRaw] = useState('')
  const [validationError, setValidationError] = useState<string | null>(null)
  const [confirmOpen, setConfirmOpen] = useState(false)
  const [pendingSnapshot, setPendingSnapshot] = useState<EnvironmentSnapshotDto | null>(
    null,
  )

  const handleReview = () => {
    const result = parseSnapshot(raw)
    if (result.error) {
      setValidationError(result.error)
      setPendingSnapshot(null)
      return
    }
    setValidationError(null)
    setPendingSnapshot(result.snapshot)
    setConfirmOpen(true)
  }

  const handleConfirm = async () => {
    setConfirmOpen(false)
    if (!pendingSnapshot) return
    await runImport(pendingSnapshot)
  }

  return (
    <Paper variant="outlined" sx={{ p: 3 }}>
      <Stack spacing={2.5}>
        <Box>
          <Typography variant="h6" component="h2">
            Import environment
          </Typography>
          <Typography variant="body2" color="text.secondary">
            Paste a previously exported backup blob to reconstruct this environment.
            Entities are restored in dependency-safe order and the import is
            idempotent — safe to re-run.
          </Typography>
        </Box>

        <Alert severity="error">
          <AlertTitle>Import overwrites existing data</AlertTitle>
          This restores the environment from the blob and overwrites matching records
          in this environment. The intended use is restoring into a fresh / empty
          environment. You will be asked to confirm before anything is written.
        </Alert>

        <TextField
          label="Paste backup JSON"
          placeholder='{ "version": "1", "exportedAtUtc": "…", … }'
          value={raw}
          onChange={(e) => {
            setRaw(e.target.value)
            if (validationError) setValidationError(null)
          }}
          multiline
          minRows={8}
          maxRows={20}
          fullWidth
          InputProps={{ sx: { fontFamily: 'monospace', fontSize: 12 } }}
        />

        {validationError && <Alert severity="warning">{validationError}</Alert>}

        <Box>
          <Button
            variant="contained"
            color="error"
            startIcon={<UploadIcon />}
            onClick={handleReview}
            disabled={importing || raw.trim().length === 0}
          >
            {importing ? 'Importing…' : 'Import environment'}
          </Button>
        </Box>

        {importError && (
          <Alert severity="error">Import failed: {importError}</Alert>
        )}

        {importSummary && (
          <Stack spacing={2.5}>
            <ImportSummaryView summary={importSummary} />
            <PostImportCaveats />
          </Stack>
        )}
      </Stack>

      <Dialog open={confirmOpen} onClose={() => setConfirmOpen(false)}>
        <DialogTitle>Overwrite this environment?</DialogTitle>
        <DialogContent>
          <DialogContentText>
            Importing will restore the environment from the pasted backup and{' '}
            <strong>overwrite existing data</strong> in this environment. This cannot
            be undone. Continue only if you are sure.
          </DialogContentText>
        </DialogContent>
        <DialogActions>
          <Button onClick={() => setConfirmOpen(false)}>Cancel</Button>
          <Button onClick={handleConfirm} color="error" variant="contained" autoFocus>
            Overwrite and import
          </Button>
        </DialogActions>
      </Dialog>
    </Paper>
  )
}
