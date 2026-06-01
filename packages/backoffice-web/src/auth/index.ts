// Auth components
export { LoginForm } from './components/LoginForm'
export type { LoginFormProps } from './components/LoginForm'

export { RegisterPage } from './components/RegisterPage'
export { ForgotPasswordPage } from './components/ForgotPasswordPage'
export { ResetPasswordPage } from './components/ResetPasswordPage'

export { AuthGate } from './AuthGate'

// Auth context and hook
export { useAuth, AuthContext } from './authContext'
export type { AuthUser, OtpStep, AuthState, AuthActions, AuthContextType } from './authContext'

// Auth provider
export { AuthProvider } from './AuthProvider'
