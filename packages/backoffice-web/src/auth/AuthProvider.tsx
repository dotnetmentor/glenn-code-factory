import { useMemo, useState, useEffect } from 'react'
import type { PropsWithChildren } from 'react'
import { usePostApiAuthSendOtp, usePostApiAuthVerifyOtp, useGetApiAuthMe, usePostApiAuthLogout, usePostApiAuthLogin, usePostApiAuthRegister } from '../api/queries-commands'
import { useQueryClient } from '@tanstack/react-query'
import { AuthContext, type AuthContextType, type AuthState, type AuthUser } from './authContext'

interface DerivedAuthState {
  isLoading: boolean
  isAuthenticated: boolean
  user: AuthUser | null
}

export function AuthProvider({ children }: PropsWithChildren) {
  const queryClient = useQueryClient()

  const [state, setState] = useState<AuthState>({
    user: null,
    isAuthenticated: false,
    isLoading: true,
    otpStep: 'idle',
    otpEmail: null,
    error: null,
    isSendingOtp: false,
    isVerifyingOtp: false,
    isLoggingIn: false,
    isRegistering: false,
  })

  const me = useGetApiAuthMe({
    query: { retry: false, staleTime: 5 * 60 * 1000, gcTime: 10 * 60 * 1000 },
  })

  const sendOtpMutation = usePostApiAuthSendOtp({})
  const verifyOtpMutation = usePostApiAuthVerifyOtp({})
  const logoutMutation = usePostApiAuthLogout({})
  const loginMutation = usePostApiAuthLogin({})
  const registerMutation = usePostApiAuthRegister({})

  const derived = useMemo((): DerivedAuthState => {
    // Only show loading on initial load, not background refetches (e.g., tab focus)
    if (me.isPending && !me.data) {
      return { isLoading: true, isAuthenticated: false, user: null }
    }

    const data = me.data
    if (!data?.isAuthenticated || !data.userId || !data.email) {
      return { isLoading: false, isAuthenticated: false, user: null }
    }

    const user: AuthUser = {
      userId: data.userId,
      email: data.email,
      roles: data.roles ?? [],
      firstName: data.firstName,
      lastName: data.lastName,
      phoneNumber: data.phoneNumber,
    }

    return { isLoading: false, isAuthenticated: true, user }
  }, [me.isPending, me.data])

  useEffect(() => {
    if (derived.isAuthenticated) {
      setState((s) => ({ ...s, otpStep: 'idle', otpEmail: null, error: null }))
    }
  }, [derived.isAuthenticated])

  const exposeState: AuthState = {
    ...state,
    user: derived.user,
    isAuthenticated: derived.isAuthenticated,
    isLoading: derived.isLoading || verifyOtpMutation.isPending,
    isSendingOtp: sendOtpMutation.isPending,
    isVerifyingOtp: verifyOtpMutation.isPending,
    isLoggingIn: loginMutation.isPending,
    isRegistering: registerMutation.isPending,
  }

  const sendOtp = async (email: string): Promise<boolean> => {
    setState((s) => ({ ...s, error: null, otpEmail: email }))
    try {
      await sendOtpMutation.mutateAsync({ data: { email } })
      setState((s) => ({ ...s, otpStep: 'email-sent', otpEmail: email }))
      return true
    } catch (err) {
      const msg = err instanceof Error ? err.message : 'Failed to send OTP'
      setState((s) => ({ ...s, error: msg, otpStep: 'idle' }))
      return false
    }
  }

  const verifyOtp = async (email: string, otpCode: string): Promise<boolean> => {
    setState((s) => ({ ...s, error: null, otpStep: 'verifying' }))
    try {
      await verifyOtpMutation.mutateAsync({ data: { email, otpCode } })
      await me.refetch()
      return true
    } catch (err) {
      const msg = err instanceof Error ? err.message : 'Invalid OTP code'
      setState((s) => ({ ...s, error: msg, otpStep: 'email-sent' }))
      return false
    }
  }

  const loginWithPassword = async (email: string, password: string): Promise<boolean> => {
    setState((s) => ({ ...s, error: null }))
    try {
      await loginMutation.mutateAsync({ data: { email, password } })
      await me.refetch()
      return true
    } catch (err: unknown) {
      const axiosErr = err as { response?: { data?: { detail?: string } } }
      const msg = axiosErr?.response?.data?.detail || (err instanceof Error ? err.message : 'Invalid email or password')
      setState((s) => ({ ...s, error: msg }))
      return false
    }
  }

  const register = async (email: string, password: string): Promise<boolean> => {
    setState((s) => ({ ...s, error: null }))
    try {
      await registerMutation.mutateAsync({ data: { email, password } })
      await me.refetch()
      return true
    } catch (err: unknown) {
      const axiosErr = err as { response?: { data?: { detail?: string } } }
      const msg = axiosErr?.response?.data?.detail || (err instanceof Error ? err.message : 'Registration failed')
      setState((s) => ({ ...s, error: msg }))
      return false
    }
  }

  const logout = async (): Promise<void> => {
    try {
      await logoutMutation.mutateAsync()
    } catch {
      // ignore
    }
    queryClient.clear()
    setState((s) => ({ ...s, user: null, isAuthenticated: false, otpStep: 'idle', otpEmail: null, error: null }))
    await me.refetch()
  }

  const clearError = () => setState((s) => ({ ...s, error: null }))
  const backToEmail = () => setState((s) => ({ ...s, otpStep: 'idle' }))
  const refetchAuth = () => me.refetch()

  const value: AuthContextType = {
    ...exposeState,
    sendOtp,
    verifyOtp,
    loginWithPassword,
    register,
    logout,
    clearError,
    backToEmail,
    refetchAuth,
  }

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>
}
