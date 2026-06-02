import { PropsWithChildren, useState } from 'react'
import { AppBar, Avatar, Box, Drawer, IconButton, Menu, MenuItem, Toolbar, Tooltip, Typography, alpha } from '@mui/material'
import { useTheme } from '@mui/material/styles'
import MenuIcon from '@mui/icons-material/Menu'
import LogoutIcon from '@mui/icons-material/Logout'
import AdminPanelSettingsIcon from '@mui/icons-material/AdminPanelSettings'
import { useNavigate } from 'react-router-dom'
import { Sidebar } from './Sidebar.tsx'
import { SidebarProvider, useSidebar } from './SidebarContext'
import { useAuth } from '../../auth/authContext'
import { useCurrentApplication } from '../navigation/useCurrentApplication'
import { getUserApplications } from '../../applications'
import { useResolveAppPath } from '../../applications/shared/hooks/useResolveAppPath'
import { ApplicationRoles } from '../../applications/shared/constants/roles'
import { useGetApiMeWorkspaces } from '../../api/queries-commands'
import { CreateWorkspaceDialog } from '../../applications/workspace/features/workspace-settings'

const drawerWidth = 220
const collapsedDrawerWidth = 52

function AppLayoutContent({ children }: PropsWithChildren) {
  const theme = useTheme()
  const navigate = useNavigate()
  const [mobileOpen, setMobileOpen] = useState(false)
  const toggle = () => setMobileOpen((v) => !v)
  const { isAuthenticated, user, logout } = useAuth()
  const [userMenuEl, setUserMenuEl] = useState<null | HTMLElement>(null)
  const openUserMenu = (e: React.MouseEvent<HTMLElement>) => setUserMenuEl(e.currentTarget)
  const closeUserMenu = () => setUserMenuEl(null)
  const handleLogout = async () => {
    closeUserMenu()
    await logout()
  }
  const avatarLabel = (user?.email?.[0] ?? 'A').toUpperCase()
  const currentApp = useCurrentApplication()
  const userApplications = getUserApplications(user?.roles || [])
  // Gate the "Admin" dropdown entry on the same role the super-admin app
  // requires — surfacing the link to non-admins would just lead to a 403.
  const canSeeAdmin = (user?.roles ?? []).includes(ApplicationRoles.SuperAdmin)
  const handleOpenAdmin = () => {
    closeUserMenu()
    navigate('/super-admin')
  }
  const { isCollapsed } = useSidebar()
  const currentDrawerWidth = isCollapsed ? collapsedDrawerWidth : drawerWidth
  const { resolve, canResolve } = useResolveAppPath()

  // Distinguish "workspaces still loading" from "workspaces loaded, none
  // exist". The picker/tab needs both signals — the former dims the tab,
  // the latter turns it into a "Create workspace" recovery CTA.
  const { isLoading: workspacesLoading, data: workspacesData } = useGetApiMeWorkspaces()
  const hasNoWorkspaces = !workspacesLoading && (workspacesData?.length ?? 0) === 0
  const [createWorkspaceOpen, setCreateWorkspaceOpen] = useState(false)

  const handleAppClick = (appBasePath: string) => {
    navigate(appBasePath)
  }

  const handleWorkspaceCreated = (slug: string) => {
    navigate(`/w/${slug}`)
  }

  return (
    <Box sx={{ display: 'flex', transition: 'all 0.3s ease' }}>
      <AppBar position="fixed" color="default">
        <Toolbar sx={{ gap: 2, px: { xs: 2, md: 2.5 } }}>
          <IconButton color="inherit" edge="start" sx={{ display: { md: 'none' } }} onClick={toggle}>
            <MenuIcon sx={{ fontSize: 20 }} />
          </IconButton>

          {/* Product brand — pinned to drawer width so it sits centered above sidebar */}
          <Box
            sx={{
              width: { md: currentDrawerWidth - 40 },
              display: 'flex',
              alignItems: 'center',
              justifyContent: { xs: 'flex-start', md: isCollapsed ? 'center' : 'flex-start' },
              gap: 1,
              cursor: 'default',
              userSelect: 'none',
              flexShrink: 0,
              transition: 'width 0.3s ease',
            }}
          >
            <svg width="22" height="22" viewBox="0 0 32 32" fill="none" xmlns="http://www.w3.org/2000/svg">
              <rect width="32" height="32" rx="7" fill={theme.palette.primary.main} opacity="0.85" />
              <path d="M16 6C10.48 6 6 10.48 6 16s4.48 10 10 10 10-4.48 10-10h-8v3h4.5c-1.1 2.35-3.5 4-6.5 4-3.87 0-7-3.13-7-7s3.13-7 7-7c1.94 0 3.68.78 4.95 2.05l2.12-2.12C21.44 7.56 18.87 6 16 6z" fill="#fff" opacity="0.92" />
            </svg>
            {!(isCollapsed) && (
              <Typography
                noWrap
                sx={{
                  fontWeight: 700,
                  fontSize: '0.9375rem',
                  color: theme.palette.text.primary,
                  letterSpacing: '-0.01em',
                  lineHeight: 1,
                }}
              >
                GlennCode Factory
              </Typography>
            )}
          </Box>

          <Box sx={{ flexGrow: 1 }} />

          {userApplications.length > 0 && (
            <Box sx={{ display: 'flex', gap: 0.5, mr: 1 }}>
              {userApplications.map((app) => {
                const isCurrentApp = currentApp?.id === app.id
                const resolvedPath = resolve(app.basePath)
                const isResolvable = canResolve(app.basePath)
                // A slug-bound app whose slug query has loaded with zero
                // results: clicking the tab is the user's recovery CTA, not
                // a navigation. Distinguish this from the brief loading
                // race window — that one stays dimmed.
                const needsSlug = app.basePath.includes(':slug')
                const isCreateWorkspaceCta = needsSlug && !isCurrentApp && !isResolvable && hasNoWorkspaces
                const isLoadingSlug = needsSlug && !isCurrentApp && !isResolvable && !hasNoWorkspaces
                const isDisabled = isLoadingSlug
                const tooltipTitle = isLoadingSlug
                  ? 'Loading workspace…'
                  : isCreateWorkspaceCta
                    ? 'Create your first workspace'
                    : ''
                return (
                  <Tooltip
                    key={app.id}
                    title={tooltipTitle}
                    placement="bottom"
                  >
                  <Box
                    onClick={() => {
                      if (isCurrentApp) return
                      if (isCreateWorkspaceCta) {
                        setCreateWorkspaceOpen(true)
                        return
                      }
                      if (!resolvedPath) return
                      handleAppClick(resolvedPath)
                    }}
                    sx={{
                      px: 1.5,
                      py: 0.75,
                      cursor: isCurrentApp ? 'default' : isDisabled ? 'not-allowed' : 'pointer',
                      opacity: isDisabled ? 0.4 : 1,
                      transition: 'all 0.15s ease',
                      position: 'relative',
                      backgroundColor: 'transparent',
                      fontWeight: isCurrentApp ? 600 : 400,
                      fontSize: '0.8125rem',
                      color: isCurrentApp ? theme.palette.text.primary : theme.palette.text.secondary,
                      '&::after': {
                        content: '""',
                        position: 'absolute',
                        bottom: 0,
                        left: '50%',
                        transform: 'translateX(-50%)',
                        width: isCurrentApp ? '100%' : 0,
                        height: '2px',
                        backgroundColor: theme.palette.primary.main,
                        borderRadius: '2px 2px 0 0',
                        transition: 'width 0.2s ease',
                      },
                      '&:hover': !isCurrentApp && !isDisabled ? {
                        color: theme.palette.text.primary,
                        '&::after': {
                          width: '60%',
                        },
                      } : {},
                    }}
                  >
                    {app.name}
                  </Box>
                  </Tooltip>
                )
              })}
            </Box>
          )}

          <IconButton size="small" onClick={isAuthenticated ? openUserMenu : undefined} aria-label="account">
            <Avatar sx={{ width: 26, height: 26, fontSize: 12, bgcolor: alpha(theme.palette.primary.main, 0.08), color: theme.palette.primary.main, fontWeight: 600 }}>{avatarLabel}</Avatar>
          </IconButton>
          <Menu
            anchorEl={userMenuEl}
            open={!!userMenuEl}
            onClose={closeUserMenu}
            anchorOrigin={{ vertical: 'bottom', horizontal: 'right' }}
            transformOrigin={{ vertical: 'top', horizontal: 'right' }}
          >
            {isAuthenticated && canSeeAdmin && (
              <MenuItem onClick={handleOpenAdmin}>
                <AdminPanelSettingsIcon fontSize="small" style={{ marginRight: 8 }} /> Admin
              </MenuItem>
            )}
            {isAuthenticated && (
              <MenuItem onClick={handleLogout}>
                <LogoutIcon fontSize="small" style={{ marginRight: 8 }} /> Logout
              </MenuItem>
            )}
          </Menu>
        </Toolbar>
      </AppBar>

      <Box component="nav" sx={{ width: { md: currentDrawerWidth }, flexShrink: { md: 0 }, transition: 'all 0.3s ease' }}>
        <Drawer
          variant="temporary"
          open={mobileOpen}
          onClose={toggle}
          ModalProps={{ keepMounted: true }}
          sx={{ display: { xs: 'block', md: 'none' }, '& .MuiDrawer-paper': { width: drawerWidth } }}
        >
          <Sidebar onNavigate={toggle} />
        </Drawer>
        <Drawer
          variant="permanent"
          sx={{ display: { xs: 'none', md: 'block' }, '& .MuiDrawer-paper': { width: currentDrawerWidth, boxSizing: 'border-box', transition: 'all 0.3s ease' } }}
          open
        >
          <Sidebar />
        </Drawer>
      </Box>

      <Box
        component="main"
        sx={{
          flexGrow: 1,
          p: { xs: 2, md: 3 },
          pt: { xs: 1.5, md: 2 },
          width: { md: `calc(100% - ${currentDrawerWidth}px)` },
          height: '100vh',
          display: 'flex',
          flexDirection: 'column',
          alignItems: 'center',
          transition: 'all 0.3s ease'
        }}
      >
        <Toolbar />
        <Box sx={{ width: '100%', maxWidth: 1200, flex: 1, minHeight: 0 }}>
          {children}
        </Box>
      </Box>

      <CreateWorkspaceDialog
        open={createWorkspaceOpen}
        onClose={() => setCreateWorkspaceOpen(false)}
        onCreated={handleWorkspaceCreated}
      />
    </Box>
  )
}

export function AppLayout({ children }: PropsWithChildren) {
  return (
    <SidebarProvider>
      <AppLayoutContent>{children}</AppLayoutContent>
    </SidebarProvider>
  )
}
