import { useState } from 'react'
import {
  Alert,
  AlertTitle,
  Box,
  Button,
  Paper,
  Snackbar,
  Stack,
  TextField,
  Typography,
} from '@mui/material'
import DownloadIcon from '@mui/icons-material/Download'
import ContentCopyIcon from '@mui/icons-material/ContentCopy'
import { useEnvironmentBackup } from '../hooks/useEnvironmentBackup'

type Props = {
  backup: ReturnType<typeof useEnvironmentBackup>
}

function downloadJson(json: string) {
  const blob = new Blob([json], { type: 'application/json' })
  const url = URL.createObjectURL(blob)
  const date = new Date().toISOString().slice(0, 10)
  const link = document.createElement('a')
  link.href = url
  link.download = `environment-backup-${date}.json`
  document.body.appendChild(link)
  link.click()
  document.body.removeChild(link)
  URL.revokeObjectURL(url)
}

export function ExportPanel({ backup }: Props) {
  const { exporting, exportError, exportJson, runExport } = backup
  const [copied, setCopied] = useState(false)
  const [copyError, setCopyError] = useState(false)

  const handleExport = async () => {
    const json = await runExport()
    if (json) downloadJson(json)
  }

  const handleCopy = async () => {
    if (!exportJson) return
    try {
      await navigator.clipboard.writeText(exportJson)
      setCopied(true)
    } catch {
      setCopyError(true)
    }
  }

  return (
    <Paper variant="outlined" sx={{ p: 3 }}>
      <Stack spacing={2.5}>
        <Box>
          <Typography variant="h6" component="h2">
            Export environment
          </Typography>
          <Typography variant="body2" color="text.secondary">
            Capture the entire environment — users, workspaces, projects, secrets,
            specifications, kanban boards, and the GitHub/Fly/runtime credentials — as
            a single JSON blob. Download it or copy it, then store it somewhere safe.
          </Typography>
        </Box>

        <Alert severity="warning">
          <AlertTitle>This blob contains secrets in clear text</AlertTitle>
          The export decrypts every secret (GitHub App private key, Fly API token,
          runtime signing keys, project secrets, Cursor keys) so the restored
          environment authenticates with no manual steps. Treat the file as highly
          sensitive — store it in a password manager such as 1Password and never
          commit it or share it over insecure channels.
        </Alert>

        <Stack direction="row" spacing={1.5} flexWrap="wrap" useFlexGap>
          <Button
            variant="contained"
            startIcon={<DownloadIcon />}
            onClick={handleExport}
            disabled={exporting}
          >
            {exporting ? 'Exporting…' : 'Export environment'}
          </Button>
          <Button
            variant="outlined"
            startIcon={<ContentCopyIcon />}
            onClick={handleCopy}
            disabled={!exportJson || exporting}
          >
            Copy JSON
          </Button>
        </Stack>

        {exportError && (
          <Alert severity="error">Export failed: {exportError}</Alert>
        )}

        {exportJson && (
          <TextField
            label="Environment backup JSON"
            value={exportJson}
            multiline
            minRows={8}
            maxRows={20}
            fullWidth
            InputProps={{
              readOnly: true,
              sx: { fontFamily: 'monospace', fontSize: 12 },
            }}
          />
        )}
      </Stack>

      <Snackbar
        open={copied}
        autoHideDuration={3000}
        onClose={() => setCopied(false)}
        message="Backup JSON copied to clipboard"
      />
      <Snackbar
        open={copyError}
        autoHideDuration={4000}
        onClose={() => setCopyError(false)}
        message="Could not copy to clipboard — select the text and copy manually"
      />
    </Paper>
  )
}
