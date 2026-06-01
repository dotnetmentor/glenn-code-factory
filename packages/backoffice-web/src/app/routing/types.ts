import { ApplicationRoles, type ApplicationRole } from '../../applications/shared/constants/roles'

export { ApplicationRoles as ApplicationRole }
export type ApplicationRoleType = ApplicationRole

export interface RouteDefinition {
  path: string
  label: string
  icon: React.ComponentType<{ fontSize?: 'small' | 'medium' | 'large' }>
  component: React.ComponentType
  children?: RouteDefinition[]
  requiresRole?: string
  hideInNavigation?: boolean
  /**
   * Opts the route out of the standard {@code AppLayout} chrome (top nav +
   * side rail). When {@code true}, the route's component is rendered full-
   * bleed and is responsible for its own shell. The {@code AppSelector} and
   * outer auth routes are unaffected. Used for the Project Workspace IDE
   * shell (P2.x) where the workspace nav folds away into a breadcrumb spine.
   */
  chromeless?: boolean
}

export interface ApplicationDefinition {
  id: string
  name: string
  description: string
  icon: React.ComponentType<{ fontSize?: 'small' | 'medium' | 'large' }>
  basePath: string
  routes: RouteDefinition[]
  requiresAuth?: boolean
  requiresRole?: string
}

export interface NavigationItem {
  path: string
  label: string
  icon: React.ComponentType<{ fontSize?: 'small' | 'medium' | 'large' }>
  children?: NavigationItem[]
  childPaths?: string[]
}
