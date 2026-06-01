import { Navigate, useLocation, useParams } from 'react-router-dom'

/**
 * Redirect target for the legacy {@code /w/:slug/integrations} route.
 *
 * <p>The standalone Integrations page was folded into the
 * {@code WorkspaceSettingsDrawer} (see {@code features/workspace-settings}).
 * The GitHub App's install Setup URL used to bounce users back to that route
 * — and old browser bookmarks / external references still point at it — so
 * we keep the path mounted and silently redirect to the workspace projects
 * view, preserving the search string so the {@code ?install=success|pending|
 * cancelled|error} flag still triggers the one-time snackbar in
 * {@code ProjectsPage}.</p>
 *
 * <p>The backend's {@code GithubInstallCallbackController} has been updated to
 * redirect to {@code /projects} directly, so this fallback is for legacy
 * traffic only — but it's cheap to keep so stale links don't 404.</p>
 */
export function IntegrationsLegacyRedirect() {
  const { slug } = useParams<{ slug: string }>()
  const { search } = useLocation()
  // `replace` so the user's back button doesn't bounce between
  // /integrations and /projects.
  return <Navigate to={`/w/${slug ?? ''}/projects${search}`} replace />
}
