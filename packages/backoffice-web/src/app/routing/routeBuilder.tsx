import { Route } from 'react-router-dom'
import { ApplicationDefinition, NavigationItem, RouteDefinition } from './types'
import { RouteGuard } from './RouteGuard'

/**
 * Converts route definitions to React Router Route components
 * Recursively flattens nested routes for React Router
 */
export function buildRoutes(routes: RouteDefinition[]) {
  const flatRoutes: ReturnType<typeof Route>[] = []
  
  const processRoute = (route: RouteDefinition) => {
    flatRoutes.push(
      <Route 
        key={route.path} 
        path={route.path} 
        element={
          <RouteGuard requiresRole={route.requiresRole}>
            <route.component />
          </RouteGuard>
        } 
      />
    )
    
    if (route.children) {
      route.children.forEach(processRoute)
    }
  }
  
  routes.forEach(processRoute)
  return flatRoutes
}

/**
 * Recursively collects all child paths from a route (including hidden ones)
 */
function getAllChildPaths(route: RouteDefinition): string[] {
  if (!route.children || route.children.length === 0) {
    return []
  }
  
  const paths: string[] = []
  route.children.forEach(child => {
    paths.push(child.path)
    paths.push(...getAllChildPaths(child))
  })
  return paths
}

/**
 * Converts route definitions to navigation items for sidebar
 * Filters out routes marked with hideInNavigation: true
 */
export function buildNavigationItems(routes: RouteDefinition[]): NavigationItem[] {
  return routes
    .filter((route) => !route.hideInNavigation)
    .map((route) => ({
      path: route.path,
      label: route.label,
      icon: route.icon,
      children: route.children ? buildNavigationItems(route.children) : undefined,
      childPaths: getAllChildPaths(route)
    }))
}

/**
 * Flattens all routes from multiple applications into a single array
 */
export function getAllRoutes(applications: ApplicationDefinition[]): RouteDefinition[] {
  return applications.flatMap(app => app.routes)
}

/**
 * Gets navigation items for a specific application
 */
export function getAppNavigationItems(application: ApplicationDefinition): NavigationItem[] {
  return buildNavigationItems(application.routes)
}
