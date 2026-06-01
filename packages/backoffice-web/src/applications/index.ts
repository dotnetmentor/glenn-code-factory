import { ApplicationDefinition } from '../app/routing/types'
import { superAdminApplication } from './super-admin'
import { workspaceApplication } from './workspace'

export const applications: ApplicationDefinition[] = [
  superAdminApplication,
  workspaceApplication,
]

export function getUserApplications(userRoles: string[] = []): ApplicationDefinition[] {
  return applications.filter(app => {
    if (app.requiresAuth === false) return true
    if (!app.requiresRole) return true
    return userRoles.includes(app.requiresRole)
  })
}
