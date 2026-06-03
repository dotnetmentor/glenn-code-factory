import { useEffect, useState } from 'react'
import {
  Button,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  FormControlLabel,
  IconButton,
  InputAdornment,
  Stack,
  Switch,
  TextField,
  Tooltip,
  Typography,
} from '@mui/material'
import VisibilityOutlinedIcon from '@mui/icons-material/VisibilityOutlined'
import VisibilityOffOutlinedIcon from '@mui/icons-material/VisibilityOffOutlined'
import {
  chromeTokens,
  semanticTokens,
  surfaceTokens,
  workspaceFontFamily,
} from '../../../../shared/designTokens'
import { ENV_KEY_PATTERN, invalidEnvKeyMessage } from './envVarKey'
import { humaniseEnvVarApiError } from './envVarApiError'
import type { EnvVarScope } from './envVarTypes'

export { ENV_KEY_PATTERN } from './envVarKey'

const tokens = {
  canvas: surfaceTokens.canvasBg,
  surface: surfaceTokens.chromeBg,
  primary: surfaceTokens.textPrimary,
  muted: surfaceTokens.textMuted,
  hairline: surfaceTokens.hairline,
  accent: chromeTokens.accent,
  danger: semanticTokens.danger,
  rowHover: chromeTokens.rowHover,
} as const

export interface EnvVarDialogValues {
  /** Existing key when editing; the dialog locks the key field in that mode. */
  key: string
  value: string
  isSecret: boolean
}

export interface EnvVarDialogProps {
  open: boolean
  /** 'add' allows editing the key; 'edit' locks it. */
  mode: 'add' | 'edit'
  /** Where the value is stored — project default or branch override. */
  scope?: EnvVarScope
  /**
   * Seed values. For the "Set value" flow on a missing required var, pass the
   * key prefilled and {@code lockKey} true so the user only fills the value.
   */
  initial?: Partial<EnvVarDialogValues>
  /** Lock the key field even in add mode (prefilled-required flow). */
  lockKey?: boolean
  isSubmitting: boolean
  onClose: () => void
  onSubmit: (values: EnvVarDialogValues) => Promise<void>
}

/**
 * Add / Edit dialog for a single branch env var. Validates the key against
 * {@link ENV_KEY_PATTERN} with an inline error, requires a non-empty value, and
 * defaults the Secret switch ON. The key field is disabled when editing (or
 * when {@code lockKey} is set for the prefilled-required flow).
 */
