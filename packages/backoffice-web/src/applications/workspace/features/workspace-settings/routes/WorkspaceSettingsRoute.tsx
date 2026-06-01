import { WorkspaceShellLayout } from '../../project-workspace/components/WorkspaceShellLayout'
import { WorkspaceSettingsView } from '../components/WorkspaceSettingsView'

/**
 * Route entry for {@code /w/:slug/settings}.
 *
 * <p>Renders the workspace settings canvas inside the structural
 * {@link WorkspaceShellLayout} so the user keeps the same left sidebar and
 * shell chrome as every other workspace surface — the settings page feels
 * like a continuation of the workspace, not a modal detour.</p>
 *
 * <p>The settings canvas doesn't watch per-project runtime state for the
 * sidebar (it's not editing a single project's session), so the live-status
 * maps are intentionally omitted — the sidebar falls back to its 15s polled
 * list which is sufficient for ambient awareness while the user is editing
 * workspace-level settings.</p>
 */
export function WorkspaceSettingsRoute() {
  return (
    <WorkspaceShellLayout>
      <WorkspaceSettingsView />
    </WorkspaceShellLayout>
  )
}
