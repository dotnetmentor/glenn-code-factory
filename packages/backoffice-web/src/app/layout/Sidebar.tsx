import { useState } from 'react'
import { Box, Collapse, List, ListItemButton, ListItemIcon, ListItemText, Typography, IconButton, Tooltip, Badge, alpha } from '@mui/material'
import { useTheme } from '@mui/material/styles'
import { ExpandLess, ExpandMore, ChevronLeft, ChevronRight } from '@mui/icons-material'
import HistoryIcon from '@mui/icons-material/History'
import BugReportIcon from '@mui/icons-material/BugReport'
import PaletteIcon from '@mui/icons-material/Palette'
import { useLocation, useNavigate, matchPath } from 'react-router-dom'
import { useCurrentApplication } from '../navigation/useCurrentApplication'
import { getAppNavigationItems } from '../routing/routeBuilder'
import { NavigationItem } from '../routing/types'
import { useSidebar } from './SidebarContext'
import { useGetApiErrorLogsCount } from '../../api/queries-commands'
import { useResolveAppPath } from '../../applications/shared/hooks/useResolveAppPath'

export function Sidebar({ onNavigate }: { onNavigate?: () => void }) {
  const theme = useTheme()
  const navigate = useNavigate()
  const { pathname } = useLocation()
  const currentApp = useCurrentApplication()
  const [expandedItems, setExpandedItems] = useState<Set<string>>(new Set())
  const { isCollapsed, toggleCollapse } = useSidebar()
  const { resolve, canResolve } = useResolveAppPath()

  const errorCountQuery = useGetApiErrorLogsCount({ query: { staleTime: 60000, refetchInterval: 60000 } })
  const unresolvedCount = errorCountQuery.data?.count ?? 0

  const toggleExpand = (path: string) => {
    setExpandedItems(prev => {
      const next = new Set(prev)
      if (next.has(path)) {
        next.delete(path)
      } else {
        next.add(path)
      }
      return next
    })
  }

  const isSelected = (currentPath: string, item: NavigationItem): boolean => {
    const resolvedItemPath = resolve(item.path)
    if (resolvedItemPath !== null && currentPath === resolvedItemPath) return true

    if (item.children && item.children.length > 0) {
      return item.children.some(child => isSelected(currentPath, child))
    }

    if (item.childPaths && item.childPaths.length > 0) {
      return item.childPaths.some(childPath => {
        const resolvedChildPath = resolve(childPath)
        return resolvedChildPath !== null && matchPath(resolvedChildPath, currentPath) !== null
      })
    }

    return false
  }

  const renderNavItem = (item: NavigationItem, depth = 0) => {
    const Icon = item.icon
    const selected = isSelected(pathname, item)
    const hasChildren = item.children && item.children.length > 0
    const isExpanded = expandedItems.has(item.path)

    if (isCollapsed && depth > 0) {
      return null
    }

    const resolvedPath = hasChildren ? null : resolve(item.path)
    const isResolvable = hasChildren ? true : canResolve(item.path)
    const isDisabled = !hasChildren && !isResolvable

    const handleClick = hasChildren
      ? () => toggleExpand(item.path)
      : () => {
          if (!resolvedPath) return
          navigate(resolvedPath)
          onNavigate?.()
        }

    // Distinguish the two flavors of "disabled" the resolver produces. A
    // `:slug` route is dimmed while workspaces are loading; a `:projectId`
    // route is dimmed because the user isn't currently on a project URL,
    // which is a different mental model and deserves its own copy.
    const needsProject = item.path.includes(':projectId')
    const disabledTooltip = needsProject
      ? 'Open a project to navigate here'
      : 'Loading workspace…'
    const tooltipTitle = isDisabled
      ? disabledTooltip
      : isCollapsed
        ? item.label
        : ''

    return (
      <Box key={item.path}>
        <Tooltip title={tooltipTitle} placement="right">
          <ListItemButton
            selected={selected && !hasChildren}
            onClick={handleClick}
            sx={{
              mb: 0.25,
              pl: isCollapsed ? 0 : 1.5 + depth * 2,
              pr: isCollapsed ? 0 : 1,
              justifyContent: isCollapsed ? 'center' : 'flex-start',
              position: 'relative',
              overflow: 'hidden',
              opacity: isDisabled ? 0.4 : 1,
              cursor: isDisabled ? 'not-allowed' : undefined,
              '&:hover': isDisabled ? { backgroundColor: 'transparent' } : undefined,
              ...(selected && !hasChildren && {
                '&::before': {
                  content: '""',
                  position: 'absolute',
                  left: 0,
                  top: '50%',
                  transform: 'translateY(-50%)',
                  width: '3px',
                  height: '60%',
                  backgroundColor: theme.palette.primary.main,
                  borderRadius: '0 3px 3px 0',
                },
              }),
            }}
          >
            <ListItemIcon sx={{ minWidth: isCollapsed ? 0 : 30, justifyContent: 'center', '& .MuiSvgIcon-root': { fontSize: 18 } }}>
              <Icon fontSize="small" />
            </ListItemIcon>
            {!isCollapsed && (
              <>
                <ListItemText primary={item.label} />
                {hasChildren && (isExpanded ? <ExpandLess sx={{ fontSize: 16 }} /> : <ExpandMore sx={{ fontSize: 16 }} />)}
              </>
            )}
          </ListItemButton>
        </Tooltip>
        {!isCollapsed && hasChildren && (
          <Collapse in={isExpanded} timeout="auto" unmountOnExit>
            <List disablePadding>
              {item.children!.map(child => renderNavItem(child, depth + 1))}
            </List>
          </Collapse>
        )}
      </Box>
    )
  }

  const navigationItems = currentApp ? getAppNavigationItems(currentApp) : []

  return (
    <Box sx={{ height: '100%', display: 'flex', flexDirection: 'column' }}>
      {/* Spacer to clear the fixed header */}
      <Box sx={{ minHeight: 48 }} />

      {/* Section label */}
      {!isCollapsed && (
        <Box sx={{ px: 2, pt: 2, pb: 1 }}>
          <Typography
            sx={{
              fontSize: '0.625rem',
              fontWeight: 600,
              textTransform: 'uppercase',
              letterSpacing: '0.08em',
              color: alpha(theme.palette.text.secondary, 0.5),
            }}
          >
            Backoffice
          </Typography>
        </Box>
      )}
      {isCollapsed && <Box sx={{ pt: 1.5 }} />}
      <List sx={{ px: 0.75, py: 0 }}>
        {navigationItems.map(item => renderNavItem(item))}
      </List>
      <Box sx={{ flexGrow: 1 }} />
      <Box
        sx={{
          display: 'flex',
          justifyContent: isCollapsed ? 'center' : 'space-between',
          alignItems: 'center',
          px: 1,
          py: 1.5,
          borderTop: '1px solid',
          borderColor: (theme) => alpha(theme.palette.divider, 0.4),
        }}
      >
        {!isCollapsed && (
          <Typography sx={{ fontSize: '0.625rem', color: 'text.disabled', pl: 0.5 }}>
            v0.1
          </Typography>
        )}
        <Box sx={{ display: 'flex', alignItems: 'center', gap: 0.25 }}>
          <Tooltip title="Domain Events">
            <IconButton
              size="small"
              onClick={() => navigate('/super-admin/domain-events')}
              sx={{
                p: 0.5,
                color: pathname.includes('/domain-events') ? 'primary.main' : 'text.disabled',
                '&:hover': { color: 'text.secondary' },
              }}
            >
              <HistoryIcon sx={{ fontSize: 16 }} />
            </IconButton>
          </Tooltip>
          <Tooltip title="Error Log">
            <IconButton
              size="small"
              onClick={() => navigate('/super-admin/error-logs')}
              sx={{
                p: 0.5,
                color: pathname.includes('/error-logs') ? 'primary.main' : 'text.disabled',
                '&:hover': { color: 'text.secondary' },
              }}
            >
              <Badge
                badgeContent={unresolvedCount}
                color="error"
                max={99}
                sx={{
                  '& .MuiBadge-badge': {
                    fontSize: '0.6rem',
                    height: 14,
                    minWidth: 14,
                    padding: '0 3px',
                    top: 1,
                    right: 1,
                  },
                }}
              >
                <BugReportIcon sx={{ fontSize: 16 }} />
              </Badge>
            </IconButton>
          </Tooltip>
          {!isCollapsed && (
            <Tooltip title="Theme Playground">
              <IconButton
                size="small"
                onClick={() => navigate('/super-admin/theme-playground')}
                sx={{
                  p: 0.5,
                  color: pathname.includes('/theme-playground') ? 'primary.main' : 'text.disabled',
                  '&:hover': { color: 'text.secondary' },
                }}
              >
                <PaletteIcon sx={{ fontSize: 16 }} />
              </IconButton>
            </Tooltip>
          )}
          <Tooltip title={isCollapsed ? 'Expand sidebar' : 'Collapse sidebar'}>
            <IconButton
              size="small"
              onClick={toggleCollapse}
              sx={{ p: 0.5, color: 'text.disabled', '&:hover': { color: 'text.secondary' } }}
            >
              {isCollapsed ? <ChevronRight sx={{ fontSize: 16 }} /> : <ChevronLeft sx={{ fontSize: 16 }} />}
            </IconButton>
          </Tooltip>
        </Box>
      </Box>
    </Box>
  )
}
