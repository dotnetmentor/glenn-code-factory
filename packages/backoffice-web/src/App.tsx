import { Route, Routes, matchPath, useLocation } from 'react-router-dom'
import { AppLayout } from './app/layout/AppLayout'
import { AppSelector } from './app/navigation/AppSelector'
import { buildRoutes, getAllRoutes } from './app/routing/routeBuilder'
import { RouteGuard } from './app/routing/RouteGuard'
import { applications, getUserApplications } from './applications'
import { useAuth } from './auth/authContext'
import { NotificationProvider } from './applications/shared/contexts/NotificationContext'
import { RegisterPage } from './auth/components/RegisterPage'
import { ForgotPasswordPage } from './auth/components/ForgotPasswordPage'
import { ResetPasswordPage } from './auth/components/ResetPasswordPage'
import { AcceptInvitePage } from './auth/components/AcceptInvitePage'

function AppRoutes() {
  const auth = useAuth()
  const accessibleApplications = getUserApplications(auth.user?.roles || [])
  const allRoutes = getAllRoutes(accessibleApplications)

  return (
    <Routes>
      <Route path="/" element={<AppSelector />} />
      <Route path="/register" element={<RegisterPage />} />
      <Route path="/forgot-password" element={<ForgotPasswordPage />} />
      <Route path="/reset-password" element={<ResetPasswordPage />} />
      <Route path="/invite/:token" element={<AcceptInvitePage />} />
      {buildRoutes(allRoutes)}

      {applications.map(app => (
        <Route
          key={`guard-${app.id}`}
          path={`${app.basePath}/*`}
          element={
            <RouteGuard requiresAuth={app.requiresAuth} requiresRole={app.requiresRole}>
              <div>Loading...</div>
            </RouteGuard>
          }
        />
      ))}
    </Routes>
  )
}

/**
 * Wrapper component that conditionally applies AppLayout
 * Member portal and public booking routes use their own layouts
 */
function AppWithLayout() {
  const location = useLocation()
  const auth = useAuth()

  // Routes that use their own layouts (no AppLayout wrapper)
  const isMemberPortal = location.pathname.startsWith('/me')
  const isPublicBooking = location.pathname.startsWith('/book')
  const isAuthPage = location.pathname === '/register' ||
                     location.pathname === '/forgot-password' ||
                     location.pathname === '/reset-password' ||
                     location.pathname.startsWith('/invite/')

  // Routes declared as `chromeless` opt out of AppLayout. The route component
  // is responsible for its own shell (breadcrumb spine, sidebar, canvas).
  // Used by the Project Workspace IDE shell — see workspace/routes.ts.
  const chromelessPaths = getAllRoutes(getUserApplications(auth.user?.roles || []))
    .filter((r) => r.chromeless)
    .map((r) => r.path)
  const isChromeless = chromelessPaths.some(
    (p) => matchPath({ path: p, end: true }, location.pathname) !== null,
  )

  // These routes have their own full-page layouts
  if (isMemberPortal || isPublicBooking || isAuthPage || isChromeless) {
    return <AppRoutes />
  }

  // Admin routes use AppLayout
  return (
    <AppLayout>
      <AppRoutes />
    </AppLayout>
  )
}

function App() {
  return (
    <NotificationProvider>
      <AppWithLayout />
    </NotificationProvider>
  )
}

export default App
