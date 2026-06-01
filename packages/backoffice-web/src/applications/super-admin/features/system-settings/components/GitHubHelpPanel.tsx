import { Alert, Link } from '@mui/material'

/**
 * Doc-link banner shown above the GitHub category fields.
 * Points the operator at the GitHub App creation flow.
 */
export function GitHubHelpPanel() {
  return (
    <Alert severity="info">
      Need credentials? Create a GitHub App at{' '}
      <Link
        href="https://github.com/settings/apps/new"
        target="_blank"
        rel="noreferrer"
      >
        github.com/settings/apps/new
      </Link>{' '}
      — see the help text on each field below for where to find each value.
    </Alert>
  )
}
