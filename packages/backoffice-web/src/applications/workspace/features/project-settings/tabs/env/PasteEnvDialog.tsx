import { useMemo, useState } from 'react'
import {
  Alert,
  Box,
  Button,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  Stack,
  TextField,
  Typography,
} from '@mui/material'
import {
  chromeTokens,
  semanticTokens,
  surfaceTokens,
  workspaceFontFamily,
} from '../../../../shared/designTokens'
import { parseDotEnv } from './parseDotEnv'
import { humaniseEnvVarApiError } from './envVarApiError'

const tokens = {
  canvas: surfaceTokens.canvasBg,
  surface: surfaceTokens.chromeBg,
  primary: surfaceTokens.textPrimary,
  muted: surfaceTokens.textMuted,
  hairline: surfaceTokens.hairline,
  rowHover: chromeTokens.rowHover,
} as const

export interface PasteEnvDialogProps {
  open: boolean
  isImporting: boolean
  onClose: () => void
  onImport: (text: string) => Promise<void>
}

export function PasteEnvDialog({ open, isImporting, onClose, onImport }: PasteEnvDialogProps) {
  const [text, setText] = useState('')
  const [submitError, setSubmitError] = useState<string | null>(null)

  const preview = useMemo(() => parseDotEnv(text), [text])

  const handleClose = () => {
    if (isImporting) return
    setText('')
    setSubmitError(null)
    onClose()
  }

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault()
    if (preview.entries.length === 0) {
      setSubmitError('Paste a .env file with at least one KEY=VALUE line.')
      return
    }
    setSubmitError(null)
    try {
      await onImport(text)
      setText('')
    } catch (err) {
      setSubmitError(humaniseEnvVarApiError(err))
    }
  }

  return (
    <Dialog
      open={open}
      onClose={handleClose}
      maxWidth={false}
      slotProps={{
        paper: {
          sx: {
            width: '100%',
            maxWidth: 560,
            bgcolor: tokens.canvas,
            border: `1px solid ${tokens.hairline}`,
            borderRadius: '12px',
            boxShadow: '0 20px 50px rgba(0,0,0,0.12)',
            backgroundImage: 'none',
          },
        },
      }}
    >
      <form onSubmit={handleSubmit}>
        <DialogTitle
          sx={{
            fontSize: '0.875rem',
            fontWeight: 600,
            color: tokens.primary,
            px: 3,
            pt: 2.5,
            pb: 1.5,
            lineHeight: 1.4,
          }}
        >
          Paste .env
        </DialogTitle>

        <DialogContent sx={{ px: 3, pb: 2.5, pt: 0 }}>
          <Stack spacing={1.75}>
            <Typography sx={{ fontSize: '0.8125rem', color: tokens.muted, lineHeight: 1.5 }}>
              Paste the contents of a <code>.env</code> file. Comments and blank lines are
              ignored. Existing keys are updated; new keys are added as secrets.
            </Typography>

            <TextField
              autoFocus
              fullWidth
              multiline
              minRows={10}
              maxRows={16}
              value={text}
              placeholder={'Jwt__Key=...\nSystemSettings__EncryptionKey=...\nDATABASE_URL=...'}
              disabled={isImporting}
              onChange={(e) => {
                setText(e.target.value)
                setSubmitError(null)
              }}
              inputProps={{ spellCheck: 'false', autoComplete: 'off' }}
              sx={{
                '& .MuiOutlinedInput-root': {
                  bgcolor: tokens.surface,
                  borderRadius: 1.5,
                  fontFamily: workspaceFontFamily.mono,
                  fontSize: '0.75rem',
                  color: tokens.primary,
                  alignItems: 'flex-start',
                },
                '& .MuiOutlinedInput-input': { px: 1.5, py: 1.25, lineHeight: 1.5 },
              }}
            />

            {text.trim().length > 0 && (
              <Typography sx={{ fontSize: '0.75rem', color: tokens.muted, lineHeight: 1.45 }}>
                {preview.entries.length} variable{preview.entries.length === 1 ? '' : 's'} ready
                {preview.skipped.length > 0
                  ? ` · ${preview.skipped.length} line${preview.skipped.length === 1 ? '' : 's'} skipped`
                  : ''}
              </Typography>
            )}

            {preview.skipped.length > 0 && (
              <Alert severity="warning" variant="quiet" sx={{ alignItems: 'flex-start', py: 1.25 }}>
                <Stack spacing={1} sx={{ width: '100%', minWidth: 0 }}>
                  <Typography sx={{ fontSize: '0.75rem', fontWeight: 600, lineHeight: 1.45 }}>
                    Skipped {preview.skipped.length} line
                    {preview.skipped.length === 1 ? '' : 's'}
                  </Typography>
                  <Box
                    component="ul"
                    sx={{
                      m: 0,
                      pl: 2.25,
                      maxHeight: 140,
                      overflow: 'auto',
                      '& > li': { mb: 0.75, '&:last-child': { mb: 0 } },
                    }}
                  >
                    {preview.skipped.map((item) => (
                      <Box component="li" key={`${item.line}-${item.reason}-${item.raw}`}>
                        <Typography
                          sx={{ fontSize: '0.75rem', color: tokens.primary, lineHeight: 1.45 }}
                        >
                          <Box component="span" sx={{ fontWeight: 600 }}>
                            Line {item.line}
                          </Box>
                          {' — '}
                          {item.reason}
                        </Typography>
                        <Typography
                          component="span"
                          sx={{
                            display: 'block',
                            fontFamily: workspaceFontFamily.mono,
                            fontSize: '0.6875rem',
                            color: tokens.muted,
                            lineHeight: 1.4,
                            mt: 0.25,
                            wordBreak: 'break-all',
                          }}
                        >
                          {truncateSkippedLine(item.raw)}
                        </Typography>
                      </Box>
                    ))}
                  </Box>
                </Stack>
              </Alert>
            )}

            {submitError && (
              <Typography sx={{ fontSize: '0.75rem', color: semanticTokens.danger, lineHeight: 1.4 }}>
                {submitError}
              </Typography>
            )}
          </Stack>
        </DialogContent>

        <DialogActions sx={{ px: 3, pb: 2.5, pt: 1, gap: 1 }}>
          <Button
            type="button"
            onClick={handleClose}
            disabled={isImporting}
            variant="text"
            sx={{
              color: tokens.muted,
              textTransform: 'none',
              fontWeight: 500,
              fontSize: '0.8125rem',
              borderRadius: 2,
              px: 1.5,
              py: 0.625,
              '&:hover': { bgcolor: tokens.rowHover, color: tokens.primary },
            }}
          >
            Cancel
          </Button>
          <Button
            type="submit"
            variant="pill"
            color="primary"
            disabled={isImporting || preview.entries.length === 0}
            sx={{ minWidth: 112 }}
          >
            {isImporting ? 'Importing…' : 'Import'}
          </Button>
        </DialogActions>
      </form>
    </Dialog>
  )
}

function truncateSkippedLine(raw: string, maxLength = 96): string {
  const trimmed = raw.trim()
  if (trimmed.length <= maxLength) return trimmed
  return `${trimmed.slice(0, maxLength - 1)}…`
}
