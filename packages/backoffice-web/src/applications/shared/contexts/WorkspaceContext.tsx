import { createContext, useContext, useMemo, type ReactNode } from 'react'
import { useLocation, useNavigate, useParams } from 'react-router-dom'
import { useGetApiMeWorkspaces, type MyWorkspaceItem } from '../../../api/queries-commands'

export type { MyWorkspaceItem }

export interface WorkspaceContextValue {
  workspaces: MyWorkspaceItem[]
  currentWorkspace: MyWorkspaceItem | null
  currentSlug: string | null
  isLoading: boolean
  switchWorkspace: (newSlug: string) => void
}

const WorkspaceContext = createContext<WorkspaceContextValue | null>(null)

/**
 * Extracts the path that follows `/w/:slug/` so we can preserve the user's
 * subpage when they switch between workspaces. Returns `null` if there is no
 * workspace-rooted subpath to preserve.
 */
function extractSubpath(pathname: string): string | null {
  // Match /w/{slug}/{rest...}
  const match = pathname.match(/^\/w\/[^/]+\/(.+)$/)
  if (!match) return null
  const rest = match[1]
  if (!rest || rest.length === 0) return null
  return rest
}

interface WorkspaceProviderProps {
  children: ReactNode
}

export function WorkspaceProvider({ children }: WorkspaceProviderProps) {
  const params = useParams<{ slug: string }>()
  const navigate = useNavigate()
  const location = useLocation()

  const { data, isLoading } = useGetApiMeWorkspaces()

  const workspaces = useMemo<MyWorkspaceItem[]>(() => data ?? [], [data])

  const currentSlug = params.slug ?? null

  const currentWorkspace = useMemo<MyWorkspaceItem | null>(() => {
    if (!currentSlug) return null
    return workspaces.find((w) => w.slug === currentSlug) ?? null
  }, [workspaces, currentSlug])

  const switchWorkspace = (newSlug: string) => {
    const subpath = extractSubpath(location.pathname)
    if (subpath) {
      navigate(`/w/${newSlug}/${subpath}`)
    } else {
      navigate(`/w/${newSlug}`)
    }
  }

  const value: WorkspaceContextValue = {
    workspaces,
    currentWorkspace,
    currentSlug,
    isLoading,
    switchWorkspace,
  }

  return <WorkspaceContext.Provider value={value}>{children}</WorkspaceContext.Provider>
}

export function useWorkspace(): WorkspaceContextValue {
  const ctx = useContext(WorkspaceContext)
  if (!ctx) {
    throw new Error('useWorkspace must be used within a WorkspaceProvider')
  }
  return ctx
}

/**
 * HOC that wraps a component in the WorkspaceProvider. Used by the workspace
 * route definitions so each page receives the context resolved from `/w/:slug`.
 */
export function withWorkspaceProvider<P extends object>(
  Component: React.ComponentType<P>,
): React.ComponentType<P> {
  const Wrapped = (props: P) => (
    <WorkspaceProvider>
      <Component {...props} />
    </WorkspaceProvider>
  )
  Wrapped.displayName = `withWorkspaceProvider(${Component.displayName ?? Component.name ?? 'Component'})`
  return Wrapped
}
