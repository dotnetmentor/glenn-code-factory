import { createContext, useContext } from 'react'

export interface AuthUser {
  userId: string
  email: string
  roles: string[]
  firstName?: string | null
  lastName?: string | null
  phoneNumber?: string | null
}

export type OtpStep = 'idle' | 'email-sent' | 'verifying'

export interface AuthState {
  user: AuthUser | null
  isAuthenticated: boolean
  isLoading: boolean
  otpStep: OtpStep
  otpEmail: string | null
  error: string | null
  isSendingOtp: boolean
  isVerifyingOtp: boolean
  isLoggingIn: boolean
  isRegistering: boolean
}

export interface AuthActions {
  sendOtp: (email: string) => Promise<boolean>
  verifyOtp: (email: string, otpCode: string) => Promise<boolean>
  loginWithPassword: (email: string, password: string) => Promise<boolean>
  register: (email: string, password: string) => Promise<boolean>
  logout: () => Promise<void>
  clearError: () => void
  backToEmail: () => void
  refetchAuth: () => void
}

export type AuthContextType = AuthState & AuthActions

export const AuthContext = createContext<AuthContextType | null>(null)

export function useAuth(): AuthContextType {
  const ctx = useContext(AuthContext)
  if (!ctx) throw new Error('useAuth must be used within AuthProvider')
  return ctx
}
