import {
  Box,
  Button,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  Typography,
} from '@mui/material'
import {
  chromeTokens,
  semanticTokens,
  surfaceTokens,
  workspaceFontFamily,
} from '../../../../shared/designTokens'
import type { EnvVarScope } from './envVarTypes'

const tokens = {
  canvas: surfaceTokens.canvasBg,
  primary: surfaceTokens.textPrimary,
  muted: surfaceTokens.textMuted,
  hairline: surfaceTokens.hairline,
  danger: semanticTokens.danger,
  rowHover: chromeTokens.rowHover,
} as const

export interface DeleteEnvVarDialogProps {
  open: boolean
  envKey: string | null
  scope: EnvVarScope
  isDeleting: boolean
  onClose: () => void
  onConfirm: () => void
}

export function DeleteEnvVarDialog({
  open,
  envKey,
  scope,
  isDeleting,
  onClose,
  onConfirm,
}: DeleteEnvVarDialogProps) {
  const isBranchOverride = scope === 'branch'

  return (
    <Dialog
      open={open}
      onClose={() => {
        if (!isDeleting) onClose()
      }}
      maxWidth={false}
      slotProps={{
        paper: {
          sx: {
            width: '100%',
            maxWidth: 420,
            bgcolor: tokens.canvas,
            border: `1px solid ${tokens.hairline}`,
            borderRadius: '12px',
            boxShadow: '0 20px 50px rgba(0,0,0,0.12)',
            backgroundImage: 'none',
          },
        },
      }}
    >
      <DialogTitle
        sx={{
          fontSize: '0.875rem',
          fontWeight: 600,
          color: tokens.primary,
          px: 3,
          pt: 2.5,
          pb: 1.5,
        }}
      >
        Remove variable
      </DialogTitle>
      <DialogContent sx={{ px: 3, pb: 2.5, pt: 0 }}>
        <Typography sx={{ fontSize: '0.8125rem', color: tokens.muted, lineHeight: 1.5 }}>
          {isBranchOverride ? (
            <>
              This removes the branch override for{' '}
              <KeyMono>{envKey}</KeyMono>. The project default value will apply
              on this branch again.
            </>
          ) : (
            <>
              This removes{' '}
              <KeyMono>{envKey}</KeyMono> from the project. Every branch loses
              this value unless it has its own override.
            </>
          )}
        </Typography>
      </DialogContent>
      <DialogActions sx={{ px: 3, pb: 2.5, pt: 1, gap: 1 }}>
        <Button
          type="button"
          onClick={onClose}
          disabled={isDeleting}
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
          type="button"
          onClick={onConfirm}
          disabled={isDeleting}
          variant="pill"
          color="error"
          sx={{ minWidth: 96 }}
        >
          {isDeleting ? 'Removing…' : 'Remove'}
        </Button>
      </DialogActions>
    </Dialog>
  )
}

function KeyMono({ children }: { children: React.ReactNode }) {
  return (
    <Box
      component="span"
      sx={{
        fontFamily: workspaceFontFamily.mono,
        color: tokens.primary,
        fontSize: '0.8125rem',
      }}
    >
      {children}
    </Box>
  )
}
