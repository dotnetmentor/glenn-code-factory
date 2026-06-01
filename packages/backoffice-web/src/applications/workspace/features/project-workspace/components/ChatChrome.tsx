import { useRef, useState } from 'react'
import { useParams } from 'react-router-dom'
import {
  Box,
  IconButton,
  Tooltip,
  Typography,
} from '@mui/material'
import KeyboardArrowDownIcon from '@mui/icons-material/KeyboardArrowDown'
import MenuIcon from '@mui/icons-material/Menu'
import SettingsIcon from '@mui/icons-material/Settings'
import { useQueryClient } from '@tanstack/react-query'
import { ProjectSettingsDrawer, type ProjectSettingsTab } from '../../project-settings'
import {
  RuntimeState,
  getGetApiConversationsIdQueryKey,
  getGetApiProjectsProjectIdBranchesBranchIdRuntimeStatusQueryKey,
  getGetApiProjectsProjectIdConversationsQueryKey,
  useGetApiConversationsId,
  useGetApiProjectsProjectIdBranchesBranchIdCost,
  usePostApiConversationsIdRename,
  usePostApiProjectsProjectIdBranchesBranchIdRuntimeRestart,
  type ProblemDetails,
} from '../../../../../api/queries-commands'
import { useNotification } from '../../../../shared/contexts/NotificationContext'
import { ConversationPickerPopover } from './ConversationPickerPopover'
import { EditableTitle } from './EditableTitle'
import { formatCostUsd } from './costFormat'
import { RuntimePill } from '../../../shared/primitives'

import {
  chromeTokens,
  surfaceTokens,
  workspaceChromeHeight,
} from '../../../shared/designTokens'

const tokens = {
  ...surfaceTokens,
  ...chromeTokens,
} as const

export interface ChatChromeProps {
  projectId: string
  branchId: string
  /**
   * The currently-active conversation, or {@code null} for the "new
   * conversation" draft state where no conversation has been committed yet
   * (Scenario 1 / Scenario 5 fresh-start canvas).
   */
  activeConversationId: string | null
  /**
   * The effective runtime state, lifted to the route so both this chrome and
   * the legacy {@code ProjectWorkspacePage} share one subscription. Pass
   * whatever the parent computed — REST cold-load OR the SignalR live value,
   * whichever is freshest.
   */
  runtimeState: RuntimeState | string | undefined | null
  /**
   * Optional human-readable error message paired with {@link runtimeState}.
   * Only meaningful when the runtime is {@code Failed} / {@code Crashed} —
   * surfaced in the RuntimePill's tooltip so the user can see <em>why</em>
   * the runtime needs a restart without leaving the chrome. {@code null} /
   * {@code undefined} means "no message" and the tooltip falls back to the
   * generic restart copy.
   */
  runtimeErrorMessage?: string | null
  /**
   * Opens the mobile sidebar drawer. When provided, the chrome renders a
   * small leading hamburger button so the user can reach the projects /
   * branches / workspace switcher without needing a separate header row above
   * the chrome. Wired from {@link WorkspaceShellLayout}'s drawer state via
   * the shell's render-prop chrome slot — pass {@code undefined} on
   * non-narrow viewports to hide the button entirely.
   */
  onOpenMobileSidebar?: () => void
  /**
   * Optional lifted "open project settings" callback. When provided the cog
   * delegates to it (so the route can also surface the same affordance
   * elsewhere — notably the mobile bottom tab bar). When omitted the chrome
   * falls back to its legacy local-state drawer, preserving standalone /
   * smoke-test callers.
   */
  onOpenSettings?: (tab?: ProjectSettingsTab) => void
  /**
   * When {@code true}, the project-settings cog is suppressed in the chrome
   * entirely. Used on narrow viewports where the same affordance is
   * surfaced in the bottom tab bar instead, to avoid duplicating the entry
   * point in two places.
   */
  hideSettingsCog?: boolean
}

