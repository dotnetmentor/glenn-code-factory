/**
 * FlatSwitch — compact horizontal toggle used in Approvals, Runtime services,
 * and Project Settings.
 *
 * <p>The default MUI {@code Switch} is 38×24 with a chunky thumb shadow; the
 * workspace mock calls for a flatter, more typographic switch (32×18, thin
 * thumb, hairline border). This primitive replaces MUI Switch on workspace
 * surfaces — anywhere else MUI Switch is still fine.
 *
 * <p>Named {@code FlatSwitch} to avoid collisions with {@code MuiSwitch} when
 * both are imported in the same module.
 */
import { Box } from '@mui/material'
import {
  surfaceTokens,
  workspaceText,
} from '../designTokens'

export interface FlatSwitchProps {
  checked: boolean
  onChange: (next: boolean) => void
  disabled?: boolean
  ariaLabel?: string
  /** Pixel width override. Defaults to 32. Track height scales to width × 0.5625. */
  width?: number
}

export function FlatSwitch({
  checked,
  onChange,
  disabled,
  ariaLabel,
  width = 32,
}: FlatSwitchProps) {
  const trackHeight = Math.round(width * 0.5625)
  const thumbSize = trackHeight - 4
  const travel = width - thumbSize - 4

  return (
    <Box
      role="switch"
      aria-checked={checked}
      aria-label={ariaLabel}
      tabIndex={disabled ? -1 : 0}
      onClick={() => {
        if (!disabled) onChange(!checked)
      }}
      onKeyDown={(e) => {
        if (disabled) return
        if (e.key === ' ' || e.key === 'Enter') {
          e.preventDefault()
          onChange(!checked)
        }
      }}
      sx={{
        position: 'relative',
        display: 'inline-block',
        width,
        height: trackHeight,
        borderRadius: 999,
        backgroundColor: checked ? workspaceText.primary : surfaceTokens.chipHoverBg,
        border: `1px solid ${checked ? workspaceText.primary : surfaceTokens.hairlineStrong}`,
        cursor: disabled ? 'not-allowed' : 'pointer',
        opacity: disabled ? 0.5 : 1,
        outline: 'none',
        transition: 'background-color 160ms ease, border-color 160ms ease',
        flexShrink: 0,
        '&:focus-visible': {
          boxShadow: `0 0 0 2px ${surfaceTokens.canvasBg}, 0 0 0 4px ${workspaceText.primary}`,
        },
      }}
    >
      <Box
        aria-hidden
        sx={{
          position: 'absolute',
          top: 1,
          left: checked ? travel + 1 : 1,
          width: thumbSize,
          height: thumbSize,
          borderRadius: '50%',
          backgroundColor: checked ? surfaceTokens.surface : surfaceTokens.surface,
          boxShadow: '0 1px 2px rgba(0,0,0,0.12)',
          transition: 'left 160ms cubic-bezier(.2,.7,.4,1)',
        }}
      />
    </Box>
  )
}
