import { useLocation } from 'react-router-dom'
import { useGetApiMeWorkspaces } from '../../../api/queries-commands'

const SLUG_PARAM = ':slug'
const PROJECT_ID_PARAM = ':projectId'

export interface AppPathResolver {
  /**
   * Resolves a path that may contain `:slug` and/or `:projectId`. Returns the
   * resolved path, or `null` if the path needs a value that's not currently
   * available (e.g. the workspaces query hasn't loaded yet, the user has no
   * workspaces, or we're not currently on a project-scoped URL so there is no
   * projectId to substitute). Callers MUST guard against `null` before
   * navigating — see {@link canResolve}.
   */
  resolve: (path: string) => string | null
  /**
   * `true` when {@link resolve} will return a non-null string for `path`.
   * Use this to disable/no-op nav controls while the required URL params
   * aren't known.
   */
  canResolve: (path: string) => boolean
}

/**
 * Returns a resolver that swaps `:slug` and `:projectId` in any path string
 * for real values pulled from the current URL (preferred) or, for `:slug`,
 * the user's first workspace.
 *
 * <p>Resolution priority for `:slug`:
 * <ol>
 *   <li>The slug from the current URL (if we're already inside a workspace
 *       path).</li>
 *   <li>The user's first workspace from <c>/api/me/workspaces</c>.</li>
 * </ol></p>
 *
 * <p>Resolution priority for `:projectId`:
 * <ol>
 *   <li>The projectId from the current URL (super-admin project surfaces use
 *       <c>/super-admin/projects/&lt;projectId&gt;/...</c>; the workspace app
 *       uses <c>/w/&lt;slug&gt;/projects/&lt;projectId&gt;/...</c>).</li>
 * </ol>
 * If no projectId is in the URL, project-scoped paths resolve to
 * <c>null</c> — there is no sensible fallback, the user isn't currently
 * inside a project.</p>
 *
 * <p>If a required value is unavailable, {@link resolve} returns <c>null</c>
 * so callers can render a disabled state instead of navigating to a literal
 * <c>/super-admin/projects/:projectId</c> URL.</p>
 */
export function useResolveAppPath(): AppPathResolver {
  const { pathname } = useLocation()
  const { data: workspaces } = useGetApiMeWorkspaces()

  const slugMatch = pathname.match(/^\/w\/([^/]+)/)
  const urlSlug = slugMatch && slugMatch[1] !== SLUG_PARAM ? slugMatch[1] : null
  const fallbackSlug = workspaces?.[0]?.slug ?? null
  const slug = urlSlug ?? fallbackSlug

  // Extract `:projectId` from either super-admin project surfaces
  // (`/super-admin/projects/<id>/...`) or workspace project surfaces
  // (`/w/<slug>/projects/<id>/...`). We deliberately exclude the literal
  // `:projectId` token so a misconfigured caller doesn't echo it back.
  const superAdminProjectMatch = pathname.match(
    /^\/super-admin\/projects\/([^/]+)/,
  )
  const workspaceProjectMatch = pathname.match(
    /^\/w\/[^/]+\/projects\/([^/]+)/,
  )
  const rawProjectId =
    superAdminProjectMatch?.[1] ?? workspaceProjectMatch?.[1] ?? null
  // Guard against false positives: `/super-admin/projects/new` is a real
  // route that's not project-scoped. Reject anything that's not a UUID-like
  // token so a static segment can't be mistaken for an id.
  const projectId =
    rawProjectId && rawProjectId !== PROJECT_ID_PARAM && rawProjectId !== 'new'
      ? rawProjectId
      : null

  const resolve = (path: string): string | null => {
    let resolved = path
    if (resolved.includes(SLUG_PARAM)) {
      if (!slug) return null
      resolved = resolved.replace(SLUG_PARAM, slug)
    }
    if (resolved.includes(PROJECT_ID_PARAM)) {
      if (!projectId) return null
      resolved = resolved.replace(PROJECT_ID_PARAM, projectId)
    }
    return resolved
  }

  const canResolve = (path: string): boolean => {
    if (path.includes(SLUG_PARAM) && !slug) return false
    if (path.includes(PROJECT_ID_PARAM) && !projectId) return false
    return true
  }

  return { resolve, canResolve }
}