/**
 * The thin horizontal strip at the top of the chat canvas (P2.3).
 *
 * <p>From left to right:
 * <ol>
 *   <li>The conversation picker trigger — shows the current conversation
 *       title (or "New conversation" in draft mode) plus a chevron. Clicking
 *       opens {@link ConversationPickerPopover}, which lists active
 *       conversations on this branch with "+ New conversation" as the first
 *       row. The title text itself remains inline-editable via
 *       {@link EditableTitle} so rename gestures still work in place.</li>
 *   <li>A spacer pushing the right cluster to the far edge.</li>
 *   <li>The runtime indicator — a single dot + one-word state label, quieter
 *       than {@code RuntimeStatusBadge}. Pulses only while the runtime is
 *       transitional.</li>
 *   <li>The project-settings overflow icon (the only place project settings
 *       is reachable from this surface).</li>
 * </ol></p>
 *
 * <p>Branch switching is intentionally <em>not</em> rendered here — that
 * affordance lives in the left workspace drawer per the
 * {@code multi-conversation-switching} spec, freeing this header to be the
 * primary surface for switching between conversations on the current branch.</p>
 *
 * <p>Runtime state is <em>not</em> fetched here — it is lifted to the route
 * so the legacy {@code ProjectWorkspacePage} and this chrome share one
 * SignalR subscription. See {@code ProjectWorkspaceRoute.tsx}.</p>
 */
