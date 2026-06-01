import { WorkspaceShellLayout } from '../../project-workspace/components/WorkspaceShellLayout'
import { WorkspaceLandingView } from '../components/WorkspaceLandingView'

/**
 * Route entry for {@code /w/:slug} — the workspace home.
 *
 * <p>Renders the calm "Welcome back" resume canvas inside the structural
 * {@link WorkspaceShellLayout}, so the workspace home shares its sidebar
 * (projects + branches) with every other workspace surface — settings,
 * new-session, the per-branch IDE. The user lands here, sees what they
 * recently worked on, and either resumes or hits "New session".</p>
 *
 * <p>No SignalR wiring at this level — the canvas only reads polled summary
 * data (projects list, installations list, me). The sidebar falls back to
 * its own 15s polling, which is plenty for picking up where you left off.</p>
 */
export function WorkspaceLandingRoute() {
  return (
    <WorkspaceShellLayout>
      <WorkspaceLandingView />
    </WorkspaceShellLayout>
  )
}
