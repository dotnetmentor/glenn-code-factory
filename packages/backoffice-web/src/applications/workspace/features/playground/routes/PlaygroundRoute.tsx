import { WorkspaceShellLayout } from '../../project-workspace/components/WorkspaceShellLayout'
import { PlaygroundView } from '../components/PlaygroundView'

/**
 * Route entry for {@code /w/:slug/playground} — the Phase 1 primitives demo
 * page. Lives inside {@link WorkspaceShellLayout} so the user can flip
 * theme/accent via the sidebar footer and watch every primitive repaint.
 *
 * <p>Internal-only — hidden from navigation; reached by typing the URL or
 * deep-linking from the design handoff.
 */
export function PlaygroundRoute() {
  return (
    <WorkspaceShellLayout>
      <PlaygroundView />
    </WorkspaceShellLayout>
  )
}