export function EnvVarDialog({
  open,
  mode,
  scope = 'project',
  initial,
  lockKey,
  isSubmitting,
  onClose,
  onSubmit,
}: EnvVarDialogProps) {
  const [key, setKey] = useState('')
  const [value, setValue] = useState('')
  const [isSecret, setIsSecret] = useState(true)
  const [showValue, setShowValue] = useState(false)
  const [keyError, setKeyError] = useState<string | null>(null)
  const [submitError, setSubmitError] = useState<string | null>(null)

  // Reset the form whenever the dialog (re-)opens, seeding from `initial`.
  // Re-mask on every open so a previously-revealed value never leaks into the
  // next thing the user edits.
  useEffect(() => {
    if (!open) return
    setKey(initial?.key ?? '')
    setValue(initial?.value ?? '')
    setIsSecret(initial?.isSecret ?? true)
    setShowValue(false)
    setKeyError(null)
    setSubmitError(null)
  }, [open, initial?.key, initial?.value, initial?.isSecret])

  const keyLocked = mode === 'edit' || !!lockKey
  const isBranchOverride = scope === 'branch'
  const title =
    mode === 'edit'
      ? isBranchOverride
        ? 'Edit branch override'
        : 'Edit variable'
      : isBranchOverride
        ? 'Add branch override'
        : 'Add variable'
  const trimmedKey = key.trim()
  const trimmedValue = value
  const keyValid = ENV_KEY_PATTERN.test(trimmedKey)
  const canSubmit =
    !isSubmitting && keyValid && trimmedValue.length > 0

  const handleSubmit = async (e?: React.FormEvent) => {
    if (e) e.preventDefault()
    if (!keyValid) {
      setKeyError(invalidEnvKeyMessage(trimmedKey))
      return
    }
    if (trimmedValue.length === 0) return
    setSubmitError(null)
    try {
      await onSubmit({ key: trimmedKey, value: trimmedValue, isSecret })
    } catch (err) {
      setSubmitError(humaniseEnvVarApiError(err, trimmedKey))
    }
  }

  return (
    <Dialog
      open={open}
      onClose={() => {
        if (!isSubmitting) onClose()
      }}
      maxWidth={false}
      slotProps={{
        paper: {
          sx: {
            width: '100%',
            maxWidth: 460,
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
          {title}
        </DialogTitle>

        <DialogContent sx={{ px: 3, pb: 2.5, pt: 0 }}>
          <Stack spacing={1.75}>
            <Stack spacing={0.75}>
              <Eyebrow>Key</Eyebrow>
              <TextField
                autoFocus={!keyLocked}
                fullWidth
                size="small"
                value={key}
                placeholder="DATABASE_URL"
                disabled={keyLocked || isSubmitting}
                onChange={(e) => {
                  setKey(e.target.value)
                  setKeyError(null)
                }}
                error={!!keyError}
                inputProps={{ spellCheck: 'false', autoComplete: 'off' }}
                sx={fieldSx}
              />
              {keyError && (
                <Typography sx={{ fontSize: '0.75rem', color: tokens.danger, lineHeight: 1.4 }}>
                  {keyError}
                </Typography>
              )}
            </Stack>

            <Stack spacing={0.75}>
              <Eyebrow>Value</Eyebrow>
              <TextField
                autoFocus={keyLocked}
                fullWidth
                size="small"
                value={value}
                type={isSecret && !showValue ? 'password' : 'text'}
                placeholder="Enter a value"
                disabled={isSubmitting}
                onChange={(e) => setValue(e.target.value)}
                inputProps={{ spellCheck: 'false', autoComplete: 'off' }}
                InputProps={
                  isSecret
                    ? {
                        endAdornment: (
                          <InputAdornment position="end">
                            <Tooltip title={showValue ? 'Hide value' : 'Show value'}>
                              <IconButton
                                onClick={() => setShowValue((v) => !v)}
                                edge="end"
                                size="small"
                                aria-label={showValue ? 'Hide value' : 'Show value'}
                                tabIndex={-1}
                              >
                                {showValue ? (
                                  <VisibilityOffOutlinedIcon fontSize="small" />
                                ) : (
                                  <VisibilityOutlinedIcon fontSize="small" />
                                )}
                              </IconButton>
                            </Tooltip>
                          </InputAdornment>
                        ),
                      }
                    : undefined
                }
                sx={fieldSx}
              />
            </Stack>

            <FormControlLabel
              control={
                <Switch
                  checked={isSecret}
                  onChange={(_, checked) => setIsSecret(checked)}
                  disabled={isSubmitting || !isBranchOverride}
                  size="small"
                />
              }
              label={
                <Typography sx={{ fontSize: '0.8125rem', color: tokens.primary }}>
                  {isBranchOverride
                    ? 'Secret — value is encrypted and masked'
                    : 'Project variables are always stored as secrets'}
                </Typography>
              }
              sx={{ mx: 0 }}
            />

            {submitError && (
              <Typography sx={{ fontSize: '0.75rem', color: tokens.danger, lineHeight: 1.4 }}>
                {submitError}
              </Typography>
            )}
          </Stack>
        </DialogContent>

        <DialogActions sx={{ px: 3, pb: 2.5, pt: 1, gap: 1 }}>
          <Button
            type="button"
            onClick={onClose}
            disabled={isSubmitting}
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
            disabled={!canSubmit}
            sx={{ minWidth: 96 }}
          >
            {isSubmitting ? 'Saving…' : mode === 'edit' ? 'Save' : 'Add'}
          </Button>
        </DialogActions>
      </form>
    </Dialog>
  )
}

function Eyebrow({ children }: { children: React.ReactNode }) {
  return (
    <Typography
      component="span"
      sx={{
        fontSize: '0.6875rem',
        fontWeight: 600,
        letterSpacing: '0.08em',
        textTransform: 'uppercase',
        color: tokens.muted,
        lineHeight: 1.4,
      }}
    >
      {children}
    </Typography>
  )
}

const fieldSx = {
  '& .MuiOutlinedInput-root': {
    bgcolor: tokens.surface,
    borderRadius: 1.5,
    fontFamily: workspaceFontFamily.mono,
    fontSize: '0.8125rem',
    color: tokens.primary,
  },
  '& .MuiOutlinedInput-input': { px: 1.5, py: 1 },
  '& .MuiOutlinedInput-input::placeholder': { color: tokens.muted, opacity: 1 },
} as const
