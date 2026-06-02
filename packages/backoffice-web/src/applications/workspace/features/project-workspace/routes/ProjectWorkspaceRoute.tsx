import { useEffect, useMemo, useRef, useState } from 'react'
import { useParams, useSearchParams } from 'react-router-dom'
import { useQueryClient } from '@tanstack/react-query'
import {
  ConversationStatus,
  RuntimeState,
  getGetApiConversationsIdQueryKey,
  getGetApiProjectsProjectIdConversationsQueryKey,
  useGetApiProjectsProjectId,
  useGetApiProjectsProjectIdBranchesBranchIdRuntimeStatus,
  useGetApiProjectsProjectIdConversations,
} from '../../../../../api/queries-commands'
import { useAgentHub, type AgentHubConnection } from '../../../../../lib/signalr'
import { useWorkspace } from '../../../../shared/contexts/WorkspaceContext'
import {
  ProjectSettingsDrawer,
  type ProjectSettingsTab,
} from '../../project-settings'
import { ChatChrome } from '../components/ChatChrome'
import { ProjectWorkspaceShell } from '../components/ProjectWorkspaceShell'
import {
  getLastBranchConversationId,
  setLastBranchConversationId,
} from '../hooks/branchConversationMemory'
import { useProjectTabTitle } from '../hooks/useProjectTabTitle'
import {
  markBranchRead,
  pushActivity,
} from '../hooks/useWorkspaceActivityStore'
import { ProjectWorkspacePage } from './ProjectWorkspacePage'

/**
 * Per-project live status overlay. The route fans hub events into this map;
 * the sidebar reads the map per-row to override the polled list's coarser
 * 15s value with the freshest SignalR-pushed state.
 *
 * <p>The map is keyed by {@code projectId} so the sidebar can show one
 * "Running / Sleeping / Needs Action" bucket per project regardless of how
 * many branches that project has spawned. {@code runtimeId} rides along on
 * the value so per-branch consumers (e.g. the chat-header runtime badge) can
 * verify the overlay event was for the runtime backing the branch they're
 * showing — otherwise a sibling branch's crash inside the same project
 * silently overwrites the map entry and bleeds into the wrong header.</p>
 */
export interface LiveProjectStatus {
  state: RuntimeState | string | null
  errorMessage: string | null
  runtimeId: string | null
}

interface RouteParams {
  slug: string
  projectId: string
  branchId: string
  [key: string]: string | undefined
}

/**
 * Map an AgentEventRunStatus (string, since the backend stringifies the
 * enum for wire stability) onto the activity log's terminal-state vocab.
 *
 * <p>Card 4 (cursor-native-chat-ux): the new wire union folds the old
 * TurnCompleted / TurnFailed / TurnCanceled events into a single
 * {@code StatusEventDto} carrying an {@code AgentEventRunStatus} field — we
 * map its terminal values to the activity log's idle / failed vocab.</p>
 *
 * <p>Returns {@code null} for in-progress / non-terminal events so the
 * activity log never logs noise.</p>
 */
function statusFromRunStatus(
  runStatus: string,
): 'idle' | 'failed' | null {
  switch (runStatus) {
    case 'Finished':
      return 'idle'
    case 'Error':
    case 'Cancelled':
    case 'Expired':
      // We surface non-clean terminals as "failed" — cancellation is rare and
      // a user-initiated cancel still counts as "this didn't finish cleanly"
      // from the activity-log POV.
      return 'failed'
    default:
      return null
  }
}

/**
 * Top-level route entry for {@code /w/:slug/projects/:projectId/branches/:branchId}.
 *
 * <p>Owns the single AgentHub subscription for this surface — both the new
 * {@link ChatChrome} strip AND the legacy {@link ProjectWorkspacePage}
 * (which still renders below the chrome until P3.1 replaces it) read the
 * same {@code runtimeState} / {@code errorMessage} / hub {@code connection}
 * via props, so we are NOT running two independent SignalR connections per
 * tab.</p>
 *
 * <p>P2.3 interim shape:
 * <ul>
 *   <li>{@link ProjectWorkspaceShell} provides the IDE shell + breadcrumb spine
 *       + conversation sidebar.</li>
 *   <li>{@link ChatChrome} is the new chrome strip at the top of the canvas.</li>
 *   <li>{@link ProjectWorkspacePage} renders the body below — its legacy
 *       header card is suppressed when called with a lifted {@code runtimeState}
 *       (i.e. via this route) since the shell + chrome already cover that
 *       surface. Only the runtime-state body (ChatPanel + provisioning /
 *       failure / suspended views) remains until P3.1 lands ChatCanvas v2.</li>
 * </ul></p>
 */
