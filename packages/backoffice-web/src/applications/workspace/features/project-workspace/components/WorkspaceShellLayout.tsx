import { useEffect, useState, type ReactNode } from 'react'
import { useLocation } from 'react-router-dom'
import { Box, Drawer, IconButton, useMediaQuery } from '@mui/material'
import { useTheme } from '@mui/material/styles'
import MenuIcon from '@mui/icons-material/Menu'
import type { LiveProjectStatus } from '../routes/ProjectWorkspaceRoute'
import { ProjectsBranchesSidebar } from './ProjectsBranchesSidebar'
import { useSidebarCollapsed } from '@/applications/shared/hooks/useSidebarCollapsed'

import {
  chromeTokens,
  surfaceTokens,
  workspaceCanvasInset,
  workspacePanelShellSx,
  workspaceSidebarWidth,
} from '../../../shared/designTokens'

/** Compact-rail width for the collapsed sidebar — matches the reference. */
const WORKSPACE_SIDEBAR_COLLAPSED_WIDTH = 56

export interface WorkspaceShellLayoutProps {
  /**
   * Per-branch live runtime-state overlay forwarded into the sidebar so
   * branch rows reflect freshest hub state for Failed/Crashed affordances.
   */
  liveStatusByBranchId?: Map<string, LiveProjectStatus>
  /** Per-project in-flight turn count delta forwarded into the sidebar. */
  liveRunningTurnByProjectId?: Map<string, number>
  /** The right-side canvas content. */
  children: ReactNode
  /**
   * Optional controlled drawer state for mobile/tablet viewports. When
   * undefined the layout owns the drawer state itself — useful for routes
   * that don't need a hamburger button.
   */
  drawerOpen?: boolean
  onDrawerClose?: () => void
  /**
   * Opt out of the auto-rendered mobile hamburger trigger. Routes that lift
   * their own trigger into a custom chrome strip (e.g.
   * {@code ProjectWorkspaceShell}'s chat chrome row) set this to {@code true}
   * so the layout doesn't paint a second hamburger on top of theirs.
   */
  suppressMobileTrigger?: boolean
}

/**
 * Structural shell shared by every workspace canvas route — the New Session
 * route and the per-branch ProjectWorkspaceShell both render through this.
 *
 * <p>Shape:
 * <ul>
 *   <li>A full-bleed paper-toned canvas (no chrome row above — the breadcrumb
 *       spine was retired with the new-session affordance).</li>
 *   <li>A {@link workspaceSidebarWidth}px fixed sidebar on the left (the
 *       projects + branches navigator).
 *       On viewports {@code <=md} the sidebar collapses into an overlay
 *       drawer; the canvas owner is expected to render its own hamburger
 *       trigger inside {@link children}.</li>
 *   <li>A right-side {@code <main>} slot that fills the rest of the viewport
 *       and renders {@link children}.</li>
 * </ul></p>
 *
 * <p>The shell does NOT render the per-branch chrome strip, debug panel, or
 * any SignalR wiring — those remain in {@code ProjectWorkspaceShell} so the
 * New Session route can opt out of them entirely.</p>
 */
