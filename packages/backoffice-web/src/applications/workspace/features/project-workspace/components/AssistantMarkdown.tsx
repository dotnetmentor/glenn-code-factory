import { memo } from 'react'
import { Box } from '@mui/material'
import ReactMarkdown, { type Components } from 'react-markdown'
import remarkGfm from 'remark-gfm'
import rehypeHighlight from 'rehype-highlight'
// Side-effect import: a light GitHub theme for fenced code blocks. Restrained
// greys + a single warm accent — sits cleanly on the paper-toned chrome below
// without any dark-mode/dimming work. (The shell is light-only by design.)
import 'highlight.js/styles/github.css'

import {
  workspaceAccent,
  workspaceColors,
  workspaceFontFamily,
  workspaceText,
} from '../../../shared/designTokens'

const palette = {
  textPrimary: workspaceText.primary,
  textMuted: workspaceText.muted,
  accent: workspaceAccent.ink,
  chrome: workspaceColors.chromeBg,
  hairline: workspaceColors.hairline,
  hairlineStrong: workspaceColors.hairlineStrong,
  inlineCodeBg: workspaceColors.chipBg,
} as const

const bodyFont = workspaceFontFamily.sans
const monoFont = workspaceFontFamily.mono

// ── Component overrides ────────────────────────────────────────────────────
//
// Every node react-markdown emits is mapped to an MUI {@code Box} (or its
// most appropriate semantic element) so we keep full control of typography
// + spacing without leaking class names into the bubble.
const markdownComponents: Components = {
  p({ children, ...props }) {
    return (
      <Box
        component="p"
        sx={{
          m: 0,
          mb: 1,
          '&:last-child': { mb: 0 },
          fontSize: '0.9375rem',
          lineHeight: 1.55,
          letterSpacing: '-0.005em',
          color: palette.textPrimary,
          fontFamily: bodyFont,
          wordBreak: 'break-word',
        }}
        {...props}
      >
        {children}
      </Box>
    )
  },
  a({ children, href, ...props }) {
    return (
      <Box
        component="a"
        href={href}
        target="_blank"
        rel="noopener noreferrer"
        sx={{
          color: palette.accent,
          textDecoration: 'none',
          '&:hover': { textDecoration: 'underline' },
        }}
        {...props}
      >
        {children}
      </Box>
    )
  },
  strong({ children, ...props }) {
    return (
      <Box
        component="strong"
        sx={{ fontWeight: 600, color: palette.textPrimary }}
        {...props}
      >
        {children}
      </Box>
    )
  },
  em({ children, ...props }) {
    return (
      <Box
        component="em"
        sx={{ fontStyle: 'italic', color: palette.textPrimary }}
        {...props}
      >
        {children}
      </Box>
    )
  },
  h1({ children, ...props }) {
    return (
      <Box
        component="h1"
        sx={{
          fontSize: '1.125rem',
          fontWeight: 600,
          letterSpacing: '-0.01em',
          color: palette.textPrimary,
          mt: 1.5,
          mb: 0.5,
          '&:first-of-type': { mt: 0 },
        }}
        {...props}
      >
        {children}
      </Box>
    )
  },
  h2({ children, ...props }) {
    return (
      <Box
        component="h2"
        sx={{
          fontSize: '1.0625rem',
          fontWeight: 600,
          letterSpacing: '-0.01em',
          color: palette.textPrimary,
          mt: 1.5,
          mb: 0.5,
          '&:first-of-type': { mt: 0 },
        }}
        {...props}
      >
        {children}
      </Box>
    )
  },
  h3({ children, ...props }) {
    return (
      <Box
        component="h3"
        sx={{
          fontSize: '1rem',
          fontWeight: 600,
          letterSpacing: '-0.01em',
          color: palette.textPrimary,
          mt: 1.5,
          mb: 0.5,
          '&:first-of-type': { mt: 0 },
        }}
        {...props}
      >
        {children}
      </Box>
    )
  },
  h4({ children, ...props }) {
    return (
      <Box
        component="h4"
        sx={{
          fontSize: '1rem',
          fontWeight: 600,
          letterSpacing: '-0.01em',
          color: palette.textPrimary,
          mt: 1.5,
          mb: 0.5,
          '&:first-of-type': { mt: 0 },
        }}
        {...props}
      >
        {children}
      </Box>
    )
  },
  ul({ children, ...props }) {
    return (
      <Box
        component="ul"
        sx={{
          pl: 3,
          my: 1,
          listStyleType: 'disc',
          '&:last-child': { mb: 0 },
        }}
        {...props}
      >
        {children}
      </Box>
    )
  },
  ol({ children, ...props }) {
    return (
      <Box
        component="ol"
        sx={{
          pl: 3,
          my: 1,
          listStyleType: 'decimal',
          '&:last-child': { mb: 0 },
        }}
        {...props}
      >
        {children}
      </Box>
    )
  },
  li({ children, ...props }) {
    return (
      <Box
        component="li"
        sx={{
          mb: 0.25,
          '&:last-child': { mb: 0 },
          fontSize: '0.9375rem',
          lineHeight: 1.55,
          letterSpacing: '-0.005em',
          color: palette.textPrimary,
          fontFamily: bodyFont,
        }}
        {...props}
      >
        {children}
      </Box>
    )
  },
  blockquote({ children, ...props }) {
    return (
      <Box
        component="blockquote"
        sx={{
          borderLeft: `3px solid ${palette.accent}`,
          opacity: 0.6,
          pl: 2,
          my: 1,
          color: palette.textMuted,
          fontStyle: 'italic',
          ml: 0,
          mr: 0,
        }}
        {...props}
      >
        {children}
      </Box>
    )
  },
  code({ className, children, ...props }) {
    // react-markdown v9+ dropped the `inline` prop — fenced blocks instead
    // carry a `language-X` class (set by remark from the fence info string).
    // Anything without `language-` is therefore inline.
    const isFenced = /language-/.test(className ?? '')
    if (!isFenced) {
      return (
        <Box
          component="code"
          sx={{
            fontFamily: monoFont,
            fontSize: '0.85em',
            backgroundColor: palette.inlineCodeBg,
            padding: '0.5px 6px',
            borderRadius: '4px',
            wordBreak: 'break-word',
          }}
          {...props}
        >
          {children}
        </Box>
      )
    }
    // Fenced code: let highlight.js own the <code> via the language class so
    // the rehype-highlight plugin can inject .hljs token spans.
    return (
      <code className={className} {...props}>
        {children}
      </code>
    )
  },
  pre({ children, ...props }) {
    return (
      <Box
        component="pre"
        sx={{
          backgroundColor: palette.chrome,
          border: `1px solid ${palette.hairline}`,
          borderRadius: '8px',
          my: 1,
          p: 1.5,
          overflowX: 'auto',
          fontFamily: monoFont,
          fontSize: '0.85rem',
          lineHeight: 1.5,
          m: 0,
          mt: 1,
          mb: 1,
          '&:last-child': { mb: 0 },
          '&:first-of-type': { mt: 0 },
          // The github.css theme paints its own background; override so the
          // chrome tone shows through and the block reads as part of the
          // bubble rather than a transplanted GitHub artefact.
          '& code.hljs, & code': {
            backgroundColor: 'transparent',
            padding: 0,
            fontSize: 'inherit',
          },
        }}
        {...props}
      >
        {children}
      </Box>
    )
  },
  hr({ ...props }) {
    return (
      <Box
        component="hr"
        sx={{
          border: 0,
          borderTop: `1px solid ${palette.hairline}`,
          my: 2,
        }}
        {...props}
      />
    )
  },
  table({ children, ...props }) {
    return (
      <Box
        component="table"
        sx={{
          borderCollapse: 'collapse',
          width: '100%',
          fontSize: '0.875rem',
          my: 1,
          fontFamily: bodyFont,
          color: palette.textPrimary,
        }}
        {...props}
      >
        {children}
      </Box>
    )
  },
  th({ children, ...props }) {
    return (
      <Box
        component="th"
        sx={{
          textAlign: 'left',
          borderBottom: `1px solid ${palette.hairlineStrong}`,
          padding: '0.5rem 0.75rem',
          fontWeight: 600,
        }}
        {...props}
      >
        {children}
      </Box>
    )
  },
  td({ children, ...props }) {
    return (
      <Box
        component="td"
        sx={{
          borderBottom: `1px solid ${palette.hairline}`,
          padding: '0.5rem 0.75rem',
        }}
        {...props}
      >
        {children}
      </Box>
    )
  },
  img({ alt, ...props }) {
    return (
      <Box
        component="img"
        alt={alt}
        sx={{
          maxWidth: '100%',
          borderRadius: '6px',
          my: 1,
        }}
        {...props}
      />
    )
  },
}

