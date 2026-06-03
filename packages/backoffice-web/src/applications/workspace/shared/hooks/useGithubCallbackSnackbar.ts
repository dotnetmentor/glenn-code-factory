import { useEffect } from 'react'
import { useSearchParams } from 'react-router-dom'
import { useNotification } from '../../../shared/contexts/NotificationContext'

/**
 * Surfaces one-time snackbars for GitHub OAuth round-trips that land on the
 * workspace home with {@code ?install=} or {@code ?reauth=} query params,
 * then strips those params so a refresh does not re-trigger the toast.
 */
export function useGithubCallbackSnackbar() {
  const [searchParams, setSearchParams] = useSearchParams()
  const { showSuccess, showError, showInfo } = useNotification()

  useEffect(() => {
    const installResult = searchParams.get('install')
    const reauthResult = searchParams.get('reauth')
    if (!installResult && !reauthResult) return

    if (installResult === 'success') {
      showSuccess('GitHub connected.')
    } else if (installResult === 'pending') {
      showInfo('GitHub installation is pending admin approval.')
    } else if (installResult === 'cancelled') {
      showError('GitHub installation was cancelled.')
    } else if (installResult === 'conflict') {
      // The chosen GitHub account is already connected to another workspace.
      // A GitHub App installs once per account, so it can only attach to one
      // workspace at a time — name the account + workspace so the fix is clear.
      const account = searchParams.get('conflictAccount')
      const workspace = searchParams.get('conflictWorkspace')
      const accountLabel = account ? `“${account}”` : 'That GitHub account'
      const workspaceLabel = workspace ? `the workspace “${workspace}”` : 'another workspace'
      showError(
        `${accountLabel} is already connected to ${workspaceLabel}. Disconnect it there first, or connect a different GitHub account.`,
      )
    } else if (installResult === 'error') {
      showError('GitHub installation failed. Please try again.')
    }
    // Unknown install values are ignored — still stripped below.

    if (reauthResult === 'success') {
      showSuccess('GitHub re-authorized.')
    } else if (reauthResult === 'error') {
      showError('Re-authorization failed. Please try again.')
    }

    const next = new URLSearchParams(searchParams)
    next.delete('install')
    next.delete('reauth')
    next.delete('conflictAccount')
    next.delete('conflictWorkspace')
    setSearchParams(next, { replace: true })
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])
}
