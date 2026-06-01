import { useLocation } from 'react-router-dom'
import { applications } from '../../applications'
import { ApplicationDefinition } from '../routing/types'

/**
 * Converts a basePath with route params (e.g. `/w/:slug`) into a regex prefix
 * matcher so a real URL like `/w/test/members` is recognized as belonging to
 * that application.
 */
function basePathMatches(pathname: string, basePath: string): boolean {
  if (!basePath.includes(':')) {
    return pathname.startsWith(basePath)
  }
  const pattern = basePath
    .split('/')
    .map(seg => (seg.startsWith(':') ? '[^/]+' : seg.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')))
    .join('/')
  const regex = new RegExp(`^${pattern}(?:/|$)`)
  return regex.test(pathname)
}

/**
 * Hook to determine the current application based on the current route
 */
export function useCurrentApplication(): ApplicationDefinition | null {
  const { pathname } = useLocation()

  // Find the application that matches the current path
  const currentApp = applications.find(app =>
    basePathMatches(pathname, app.basePath)
  )

  return currentApp || null
}