// ── Public component ───────────────────────────────────────────────────────

/**
 * Renders an assistant message as markdown using the calm paper-tone palette
 * shared with the rest of the workspace shell. Supports GitHub-flavoured
 * markdown (tables, strikethrough, task lists, autolinks) plus syntax-
 * highlighted fenced code blocks via {@code rehype-highlight}.
 *
 * <p>The component is intentionally minimal — no theme provider, no context,
 * no extra wrappers. All typography is owned by MUI {@code sx} overrides on
 * the elements react-markdown emits. Raw HTML is NOT rendered: react-markdown
 * sanitises by default and we deliberately do not pull in {@code rehype-raw}.
 */
// Memoized so that re-renders of the surrounding bubble (and grandparent
// ChatCanvas) don't re-parse the markdown when the prose itself hasn't
// changed. Parsing + syntax-highlighting cost grows with conversation length;
// without this guard every keystroke in the composer above would re-walk every
// assistant message in the transcript.
function AssistantMarkdownImpl({ text }: { text: string }) {
  return (
    <ReactMarkdown
      remarkPlugins={[remarkGfm]}
      rehypePlugins={[rehypeHighlight]}
      components={markdownComponents}
    >
      {text}
    </ReactMarkdown>
  )
}

export const AssistantMarkdown = memo(AssistantMarkdownImpl)

export default AssistantMarkdown
