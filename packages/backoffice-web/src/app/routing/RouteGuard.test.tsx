import { describe, it, expect, vi, afterEach } from 'vitest'
import { render, screen, cleanup } from '@testing-library/react'

import type { AuthContextType, AuthUser } from '../../auth/authContext'
import { ApplicationRole } from './types'

// Drive useAuth() per-test so we can flip the role membership without spinning
// up the real AuthProvider (which fires HTTP calls on mount).
const useAuthMock = vi.fn<() => AuthContextType>()
vi.mock('../../auth/authContext', async () => {
  const actual = await vi.importActual<typeof import('../../auth/authContext')>('../../auth/authContext')
  return {
    ...actual,
    useAuth: () => useAuthMock(),
  }
})

import { RouteGuard } from './RouteGuard'

afterEach(() => {
  cleanup()
  useAuthMock.mockReset()
})

function makeAuth(user: AuthUser | null): AuthContextType {
  return {
    user,
    isAuthenticated: user !== null,
    isLoading: false,
    otpStep: 'idle',
    otpEmail: null,
    error: null,
    isSendingOtp: false,
    isVerifyingOtp: false,
    isLoggingIn: false,
    isRegistering: false,
    sendOtp: vi.fn(),
    verifyOtp: vi.fn(),
    loginWithPassword: vi.fn(),
    register: vi.fn(),
    logout: vi.fn(),
    clearError: vi.fn(),
    backToEmail: vi.fn(),
    refetchAuth: vi.fn(),
  }
}

describe('RouteGuard', () => {
  it('renders children when no requiresRole is set', () => {
    useAuthMock.mockReturnValue(makeAuth(null))
    render(
      <RouteGuard>
        <div>protected child</div>
      </RouteGuard>,
    )
    expect(screen.getByText('protected child')).toBeInTheDocument()
  })

  it('hides a WorkspaceUser-required route from a user without that role', () => {
    useAuthMock.mockReturnValue(
      makeAuth({
        userId: 'u1',
        email: 'a@b.c',
        roles: ['SuperAdmin'],
      }),
    )
    render(
      <RouteGuard requiresRole={ApplicationRole.WorkspaceUser}>
        <div>workspace shell</div>
      </RouteGuard>,
    )
    expect(screen.queryByText('workspace shell')).not.toBeInTheDocument()
    expect(screen.getByText(/Access Denied/i)).toBeInTheDocument()
  })

  it('shows a WorkspaceUser-required route to a user with that role', () => {
    useAuthMock.mockReturnValue(
      makeAuth({
        userId: 'u1',
        email: 'a@b.c',
        roles: ['WorkspaceUser'],
      }),
    )
    render(
      <RouteGuard requiresRole={ApplicationRole.WorkspaceUser}>
        <div>workspace shell</div>
      </RouteGuard>,
    )
    expect(screen.getByText('workspace shell')).toBeInTheDocument()
  })

  it('blocks unauthenticated users from a role-protected route', () => {
    useAuthMock.mockReturnValue(makeAuth(null))
    render(
      <RouteGuard requiresRole={ApplicationRole.WorkspaceUser}>
        <div>workspace shell</div>
      </RouteGuard>,
    )
    expect(screen.queryByText('workspace shell')).not.toBeInTheDocument()
    expect(screen.getByText(/Access Denied/i)).toBeInTheDocument()
  })

  it('allows access when requiresAuth is explicitly false', () => {
    useAuthMock.mockReturnValue(makeAuth(null))
    render(
      <RouteGuard requiresAuth={false}>
        <div>public child</div>
      </RouteGuard>,
    )
    expect(screen.getByText('public child')).toBeInTheDocument()
  })
})
