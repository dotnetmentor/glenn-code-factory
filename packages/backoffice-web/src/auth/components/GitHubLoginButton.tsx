import { Button } from '@mui/material'
import GitHubIcon from '@mui/icons-material/GitHub'

interface GitHubLoginButtonProps {
  /** Where the user should land after the GitHub identity round-trip. Defaults to "/". */
  redirectTo?: string
  /** Optional override for button text — defaults to "Sign in with GitHub". */
  label?: string
  fullWidth?: boolean
}

/**
 * Triggers the GitHub user-identity OAuth flow. We use a top-level navigation
 * (window.location.href) — NOT fetch — because the backend sets a state cookie
 * that has to survive the GitHub round-trip; only a real navigation does that.
 */
export function GitHubLoginButton({
  redirectTo = '/',
  label = 'Sign in with GitHub',
  fullWidth = true,
}: GitHubLoginButtonProps) {
  const handleClick = () => {
    const params = new URLSearchParams({ redirectTo })
    window.location.href = `/api/github/login?${params.toString()}`
  }

  return (
    <Button
      variant="outlined"
      color="inherit"
      fullWidth={fullWidth}
      onClick={handleClick}
      startIcon={<GitHubIcon />}
      size="large"
      sx={{
        py: 1.5,
        fontSize: '0.9375rem',
        textTransform: 'none',
        borderColor: 'divider',
        color: 'text.primary',
        '&:hover': {
          borderColor: 'text.secondary',
          bgcolor: 'action.hover',
        },
      }}
    >
      {label}
    </Button>
  )
}