export function ChatChrome({
  projectId,
  branchId,
  activeConversationId,
  runtimeState,
  runtimeErrorMessage,
  onOpenMobileSidebar,
  onOpenSettings,
  hideSettingsCog = false,
}: ChatChromeProps) {
  const queryClient = useQueryClient()
  const { slug = '' } = useParams<{ slug: string }>()

  // ── Conversation title (left zone) ──────────────────────────────────────
  const conversationQuery = useGetApiConversationsId(activeConversationId ?? '', {
    query: { enabled: !!activeConversationId },
  })
  const renameMutation = usePostApiConversationsIdRename()

  const conversationTitle = conversationQuery.data?.title ?? ''

  // ── Conversation picker popover ────────────────────────────────────────
  //
  // This is now the PRIMARY trigger in the chat header — branch switching
  // has moved to the left workspace drawer per the
  // {@code multi-conversation-switching} spec. The popover is anchored off
  // the title cluster; the chevron is always visible because
  // "+ New conversation" is the first action inside, so the trigger has
  // value even on branches with zero existing conversations. Clicking the
  // title text itself still enters rename mode via EditableTitle's
  // uncontrolled click-to-edit, preserving the existing rename UX.
  const titleAnchorRef = useRef<HTMLDivElement | null>(null)
  const [pickerOpen, setPickerOpen] = useState(false)

  const onTitleCommit = async (next: string) => {
    if (!activeConversationId) return
    await new Promise<void>((resolve) => {
      renameMutation.mutate(
        { id: activeConversationId, data: { title: next } },
        {
          onSuccess: () => resolve(),
          onError: () => {
            console.warn('[ChatChrome] rename failed; reverted')
            resolve()
          },
          onSettled: () => {
            // Refresh both the conversation detail (this title) and the
            // sidebar list (which renders the same title in the row).
            queryClient.invalidateQueries({
              queryKey: getGetApiConversationsIdQueryKey(activeConversationId),
            })
            queryClient.invalidateQueries({
              queryKey: getGetApiProjectsProjectIdConversationsQueryKey(projectId),
            })
          },
        },
      )
    })
  }

  // ── Branch cost rollup (right zone) ─────────────────────────────────────
  // Fetches the running total spend for THIS branch across every session that
  // has cost stamped on it. The number itself is informational: there's no
  // click handler in v1, just a tooltip explaining what the number represents
  // and a session count for context. We hide the badge while the query is
  // loading or when the total is exactly $0 — a freshly-created branch with no
  // billed turns yet shouldn't paint a "$0.00" anchor in the chrome.
  const branchCostQuery = useGetApiProjectsProjectIdBranchesBranchIdCost(
    projectId,
    branchId,
    {
      query: {
        enabled: !!projectId && !!branchId,
        staleTime: 30_000,
        refetchOnWindowFocus: true,
      },
    },
  )
  const branchCost = branchCostQuery.data
  const showBranchCost =
    branchCost !== undefined &&
    !branchCostQuery.isLoading &&
    (branchCost.totalCostUsd ?? 0) > 0

  // ── Runtime restart (Suspended → Waking, Failed/Crashed → Booting) ──────
  //
  // The RuntimePill below is the SINGLE restart/wake entry-point — the old
  // "Runtime is suspended. Wake" / "The runtime didn't finish provisioning.
  // Restart" banner strips have been removed entirely. The pill becomes
  // interactive whenever the runtime is in a state the user can recover
  // from with one click: {@link RuntimeState.Suspended} (cold machine,
  // backend dispatches a Fly StartMachine wake), {@link RuntimeState.Failed}
  // and {@link RuntimeState.Crashed} (full restart). The endpoint is the
  // same for all three — the backend dispatches internally based on the
  // current state.
  //
  // On success we invalidate the runtime status query so the SignalR
  // state-change push repaints the pill in real time. Disabled while the
  // mutation is in flight so a quick double-tap doesn't queue two requests.
  const { showError } = useNotification()
  const restartMutation = usePostApiProjectsProjectIdBranchesBranchIdRuntimeRestart({
    mutation: {
      onSuccess: () => {
        void queryClient.invalidateQueries({
          queryKey:
            getGetApiProjectsProjectIdBranchesBranchIdRuntimeStatusQueryKey(
              projectId,
              branchId,
            ),
        })
      },
      onError: (err: unknown) => {
        showError(mapRestartError(err))
      },
    },
  })
  const isSuspended = runtimeState === RuntimeState.Suspended
  const isFailed =
    runtimeState === RuntimeState.Failed || runtimeState === RuntimeState.Crashed
  const needsRestart = isSuspended || isFailed
  const handleRestart = () => {
    if (!needsRestart) return
    if (restartMutation.isPending) return
    restartMutation.mutate({ projectId, branchId })
  }
  // While the restart request is in flight we swap the pill's sub-text to a
  // verb-tense matching the current state so the user sees their click
  // landed before the SignalR push flips the state. Once the backend pushes
  // the new state (Waking / Booting) the default sub for the RuntimePill
  // takes over automatically.
  const runtimePillSub = restartMutation.isPending
    ? isSuspended
      ? 'waking…'
      : isFailed
        ? 'restarting…'
        : undefined
    : undefined
  // Tooltip copy folds the (optional) backend error message into the
  // restart hint so the user can see WHY the runtime needs a restart
  // without leaving the chrome. Suspended doesn't carry an error message
  // (it's a healthy "cold machine" state), so it always uses the wake copy.
  const runtimePillTitle = (() => {
    if (isSuspended) {
      return restartMutation.isPending
        ? 'Waking runtime…'
        : 'Click to wake runtime'
    }
    if (isFailed) {
      const action = restartMutation.isPending
        ? 'Restarting runtime…'
        : 'Click to restart runtime'
      return runtimeErrorMessage
        ? `${action}\n${runtimeErrorMessage}`
        : action
    }
    return undefined
  })()

  // ── Project settings drawer ─────────────────────────────────────────────
  //
  // Replaces the legacy overflow MoreVert popover menu. Tapping the cog opens
  // a right-anchored drawer with General / Agent permissions / Credentials
  // tabs — the chat thread stays partially visible behind it so the user
  // never loses their place.
  //
  // When the route has lifted the drawer (provides {@code onOpenSettings})
  // we delegate to it and skip rendering our own drawer to avoid two
  // independent dialogs sharing the same surface. The local-state path
  // remains the fallback for standalone / smoke-test callers.
  const isSettingsLifted = onOpenSettings !== undefined
  const [localSettingsOpen, setLocalSettingsOpen] = useState(false)
  const [localSettingsInitialTab, setLocalSettingsInitialTab] =
    useState<ProjectSettingsTab>('general')
  const openSettings = (tab: ProjectSettingsTab = 'general') => {
    if (isSettingsLifted) {
      onOpenSettings(tab)
      return
    }
    setLocalSettingsInitialTab(tab)
    setLocalSettingsOpen(true)
  }

  return (
    <Box
      component="header"
      aria-label="Chat chrome"
      sx={{
        flexShrink: 0,
        // Lock to {@link workspaceChromeHeight} so the chat chrome lid
        // sits flush with the sidebar workspace switcher and the app
        // container's preview / changes chromes — every horizontal
        // hairline divider across the three panels lines up on the same
        // y-grid, making the workspace read as one continuous shelf.
        height: workspaceChromeHeight,
        // Constrain the chrome to its parent chat panel's width. Without an
        // explicit {@code width: 100%} / {@code minWidth: 0}, the right
        // cluster (RuntimePill + cog) was overflowing past the chat panel's
        // right edge because the title's inner {@code minWidth: 0} let the
        // left cluster collapse to width 0 instead of absorbing the squeeze.
        // {@code overflow: hidden} is the seatbelt — the parent panel shell
        // already clips, but stopping the bleed at the chrome itself keeps
        // hover/focus rings inside the panel.
        width: '100%',
        minWidth: 0,
        overflow: 'hidden',
        // Tighten left padding when the hamburger is present so the trigger
        // sits flush at the gutter edge — matches the rhythm of the sidebar's
        // own left padding when the drawer slides in. Right padding shrinks
        // on phones too because the settings cog is suppressed there (the
        // bottom tab bar carries the affordance instead), so the runtime
        // indicator can sit closer to the edge without feeling cramped.
        pl: onOpenMobileSidebar ? 1 : 2,
        pr: { xs: 1.5, md: 1.75 },
        // Reduce the inter-cluster gap on phones — the chrome is title /
        // chevron / spacer / runtime, and 12px gaps eat ~24px of precious
        // 390px-wide real estate for no visible gain.
        gap: { xs: 1, md: 1.5 },
        display: 'flex',
        alignItems: 'center',
        backgroundColor: tokens.chromeBg,
        borderBottom: `1px solid ${tokens.hairline}`,
      }}
    >
      {/* ── Mobile sidebar trigger ──────────────────────────────────────────
          When the parent shell tells us the sidebar is collapsed into an
          overlay drawer (i.e. on narrow viewports) we render a small leading
          hamburger button. It sits inline with the title + status cluster
          so the user gets a navigation affordance without paying for a
          dedicated header row above the chrome. */}
      {onOpenMobileSidebar && (
        <Tooltip title="Open navigation" enterDelay={400}>
          <IconButton
            size="small"
            aria-label="Open workspace navigation"
            onClick={onOpenMobileSidebar}
            sx={{
              flexShrink: 0,
              color: tokens.textMuted,
              p: 0.75,
              '&:hover': {
                color: tokens.textPrimary,
                backgroundColor: tokens.chipHoverBg,
              },
              '&:focus-visible': {
                outline: `2px solid ${tokens.accent}`,
                outlineOffset: 2,
              },
            }}
          >
            <MenuIcon sx={{ fontSize: 18 }} />
          </IconButton>
        </Tooltip>
      )}

      {/* Left zone — title + (quiet) picker chevron.
          {@code flex: 1} mirrors the reference design: the title cluster
          grows to fill all space NOT consumed by the right cluster (runtime
          pill / divider / cog). That replaces the old explicit
          {@code <Box flex={1} />} spacer, which was creating ambiguity in
          how flex distributed remaining width and letting the title cluster
          collapse to its content's {@code min-width: 0} (i.e. zero) instead
          of taking the empty middle. */}
      <Box
        ref={titleAnchorRef}
        sx={{
          minWidth: 0,
          flex: 1,
          display: 'flex',
          alignItems: 'center',
          gap: 0.25,
        }}
      >
        {activeConversationId ? (
          <EditableTitle
            variant="chrome"
            value={conversationTitle}
            onCommit={onTitleCommit}
            ariaLabel="Conversation title"
            // While the conversation is loading we still show *something*
            // calm so the strip doesn't pop in width-wise. After load the
            // real title takes over; if the backend explicitly stored an
            // empty title the same fallback covers that edge case.
            emptyFallback={conversationQuery.isLoading ? '' : 'Untitled conversation'}
            disabled={conversationQuery.isLoading}
          />
        ) : (
          // In draft mode the placeholder doubles as the trigger label —
          // clicking it opens the picker so the user can pick an existing
          // thread or commit to "+ New conversation". The chevron below is
          // the canonical affordance; this just widens the click target.
          <Box
            component="button"
            type="button"
            aria-label="Switch conversation"
            aria-haspopup="dialog"
            aria-expanded={pickerOpen}
            onClick={() => setPickerOpen(true)}
            sx={{
              border: 0,
              outline: 0,
              background: 'transparent',
              p: 0,
              cursor: 'pointer',
              fontSize: '0.9375rem',
              fontWeight: 500,
              letterSpacing: '-0.005em',
              color: 'rgba(0,0,0,0.36)',
              transition: 'color 120ms ease',
              '&:hover': { color: tokens.textPrimary },
              '&:focus-visible': {
                outline: `2px solid ${tokens.accent}`,
                outlineOffset: 2,
                borderRadius: 4,
              },
            }}
          >
            New conversation
          </Box>
        )}

        {/* Conversation picker chevron — the primary trigger in the header
            now that branch switching has moved to the left drawer. Always
            visible on a branch (no gate): the popover's first row is
            "+ New conversation", so the trigger has value even on branches
            with zero existing conversations. The title text keeps its own
            click-to-rename gesture via EditableTitle, so this chevron is the
            unambiguous "open the picker" affordance. */}
        <Tooltip title="Switch conversation" enterDelay={400}>
          <Box
            component="button"
            type="button"
            aria-label="Switch conversation"
            aria-haspopup="dialog"
            aria-expanded={pickerOpen}
            onClick={() => setPickerOpen(true)}
            sx={{
              display: 'inline-flex',
              alignItems: 'center',
              justifyContent: 'center',
              border: 0,
              outline: 0,
              background: 'transparent',
              p: 0.375,
              ml: 0.25,
              borderRadius: 1,
              color: tokens.textMuted,
              cursor: 'pointer',
              transition: 'background-color 120ms ease, color 120ms ease',
              '&:hover': {
                color: tokens.textPrimary,
                backgroundColor: tokens.chipHoverBg,
              },
              '&:focus-visible': {
                color: tokens.textPrimary,
                outline: `2px solid ${tokens.accent}`,
                outlineOffset: 1,
              },
            }}
          >
            <KeyboardArrowDownIcon sx={{ fontSize: 16 }} />
          </Box>
        </Tooltip>
      </Box>

      {/* Conversation picker — anchored off the title cluster. */}
      <ConversationPickerPopover
        open={pickerOpen}
        anchorEl={titleAnchorRef.current}
        onClose={() => setPickerOpen(false)}
        projectId={projectId}
        branchId={branchId}
        activeConversationId={activeConversationId}
      />

      {/* Branch cost rollup — quiet "$X.XX" in the right cluster. Hidden when
          loading or when the total is exactly $0 (a brand-new branch with no
          billed turns shouldn't paint a $0.00 anchor in the chrome). The
          tooltip explains the scope so the number doesn't read as ambiguous
          (per-conversation? per-turn? lifetime?). v1 is purely informational
          — no click handler, no drill-down panel. */}
      {showBranchCost && branchCost && (
        <Tooltip
          title={`Total branch spend across ${branchCost.sessionCount} session${branchCost.sessionCount === 1 ? '' : 's'}. Click for details`}
          enterDelay={400}
        >
          <Typography
            component="span"
            aria-label={`Branch total ${formatCostUsd(branchCost.totalCostUsd)}`}
            sx={{
              flexShrink: 0,
              fontSize: '0.6875rem',
              fontWeight: 500,
              color: tokens.textMuted,
              letterSpacing: '-0.005em',
              lineHeight: 1,
              fontVariantNumeric: 'tabular-nums',
              cursor: 'default',
              transition: 'color 120ms ease',
              '&:hover': { color: tokens.textPrimary },
              // Hide on the narrowest phones — the chrome is already packed
              // (title / chevron / runtime dot / cog), and the cost is the
              // lowest-priority piece in that cluster.
              display: { xs: 'none', sm: 'inline' },
            }}
          >
            {formatCostUsd(branchCost.totalCostUsd)}
          </Typography>
        </Tooltip>
      )}

      {/* ── Runtime pill (Phase 3) ──────────────────────────────────────────
          The Phase 1 {@link RuntimePill} primitive carries the state dot,
          status label, and optional sub-text (e.g. "Running · 2 turns") in a
          chip-shaped 28px-tall capsule that matches the branch pill's visual
          weight. State→color/pulse/label mapping lives inside the primitive
          so this surface stays declarative — we just hand it the freshest
          {@code runtimeState} the route observed.
          <p>Wrapped in a {@code flexShrink: 0} Box so the right cluster
          (cost / runtime / cog) holds its intrinsic width and the title
          cluster on the left absorbs any width shortage instead of letting
          the cog overflow past the chat panel's right edge.</p> */}
      <Box sx={{ flexShrink: 0, display: 'inline-flex', alignItems: 'center' }}>
        <RuntimePill
          state={runtimeState}
          // The pill is the SINGLE restart/wake entry-point. Suspended →
          // wake (Fly StartMachine), Failed / Crashed → full restart. The
          // backend dispatches internally — same endpoint, same hook. Every
          // other state keeps the pill inert (default cursor, no hover) so
          // the affordance is unambiguous when it IS available.
          onClick={needsRestart ? handleRestart : undefined}
          subLabel={runtimePillSub}
          title={runtimePillTitle}
        />
      </Box>

      {/* Vertical hairline divider — visually separates the live-state
          cluster (cost + runtime) from the static configuration affordance
          (the settings cog). 18px tall so it reads as a deliberate seam
          rather than the chrome's own bottom border bleeding upward.
          ⚠️ {@code width: '1px'} (not {@code width: 1}) — in MUI sx the
          numeric {@code 1} is the theme spacing multiplier and resolves to
          {@code 100%} for the width prop, which would bloat this sliver
          into a full-chrome-width bar and crush the title + cog clusters. */}
      {!hideSettingsCog && (
        <Box
          aria-hidden
          sx={{
            width: '1px',
            height: 18,
            backgroundColor: tokens.hairline,
            flexShrink: 0,
            mx: 0.25,
            display: { xs: 'none', sm: 'block' },
          }}
        />
      )}

      {/* Project settings affordance — opens the right-anchored drawer.
          Suppressed on narrow viewports where the same affordance is
          surfaced from the bottom tab bar in the canvas instead, so we don't
          paint two entry points fighting for the same 375px-wide strip. */}
      {!hideSettingsCog && (
        <Tooltip title="Project settings">
          <IconButton
            size="small"
            aria-label="Project settings"
            onClick={() => openSettings('general')}
            sx={{
              // Hold the cog at its intrinsic 30px size so a tight chrome (a
              // long conversation title plus a wide RuntimePill) can't squeeze
              // it to 0 width and visually float it past the chat panel's
              // right edge.
              flexShrink: 0,
              color: tokens.textMuted,
              p: 0.75,
              '&:hover': {
                color: tokens.textPrimary,
                backgroundColor: tokens.chipHoverBg,
              },
            }}
          >
            <SettingsIcon sx={{ fontSize: 18 }} />
          </IconButton>
        </Tooltip>
      )}

      {/* Only render the drawer locally when the parent hasn't lifted it.
          When lifted, the route owns one drawer instance shared with the
          mobile tab bar's settings button — see ProjectWorkspaceRoute. */}
      {!isSettingsLifted && (
        <ProjectSettingsDrawer
          open={localSettingsOpen}
          onClose={() => setLocalSettingsOpen(false)}
          projectId={projectId}
          branchId={branchId}
          slug={slug}
          initialTab={localSettingsInitialTab}
        />
      )}
    </Box>
  )
}