export function ProjectWorkspaceRoute() {
  const { slug = '', projectId = '', branchId = '' } = useParams<RouteParams>()
  const [searchParams, setSearchParams] = useSearchParams()
  const activeConversationId = searchParams.get('c')
  const queryClient = useQueryClient()

  // ── P4.2 Rehydration: auto-select last-opened conversation ───────────────
  //
  // When the user lands on this route with NO `?c=` param, restore the last
  // conversation they had open on this branch (sessionStorage), falling back
  // to the most recently-active non-archived conversation when nothing is
  // remembered or the remembered id is stale.
  //
  // Sidebar branch links include `?c=` from the same store so branch switches
  // usually skip this path entirely.
  const autoSelectFiredForBranchRef = useRef<string | null>(null)
  const prevBranchIdRef = useRef(branchId)
  if (prevBranchIdRef.current !== branchId) {
    prevBranchIdRef.current = branchId
    autoSelectFiredForBranchRef.current = null
  }
  if (
    autoSelectFiredForBranchRef.current !== branchId &&
    activeConversationId !== null
  ) {
    // Cold-load with a `?c=` already in the URL — no auto-select needed for
    // this branch, latch it so a future explicit clear isn't undone.
    autoSelectFiredForBranchRef.current = branchId
  }
  const shouldAutoSelect =
    activeConversationId === null &&
    autoSelectFiredForBranchRef.current !== branchId
  const conversationsListQuery = useGetApiProjectsProjectIdConversations(
    projectId,
    { includeArchived: false },
    {
      query: {
        enabled: !!projectId && !!branchId && shouldAutoSelect,
      },
    },
  )

  const autoSelectCandidate = useMemo(() => {
    if (!shouldAutoSelect) return null
    if (!branchId) return null
    const list = conversationsListQuery.data
    if (!list) return null
    const matches = list.filter(
      (c) => c.branchId === branchId && c.status === ConversationStatus.Active,
    )
    const rememberedId = getLastBranchConversationId(branchId)
    if (rememberedId) {
      const remembered = matches.find((c) => c.id === rememberedId)
      if (remembered) return remembered
    }
    return (
      matches
        .slice()
        .sort((a, b) => {
          const ta = a.lastActivityAt ? Date.parse(a.lastActivityAt) : 0
          const tb = b.lastActivityAt ? Date.parse(b.lastActivityAt) : 0
          return tb - ta
        })[0] ?? null
    )
  }, [shouldAutoSelect, branchId, conversationsListQuery.data])

  useEffect(() => {
    if (!branchId || !activeConversationId) return
    setLastBranchConversationId(branchId, activeConversationId)
  }, [branchId, activeConversationId])

  useEffect(() => {
    // Only fires on the cold-load path — the {@code shouldAutoSelect} gate
    // (latched by the branch ref above) blocks subsequent re-entries so an
    // explicit "Start a new conversation" click can land on the empty state
    // instead of being bounced back into the most-recent conversation.
    if (!shouldAutoSelect) return
    if (!autoSelectCandidate) return
    autoSelectFiredForBranchRef.current = branchId
    const next = new URLSearchParams(searchParams)
    next.set('c', autoSelectCandidate.id)
    setSearchParams(next, { replace: true })
  }, [
    shouldAutoSelect,
    autoSelectCandidate,
    branchId,
    searchParams,
    setSearchParams,
  ])

  // ── live runtime state (lifted from ProjectWorkspacePage) ────────────────
  // The HTTP cold-load paints first; the hub takes over as the source of
  // truth the moment it fires a RuntimeStateChangedNotification. Errors are
  // logged in the page itself — we don't need to mirror that surface here.
  const projectQuery = useGetApiProjectsProjectId(projectId, {
    query: { enabled: !!projectId },
  })
  const runtimeStatusQuery = useGetApiProjectsProjectIdBranchesBranchIdRuntimeStatus(
    projectId,
    branchId,
    {
      query: { enabled: !!projectId && !!branchId },
    },
  )

  const { currentWorkspace } = useWorkspace()
  const workspaceId = currentWorkspace?.id

  // ── Workspace-wide live status overlay ───────────────────────────────────
  // Keyed by projectId. The route joins both the project-{projectId} AND
  // workspace-{workspaceId} groups via {@link useAgentHub}, so every runtime
  // state transition for every project in this workspace lands in this map.
  // The sidebar consumes it to overlay live state on top of the polled
  // {@code useGetApiWorkspacesSlugProjects} list — no per-row subscriptions.
  const [liveStatusByProjectId, setLiveStatusByProjectId] = useState<
    Map<string, LiveProjectStatus>
  >(() => new Map())

  // Parallel map for the in-flight turn count delta. The polled list carries
  // {@code runningTurnCount} on every refetch (every 15s); this map provides
  // the instant-feedback delta between refetches so a TurnStarted lifts a
  // project from IDLE → RUNNING without waiting for the next poll cycle.
  //
  // Note: agentEvent notifications don't carry projectId, so we can only
  // attribute deltas to the actively-viewed project (the only conversation
  // for which we know the projectId without an extra lookup). For other
  // workspace projects the 15s poll is the source of truth — pragmatic
  // trade-off matching the spec's "don't over-engineer" guidance.
  const [liveRunningTurnByProjectId, setLiveRunningTurnByProjectId] = useState<
    Map<string, number>
  >(() => new Map())

  const { connection } = useAgentHub({
    projectId: projectId || undefined,
    // Branch scopes the AgentHub group membership — without it the connection
    // would land in no branch-{id} group and miss live AgentEvent ticks. The
    // hook rekeys on branchId so a branch switch tears down the old socket
    // and joins the new branch's group.
    branchId: branchId || undefined,
    workspaceId: workspaceId || undefined,
    enabled: !!projectId,
  })

  // Runtime state fan-out: react to events for ANY project (workspace group),
  // not just the active one — the sidebar needs the map across every row.
  useEffect(() => {
    if (!connection) return
    const unsubscribe = connection.onRuntimeStateChanged((payload) => {
      setLiveStatusByProjectId((prev) => {
        const next = new Map(prev)
        next.set(payload.projectId, {
          state: payload.toState,
          errorMessage: payload.errorMessage ?? null,
          // Carry the runtime id so per-branch consumers can verify this
          // event was for THEIR runtime — without it, sibling branches in
          // the same project silently overwrite each other's status.
          runtimeId: payload.runtimeId ?? null,
        })
        return next
      })
    })
    return () => {
      unsubscribe()
    }
  }, [connection])

  // Turn lifecycle fan-out: bump / decrement the live delta for the active
  // project. We attribute events to {@code projectId} because the
  // agentEvent payload doesn't carry it; events from other workspace
  // projects also arrive here but we can't route them, so they're ignored
  // and the 15s poll picks them up. Clamp at 0 so a stray Completed without
  // a matching Started can't drag the count negative.
  useEffect(() => {
    if (!connection) return
    if (!projectId) return
    const unsubscribe = connection.onAgentEvent((evt) => {
      // Card 4 (cursor-native-chat-ux): the inner polymorphic event lives
      // at {@code evt.event} with an {@code eventKind} discriminator and a
      // structural {@code status} field for the status subtype. We only
      // care about status transitions here — every other kind is silent.
      const inner = evt.event as { eventKind?: string; status?: string }
      if (inner.eventKind !== 'status') return
      const isStart = inner.status === 'Running'
      const isTerminal =
        inner.status === 'Finished' ||
        inner.status === 'Error' ||
        inner.status === 'Cancelled' ||
        inner.status === 'Expired'
      if (!isStart && !isTerminal) return
      setLiveRunningTurnByProjectId((prev) => {
        const next = new Map(prev)
        const current = next.get(projectId) ?? 0
        if (isStart) {
          next.set(projectId, current + 1)
        } else {
          next.set(projectId, Math.max(0, current - 1))
        }
        return next
      })
    })
    return () => {
      unsubscribe()
    }
  }, [connection, projectId])

  // ── Workspace activity log fan-out ───────────────────────────────────────
  //
  // The route already owns the single AgentHub connection (joined to both the
  // active project AND the current workspace groups). The workspace group
  // delivers every agent event across every project in the workspace — so
  // this subscription is the single bottleneck for the sidebar activity log.
  //
  // We only push terminal-session events (TurnCompleted / TurnFailed /
  // TurnCanceled) into the store. Mid-turn ticks (AssistantText, ToolCall,
  // etc.) intentionally skip so the log doesn't churn — the spec calls for
  // a single line per finished session, not a play-by-play.
  //
  // The push carries ONLY IDs. {@link WorkspaceActivityLog} resolves the
  // project / branch names at render-time via TanStack Query so renamed
  // entities reflect live and we never paint a stale "(unknown project)"
  // placeholder. The SignalR event payload now carries projectId/branchId
  // directly, so no lookup is needed.
  useEffect(() => {
    if (!connection) return
    if (!slug) return
    const unsubscribe = connection.onAgentEvent((evt) => {
      // Card 4 (cursor-native-chat-ux): drill into the polymorphic event
      // and map its {@code status} (for the {@code status} subtype) onto
      // the activity log's terminal vocab. Every other event kind returns
      // {@code null} from {@link statusFromRunStatus} and short-circuits.
      const inner = evt.event as {
        eventKind?: string
        status?: string
        sessionId: string
        sequence: number
        createdAt: Date | string
      }
      if (inner.eventKind !== 'status') return
      const status = statusFromRunStatus(inner.status ?? '')
      if (status === null) return

      const id = `${inner.sessionId}:${inner.sequence}`
      const timestamp = (() => {
        const raw = inner.createdAt
        const parsed =
          typeof raw === 'string' ? Date.parse(raw) : raw?.getTime?.() ?? NaN
        return Number.isNaN(parsed) ? Date.now() : parsed
      })()

      // Inbox semantics: if the terminal event belongs to the branch the
      // user is CURRENTLY viewing, store it with {@code unread: false}.
      // Standing on the branch's chat is the implicit "I've seen what
      // happened here" — surfacing an unread badge for the branch you're
      // already looking at is pure noise. We still push (instead of
      // skipping) so the dedup-by-id guard in {@link pushActivity}
      // protects against a SignalR reconnect replay later flipping the
      // entry back to unread.
      const isActiveBranch = !!branchId && evt.branchId === branchId
      pushActivity({
        id,
        conversationId: evt.conversationId,
        projectId: evt.projectId,
        branchId: evt.branchId,
        status,
        timestamp,
        unread: !isActiveBranch,
      })
    })
    return () => {
      unsubscribe()
    }
  }, [connection, slug, branchId])

  // ── Mark-read on branch navigation ──────────────────────────────────────
  //
  // Inbox semantics — the act of opening a branch is the "I've seen this"
  // gesture. Any unread activity entries pointing at the branch we just
  // landed on get cleared in one pass, so the Activity panel only ever
  // surfaces entries the user hasn't acknowledged. Re-runs on every
  // {@code branchId} change; {@link markBranchRead} is a no-op when there
  // are no matching unread entries, so it's safe to call eagerly.
  useEffect(() => {
    if (!branchId) return
    markBranchRead(branchId)
  }, [branchId])

  // ── P4.1 Auto-titling: live refresh on ConversationRenamed ───────────────
  //
  // The auto-retitle path runs entirely server-side off the first AssistantText
  // chunk — the browser never issues an HTTP rename for it, so the chrome's
  // title detail query + the sidebar list would otherwise stay stale until a
  // manual refetch. We subscribe to the hub fan-out here (the route already
  // owns the single AgentHub connection for this surface) and invalidate the
  // two affected query keys via TanStack so the existing query hooks re-fetch
  // and re-render naturally. User-initiated renames already invalidate on
  // mutation success — this effect strictly covers the server-pushed path.
  useEffect(() => {
    if (!connection) return
    const unsubscribe = connection.onConversationRenamed((payload) => {
      if (payload.projectId !== projectId) return
      // Chrome title detail query — single conversation by id.
      queryClient.invalidateQueries({
        queryKey: getGetApiConversationsIdQueryKey(payload.conversationId),
      })
      // Sidebar list — invalidate by prefix so every cached variant
      // (includeArchived: true | false | undefined) is refetched.
      queryClient.invalidateQueries({
        queryKey: getGetApiProjectsProjectIdConversationsQueryKey(
          payload.projectId,
        ),
      })
    })
    return () => {
      unsubscribe()
    }
  }, [connection, projectId, queryClient])

  const liveActive = liveStatusByProjectId.get(projectId)
  // Per-branch runtime id, lifted so the diff endpoints in the Changes tab
  // hit the runtime that actually backs the currently-viewed branch. The
  // project's own {@code runtimeId} field is the DEFAULT-branch runtime —
  // using it on a non-default branch tab produces 503s from the runtime
  // diff endpoints. Prefer the per-branch {@code runtime/status} value;
  // fall back to the project's default-branch id only as a last resort
  // during the brief window before that query resolves.
  const effectiveRuntimeId: string | undefined =
    runtimeStatusQuery.data?.runtimeId ?? projectQuery.data?.runtimeId

  // The {@code liveStatusByProjectId} overlay is keyed by projectId because
  // the sidebar consumes it per-project (one bucket per row across all
  // branches). On a project with multiple checked-out branches that means
  // every branch's runtime events write to the SAME entry — last-writer
  // wins. So before letting the live overlay override the per-branch HTTP
  // status query, gate on the overlay event being for THIS branch's
  // runtime. Without this guard, a sibling branch crashing flips the
  // current branch's chat-header badge to "Crashed · restart required"
  // and the tab title to "— crashed", even though our runtime is fine.
  const liveActiveMatchesBranch =
    !!liveActive &&
    !!effectiveRuntimeId &&
    !!liveActive.runtimeId &&
    liveActive.runtimeId === effectiveRuntimeId

  const effectiveState: RuntimeState | string | undefined =
    (liveActiveMatchesBranch ? liveActive.state ?? undefined : undefined) ??
    runtimeStatusQuery.data?.state ??
    projectQuery.data?.runtimeState

  const effectiveErrorMessage: string | null =
    (liveActiveMatchesBranch ? liveActive.errorMessage : null) ??
    runtimeStatusQuery.data?.errorMessage ??
    null

  // GitHub-style live document.title: project name + runtime/turn status,
  // with a leading ● badge when a noteworthy transition happens while the
  // tab is blurred. Strictly scoped to the workspace route — restores the
  // previous title on unmount / route change.
  useProjectTabTitle({
    projectId,
    projectName: projectQuery.data?.name,
    runtimeState: effectiveState,
    connection,
  })

  // ── Project-settings drawer (lifted to the route) ───────────────────────
  //
  // Lifted here so both the chrome cog (desktop) AND the mobile bottom tab
  // bar's Settings button can open the same drawer instance. Sharing one
  // dialog avoids a class of bugs where two independent drawer states drift
  // out of sync, and it lets us hide the chrome cog on narrow viewports
  // without losing the entry point — the bottom tab bar covers it there.
  const [settingsOpen, setSettingsOpen] = useState(false)
  const [settingsInitialTab, setSettingsInitialTab] =
    useState<ProjectSettingsTab>('general')
  const openSettings = (tab: ProjectSettingsTab = 'general') => {
    setSettingsInitialTab(tab)
    setSettingsOpen(true)
  }

  return (
    <ProjectWorkspaceShell
      liveStatusByProjectId={liveStatusByProjectId}
      liveRunningTurnByProjectId={liveRunningTurnByProjectId}
      chrome={({ isNarrow, onOpenMobileSidebar }) =>
        projectId && branchId ? (
          <ChatChrome
            projectId={projectId}
            branchId={branchId}
            activeConversationId={activeConversationId}
            runtimeState={effectiveState}
            // Surface the backend's failure message in the runtime pill's
            // tooltip when the runtime is Failed / Crashed. Replaces the
            // legacy RuntimeStateStrip banner that used to carry the
            // restart affordance + message — the pill is now the single
            // entry-point for both signals.
            runtimeErrorMessage={effectiveErrorMessage}
            // Only surface the leading hamburger when the sidebar has
            // actually collapsed into an overlay — otherwise the chrome
            // would render an inert button on desktop.
            onOpenMobileSidebar={isNarrow ? onOpenMobileSidebar : undefined}
            onOpenSettings={openSettings}
            // Cog is suppressed on narrow viewports because the mobile
            // bottom tab bar carries the same affordance — two entry points
            // crowding a 375px-wide strip reads as noise.
            hideSettingsCog={isNarrow}
          />
        ) : null
      }
    >
      <ProjectWorkspacePage
        runtimeState={effectiveState}
        runtimeErrorMessage={effectiveErrorMessage}
        runtimeId={effectiveRuntimeId}
        hubConnection={connection}
        conversationId={activeConversationId}
        onOpenSettings={() => openSettings('general')}
        settingsOpen={settingsOpen}
      />
      {projectId && (
        <ProjectSettingsDrawer
          open={settingsOpen}
          onClose={() => setSettingsOpen(false)}
          projectId={projectId}
          branchId={branchId}
          slug={slug}
          initialTab={settingsInitialTab}
        />
      )}
    </ProjectWorkspaceShell>
  )
}

// Re-exported here so the route file is the single source of the lifted
// runtime state plumbing. Other call sites that want this view-model should
// use ProjectWorkspaceRoute, not ProjectWorkspacePage directly.
export type { AgentHubConnection }

