import { useMemo } from 'react'
import ReactMarkdown from 'react-markdown'
import remarkGfm from 'remark-gfm'
import { Box, Typography, Link, type Theme } from '@mui/material'
import { useTheme, alpha } from '@mui/material/styles'
import type { Components } from 'react-markdown'

interface MarkdownContentProps {
  content: string
}

function buildComponents(theme: Theme): Components {
  return {
    p: ({ children }) => (
      <Typography
        component="p"
        sx={{
          fontSize: '0.8125rem',
          lineHeight: 1.7,
          color: 'text.primary',
          my: 0.75,
          '&:first-of-type': { mt: 0 },
          '&:last-of-type': { mb: 0 },
        }}
      >
        {children}
      </Typography>
    ),
    strong: ({ children }) => (
      <Box component="strong" sx={{ fontWeight: 600 }}>
        {children}
      </Box>
    ),
    em: ({ children }) => (
      <Box component="em" sx={{ fontStyle: 'italic' }}>
        {children}
      </Box>
    ),
    a: ({ href, children }) => (
      <Link
        href={href}
        target="_blank"
        rel="noopener noreferrer"
        sx={{
          color: 'primary.main',
          textDecoration: 'none',
          '&:hover': { textDecoration: 'underline' },
        }}
      >
        {children}
      </Link>
    ),
    code: ({ className, children }) => {
      const isBlock = className?.includes('language-')
      if (isBlock) {
        return (
          <Box
            component="code"
            sx={{
              display: 'block',
              fontFamily: '"JetBrains Mono", "Fira Code", monospace',
              fontSize: '0.75rem',
              lineHeight: 1.6,
              color: theme.palette.text.primary,
            }}
          >
            {children}
          </Box>
        )
      }
      return (
        <Box
          component="code"
          sx={{
            fontFamily: '"JetBrains Mono", "Fira Code", monospace',
            fontSize: '0.75rem',
            backgroundColor: theme.palette.action.selected,
            color: theme.palette.text.primary,
            px: 0.6,
            py: 0.15,
            borderRadius: '4px',
            fontWeight: 500,
          }}
        >
          {children}
        </Box>
      )
    },
    pre: ({ children }) => (
      <Box
        component="pre"
        sx={{
          backgroundColor: theme.palette.grey[50],
          border: `1px solid ${theme.palette.divider}`,
          borderRadius: '8px',
          p: 1.5,
          my: 1,
          overflowX: 'auto',
          '&::-webkit-scrollbar': { height: 4 },
          '&::-webkit-scrollbar-thumb': {
            backgroundColor: theme.palette.grey[400],
            borderRadius: 2,
          },
        }}
      >
        {children}
      </Box>
    ),
    blockquote: ({ children }) => (
      <Box
        component="blockquote"
        sx={{
          borderLeft: `3px solid ${theme.palette.divider}`,
          pl: 1.5,
          ml: 0,
          my: 1,
          color: 'text.secondary',
        }}
      >
        {children}
      </Box>
    ),
    ul: ({ children }) => (
      <Box
        component="ul"
        sx={{
          pl: 2.5,
          my: 0.75,
          '& li': {
            fontSize: '0.8125rem',
            lineHeight: 1.7,
            color: 'text.primary',
            mb: 0.25,
          },
        }}
      >
        {children}
      </Box>
    ),
    ol: ({ children }) => (
      <Box
        component="ol"
        sx={{
          pl: 2.5,
          my: 0.75,
          '& li': {
            fontSize: '0.8125rem',
            lineHeight: 1.7,
            color: 'text.primary',
            mb: 0.25,
          },
        }}
      >
        {children}
      </Box>
    ),
    h1: ({ children }) => (
      <Typography
        variant="h5"
        component="h1"
        sx={{ mt: 2, mb: 1, fontWeight: 600 }}
      >
        {children}
      </Typography>
    ),
    h2: ({ children }) => (
      <Typography
        variant="h6"
        component="h2"
        sx={{ mt: 1.5, mb: 0.75, fontWeight: 600 }}
      >
        {children}
      </Typography>
    ),
    h3: ({ children }) => (
      <Typography
        component="h3"
        sx={{
          mt: 1.5,
          mb: 0.5,
          fontWeight: 600,
          fontSize: '0.875rem',
        }}
      >
        {children}
      </Typography>
    ),
    h4: ({ children }) => (
      <Typography
        component="h4"
        sx={{
          mt: 1,
          mb: 0.5,
          fontWeight: 600,
          fontSize: '0.8125rem',
        }}
      >
        {children}
      </Typography>
    ),
    table: ({ children }) => (
      <Box
        sx={{
          overflowX: 'auto',
          my: 1,
          border: `1px solid ${theme.palette.divider}`,
          borderRadius: '8px',
          '&::-webkit-scrollbar': { height: 4 },
          '&::-webkit-scrollbar-thumb': {
            backgroundColor: theme.palette.grey[400],
            borderRadius: 2,
          },
        }}
      >
        <Box
          component="table"
          sx={{
            width: '100%',
            borderCollapse: 'collapse',
            fontSize: '0.75rem',
          }}
        >
          {children}
        </Box>
      </Box>
    ),
    thead: ({ children }) => (
      <Box component="thead" sx={{ backgroundColor: theme.palette.grey[50] }}>
        {children}
      </Box>
    ),
    th: ({ children }) => (
      <Box
        component="th"
        sx={{
          textAlign: 'left',
          px: 1.5,
          py: 0.75,
          fontWeight: 500,
          fontSize: '0.6875rem',
          textTransform: 'uppercase',
          letterSpacing: '0.05em',
          color: theme.palette.text.secondary,
          borderBottom: `1px solid ${theme.palette.divider}`,
          whiteSpace: 'nowrap',
        }}
      >
        {children}
      </Box>
    ),
    td: ({ children }) => (
      <Box
        component="td"
        sx={{
          px: 1.5,
          py: 0.75,
          fontSize: '0.75rem',
          color: theme.palette.text.primary,
          borderBottom: `1px solid ${alpha(theme.palette.divider, 0.5)}`,
        }}
      >
        {children}
      </Box>
    ),
    hr: () => (
      <Box
        component="hr"
        sx={{
          border: 'none',
          borderTop: `1px solid ${theme.palette.divider}`,
          my: 1.5,
        }}
      />
    ),
  }
}

export function MarkdownContent({ content }: MarkdownContentProps) {
  const theme = useTheme()
  const components = useMemo(() => buildComponents(theme), [theme])

  return (
    <Box sx={{ '& > *:first-of-type': { mt: 0 }, '& > *:last-child': { mb: 0 } }}>
      <ReactMarkdown remarkPlugins={[remarkGfm]} components={components}>
        {content}
      </ReactMarkdown>
    </Box>
  )
}