interface AxiosLikeError {
  response?: {
    status?: number
    data?: ProblemDetails & { error?: string }
  }
  message?: string
}

/**
 * Map a runtime-restart mutation error to a toast message. Used for both the
 * Suspended → wake and the Failed/Crashed → restart paths (same endpoint).
 *
 * <p>The backend returns errors in <c>ProblemDetails</c> shape with a
 * project-specific <c>error</c> string set by the handler (see
 * <c>RestartRuntimeHandler</c>) — typically prefixed with <c>conflict:</c> or
 * <c>not-found:</c>. We strip those sentinel prefixes (they're for the
 * controller's status-code mapping, not the user) and surface the rest, since
 * the message is already user-readable (e.g. "Runtime is in state Online; can
 * only restart from Suspended, Failed, or Crashed.").</p>
 *
 * <p>The previous "Runtime is already restarting." copy on every 409 was
 * misleading — the duplicate-click race it was guarding is already prevented
 * by <c>restartMutation.isPending</c> at the call site, and the actual 409
 * paths the backend exercises are wrong-state restarts and wake-command
 * failures, none of which mean "already restarting".</p>
 */
function mapRestartError(err: unknown): string {
  const e = err as AxiosLikeError | null | undefined
  const raw = e?.response?.data?.error ?? e?.response?.data?.detail
  if (raw) {
    // Strip the handler's sentinel prefixes ("conflict: ...", "not-found: ...")
    // — they exist to drive the controller's status-code mapping, not for the
    // user. The remaining message is already phrased for the user.
    return raw.replace(/^(conflict:|not-found:)\s*/, '').trim()
  }
  if (e?.response?.status === 409) {
    return "Runtime is in a state that can't be restarted right now."
  }
  return "Couldn't restart the runtime. Try again in a moment."
}
