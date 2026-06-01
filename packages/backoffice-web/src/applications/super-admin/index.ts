import AdminPanelSettingsIcon from '@mui/icons-material/AdminPanelSettings'
import { ApplicationDefinition, ApplicationRole } from '../../app/routing/types'
import { superAdminRoutes } from './routes'

export const superAdminApplication: ApplicationDefinition = {
  id: 'super-admin',
  name: 'Admin',
  description: 'System administration and configuration',
  icon: AdminPanelSettingsIcon,
  basePath: '/super-admin',
  requiresAuth: true,
  routes: superAdminRoutes,
  requiresRole: ApplicationRole.SuperAdmin
}

export * from './routes'
