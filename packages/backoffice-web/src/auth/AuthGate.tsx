import { Box, CircularProgress } from '@mui/material'
import { useLocation } from 'react-router-dom'
import { useAuth } from './authContext'
import { LoginForm } from './components/LoginForm'

export function AuthGate({ children }: { children: React.ReactNode }) {
  const { isAuthenticated, isLoading } = useAuth()
  const location = useLocation()

  // Routes that bypass authentication gate
  const isExampleApp = location.pathname.startsWith('/example')
  const isBookingWidget = location.pathname.startsWith('/book')
  const isMemberArea = location.pathname.startsWith('/me')
  const isRegisterPage = location.pathname === '/register'
  const isForgotPassword = location.pathname === '/forgot-password'
  const isResetPassword = location.pathname === '/reset-password'
  const isInvitePage = location.pathname.startsWith('/invite/')

  // Show loading state while checking auth
  if (isLoading) {
    return (
      <Box sx={{ display: 'grid', placeItems: 'center', height: '100vh', bgcolor: 'white' }}>
        <CircularProgress size={20} sx={{ color: 'text.disabled' }} />
      </Box>
    )
  }

  // Allow these routes through without blocking
  // - /example/* - Demo/example routes
  // - /book/* - Booking widget (handles its own auth via LoginDialog)
  // - /me/* - Member area (will require login but handled by the route itself)
  if (isExampleApp || isBookingWidget || isMemberArea || isRegisterPage || isForgotPassword || isResetPassword || isInvitePage) {
    return <>{children}</>
  }

  // For all other routes, require authentication
  if (!isAuthenticated) {
    return <LoginForm variant="page" />
  }

  return <>{children}</>
}
