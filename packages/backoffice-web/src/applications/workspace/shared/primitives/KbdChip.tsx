/**
 * KbdChip — tiny {@code <kbd>}-style chip for keyboard hints.
 *
 * <p>Used in the composer hint row ({@code ⌘ ↵}), command-palette items, and
 * anywhere a shortcut needs to read as "this is a key you press" without
 * shouting. Visually a faint hairline rectangle with mono digits inside.
 *
 * <pre>
 *   &lt;KbdChip&gt;⌘&lt;/KbdChip&gt;
 *   &lt;KbdChip&gt;↵&lt;/KbdChip&gt;
 * </pre>
 *
 * <p>Renders a semantic {@code <kbd>} element so screen-readers announce
 * "keyboard" rather than just reading the glyph as text.
 */
import { Box } from '@mui/material'
import {
  surfaceTokens,
  workspaceFontFamily,
  workspaceText,
} from '../designTokens'

export interface KbdChipProps {
  children: React.ReactNode
  /** Optional aria-label override (e.g. "Command key"). Defaults to the visible text. */
  ariaLabel?: string
}

export function KbdChip({ children, ariaLabel }: KbdChipProps) {
  return (
    <Box
      component="kbd"
      aria-label={ariaLabel}
      sx={{
        display: 'inline-flex',
        alignItems: 'center',
        justifyContent: 'center',
        minWidth: 18,
        height: 18,
        padding: '0 4px',
        borderRadius: '4px',
        backgroundColor: surfaceTokens.chipBg,
        border: `1px solid ${surfaceTokens.hairline}`,
        color: workspaceText.muted,
        fontFamily: workspaceFontFamily.mono,
        fontSize: '0.6875rem',
        fontWeight: 500,
        lineHeight: 1,
        letterSpacing: 0,
        fontVariantNumeric: 'tabular-nums',
        userSelect: 'none',
        // Sit on the baseline; <kbd> defaults to inline which adds descender wobble.
        verticalAlign: 'baseline',
      }}
    >
      {children}
    </Box>
  )
}
