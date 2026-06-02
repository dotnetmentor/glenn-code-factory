import { Navigate, useLocation, useParams } from 'react-router-dom'

/**
 * Redirect legacy workspace paths to {@code /w/:slug} (the workspace home).
 *
 * <p>Used for retired routes such as {@code /projects} (old index) and
 * {@code /integrations}. Preserves the search string so {@code ?install=} and
 * {@code ?reauth=} OAuth flags still reach {@link WorkspaceLandingRoute}.</p>
 */
export function WorkspaceHomeLegacyRedirect() {
  const { slug } = useParams<{ slug: string }>()
  const { search } = useLocation()
  return <Navigate to={`/w/${slug ?? ''}${search}`} replace />
}
