import { createContext, useContext, useState, type ReactNode } from 'react'
import { useParams } from 'react-router-dom'
import { Box, useMediaQuery } from '@mui/material'
import { useTheme } from '@mui/material/styles'
import {
  RuntimeDebugPanelProvider,
  RuntimeLogsPanel,
  useRuntimeDebugPanel,
} from '@/applications/shared/runtime'
import type { LiveProjectStatus } from '../routes/ProjectWorkspaceRoute'
import { WorkspaceShellLayout } from './WorkspaceShellLayout'

/**
 * Holds the resolved chrome node so deep descendants ({@code
 * ProjectWorkspacePage}) can slot it inside the chat panel rather than
 * having the shell paint it as a floating header above the three workspace
 * columns. Provided by {@link ProjectWorkspaceShell}, read by
 * {@code useShellChrome}.
 */
const ShellChromeContext = createContext<ReactNode>(null)

/**
 * Returns the chat chrome node provided by the surrounding
 * {@link ProjectWorkspaceShell}. {@code null} when the shell wasn't given
 * a chrome render-prop (e.g. the New Session route).
 */
export function useShellChrome(): ReactNode {
  return useContext(ShellChromeContext)
}

/**
 * Argument bag passed into the {@link ProjectWorkspaceShellProps#chrome}
 * render-prop. Lets the chrome decide for itself whether to surface a mobile
 * navigation trigger (and how to style it inline) without the shell having
 * to render a separate header row above the chrome.
 */
export interface ChatChromeRenderArgs {
  /** True when the shell has collapsed the desktop sidebar into the overlay
   *  drawer. The chrome typically renders a leading hamburger only in this
   *  case. */
  isNarrow: boolean
  /** Opens the overlay drawer. Stable identity across re-renders. */
  onOpenMobileSidebar: () => void
}

interface RouteParams {
  slug: string
  projectId: string
  branchId: string
  [key: string]: string | undefined
}

interface ProjectWorkspaceShellProps {
  /** The route content that renders inside the canvas (right column). */
  children: ReactNode
  /**
   * Optional thin chrome strip rendered at the top of the canvas column,
   * <em>above</em> {@link children}. Lands the {@code ChatChrome}
   * (title + branch switcher + runtime indicator).
   *
   * <p>Accepts either a plain {@link ReactNode} or a render-prop. The render
   * form lets the chrome receive {@link ChatChromeRenderArgs} (notably the
   * mobile sidebar open callback) so the chrome can inline a hamburger
   * trigger instead of forcing the shell to paint a dedicated header row
   * above it.</p>
   */
  chrome?: ReactNode | ((args: ChatChromeRenderArgs) => ReactNode)
  /**
   * Per-project live runtime-state overlay keyed by projectId. The route
   * owns the single AgentHub connection (joined to both the project AND
   * workspace groups) and fans every {@code runtimeStateChanged} into this
   * map; the sidebar reads it to overlay the freshest live state on top of
   * the polled list value for every row.
   */
  liveStatusByProjectId?: Map<string, LiveProjectStatus>
  /**
   * Per-project in-flight turn count delta keyed by projectId. The polled
   * list provides the baseline {@code runningTurnCount}; this map carries
   * the instant SignalR delta so the sidebar can move a row between IDLE
   * and RUNNING sections without waiting for the next 15s poll.
   */
  liveRunningTurnByProjectId?: Map<string, number>
}

/**
 * Per-branch canvas shell. Composes the structural
 * {@link WorkspaceShellLayout} with the chrome strip + bottom debug panel
 * that are specific to the running-conversation view. The breadcrumb spine
 * was removed when the New Session feature shipped — navigation lives in
 * the sidebar's slim "+ New session" affordance instead.
 */
export function ProjectWorkspaceShell(props: ProjectWorkspaceShellProps) {
  // The bottom log panel needs to be openable from BOTH the conversation
  // sidebar footer (in the chrome column) and the Services tab inside the
  // settings drawer (rendered off the chat chrome). Lifting the state to a
  // provider at the shell root is the only place that's an ancestor of all
  // three call sites.
  return (
    <RuntimeDebugPanelProvider>
      <ProjectWorkspaceShellInner {...props} />
    </RuntimeDebugPanelProvider>
  )
}

function ProjectWorkspaceShellInner({
  children,
  chrome,
  liveStatusByProjectId,
  liveRunningTurnByProjectId,
}: ProjectWorkspaceShellProps) {
  const { projectId = '', branchId = '' } = useParams<RouteParams>()
  const debugPanel = useRuntimeDebugPanel()
  const theme = useTheme()
  const isNarrow = useMediaQuery(theme.breakpoints.down('md'))
  const [drawerOpen, setDrawerOpen] = useState(false)

  const openDrawer = () => setDrawerOpen(true)
  // Resolve the chrome — plain node or render-prop. The render form lets the
  // chrome (typically {@code ChatChrome}) decide whether to inline a leading
  // hamburger; on desktop {@code isNarrow} is false and the chrome should
  // omit the trigger entirely so we don't render an inert affordance.
  const resolvedChrome =
    typeof chrome === 'function'
      ? chrome({ isNarrow, onOpenMobileSidebar: openDrawer })
      : chrome

  return (
    <ShellChromeContext.Provider value={resolvedChrome}>
      <WorkspaceShellLayout
        liveStatusByProjectId={liveStatusByProjectId}
        liveRunningTurnByProjectId={liveRunningTurnByProjectId}
        drawerOpen={drawerOpen}
        onDrawerClose={() => setDrawerOpen(false)}
        suppressMobileTrigger
      >
        {/* ── Canvas column ───────────────────────────────────────────────
            Holds the chat + app-container row beneath. The chrome strip is
            NOT a separate top-level panel anymore — it renders INSIDE the
            chat panel (see {@code ProjectWorkspacePage}, which reads the
            chrome from {@link useShellChrome}) so the three workspace
            columns (sidebar, chat, app) read as the same height instead of
            a floating header above them. */}
        <Box
          sx={{
            flex: 1,
            minHeight: 0,
            minWidth: 0,
            display: 'flex',
            flexDirection: 'column',
          }}
        >
          <Box
            sx={{
              flex: 1,
              minHeight: 0,
              p: { xs: 2, md: 0 },
              display: 'flex',
              flexDirection: 'column',
              overflow: { xs: 'auto', md: 'hidden' },
            }}
          >
            {children}
          </Box>
        </Box>
        {projectId && (
          <RuntimeLogsPanel
            projectId={projectId}
            branchId={branchId}
            open={debugPanel.open}
            initialServiceName={debugPanel.initialServiceName}
            onClose={debugPanel.closePanel}
          />
        )}
      </WorkspaceShellLayout>
    </ShellChromeContext.Provider>
  )
}
