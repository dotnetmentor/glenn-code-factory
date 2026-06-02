import { renderHook } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'

const showSuccess = vi.fn()
const showError = vi.fn()
const showInfo = vi.fn()
const setSearchParams = vi.fn()

let searchParams = new URLSearchParams()

vi.mock('../../../../shared/contexts/NotificationContext', () => ({
  useNotification: () => ({ showSuccess, showError, showInfo }),
}))

vi.mock('react-router-dom', () => ({
  useSearchParams: () => [searchParams, setSearchParams],
}))

import { useGithubCallbackSnackbar } from '../useGithubCallbackSnackbar'

beforeEach(() => {
  searchParams = new URLSearchParams()
  vi.clearAllMocks()
})

describe('useGithubCallbackSnackbar', () => {
  it('shows install success toast and strips the param', () => {
    searchParams = new URLSearchParams('install=success')

    renderHook(() => useGithubCallbackSnackbar())

    expect(showSuccess).toHaveBeenCalledWith('GitHub connected.')
    expect(setSearchParams).toHaveBeenCalledWith(expect.any(URLSearchParams), { replace: true })
    const next = setSearchParams.mock.calls[0]![0] as URLSearchParams
    expect(next.get('install')).toBeNull()
  })

  it('shows reauth error toast and strips the param', () => {
    searchParams = new URLSearchParams('reauth=error')

    renderHook(() => useGithubCallbackSnackbar())

    expect(showError).toHaveBeenCalledWith('Re-authorization failed. Please try again.')
    const next = setSearchParams.mock.calls[0]![0] as URLSearchParams
    expect(next.get('reauth')).toBeNull()
  })

  it('does nothing when no OAuth callback params are present', () => {
    renderHook(() => useGithubCallbackSnackbar())

    expect(showSuccess).not.toHaveBeenCalled()
    expect(showError).not.toHaveBeenCalled()
    expect(showInfo).not.toHaveBeenCalled()
    expect(setSearchParams).not.toHaveBeenCalled()
  })
})
