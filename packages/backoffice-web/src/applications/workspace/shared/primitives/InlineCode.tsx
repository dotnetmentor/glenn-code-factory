/**
 * InlineCode — workspace-themed {@code <code>} chip used for filenames,
 * commit hashes, slugs, IDs, env vars, and any inline literal that should
 * read as "this is a value, not prose".
 *
 * <p>Visually a tinted hairline rectangle with mono digits inside. Matches
 * the {@code codeBg} / {@code codeBorder} tokens so the chip flips light → dark
 * with the workspace.
 *
 * <pre>
 *   Read &lt;InlineCode&gt;package.json&lt;/InlineCode&gt; before editing.
 * </pre>
 *
 * <p>Renders a semantic {@code <code>} element so screen-readers and copy-paste
 * preserve the "this is code" intent.
 */
import { Box } from '@mui/material'
import {
  surfaceTokens,
  workspaceFontFamily,
  workspaceText,
} from '../designTokens'

export interface InlineCodeProps {
  children: React.ReactNode
  /** Size override — {@code 'sm'} is 11px, {@code 'md'} (default) is 12px. */
  size?: 'sm' | 'md'
  /** Slightly bolder weight + brighter color — useful when the chip is the focus of the line. */
  emphasis?: boolean
}

export function InlineCode({ children, size = 'md', emphasis = false }: InlineCodeProps) {
  const fontSize = size === 'sm' ? '0.6875rem' : '0.75rem'
  return (
    <Box
      component="code"
      sx={{
        display: 'inline-flex',
        alignItems: 'center',
        padding: '1px 6px',
        borderRadius: '4px',
        backgroundColor: surfaceTokens.codeBg,
        border: `1px solid ${surfaceTokens.codeBorder}`,
        color: emphasis ? workspaceText.primary : workspaceText.muted,
        fontFamily: workspaceFontFamily.mono,
        fontSize,
        fontWeight: emphasis ? 600 : 500,
        lineHeight: 1.45,
        letterSpacing: 0,
        verticalAlign: 'baseline',
        wordBreak: 'break-all',
      }}
    >
      {children}
    </Box>
  )
}
