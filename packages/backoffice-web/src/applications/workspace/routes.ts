import AddIcon from '@mui/icons-material/Add'
import PaletteIcon from '@mui/icons-material/Palette'
import RocketLaunchIcon from '@mui/icons-material/RocketLaunch'
import SettingsIcon from '@mui/icons-material/Settings'
import WorkspacesIcon from '@mui/icons-material/Workspaces'
import { RouteDefinition } from '../../app/routing/types'
import { withWorkspaceProvider } from '../shared/contexts/WorkspaceContext'
import { NewProjectPage } from './features/new-project'
import { NewSessionRoute } from './features/new-session'
import { PlaygroundRoute } from './features/playground'
import { ProjectWorkspaceRoute } from './features/project-workspace'
import {
  ProjectRedirectPage,
  WorkspaceHomeLegacyRedirect,
} from './features/projects'
import { WorkspaceLandingRoute } from './features/workspace-landing'
import { WorkspaceSettingsRoute } from './features/workspace-settings'

/**
 * Workspace app routes. The dashboard, members, integrations, repositories,
 * and settings pages were folded into the {@code WorkspaceSettingsDrawer} —
 * see {@code features/workspace-settings}. Project-level agent permissions
 * and BYOK live behind the {@code ProjectSettingsDrawer}. {@code /w/:slug}
 * is the workspace home; {@code /w/:slug/projects} redirects there.
 */
export const workspaceRoutes: RouteDefinition[] = [
  {
    // Workspace home — the calm "Welcome back" resume canvas. Lives inside
    // the WorkspaceShellLayout so the sidebar (projects + branches) is the
    // same one the user navigates with on /new-session, /settings, and the
    // per-branch IDE. Chromeless because the route owns the full viewport.
    path: '/w/:slug',
    label: 'Workspace',
    icon: WorkspacesIcon,
    component: withWorkspaceProvider(WorkspaceLandingRoute),
    hideInNavigation: true,
    chromeless: true,
  },
  {
    // Legacy redirect — the projects index used to be the workspace home.
    // Bookmarks and stale GitHub callback URLs may still land here; bounce
    // to /w/:slug and preserve ?install= / ?reauth= for the landing snackbar.
    path: '/w/:slug/projects',
    label: 'Projects',
    icon: WorkspacesIcon,
    component: withWorkspaceProvider(WorkspaceHomeLegacyRedirect),
    hideInNavigation: true,
    chromeless: true,
  },
  {
    path: '/w/:slug/projects/new',
    label: 'New project',
    icon: AddIcon,
    component: withWorkspaceProvider(NewProjectPage),
    hideInNavigation: true,
  },
  {
    // Placed BEFORE the project-id routes so it cannot be caught as a
    // project slug. The new-session view renders a full-bleed shell with
    // its own sidebar — opt out of the standard top-nav chrome.
    path: '/w/:slug/new-session',
    label: 'New session',
    icon: AddIcon,
    component: withWorkspaceProvider(NewSessionRoute),
    hideInNavigation: true,
    chromeless: true,
  },
  {
    // Legacy redirect — the standalone Integrations page was folded into the
    // settings drawer, but the GitHub App's Setup URL used to bounce users
    // here. Keep the path mounted as a silent <Navigate> to the workspace
    // home so old bookmarks / external references don't 404.
    path: '/w/:slug/integrations',
    label: 'Integrations',
    icon: SettingsIcon,
    component: withWorkspaceProvider(WorkspaceHomeLegacyRedirect),
    hideInNavigation: true,
    chromeless: true,
  },
  {
    // Workspace settings page — General / Members / Integrations /
    // Repositories stacked vertically inside the workspace shell. Sits
    // alongside /new-session and is also reachable from the gear icon at
    // the top of the projects sidebar. Chromeless so the route owns the
    // full viewport — its own sidebar lives inside WorkspaceShellLayout.
    path: '/w/:slug/settings',
    label: 'Workspace settings',
    icon: SettingsIcon,
    component: withWorkspaceProvider(WorkspaceSettingsRoute),
    hideInNavigation: true,
    chromeless: true,
  },
  {
    // Intermediate "open project" route — resolves the project's default
    // branch then `<Navigate replace>`s to the per-branch IDE shell. The
    // resolve takes a single network roundtrip (~100–400ms). Chromeless so
    // the user doesn't see the old AppLayout flash painting behind a brief
    // spinner before the redirect lands on the also-chromeless branch shell.
    path: '/w/:slug/projects/:projectId',
    label: 'Open project',
    icon: RocketLaunchIcon,
    component: withWorkspaceProvider(ProjectRedirectPage),
    hideInNavigation: true,
    chromeless: true,
  },
  {
    path: '/w/:slug/projects/:projectId/branches/:branchId',
    label: 'Project workspace',
    icon: RocketLaunchIcon,
    component: withWorkspaceProvider(ProjectWorkspaceRoute),
    hideInNavigation: true,
    // Opt out of the standard workspace chrome (top nav + side rail). The
    // route renders its own full-bleed IDE shell — see ProjectWorkspaceShell.
    chromeless: true,
  },
  {
    // Phase 1 primitives playground — internal-only demo surface that shows
    // every workspace primitive in every state, so the (mode, accent) flips
    // can be verified without crawling through real screens.
    path: '/w/:slug/playground',
    label: 'Playground',
    icon: PaletteIcon,
    component: withWorkspaceProvider(PlaygroundRoute),
    hideInNavigation: true,
    chromeless: true,
  },
]
