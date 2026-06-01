import { useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { Link as RouterLink, useParams } from 'react-router-dom'
import {
  Alert,
  Box,
  Button,
  Card,
  CardContent,
  IconButton,
  Skeleton,
  Stack,
  Tooltip,
  Typography,
  useMediaQuery,
} from '@mui/material'
import { useTheme } from '@mui/material/styles'
import SettingsIcon from '@mui/icons-material/Settings'
import SettingsOutlinedIcon from '@mui/icons-material/SettingsOutlined'
import ChatBubbleOutlineIcon from '@mui/icons-material/ChatBubbleOutline'
import VisibilityIcon from '@mui/icons-material/Visibility'
import DifferenceOutlinedIcon from '@mui/icons-material/DifferenceOutlined'
import ArticleOutlinedIcon from '@mui/icons-material/ArticleOutlined'
import ViewKanbanOutlinedIcon from '@mui/icons-material/ViewKanbanOutlined'
import LinkOffIcon from '@mui/icons-material/LinkOff'
import {
  RuntimeState,
  useGetApiProjectsProjectId,
  useGetApiProjectsProjectIdBranches,
  useGetApiProjectsProjectIdBranchesBranchIdRuntimeStatus,
  type ProblemDetails,
  type ProjectBranchDto,
} from '../../../../../api/queries-commands'
import { useAgentHub, type AgentHubConnection } from '../../../../../lib/signalr'
import { useWorkspace } from '../../../../shared/contexts/WorkspaceContext'
import {
  ReconnectProjectsDialog,
  workspaceColors,
  workspaceFontFamily,
  workspacePanelShellSx,
  workspaceText,
} from '../../../shared'
import { ChatCanvas } from '../components/ChatCanvas'
import { useShellChrome } from '../components/ProjectWorkspaceShell'
import { RuntimeStatusBadge } from '../components/RuntimeStatusBadge'
import { AppContainer, type AppTabId } from '../components/app-container/AppContainer'
import { appContainerTokens } from '../components/app-container/tokens'
import { useResizableSplit } from '../components/app-container/useResizableSplit'

function readErrorDetail(err: unknown): string | null {
  const maybe = err as { response?: { data?: ProblemDetails }; message?: string } | undefined
  return maybe?.response?.data?.detail ?? maybe?.response?.data?.title ?? maybe?.message ?? null
}

interface RouteParams {
  projectId: string
  branchId: string
  [key: string]: string | undefined
}

interface ProjectWorkspacePageProps {
  /**
   * Optional pre-computed runtime state lifted from the route ({@code
   * ProjectWorkspaceRoute}). When provided the page does NOT open its own
   * AgentHub subscription — it just consumes the parent's value. When
   * absent (older call sites / tests) the page falls back to its own HTTP
   * + SignalR plumbing exactly like before.
   */
  runtimeState?: RuntimeState | string | undefined
  /** Optional pre-computed runtime error message paired with {@link runtimeState}. */
  runtimeErrorMessage?: string | null
  /**
   * Optional pre-computed per-branch runtime id lifted from the route. When
   * provided, this is used in place of the project's default-branch
   * {@code runtimeId} so per-branch features (e.g. the diff endpoint in the
   * Changes tab) target the correct runtime on non-default branches. When
   * absent the page falls back to {@code runtimeStatusQuery.data?.runtimeId}
   * (its own per-branch query) and finally to {@code project.runtimeId}.
   */
  runtimeId?: string
  /**
   * Optional AgentHub connection lifted from the route. When provided the
   * page reuses it (for the future chat panel) and skips opening its own.
   * When absent it opens its own — preserving the standalone behavior.
   */
  hubConnection?: AgentHubConnection | null
  /**
   * Active conversation id from the route's {@code ?c=} search param. When
   * {@code null}/{@code undefined} the chat surface renders its empty state
   * (fresh conversation — no transcript above the composer).
   */
  conversationId?: string | null
  /**
   * Opens the lifted project-settings drawer. When provided, the mobile
   * bottom tab bar renders a 4th "Settings" button that calls this callback
   * instead of switching the canvas panel. {@code undefined} on legacy /
   * standalone call sites — the button is hidden in that case.
   */
  onOpenSettings?: () => void
  /**
   * Whether the lifted project-settings drawer is currently open. Threaded
   * down so the mobile Settings tab button can paint its active-edge
   * underline while the drawer is visible.
   */
  settingsOpen?: boolean
  /**
   * The chat chrome strip (title + conversation picker + runtime pill +
   * cog) rendered INSIDE the chat panel rather than above it. This way the
   * three workspace columns (sidebar, chat, app container) read as the
   * same height — there's no floating header above them. The route
   * provides this node; the page is responsible for slotting it at the top
   * of the chat panel on both desktop and mobile.
   */
  chrome?: ReactNode
}

/**
 * Project / branch workspace page (Spec: e2e-smoketest, Scene 3).
 *
 * <p>This is the surface Anna lands on the moment a project is created. It
 * holds three jobs in this card:
 * <ul>
 *   <li>Resolve project + branch metadata for the header.</li>
 *   <li>Show a live runtime status badge driven by SignalR (the source of
 *       truth) with an HTTP fetch as the initial paint.</li>
 *   <li>Reserve a slot for the chat panel that lands in the next card.</li>
 * </ul>
 * </p>
 *
 * <p>P2.3 update: the runtime state and AgentHub connection are now
 * <em>optionally</em> lifted by {@code ProjectWorkspaceRoute} so the new
 * {@code ChatChrome} strip and this page share one subscription. The page
 * still opens its own when called without those props, preserving
 * compatibility for any standalone / smoke-test callers.</p>
 */
export function ProjectWorkspacePage({
  runtimeState: runtimeStateProp,
  runtimeErrorMessage: runtimeErrorMessageProp,
  runtimeId: runtimeIdProp,
  hubConnection: hubConnectionProp,
  conversationId,
  onOpenSettings,
  settingsOpen = false,
  chrome: chromeProp,
}: ProjectWorkspacePageProps = {}) {
  // The chrome can arrive either as an explicit prop (legacy / smoke-test
  // callers) or via the surrounding {@link ProjectWorkspaceShell} context
  // ({@code ProjectWorkspaceRoute} provides it that way so the shell
  // doesn't paint a floating header above the three workspace columns).
  const contextChrome = useShellChrome()
  const chrome = chromeProp ?? contextChrome
  const { projectId = '', branchId = '' } = useParams<RouteParams>()
  const { currentWorkspace, currentSlug } = useWorkspace()

  const projectQuery = useGetApiProjectsProjectId(projectId, {
    query: { enabled: !!projectId },
  })
  const branchesQuery = useGetApiProjectsProjectIdBranches(
    projectId,
    undefined,
    {
      query: { enabled: !!projectId },
    },
  )
  // Only fetch the runtime status here if the parent didn't lift it. This
  // keeps the standalone path unchanged while avoiding a duplicate request
  // when used under ProjectWorkspaceRoute.
  const liftedRuntime = runtimeStateProp !== undefined
  const runtimeStatusQuery = useGetApiProjectsProjectIdBranchesBranchIdRuntimeStatus(
    projectId,
    branchId,
    {
      query: { enabled: !!projectId && !!branchId && !liftedRuntime },
    },
  )

  // ── live runtime state ───────────────────────────────────────────────────
  // Initial paint comes from the HTTP query. Once the AgentHub fires a
  // RuntimeStateChangedNotification we trust SignalR over polling — the hub
  // is the source of truth, the REST endpoint is just for the cold load.
  const [liveRuntimeState, setLiveRuntimeState] = useState<RuntimeState | string | null>(null)
  // Live error message that pairs with `liveRuntimeState`. Only meaningful
  // when the state is Failed — the badge / failure view fall back to the
  // REST cold-load value when this is null.
  const [liveErrorMessage, setLiveErrorMessage] = useState<string | null>(null)
  // Controls the "Reconnect GitHub" dialog launched from the detached-project
  // banner. Declared here (above all early returns) so its hook order is
  // stable across the loading → success transition — moving it below the
  // guards would break the Rules of Hooks the moment the project resolves.
  const [detachedDialogOpen, setDetachedDialogOpen] = useState(false)

  useEffect(() => {
    if (liftedRuntime) return
    if (liveRuntimeState !== null) return
    const initial = runtimeStatusQuery.data?.state ?? projectQuery.data?.runtimeState
    if (initial) setLiveRuntimeState(initial)
  }, [liftedRuntime, runtimeStatusQuery.data?.state, projectQuery.data?.runtimeState, liveRuntimeState])

  const ownHub = useAgentHub({
    projectId: projectId || undefined,
    // Branch scopes the AgentHub group membership for live AgentEvent ticks.
    // Without it, sibling-branch tabs would receive each other's live chat
    // events. Almost always inert because ProjectWorkspaceRoute passes a
    // pre-lifted connection — kept for the standalone / smoke-test callers.
    branchId: branchId || undefined,
    enabled: !!projectId && hubConnectionProp === undefined,
  })
  const connection: AgentHubConnection | null =
    hubConnectionProp !== undefined ? hubConnectionProp : ownHub.connection

  useEffect(() => {
    if (liftedRuntime) return
    if (!connection) return
    const unsubscribe = connection.onRuntimeStateChanged((payload) => {
      // Defence-in-depth: the AgentHub already scopes pushes to per-project
      // groups, but we double-check the projectId match in case a stale
      // connection from a previous navigation gets reused.
      if (payload.projectId !== projectId) return
      setLiveRuntimeState(payload.toState)
      // The backend only attaches `errorMessage` on Failed transitions; clear
      // it on every other transition so a stale message can't linger after
      // the user recreates / recovers the runtime.
      setLiveErrorMessage(payload.errorMessage ?? null)
    })
    return () => {
      unsubscribe()
    }
  }, [liftedRuntime, connection, projectId])

  // ── derived view-model ───────────────────────────────────────────────────
  const branch: ProjectBranchDto | undefined = useMemo(() => {
    const list = branchesQuery.data ?? []
    return list.find((b) => b.id === branchId)
  }, [branchesQuery.data, branchId])

  const effectiveState: RuntimeState | string | undefined = liftedRuntime
    ? runtimeStateProp
    : (liveRuntimeState ??
        runtimeStatusQuery.data?.state ??
        projectQuery.data?.runtimeState)

  // Prefer the SignalR value when we have one (the hub is the source of
  // truth post-subscribe), but fall back to the REST cold-load so the user
  // sees the failure reason even on a hard refresh of an already-Failed
  // runtime.
  const effectiveErrorMessage: string | null = liftedRuntime
    ? (runtimeErrorMessageProp ?? null)
    : (liveErrorMessage ?? runtimeStatusQuery.data?.errorMessage ?? null)

  // ── render guards ────────────────────────────────────────────────────────
  if (!projectId || !branchId) {
    return (
      <Card>
        <CardContent>
          <Alert severity="error">Missing project or branch in URL.</Alert>
        </CardContent>
      </Card>
    )
  }

  const isInitialLoading =
    projectQuery.isLoading ||
    branchesQuery.isLoading ||
    (!liftedRuntime && runtimeStatusQuery.isLoading)

  if (isInitialLoading && !projectQuery.data) {
    return (
      <Card>
        <CardContent sx={{ p: 4 }}>
          <Stack spacing={2}>
            <Skeleton variant="text" width="40%" height={32} />
            <Skeleton variant="text" width="25%" height={20} />
            <Skeleton variant="rounded" height={120} />
          </Stack>
        </CardContent>
      </Card>
    )
  }

  const projectError = projectQuery.isError ? readErrorDetail(projectQuery.error) : null
  const runtimeError =
    !liftedRuntime && runtimeStatusQuery.isError
      ? readErrorDetail(runtimeStatusQuery.error)
      : null

  if (projectError) {
    return (
      <Card>
        <CardContent sx={{ p: 4 }}>
          <Stack spacing={2}>
            <Alert severity="error">{projectError ?? 'Could not load project.'}</Alert>
            <Box>
              <Button variant="outlined" onClick={() => projectQuery.refetch()}>
                Retry
              </Button>
            </Box>
          </Stack>
        </CardContent>
      </Card>
    )
  }

  const project = projectQuery.data
  if (!project) {
    return (
      <Card>
        <CardContent>
          <Alert severity="error">Project not found.</Alert>
        </CardContent>
      </Card>
    )
  }

  // The per-branch runtime id. The lifted prop (from
  // {@code ProjectWorkspaceRoute}) wins because the route already runs the
  // per-branch {@code runtime/status} query; otherwise we fall back to our
  // own copy of that query (standalone path), and finally to
  // {@code project.runtimeId} which is the project's DEFAULT-branch runtime.
  // The default-branch fallback is wrong for non-default branches, which is
  // exactly the bug we're fixing — but it's still the safest last-resort
  // value during the brief moment before {@code runtimeStatusQuery} resolves.
  const effectiveRuntimeId: string | undefined =
    runtimeIdProp ?? runtimeStatusQuery.data?.runtimeId ?? project.runtimeId

  const branchDisplayName = branch?.name ?? project.defaultBranchName ?? 'unknown'
  const workspaceName = currentWorkspace?.name ?? 'Workspace'
  // The project's GitHub installation is null when it was soft-detached (e.g.
  // an org admin disconnected the installation that owned this project). In
  // that state branch / clone / push endpoints all fail with "project is
  // detached — reconnect…", so we surface a calm banner at the top of the
  // canvas so the user understands WHY things look broken AND can fix it.
  const isDetached = project.githubInstallationId == null
  const workspaceSlug = currentSlug ?? ''

  // When the page is mounted inside ProjectWorkspaceRoute, the new shell
  // (ProjectWorkspaceShell + ChatChrome) already shows the workspace, project,
  // branch, runtime state, and settings entry point. Rendering our legacy
  // header Card on top of that would duplicate every one of those affordances,
  // so we hide it. The standalone path (no lifted runtime) keeps the old
  // header intact for backward compatibility.
  return (
    <Stack
      spacing={liftedRuntime ? 0 : 3}
      sx={liftedRuntime ? { flex: 1, minHeight: 0 } : undefined}
    >
      {!liftedRuntime && (
        <Card>
          <CardContent sx={{ p: 4 }}>
            <Stack
              direction={{ xs: 'column', sm: 'row' }}
              spacing={2}
              alignItems={{ xs: 'flex-start', sm: 'center' }}
              justifyContent="space-between"
            >
              <Box>
                <Typography variant="overline" color="text.secondary">
                  {workspaceName}
                </Typography>
                <Typography variant="h4" component="h1">
                  {project.name}
                </Typography>
                <Typography variant="body2" color="text.secondary" sx={{ mt: 0.5 }}>
                  {project.name} · branch: {branchDisplayName}
                </Typography>
              </Box>
              <Stack direction="row" spacing={1} alignItems="center">
                <RuntimeStatusBadge
                  state={effectiveState}
                  errorMessage={effectiveErrorMessage}
                />
                <Tooltip title="Project settings">
                  <IconButton
                    component={RouterLink}
                    to={`/super-admin/projects/${projectId}/settings/credentials`}
                    size="small"
                    aria-label="Project settings"
                  >
                    <SettingsIcon fontSize="small" />
                  </IconButton>
                </Tooltip>
              </Stack>
            </Stack>
          </CardContent>
        </Card>
      )}

      {runtimeError && (
        <Alert
          severity="warning"
          action={
            <Button color="inherit" size="small" onClick={() => runtimeStatusQuery.refetch()}>
              Retry
            </Button>
          }
        >
          {runtimeError}
        </Alert>
      )}

      {/* Detached-installation banner. Paper-tone — NOT red — so it reads as
          "this needs your attention" rather than "something exploded". The
          right side carries the primary recovery affordance (open the
          reconnect dialog preset to just this project). When mounted under
          the new project shell ({@code liftedRuntime}) we render flush with
          the canvas chrome above and below; otherwise we sit inside the
          legacy stack's normal spacing rhythm. */}
      {isDetached && workspaceSlug && (
        <Box
          sx={{
            display: 'flex',
            flexDirection: { xs: 'column', sm: 'row' },
            alignItems: { xs: 'flex-start', sm: 'center' },
            justifyContent: 'space-between',
            gap: 1.5,
            p: 1.5,
            ...(liftedRuntime
              ? {
                  mx: { xs: 2, md: 0 },
                  mt: { xs: 2, md: 0 },
                  mb: { xs: -1, md: 0 },
                }
              : {}),
            backgroundColor: workspaceColors.chromeBg,
            border: `1px solid ${workspaceColors.hairline}`,
            borderRadius: 2,
          }}
        >
          <Box sx={{ display: 'flex', alignItems: 'flex-start', gap: 1.25, minWidth: 0 }}>
            <Box
              sx={{
                flexShrink: 0,
                width: 24,
                height: 24,
                borderRadius: '50%',
                backgroundColor: 'rgba(0,0,0,0.05)',
                color: workspaceText.muted,
                display: 'inline-flex',
                alignItems: 'center',
                justifyContent: 'center',
                mt: 0.125,
              }}
            >
              <LinkOffIcon sx={{ fontSize: 14 }} />
            </Box>
            <Box sx={{ minWidth: 0 }}>
              <Typography
                sx={{
                  fontFamily: workspaceFontFamily.sans,
                  fontSize: '0.875rem',
                  fontWeight: 500,
                  color: workspaceText.primary,
                  letterSpacing: '-0.005em',
                  lineHeight: 1.35,
                }}
              >
                This project&rsquo;s GitHub installation was disconnected.
              </Typography>
              <Typography
                sx={{
                  fontFamily: workspaceFontFamily.sans,
                  fontSize: '0.8125rem',
                  color: workspaceText.muted,
                  letterSpacing: '-0.005em',
                  lineHeight: 1.5,
                  mt: 0.25,
                }}
              >
                Reconnect to enable branches, clones and pushes.
              </Typography>
            </Box>
          </Box>
          <Button
            size="small"
            variant="pill" color="primary"
            onClick={() => setDetachedDialogOpen(true)}
            sx={{ flexShrink: 0, fontSize: '0.8125rem' }}
          >
            Reconnect
          </Button>
        </Box>
      )}

      {isDetached && workspaceSlug && (
        <ReconnectProjectsDialog
          open={detachedDialogOpen}
          onClose={() => setDetachedDialogOpen(false)}
          workspaceSlug={workspaceSlug}
          presetProjectIds={[projectId]}
        />
      )}

      <RuntimeStateView
        state={effectiveState}
        projectId={projectId}
        branchId={branchId}
        runtimeId={effectiveRuntimeId}
        connection={connection}
        conversationId={conversationId ?? null}
        previewHostname={branch?.previewHostname ?? null}
        onOpenSettings={onOpenSettings}
        settingsOpen={settingsOpen}
        currentBranch={branchDisplayName}
        chrome={chrome}
      />
    </Stack>
  )
}

interface RuntimeStateViewProps {
  state: RuntimeState | string | undefined
  projectId: string
  branchId: string
  /**
   * The branch's active runtime id (per-branch, NOT the project's default-
   * branch runtime). Threaded down so the {@link AppContainer} can scope
   * its diff queries (the diff REST endpoints are runtime-scoped) to the
   * branch the user is actually viewing. May be {@code undefined} during
   * the brief window before the per-branch runtime/status query resolves;
   * the leaf consumers gate their queries on it via {@code enabled}.
   */
  runtimeId: string | undefined
  connection: AgentHubConnection | null
  /** Active conversation id from the route's {@code ?c=} param, or null for the empty-canvas state. */
  conversationId: string | null
  /**
   * The active branch's preview subdomain (hostname only). Threaded down from
   * the page-level {@code branch} so the {@link AppContainer} can render its
   * live preview iframe without re-fetching branches.
   */
  previewHostname: string | null
  /**
   * Opens the lifted project-settings drawer (the one owned by
   * {@code ProjectWorkspaceRoute}). When provided, the mobile bottom tab
   * bar renders a Settings button that calls it; when {@code undefined} the
   * Settings button is hidden so legacy / standalone callers don't get a
   * non-functional affordance.
   */
  onOpenSettings?: () => void
  /** Whether that lifted drawer is currently open — drives the tab's
   *  active underline. */
  settingsOpen?: boolean
  /**
   * Resolved display name of the current branch — threaded into
   * {@link AppContainer} so the Changes tab can detect the
   * "you're on the base branch" self-compare case and default its
   * scope to working-tree instead of branch-vs-base.
   */
  currentBranch?: string
  /**
   * The chat chrome strip, rendered INSIDE the chat panel rather than as
   * a floating header above the three workspace columns. On desktop it's
   * the first child of the chat panel; on mobile it sits at the top of the
   * chat-tab area so it stays in lock-step with the conversation surface.
   */
  chrome?: ReactNode
}

/**
 * P4.3 — Runtime state choreography.
 *
 * <p>Replaces the legacy "swap the entire canvas to a Paper for non-Online
 * states" treatment. The transcript surface (sidebar + ChatCanvas) now stays
 * mounted in EVERY runtime state — the runtime is choreographed inline
 * instead:
 * <ul>
 *   <li>The chrome's {@code RuntimePill} is the single restart/wake entry
 *       point. It becomes interactive for {@link RuntimeState.Suspended},
 *       {@link RuntimeState.Failed}, and {@link RuntimeState.Crashed} —
 *       clicking the badge fires the runtime restart endpoint (Suspended →
 *       Fly StartMachine, Failed/Crashed → full restart). The pill carries
 *       the error message in its tooltip, so no banner strip is needed.</li>
 *   <li>{@link ChatCanvas} always renders below. It already adapts its
 *       composer placeholder based on {@code runtimeState} (Suspended wakes
 *       on send; Booting hints at queuing; Failed disables Send). The
 *       backend (P1.6) queues prompts submitted during Booting and
 *       dispatches them once the runtime comes Online.</li>
 * </ul></p>
 *
 * <p>The deliberate design intent: non-Online states should never "eat the
 * room". The canvas stays calm and composed — sidebar + transcript history
 * remain usable while the runtime warms up / cools down / needs attention,
 * and the call to action lives in the chrome badge the user is already
 * looking at for status.</p>
 */
/** The one source of truth for which surface the mobile composition is showing. */
type MobileTabId = 'chat' | 'preview' | 'changes' | 'specs' | 'kanban'

function RuntimeStateView({
  state,
  projectId,
  branchId,
  runtimeId,
  connection,
  conversationId,
  previewHostname,
  onOpenSettings,
  settingsOpen = false,
  currentBranch,
  chrome,
}: RuntimeStateViewProps) {
  // ── Horizontal split between chat (left) and AppContainer (right) ────────
  //
  // The fraction is persisted per project + branch so each project keeps its
  // own preferred ratio. The container ref lets the resize handler measure
  // the row's current pixel width on each mousemove tick.
  const splitContainerRef = useRef<HTMLDivElement | null>(null)
  const { chatFraction, onResizeStart, isResizing } = useResizableSplit({
    storageKey: `appContainerSplit:${projectId}:${branchId}`,
    defaultFraction: 0.55,
    minFraction: 0.25,
    maxFraction: 0.8,
    containerRef: splitContainerRef,
  })

  // ── Responsive composition ───────────────────────────────────────────────
  //
  // <md (i.e. < 900px) collapses the side-by-side split into a single full-
  // width column with a 3-tab bar (Chat / Preview / Changes) at the bottom.
  // Matches WorkspaceShellLayout's {@code isNarrow} breakpoint so the sidebar
  // collapse and canvas tabbing thresholds line up exactly.
  const theme = useTheme()
  const isMobile = useMediaQuery(theme.breakpoints.down('md'))

  // The mobile bar's selected surface. Default to Chat — most useful first
  // surface on a phone-sized device. We keep this state alive across
  // viewport transitions so a user who switches phone→desktop→phone lands
  // back on whichever surface they were last on.
  const [mobileActiveTab, setMobileActiveTab] = useState<MobileTabId>('chat')

  // The Preview / Changes selection within AppContainer. Lifted up so the
  // mobile 3-tab bar can drive it directly — and so that selection survives
  // resizing back to the desktop layout (the AppContainer accepts it as a
  // controlled prop). Default 'preview' matches AppContainer's own previous
  // uncontrolled default.
  const [appActiveTab, setAppActiveTab] = useState<AppTabId>('preview')

  // The transcript / canvas surfaces are ALWAYS mounted in mobile mode too,
  // toggled purely via display:none. This is load-bearing: ChatCanvas owns
  // its own scroll position, the preview iframe owns its in-page state, and
  // the Changes tab owns its diff selection. Unmounting any of them on tab
  // switch would feel broken.

  // Mobile-only — true while the chat composer textarea has focus. Instantly
  // hides the bottom tab bar (display:'none') so the on-screen keyboard has
  // more vertical room to type. Debounced inside ChatCanvas so quick focus
  // hand-offs to nearby affordances (e.g. Send button) don't flash the bar
  // back in. Bar stays mounted so active tab + badge counts survive hide/show.
  const [chatInputFocused, setChatInputFocused] = useState(false)

  if (isMobile) {
    return (
      <Box
        sx={{
          flex: 1,
          minHeight: 0,
          display: 'flex',
          flexDirection: 'column',
          width: '100%',
        }}
      >
        {/* Single panel area — children scroll themselves. */}
        <Box
          sx={{
            flex: 1,
            minHeight: 0,
            display: 'flex',
            flexDirection: 'column',
            position: 'relative',
          }}
        >
          <MobilePanel active={mobileActiveTab === 'chat'}>
            {/* Chrome strip — title + conversation picker + runtime pill
                + cog — pinned at the top of the chat panel so the three
                workspace columns read as the same height (no floating
                header above them). Stays in lock-step with the chat tab
                rather than spanning the whole canvas column. */}
            {chrome}
            <ChatCanvas
              projectId={projectId}
              branchId={branchId}
              conversationId={conversationId}
              connection={connection}
              runtimeState={state}
              onComposerFocusChange={setChatInputFocused}
            />
          </MobilePanel>
          <MobilePanel active={mobileActiveTab !== 'chat'}>
            {/*
              When the user picks Preview or Changes on the mobile bar we
              both flip the mobile tab AND drive AppContainer's internal
              selection so they stay in lock-step. AppContainer's own
              bottom strip is hidden in this mode — the outer 3-tab bar
              supersedes it.
            */}
            <AppContainer
              projectId={projectId}
              branchId={branchId}
              runtimeId={runtimeId ?? ''}
              previewHostname={previewHostname}
              runtimeState={state}
              connection={connection}
              activeTab={appActiveTab}
              onActiveTabChange={setAppActiveTab}
              currentBranch={currentBranch}
              hideTabStrip
            />
          </MobilePanel>
        </Box>
        {/* Bottom tab bar — instantly hidden out of flex flow while the chat
            composer is focused so the on-screen keyboard has more vertical
            room. {@code display:'none'} keeps the bar mounted (active-tab +
            badge state survives) but removes it from layout, letting the
            chat panel above expand. No transition — user asked for instant. */}
        <Box sx={{ display: chatInputFocused ? 'none' : 'block' }}>
          <MobileTabBar
            active={mobileActiveTab}
            settingsOpen={settingsOpen}
            onSelect={(id) => {
              setMobileActiveTab(id)
              if (id === 'preview' || id === 'changes' || id === 'specs' || id === 'kanban') {
                setAppActiveTab(id)
              }
            }}
            onOpenSettings={onOpenSettings}
          />
        </Box>
      </Box>
    )
  }

  // ── Desktop (≥md) — UNCHANGED side-by-side split ─────────────────────────
  return (
    <Box sx={{ flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column' }}>
      <Box
        ref={splitContainerRef}
        sx={{
          flex: 1,
          minHeight: 0,
          display: 'flex',
          flexDirection: 'row',
        }}
      >
        <Box
          sx={{
            ...workspacePanelShellSx,
            flex: `${chatFraction} 1 0`,
            minWidth: 0,
            display: 'flex',
            flexDirection: 'column',
          }}
        >
          {/* Chrome strip — title + conversation picker + runtime pill
              + cog — pinned at the top of the chat panel so the three
              workspace columns (sidebar, chat, app container) read as the
              same height. Lives INSIDE the chat panel rather than as a
              floating header above the columns. */}
          {chrome}
          <ChatCanvas
            projectId={projectId}
            branchId={branchId}
            conversationId={conversationId}
            connection={connection}
            runtimeState={state}
          />
        </Box>
        <SplitHandle onMouseDown={onResizeStart} isResizing={isResizing} />
        <Box
          sx={{
            ...workspacePanelShellSx,
            flex: `${1 - chatFraction} 1 0`,
            minWidth: 0,
            display: 'flex',
            // While the user is dragging the splitter, suppress pointer events
            // on the right pane so the preview iframe (and anything else in
            // AppContainer) doesn't capture mousemove and stall the drag.
            // Without this, the moment the cursor crosses into the iframe the
            // parent window stops receiving mousemove events and the divider
            // appears "stuck" — symptom is sharply worse during streaming
            // because the live app inside the iframe is actively rendering
            // and eagerly consuming pointer events.
            pointerEvents: isResizing ? 'none' : 'auto',
          }}
        >
          <AppContainer
            projectId={projectId}
            branchId={branchId}
            runtimeId={runtimeId ?? ''}
            previewHostname={previewHostname}
            runtimeState={state}
            connection={connection}
            activeTab={appActiveTab}
            onActiveTabChange={setAppActiveTab}
            currentBranch={currentBranch}
          />
        </Box>
      </Box>
    </Box>
  )
}

interface MobilePanelProps {
  active: boolean
  children: ReactNode
}

/**
 * Wrapper that keeps each mobile panel mounted at all times, toggling
 * visibility via {@code display: none}. Mirrors the same pattern
 * AppContainer uses internally for its Preview / Changes tabs — preserving
 * scroll, iframe state, and in-flight queries across tab switches.
 */
function MobilePanel({ active, children }: MobilePanelProps) {
  return (
    <Box
      sx={{
        position: 'absolute',
        inset: 0,
        display: active ? 'flex' : 'none',
        flexDirection: 'column',
        minHeight: 0,
      }}
    >
      {children}
    </Box>
  )
}

interface MobileTabBarProps {
  active: MobileTabId
  /** True when the lifted settings drawer is open — paints the Settings
   *  button's active underline so it reads as the current focus. */
  settingsOpen: boolean
  onSelect: (id: MobileTabId) => void
  /** Optional — when omitted the Settings entry is suppressed entirely
   *  (legacy / standalone callers without a lifted drawer). */
  onOpenSettings?: () => void
}

/**
 * Bottom tab bar for the mobile layout — Chat / Preview / Changes / Settings.
 *
 * <p>Sits flush with the bottom of the canvas column, above any browser
 * chrome. Each button is a 44px minimum touch target per Apple HIG /
 * Material accessibility. The visual idiom — accent underline on the active
 * button — matches {@code AppContainer}'s internal tab strip so the language
 * stays coherent across breakpoints.</p>
 *
 * <p>Mobile mode is intentionally <em>icons only</em>: at phone widths the
 * narrow labels (Chat / Preview / Changes / Settings) crowd the bar and
 * compete with the surfaces above it. Each button carries an {@code
 * aria-label} so screen readers still announce them, and a tooltip is
 * available on hover/long-press for sighted users who need a hint.</p>
 *
 * <p>The Settings button differs from the other three: it does not switch
 * the active panel — it opens a right-anchored drawer that overlays the
 * canvas. The button paints its active underline only while that drawer is
 * open, so the user always sees which surface is currently in focus.</p>
 */
function MobileTabBar({
  active,
  settingsOpen,
  onSelect,
  onOpenSettings,
}: MobileTabBarProps) {
  return (
    <Box
      component="nav"
      aria-label="Workspace surfaces"
      sx={{
        // 56px tab bar height, plus an extra strip of padding equal to the iOS
        // home-indicator inset. On iPhones with a notch / Dynamic Island the
        // bottom of the viewport is occupied by the home-indicator handle —
        // without this padding our last row of tabs sits behind it and is
        // hard to tap. `env(safe-area-inset-bottom)` resolves to 0 on every
        // platform that doesn't have a home indicator, so this is free.
        height: 56,
        paddingBottom: 'env(safe-area-inset-bottom, 0px)',
        boxSizing: 'content-box',
        flexShrink: 0,
        display: 'flex',
        alignItems: 'stretch',
        backgroundColor: appContainerTokens.chromeBg,
        borderTop: `1px solid ${appContainerTokens.hairline}`,
      }}
    >
      {MOBILE_TABS.map((tab) => (
        <MobileTabButton
          key={tab.id}
          active={active === tab.id && !settingsOpen}
          ariaLabel={tab.label}
          onClick={() => onSelect(tab.id)}
          icon={tab.icon}
        />
      ))}
      {onOpenSettings && (
        <MobileTabButton
          active={settingsOpen}
          ariaLabel="Project settings"
          onClick={onOpenSettings}
          icon={
            settingsOpen ? (
              <SettingsIcon fontSize="small" />
            ) : (
              <SettingsOutlinedIcon fontSize="small" />
            )
          }
        />
      )}
    </Box>
  )
}

interface MobileTab {
  id: MobileTabId
  /** Screen-reader / tooltip label only — never rendered as visible text
   *  in the mobile bar (icons-only design). */
  label: string
  icon: ReactNode
}

const MOBILE_TABS: readonly MobileTab[] = [
  { id: 'chat', label: 'Chat', icon: <ChatBubbleOutlineIcon fontSize="small" /> },
  { id: 'preview', label: 'Preview', icon: <VisibilityIcon fontSize="small" /> },
  { id: 'changes', label: 'Changes', icon: <DifferenceOutlinedIcon fontSize="small" /> },
  { id: 'specs', label: 'Specs', icon: <ArticleOutlinedIcon fontSize="small" /> },
  { id: 'kanban', label: 'Kanban', icon: <ViewKanbanOutlinedIcon fontSize="small" /> },
] as const

interface MobileTabButtonProps {
  active: boolean
  /** Accessible name — surfaced via {@code aria-label} and {@code title}.
   *  Labels are intentionally not rendered as visible text in mobile mode. */
  ariaLabel: string
  onClick: () => void
  icon: ReactNode
}

function MobileTabButton({ active, ariaLabel, onClick, icon }: MobileTabButtonProps) {
  return (
    <Tooltip title={ariaLabel} enterDelay={500}>
      <Box
        component="button"
        type="button"
        onClick={onClick}
        aria-label={ariaLabel}
        aria-pressed={active}
        sx={{
          position: 'relative',
          flex: 1,
          minWidth: 44,
          minHeight: 44,
          display: 'inline-flex',
          alignItems: 'center',
          justifyContent: 'center',
          border: 0,
          background: 'transparent',
          cursor: 'pointer',
          color: active ? appContainerTokens.textPrimary : appContainerTokens.textMuted,
          transition: 'color 200ms ease, background-color 200ms ease',
          '& .MuiSvgIcon-root': {
            // Bump the icon slightly — without a label below, a 20px glyph
            // reads as the primary affordance instead of an afterthought.
            fontSize: 22,
          },
          '&:hover': {
            color: appContainerTokens.textPrimary,
            backgroundColor: 'rgba(0, 0, 0, 0.02)',
          },
          '&:focus-visible': {
            outline: `2px solid ${appContainerTokens.accent}`,
            outlineOffset: -2,
          },
          // Active indicator — mirrors AppContainer's TabButton ::after
          // underline, but flipped to render at the top edge since the bar
          // is anchored to the bottom of the canvas.
          '&::after': active
            ? {
                content: '""',
                position: 'absolute',
                left: 12,
                right: 12,
                top: 0,
                height: 2,
                backgroundColor: appContainerTokens.accent,
                borderBottomLeftRadius: 1,
                borderBottomRightRadius: 1,
              }
            : undefined,
        }}
      >
        {icon}
      </Box>
    </Tooltip>
  )
}

interface SplitHandleProps {
  onMouseDown: (event: React.MouseEvent) => void
  isResizing: boolean
}

/**
 * Hand-rolled vertical drag handle between the chat column and the
 * AppContainer. Sits in the gutter between the two floating panels —
 * width matches the canvas gap so it reads as part of the stage, not a
 * third panel. Hover and active states tint to the workspace accent at
 * low opacity to make the affordance discoverable without shouting.
 */
function SplitHandle({ onMouseDown, isResizing }: SplitHandleProps) {
  return (
    <Box
      role="separator"
      aria-orientation="vertical"
      aria-label="Resize chat and preview panels"
      onMouseDown={onMouseDown}
      sx={{
        flexShrink: 0,
        alignSelf: 'stretch',
        width: 8,
        cursor: 'col-resize',
        backgroundColor: isResizing
          ? appContainerTokens.accentActive
          : 'transparent',
        borderRadius: 1,
        transition: 'background-color 200ms ease',
        '&:hover': {
          backgroundColor: appContainerTokens.accentSurface,
        },
      }}
    />
  )
}
