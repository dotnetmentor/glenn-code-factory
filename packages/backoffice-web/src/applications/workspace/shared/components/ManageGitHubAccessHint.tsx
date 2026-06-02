import OpenInNewIcon from '@mui/icons-material/OpenInNew'
import { Box, Typography } from '@mui/material'
import { captionSx, workspaceAccent, workspaceFontFamily } from '../designTokens'

interface ManageGitHubAccessHintProps {
  url: string
}

/**
 * Muted link to GitHub's installation repository-access page when a repo
 * is missing from our list after the user granted the app more repos.
 */
export function ManageGitHubAccessHint({ url }: ManageGitHubAccessHintProps) {
  return (
    <Typography sx={{ ...captionSx, fontSize: '0.8125rem' }}>
      Can&apos;t see your repository?{' '}
      <Box
        component="a"
        href={url}
        target="_blank"
        rel="noopener noreferrer"
        sx={{
          color: workspaceAccent.ink,
          fontFamily: workspaceFontFamily.sans,
          fontWeight: 500,
          textDecoration: 'none',
          display: 'inline-flex',
          alignItems: 'center',
          gap: 0.25,
          '&:hover': { textDecoration: 'underline' },
        }}
      >
        Modify access
        <OpenInNewIcon sx={{ fontSize: 12 }} />
      </Box>
    </Typography>
  )
}