export function WorkspaceShellLayout({
  liveStatusByBranchId,
  liveRunningTurnByProjectId,
  children,
  drawerOpen: controlledDrawerOpen,
  onDrawerClose,
  suppressMobileTrigger = false,
}: WorkspaceShellLayoutProps) {
  const theme = useTheme()
  const isNarrow = useMediaQuery(theme.breakpoints.down('md'))
  const [uncontrolledDrawerOpen, setUncontrolledDrawerOpen] = useState(false)
  const drawerOpen =
    controlledDrawerOpen ?? uncontrolledDrawerOpen
  const closeDrawer =
    onDrawerClose ?? (() => setUncontrolledDrawerOpen(false))
  const openDrawer = () => setUncontrolledDrawerOpen(true)

  // Auto-close the mobile drawer on route changes. Without this, the user
  // taps a project / branch row, the route changes, but the overlay drawer
  // stays open and they have to manually dismiss it before they can see the
  // canvas they just navigated to. Tracks {@code location.key} so any
  // navigation (incl. replace, back-button) closes the drawer — but only if
  // it's actually open, so we don't fight controlled drawer state.
  const location = useLocation()
  useEffect(() => {
    if (drawerOpen) {
      closeDrawer()
    }
    // Intentionally only watching the route key — we want this to fire on
    // navigation, not on every re-render where drawerOpen happens to be true.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [location.key])

  // ── Sidebar (agent-native projects + branches navigator) ─────────────────
  //
  // Collapsed state is persisted via {@link useSidebarCollapsed} and synced
  // in-tab so toggling it from inside the sidebar reactively resizes this
  // outer wrapper. We only honour the collapsed width on wide viewports —
  // the mobile drawer is already a temporary surface, so collapse is moot.
  const [collapsed] = useSidebarCollapsed()
  const desktopWidth = collapsed
    ? WORKSPACE_SIDEBAR_COLLAPSED_WIDTH
    : workspaceSidebarWidth
  const sidebarContent = (
    <Box
      sx={{
        width: isNarrow ? workspaceSidebarWidth : desktopWidth,
        // Smooth width animation when the user toggles collapse. Matches the
        // 200ms cubic-bezier used elsewhere in the workspace shell so chrome
        // motion reads as a single coordinated language.
        transition: 'width 200ms cubic-bezier(.2,.7,.2,1)',
        height: '100%',
        display: 'flex',
        flexDirection: 'column',
        flexShrink: 0,
        // {@link workspacePanelShellSx} sets {@code overflow: hidden} on the
        // desktop branch; the narrow branch adds it inline so the drawer
        // chrome doesn't bleed past the sidebar's rounded corners.
        ...(isNarrow
          ? {
              backgroundColor: surfaceTokens.chromeBg,
              borderRight: `1px solid ${surfaceTokens.hairline}`,
              overflow: 'hidden',
            }
          : workspacePanelShellSx),
      }}
    >
      <ProjectsBranchesSidebar
        liveStatusByBranchId={liveStatusByBranchId}
        liveRunningTurnByProjectId={liveRunningTurnByProjectId}
      />
    </Box>
  )

  return (
    <Box
      sx={{
        // iOS Safari quirk: `100vh` includes the URL bar + bottom chrome that
        // dynamically retracts as the user scrolls, so the chat container ends
        // up taller than the actual visible area and rows get clipped under the
        // bottom nav. `100dvh` (dynamic viewport height) tracks the *currently
        // visible* viewport and recalculates as chrome shows/hides. We keep
        // `100vh` as a fallback for browsers that don't support `dvh` yet
        // (pre-iOS 15.4 / Chrome 108).
        height: '100vh',
        '@supports (height: 100dvh)': {
          height: '100dvh',
        },
        width: '100vw',
        display: 'flex',
        flexDirection: 'row',
        backgroundColor: surfaceTokens.canvasBg,
        color: surfaceTokens.textPrimary,
        overflow: 'hidden',
        ...(!isNarrow && {
          p: workspaceCanvasInset.desktopPadding,
          gap: workspaceCanvasInset.panelGap,
          [`@media (min-width: ${theme.breakpoints.values.lg}px)`]: {
            gap: workspaceCanvasInset.panelGapLg,
          },
        }),
      }}
    >
      {/* Desktop sidebar (fixed, non-resizable in v1) */}
      {!isNarrow && sidebarContent}

      {/* Mobile / tablet overlay drawer */}
      {isNarrow && (
        <Drawer
          anchor="left"
          open={drawerOpen}
          onClose={closeDrawer}
          ModalProps={{ keepMounted: true }}
          PaperProps={{
            sx: {
              width: workspaceSidebarWidth,
              backgroundColor: surfaceTokens.chromeBg,
              borderRight: `1px solid ${surfaceTokens.hairline}`,
            },
          }}
        >
          <Box sx={{ width: workspaceSidebarWidth, height: '100%' }}>
            <ProjectsBranchesSidebar
              liveStatusByBranchId={liveStatusByBranchId}
              liveRunningTurnByProjectId={liveRunningTurnByProjectId}
            />
          </Box>
        </Drawer>
      )}

      {/* Right canvas */}
      <Box
        component="main"
        sx={{
          flex: 1,
          minWidth: 0,
          height: '100%',
          display: 'flex',
          flexDirection: 'column',
          backgroundColor: surfaceTokens.canvasBg,
          transition: 'background-color 200ms ease',
          overflow: 'hidden',
          position: 'relative',
          // iOS PWA with status-bar-style=black-translucent extends content
          // under the notch / Dynamic Island. Without this pad the top row of
          // chat messages / floating hamburger ends up behind the iPhone
          // hardware. `env(safe-area-inset-top)` resolves to 0 on devices
          // without a notch and on browsers in non-immersive mode.
          paddingTop: 'env(safe-area-inset-top, 0px)',
        }}
      >
        {/* ── Floating mobile hamburger ─────────────────────────────────────
            On mobile the desktop sidebar is hidden inside an overlay drawer.
            Without a trigger button, the user has no way to reach the
            project list, workspace switcher, or settings. This floating
            IconButton sits in the top-left of the canvas column,
            position-absolute so it doesn't push canvas content down on its
            own. Calm canvases (landing, new-session, settings) have enough
            top padding that the button sits naturally in whitespace.

            Surfaces that paint their own inline trigger (e.g.
            {@code ProjectWorkspaceShell}'s chat chrome strip) pass
            {@code suppressMobileTrigger} to opt out so two hamburgers don't
            stack on top of each other. */}
        {isNarrow && !suppressMobileTrigger && (
          <IconButton
            size="small"
            aria-label="Open workspace navigation"
            onClick={openDrawer}
            sx={{
              position: 'absolute',
              top: 8,
              left: 8,
              zIndex: 10,
              color: surfaceTokens.textMuted,
              backgroundColor: 'rgba(255, 255, 255, 0.85)',
              backdropFilter: 'blur(6px)',
              border: `1px solid ${surfaceTokens.hairline}`,
              borderRadius: 1.5,
              '&:hover': {
                color: surfaceTokens.textPrimary,
                backgroundColor: 'rgba(255, 255, 255, 1)',
              },
              '&:focus-visible': {
                outline: `2px solid ${chromeTokens.accent}`,
                outlineOffset: 2,
              },
            }}
          >
            <MenuIcon fontSize="small" />
          </IconButton>
        )}
        {children}
      </Box>
    </Box>
  )
}
