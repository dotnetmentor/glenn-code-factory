import { WorkspaceShellLayout } from '../../project-workspace/components/WorkspaceShellLayout'
import { NewSessionView } from '../components/NewSessionView'

/**
 * Route entry for {@code /w/:slug/new-session}.
 *
 * <p>Renders the calm "Start a new session" canvas inside the structural
 * {@link WorkspaceShellLayout} so the user sees the same projects + branches
 * sidebar they already navigate with — the new-session affordance feels
 * like a continuation of the workspace, not a modal detour.</p>
 *
 * <p>The new-session view doesn't watch runtime state for the sidebar
 * (there's no per-branch SignalR connection here), so the live-status maps
 * are intentionally omitted — the sidebar falls back to the 15s polled list
 * which is already sufficient for picking a project.</p>
 */
export function NewSessionRoute() {
  return (
    <WorkspaceShellLayout>
      <NewSessionView />
    </WorkspaceShellLayout>
  )
}
