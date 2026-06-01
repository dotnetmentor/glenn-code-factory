import WorkIcon from '@mui/icons-material/Work'
import { ApplicationDefinition, ApplicationRole } from '../../app/routing/types'
import { workspaceRoutes } from './routes'

export const workspaceApplication: ApplicationDefinition = {
  id: 'workspace',
  name: 'Workspace',
  description: 'Your workspace',
  icon: WorkIcon,
  basePath: '/w/:slug',
  requiresAuth: true,
  routes: workspaceRoutes,
  requiresRole: ApplicationRole.WorkspaceUser,
}

export * from './routes'
