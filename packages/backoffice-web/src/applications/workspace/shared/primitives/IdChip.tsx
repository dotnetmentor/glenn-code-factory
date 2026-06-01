/**
 * IdChip — compact mono chip for runtime / machine / resource identifiers.
 *
 * <p>The canonical "this short opaque id is a value" chip used in the debug
 * panel header next to the RUNTIME caption. Distinct from {@link InlineCode}:
 * it sits on the neutral {@code chipBg} surface with a hairline border, a
 * tighter 5px radius, and primary (not muted) text so a runtime id reads as a
 * confident, copyable token rather than inline prose. Mirrors the prototype's
 * {@code DrawerHeader} code chip.
 *
 * <pre>
 *   &lt;IdChip&gt;97aab608&lt;/IdChip&gt;
 * </pre>
 *
 * <p>Renders a semantic {@code <code>} element so screen-readers and
 * copy-paste preserve the "this is an identifier" intent. Colors come from
 * {@link surfaceTokens}, so the chip flips light → dark with the workspace.
 */
import { Box } from '@mui/material'
import {
  surfaceTokens,
  workspaceFontFamily,
  workspaceText,
} from '../designTokens'

export interface IdChipProps {
  children: React.ReactNode
  /** Optional tooltip / accessible title forwarded to the chip. */
  title?: string
}

export function IdChip({ children, title }: IdChipProps) {
  return (
    <Box
      component="code"
      title={title}
      sx={{
        display: 'inline-flex',
        alignItems: 'center',
        padding: '2px 6px',
        borderRadius: '5px',
        backgroundColor: surfaceTokens.chipBg,
        border: `1px solid ${surfaceTokens.hairline}`,
        color: workspaceText.primary,
        fontFamily: workspaceFontFamily.mono,
        fontSize: '0.71875rem',
        fontWeight: 500,
        lineHeight: 1.45,
        letterSpacing: 0,
        fontVariantNumeric: 'tabular-nums',
        verticalAlign: 'baseline',
        wordBreak: 'break-all',
      }}
    >
      {children}
    </Box>
  )
}
