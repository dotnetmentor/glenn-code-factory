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
    setSearchParams(next, { replace: true })
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])
}
