import { Typography, Box, Alert } from '@mui/material'
import { useAuth } from '../../auth/authContext'

interface RouteGuardProps {
  children: React.ReactNode
  requiresAuth?: boolean
  requiresRole?: string
}

export function RouteGuard({ children, requiresAuth, requiresRole }: RouteGuardProps) {
  const auth = useAuth()

  // If requiresAuth is false, allow access without authentication (example app)
  if (requiresAuth === false) {
    return <>{children}</>
  }

  // If no role required, allow access
  if (!requiresRole) {
    return <>{children}</>
  }

  // Check if user is authenticated
  if (!auth.isAuthenticated || !auth.user) {
    return (
      <Box sx={{ p: 3 }}>
        <Alert severity="error">
          <Typography variant="h6">Access Denied</Typography>
          <Typography>You need to be logged in to access this page.</Typography>
        </Alert>
      </Box>
    )
  }

  // Check if user has required role
  if (!auth.user.roles.includes(requiresRole)) {
    return (
      <Box sx={{ p: 3 }}>
        <Alert severity="error">
          <Typography variant="h6">Access Denied</Typography>
          <Typography>
            You do not have access to this page. Required role: {requiresRole}
          </Typography>
        </Alert>
      </Box>
    )
  }

  return <>{children}</>
}
