import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react'
import { Link as RouterLink, useNavigate, useParams } from 'react-router-dom'
import {
  Box,
  ButtonBase,
  CircularProgress,
  Divider,
  IconButton,
  Menu,
  MenuItem,
  Tooltip,
  Typography,
  type SxProps,
  type Theme,
  type TooltipProps,
} from '@mui/material'
import AddIcon from '@mui/icons-material/Add'
import ExpandMoreIcon from '@mui/icons-material/ExpandMore'
import ChevronRightIcon from '@mui/icons-material/ChevronRight'
import ContentCopyIcon from '@mui/icons-material/ContentCopy'
import ArchiveOutlinedIcon from '@mui/icons-material/ArchiveOutlined'
import RestartAltIcon from '@mui/icons-material/RestartAlt'
import BedtimeOutlinedIcon from '@mui/icons-material/BedtimeOutlined'
import StopCircleOutlinedIcon from '@mui/icons-material/StopCircleOutlined'
import MenuIcon from '@mui/icons-material/Menu'
import BugReportIcon from '@mui/icons-material/BugReport'
import CheckIcon from '@mui/icons-material/Check'
import DarkModeOutlinedIcon from '@mui/icons-material/DarkModeOutlined'
import LightModeOutlinedIcon from '@mui/icons-material/LightModeOutlined'
import KeyboardArrowDownIcon from '@mui/icons-material/KeyboardArrowDown'
import SettingsIcon from '@mui/icons-material/Settings'
import SwapHorizIcon from '@mui/icons-material/SwapHoriz'
import KeyboardDoubleArrowLeftIcon from '@mui/icons-material/KeyboardDoubleArrowLeft'
import KeyboardDoubleArrowRightIcon from '@mui/icons-material/KeyboardDoubleArrowRight'
import { useThemeMode, instrumentAccents } from '@/themes'
import { useQueryClient } from '@tanstack/react-query'
import { formatDistanceToNow, parseISO } from 'date-fns'
import {
  RuntimeState,
  getGetApiProjectsProjectIdBranchesBranchIdRuntimeStatusQueryKey,
  getGetApiProjectsProjectIdBranchesQueryKey,
  getGetApiWorkspacesSlugProjectsQueryKey,
  useGetApiProjectsProjectIdBranches,
  useGetApiProjectsProjectIdBranchesBranchIdRuntimeStatus,
  useGetApiWorkspacesSlugProjects,
  useGetApiWorkspacesWorkspaceIdCost,
  usePostApiProjectsProjectIdBranchesBranchIdArchive,
  usePostApiProjectsProjectIdBranchesBranchIdRuntimeRestart,
  usePostApiProjectsProjectIdBranchesBranchIdRuntimeSuspend,
  usePostApiProjectsProjectIdBranchesBranchIdRuntimeForceStop,
  type ProjectBranchDto,
  type ProjectSummaryDto,
} from '../../../../../api/queries-commands'
import { useAuth } from '../../../../../auth'
import { ApplicationRoles } from '../../../../shared/constants/roles'
import { useNotification } from '../../../../shared/contexts/NotificationContext'
import { useRuntimeDebugPanel } from '@/applications/shared/runtime'
import { useSidebarCollapsed } from '@/applications/shared/hooks/useSidebarCollapsed'
import { useWorkspace } from '../../../../shared/contexts/WorkspaceContext'
import {
  DetachedGithubPill,
  ReconnectProjectsDialog,
} from '../../../shared'
import type { LiveProjectStatus } from '../routes/ProjectWorkspaceRoute'
import { CopyBranchDialog } from './CopyBranchDialog'
import { StatusDot } from './StatusDot'
import { WorkspaceActivityLog } from './WorkspaceActivityLog'
import { ProjectCostTag } from './ProjectCostTag'
import { ProjectSettingsDrawer } from '../../project-settings'
import { formatCostUsd } from './costFormat'
import { useBranchUnreadActivityStatus } from '../hooks/useWorkspaceActivityStore'
import { branchWorkspaceHref } from '../hooks/branchConversationMemory'

import {
  chromeTokens,
  semanticTokens,
  semanticWarningRgb,
  surfaceTokens,
  workspaceChromeHeight,
  workspaceFontFamily,
} from '../../../shared/designTokens'

const tokens = {
  ...surfaceTokens,
  ...chromeTokens,
  attention: semanticTokens.attention,
  unreadDotIdle: semanticTokens.successDot,
  unreadDotFailed: semanticTokens.failureDot,
}

// ── Running-transition row wash ─────────────────────────────────────────────
//
// A one-shot ~1.5s background-color wash applied to a project row when it
// transitions INTO the `running` section. semantic warning matches StatusDot booting state.
// Background-color only — no transforms, no opacity on text —
// so the eye is drawn without anything feeling jarring.
const RUNNING_WASH_KEYFRAMES = `
@keyframes sidebarRunningWash {
  0%   { background-color: rgba(${semanticWarningRgb}, 0.0); }
  30%  { background-color: rgba(${semanticWarningRgb}, 0.14); }
  100% { background-color: rgba(${semanticWarningRgb}, 0.0); }
}
@keyframes sidebarStatusBreathe {
  0%, 100% { opacity: 1;   transform: scale(1); }
  50%      { opacity: 0.5; transform: scale(0.92); }
}
`
const RUNNING_WASH_DURATION_MS = 1500

// ── Section model ───────────────────────────────────────────────────────────

type SectionKey = 'needsAction' | 'running' | 'idle' | 'sleeping'

interface SectionConfig {
  key: SectionKey
  label: string
  /** Override the muted label color (e.g. rust for "NEEDS ACTION"). */
  color?: string
}

const SECTIONS: readonly SectionConfig[] = [
  { key: 'needsAction', label: 'Needs Action', color: tokens.attention },
  { key: 'running', label: 'Running' },
  { key: 'idle', label: 'Idle' },
  { key: 'sleeping', label: 'Sleeping' },
] as const

/**
 * Map a project's effective runtime state + live running-turn count to a
 * section bucket. {@code Deleting}/{@code Deleted} return {@code null} — those
 * projects are filtered out of the sidebar entirely in v1.
 */
function bucketFor(
  state: RuntimeState | string | null | undefined,
  runningTurnCount: number,
): SectionKey | null {
  switch (state) {
    case RuntimeState.Failed:
    case RuntimeState.Crashed:
      return 'needsAction'
    case RuntimeState.Pending:
    case RuntimeState.Booting:
    case RuntimeState.Bootstrapping:
    case RuntimeState.Waking:
      return 'running'
    case RuntimeState.Online:
      return runningTurnCount > 0 ? 'running' : 'idle'
    case RuntimeState.Suspended:
    case RuntimeState.Suspending:
      return 'sleeping'
    case RuntimeState.Deleting:
    case RuntimeState.Deleted:
      return null
    default:
      // Unknown / null state: bucket into IDLE so the row still surfaces.
      return 'idle'
  }
}

export interface ProjectsBranchesSidebarProps {
  /**
   * Per-project live runtime-state overlay. The route already owns the
   * single SignalR subscription (now joined to both project AND workspace
   * groups) and fans every {@code runtimeStateChanged} push into this map;
   * the sidebar reads it per-row to override the polled list's coarser
   * 15s value with whatever the hub said most recently.
   *
   * <p>For rows not present in the map we fall back to
   * {@code project.runtimeState} from the polled
   * {@code useGetApiWorkspacesSlugProjects} list, which refreshes every 15s
   * so non-active rows reflect coarse-grained reality without each row
   * owning its own SignalR subscription.</p>
   */
  liveStatusByProjectId?: Map<string, LiveProjectStatus>
  /**
   * Per-project in-flight turn count delta. The polled list carries the
   * baseline {@code runningTurnCount}; this map provides the instant
   * SignalR delta between polls. For each row we take {@code Math.max} of
   * the two so the section grouping reacts to a fresh TurnStarted without
   * waiting for the next 15s refetch.
   */
  liveRunningTurnByProjectId?: Map<string, number>
}

/**
 * Tiny rounded-square workspace avatar — single uppercase initial on the
 * current accent fill. Sits at the left edge of the workspace switcher header
 * so the user always has an at-a-glance identity for "which workspace am I
 * in". 22px is the reference design's size — large enough to read the letter
 * at a glance, small enough to slot into a 52px header without dominating.
 *
 * <p>The fill is the SOLID accent {@code base} (not the chrome-token "ink"
 * overlay), so the avatar stays a vivid colored tile that doesn't fade into
 * the dark sidebar. Pairs with {@code .on} for the initial — every accent
 * preset declares a contrast color that's guaranteed legible on its fill.</p>
 *
 * <p>Kept inline rather than promoted to a primitive because it's only used
 * in one place (the switcher) and is workspace-specific.</p>
 */
function WorkspaceAvatar({ label }: { label: string }) {
  const { accent } = useThemeMode()
  const palette = instrumentAccents[accent]
  const initial = (label.trim().charAt(0) || 'W').toUpperCase()
  return (
    <Box
      aria-hidden
      sx={{
        width: 22,
        height: 22,
        borderRadius: '6px',
        flexShrink: 0,
        backgroundColor: palette.base,
        color: palette.on,
        display: 'inline-flex',
        alignItems: 'center',
        justifyContent: 'center',
        fontSize: 11,
        fontWeight: 600,
        letterSpacing: '-0.02em',
        lineHeight: 1,
      }}
    >
      {initial}
    </Box>
  )
}

/**
 * 22px rounded square that surfaces a project's first letter in the collapsed
 * 56px sidebar rail. Visually parallel to {@link WorkspaceAvatar} but uses a
 * muted chip background (rather than the solid accent fill) so the workspace
 * switcher remains the single accent-tinted square at the top of the rail —
 * project rows read as a uniform stack of muted chips below it.
 */
function ProjectAvatar({ label }: { label: string }) {
  const initial = (label.trim().charAt(0) || 'P').toUpperCase()
  return (
    <Box
      aria-hidden
      sx={{
        width: 22,
        height: 22,
        borderRadius: '6px',
        flexShrink: 0,
        backgroundColor: tokens.chipBg,
        color: tokens.textPrimary,
        border: `1px solid ${tokens.hairline}`,
        display: 'inline-flex',
        alignItems: 'center',
        justifyContent: 'center',
        fontSize: 11,
        fontWeight: 600,
        letterSpacing: '-0.02em',
        lineHeight: 1,
      }}
    >
      {initial}
    </Box>
  )
}

/**
 * 6px status dot tucked into the top-right corner of a collapsed project row.
 * Color reflects the project's section bucket so the user can still scan
 * "what needs attention" at a glance when the rail is collapsed and the
 * section headers are hidden. The {@code running} bucket animates with the
 * shared {@code sidebarStatusBreathe} keyframe — the only auto-pulse signal
 * the rail emits.
 */
function ProjectCornerDot({ sectionKey }: { sectionKey: SectionKey }) {
  const color =
    sectionKey === 'needsAction'
      ? tokens.attention
      : sectionKey === 'running'
        ? semanticTokens.success
        : // idle + sleeping both read as "calm" — the dot ghosts to the
          // surface-faint token, present but visually quiet.
          tokens.statusDot
  return (
    <Box
      aria-hidden
      sx={{
        position: 'absolute',
        top: 4,
        right: 4,
        width: 6,
        height: 6,
        borderRadius: '50%',
        backgroundColor: color,
        // 1.5px panel-tinted ring lifts the dot off the avatar so it reads
        // as a separate badge rather than blending into the chip.
        boxShadow: `0 0 0 1.5px ${tokens.chromeBg}`,
        ...(sectionKey === 'running' && {
          animation: 'sidebarStatusBreathe 1.8s ease-in-out infinite',
        }),
      }}
    />
  )
}

/**
 * The new agent-native left sidebar (replaces {@code ConversationSidebar} at the
 * shell level). Groups every project in the current workspace by attention
 * priority — Needs Action → Running → Idle → Sleeping — and lets each row
 * expand into its branches, sorted by recency. The quiet {@link StatusDot}
 * per project gives the user a calm scan of their parallel agents at a glance.
 *
 * <p>The conversation picker has moved out of this surface — it now lives
 * inline in {@code ChatChrome} (clickable title → popover).</p>
 */
export function ProjectsBranchesSidebar({
  liveStatusByProjectId,
  liveRunningTurnByProjectId,
}: ProjectsBranchesSidebarProps) {
  const navigate = useNavigate()
  const { slug = '', projectId: activeProjectId = '', branchId: activeBranchId = '' } =
    useParams<{ slug: string; projectId: string; branchId: string }>()
  const debugPanel = useRuntimeDebugPanel()
  const { mode, toggleMode } = useThemeMode()
  const isDarkMode = mode === 'dark'
  // Collapsed (icon-rail) mode for the sidebar. Persisted across reloads and
  // synced across in-tab consumers (see {@link useSidebarCollapsed}) so the
  // layout's outer width wrapper and this component always agree. When true
  // we render a 56px icon rail: avatars only, no text, branches hidden,
  // status strip + group headers + activity log all suppressed. Footer
  // shows just the expand toggle.
  const [collapsed, setCollapsed] = useSidebarCollapsed()
  const { currentWorkspace, currentSlug, workspaces, switchWorkspace } =
    useWorkspace()
  // Prefer the resolved workspace name; fall back to the URL slug while the
  // /api/me/workspaces query is still in flight so the header never renders
  // as a blank row on first paint.
  const workspaceHeaderLabel =
    currentWorkspace?.name ?? currentSlug ?? slug ?? 'Workspace'

  // ── Workspace switcher menu ─────────────────────────────────────────────
  // Anchored to the sidebar header pill — clicking the workspace name opens
  // a Linear/Notion-style popover with every workspace the user belongs to,
  // the current one ticked, and a "Manage workspaces" item that drops back
  // to the root workspace switcher screen. Closed by clicking elsewhere,
  // pressing Esc, or selecting an item.
  // Reconnect dialog state — opened when the user clicks the small "detached"
  // pill next to a sidebar project row. We preset the dialog to the single
  // project so the action stays scoped; the user can still uncheck others if
  // they want to act selectively from the dialog itself.
  const [reconnectProjectId, setReconnectProjectId] = useState<string | null>(null)
  const reconnectOpen = reconnectProjectId !== null
  const [settingsProjectId, setSettingsProjectId] = useState<string | null>(null)

  const [workspaceMenuAnchor, setWorkspaceMenuAnchor] =
    useState<HTMLElement | null>(null)
  const workspaceMenuOpen = workspaceMenuAnchor !== null
  const openWorkspaceMenu = (e: React.MouseEvent<HTMLElement>) => {
    setWorkspaceMenuAnchor(e.currentTarget)
  }
  const closeWorkspaceMenu = () => setWorkspaceMenuAnchor(null)
  const handlePickWorkspace = (targetSlug: string) => {
    closeWorkspaceMenu()
    // Same-slug click is the implicit "home" affordance — the user wants to
    // dock at the workspace landing canvas. Other slugs use the context's
    // switchWorkspace which preserves the current sub-path when sensible.
    if (targetSlug === currentSlug) {
      navigate(`/w/${targetSlug}`)
    } else {
      switchWorkspace(targetSlug)
    }
  }
  const handleManageWorkspaces = () => {
    closeWorkspaceMenu()
    navigate('/')
  }

  const projectsQuery = useGetApiWorkspacesSlugProjects(slug, {
    query: {
      enabled: !!slug,
      refetchInterval: 15_000,
    },
  })

  // ── Effective state + running count per row ──────────────────────────────
  // We compute these once per render keyed by the polled list + the two live
  // overlay maps. Each section is then derived from a single pass over the
  // resolved rows, so adding a new bucket later is a one-line change.
  const resolvedProjects = useMemo(() => {
    const list = projectsQuery.data ?? []
    return list.map((project) => {
      const live = liveStatusByProjectId?.get(project.id)
      const effectiveState: RuntimeState | string | null =
        (live?.state ?? null) ?? project.runtimeState ?? null
      const livePartial = liveRunningTurnByProjectId?.get(project.id)
      const effectiveRunningTurnCount = Math.max(
        project.runningTurnCount ?? 0,
        livePartial ?? 0,
      )
      return {
        project,
        effectiveState,
        effectiveRunningTurnCount,
        section: bucketFor(effectiveState, effectiveRunningTurnCount),
      }
    })
  }, [projectsQuery.data, liveStatusByProjectId, liveRunningTurnByProjectId])

  // ── Group + intra-section sort (latestActivityAt desc) ───────────────────
  const sectionedProjects = useMemo(() => {
    const buckets: Record<SectionKey, typeof resolvedProjects> = {
      needsAction: [],
      running: [],
      idle: [],
      sleeping: [],
    }
    for (const row of resolvedProjects) {
      if (row.section === null) continue // Deleting / Deleted — drop from sidebar.
      buckets[row.section].push(row)
    }
    const compareDesc = (a: ProjectSummaryDto, b: ProjectSummaryDto) => {
      const ta = a.latestActivityAt ? Date.parse(a.latestActivityAt) : 0
      const tb = b.latestActivityAt ? Date.parse(b.latestActivityAt) : 0
      return tb - ta
    }
    ;(Object.keys(buckets) as SectionKey[]).forEach((k) => {
      buckets[k].sort((x, y) => compareDesc(x.project, y.project))
    })
    return buckets
  }, [resolvedProjects])

  // ── Expanded state ──────────────────────────────────────────────────────
  // The active project is always expanded. Other projects open on chevron
  // click. We seed the set from the active project so a deep-link to a branch
  // immediately reveals the surrounding context.
  const [expandedIds, setExpandedIds] = useState<Set<string>>(
    () => new Set(activeProjectId ? [activeProjectId] : []),
  )
  useEffect(() => {
    if (!activeProjectId) return
    setExpandedIds((prev) => {
      if (prev.has(activeProjectId)) return prev
      const next = new Set(prev)
      next.add(activeProjectId)
      return next
    })
  }, [activeProjectId])

  const toggleExpanded = (id: string) => {
    setExpandedIds((prev) => {
      const next = new Set(prev)
      if (next.has(id)) next.delete(id)
      else next.add(id)
      return next
    })
  }

  /**
   * Side-effect fired when the user clicks a project row. Just expands the
   * row in this tab — navigation itself is handled by the {@code <RouterLink>}
   * inside {@code ProjectRow}, so {@code ⌘+click} / middle-click correctly
   * open a new browser tab while the current one keeps its visual state.
   */
  const onProjectRowSelect = (project: ProjectSummaryDto) => {
    setExpandedIds((prev) => {
      if (prev.has(project.id)) return prev
      const next = new Set(prev)
      next.add(project.id)
      return next
    })
  }

  // ── Active row scroll-into-view ─────────────────────────────────────────
  const activeRowRef = useRef<HTMLDivElement | null>(null)
  useEffect(() => {
    if (!activeRowRef.current) return
    activeRowRef.current.scrollIntoView({ block: 'nearest' })
  }, [activeProjectId, activeBranchId])

  const totalVisibleProjects =
    sectionedProjects.needsAction.length +
    sectionedProjects.running.length +
    sectionedProjects.idle.length +
    sectionedProjects.sleeping.length

  // ── Workspace summary line ──────────────────────────────────────────────
  // ONE calm sentence surfacing the most urgent state across the workspace.
  // Priority: needsAction → running → all-quiet → omitted (no projects).
  const summaryLine = useMemo(() => {
    const needsActionCount = sectionedProjects.needsAction.length
    const runningCount = sectionedProjects.running.length
    if (needsActionCount > 0) {
      const noun = needsActionCount === 1 ? 'project needs' : 'projects need'
      return {
        kind: 'needsAction' as const,
        text: `${needsActionCount} ${noun} attention`,
        color: tokens.attention,
        weight: 500 as const,
        italic: false,
      }
    }
    if (runningCount > 0) {
      const noun = runningCount === 1 ? 'agent' : 'agents'
      return {
        kind: 'running' as const,
        text: `${runningCount} ${noun} running`,
        color: tokens.textMuted,
        weight: 400 as const,
        italic: true,
      }
    }
    if (totalVisibleProjects > 0) {
      return {
        kind: 'quiet' as const,
        text: 'All quiet',
        color: tokens.textMuted,
        weight: 400 as const,
        italic: true,
      }
    }
    return null
  }, [
    sectionedProjects.needsAction.length,
    sectionedProjects.running.length,
    totalVisibleProjects,
  ])

  // ── Running-transition row wash ─────────────────────────────────────────
  // Track each project's current section bucket so we can detect when a
  // project transitions INTO `running` (and ONLY into running). On that
  // transition, mark the project id in `flashRunningSet` so its row renders
  // with the one-shot wash animation; we clear the entry 1.5s later.
  //
  // First mount establishes the baseline silently — we don't want every
  // already-running project to flash on initial paint.
  const prevSectionMapRef = useRef<Map<string, SectionKey>>(new Map())
  const isInitialRenderRef = useRef(true)
  const flashTimersRef = useRef<Map<string, ReturnType<typeof setTimeout>>>(
    new Map(),
  )
  const [flashRunningSet, setFlashRunningSet] = useState<Set<string>>(
    () => new Set(),
  )

  // Build a stable map of current section assignments. Memoized off the
  // already-derived sections array so this is cheap.
  const currentSectionMap = useMemo(() => {
    const map = new Map<string, SectionKey>()
    for (const row of resolvedProjects) {
      if (row.section === null) continue
      map.set(row.project.id, row.section)
    }
    return map
  }, [resolvedProjects])

  useEffect(() => {
    // First-mount: record baseline silently, no flashes.
    if (isInitialRenderRef.current) {
      isInitialRenderRef.current = false
      prevSectionMapRef.current = currentSectionMap
      return
    }

    const prev = prevSectionMapRef.current
    const newlyRunning: string[] = []
    currentSectionMap.forEach((section, projectId) => {
      if (section !== 'running') return
      const prevSection = prev.get(projectId)
      if (prevSection === 'running') return // No transition.
      newlyRunning.push(projectId)
    })

    prevSectionMapRef.current = currentSectionMap

    if (newlyRunning.length === 0) return

    setFlashRunningSet((prevSet) => {
      let mutated = false
      const next = new Set(prevSet)
      for (const id of newlyRunning) {
        // Don't double-fire: if a wash is still in flight, leave it alone.
        if (next.has(id)) continue
        next.add(id)
        mutated = true
        const existing = flashTimersRef.current.get(id)
        if (existing) clearTimeout(existing)
        const timer = setTimeout(() => {
          flashTimersRef.current.delete(id)
          setFlashRunningSet((s) => {
            if (!s.has(id)) return s
            const n = new Set(s)
            n.delete(id)
            return n
          })
        }, RUNNING_WASH_DURATION_MS)
        flashTimersRef.current.set(id, timer)
      }
      return mutated ? next : prevSet
    })
  }, [currentSectionMap])

  // Cleanup any in-flight timers on unmount.
  useEffect(() => {
    return () => {
      flashTimersRef.current.forEach((t) => clearTimeout(t))
      flashTimersRef.current.clear()
    }
  }, [])

  // ── Render ──────────────────────────────────────────────────────────────
  return (
    <Box
      sx={{
        width: '100%',
        height: '100%',
        display: 'flex',
        flexDirection: 'column',
        backgroundColor: tokens.chromeBg,
        color: tokens.textPrimary,
        minHeight: 0,
      }}
    >
      {/* Inject the running-wash keyframes once per sidebar mount. Scoped to
          the document, but the animation name is unique enough not to clash. */}
      <style>{RUNNING_WASH_KEYFRAMES}</style>

      {/* ── Workspace header ─────────────────────────────────────────────────
          The workspace's identity row, pinned at the top of the sidebar so it
          never scrolls away. Just the workspace name on its own calm line,
          clickable through to the workspace landing canvas.

          Sits OUTSIDE the scrolling list container so it stays anchored as the
          user scrolls long project lists. Settings is intentionally NOT here —
          it lives in the footer next to the debug toggle so the two "rare,
          meta-level" affordances cluster at the bottom and don't compete with
          the workspace name + the "+" new-session affordance up top. */}
      <Box
        component="header"
        sx={{
          flexShrink: 0,
          // Lock to {@link workspaceChromeHeight} so this row sits on the
          // same horizontal y-grid as the chat chrome and the app
          // container's preview / changes chromes. When you compare the
          // three panels side-by-side, every hairline divider lines up —
          // making the workspace feel like one continuous shelf rather
          // than three panels with mismatched lids. The hairline below
          // separates the workspace identity from the project list so the
          // user always knows "which workspace am I in".
          height: workspaceChromeHeight,
          padding: collapsed ? '0' : '0 0.75rem',
          display: 'flex',
          alignItems: 'center',
          justifyContent: collapsed ? 'center' : 'flex-start',
          borderBottom: `1px solid ${tokens.hairline}`,
        }}
      >
        {/* Workspace switcher trigger — Linear/Notion model, polished per the
            Phase 2 reference: a 22px accent-filled avatar with the workspace
            initial sits at the left edge, paired with a two-line text block
            (workspace name + "Personal workspace" sub-label). On hover the
            row tints + reveals a small chevron, signalling that more lies
            behind it. Clicking opens the menu of every workspace the user
            belongs to. */}
        <Tooltip
          title={collapsed ? workspaceHeaderLabel : 'Switch workspace'}
          placement="right"
          enterDelay={500}
          disableHoverListener={workspaceMenuOpen}
        >
          <ButtonBase
            onClick={openWorkspaceMenu}
            aria-haspopup="menu"
            aria-expanded={workspaceMenuOpen}
            aria-label={`Switch workspace (current: ${workspaceHeaderLabel})`}
            sx={{
              display: 'flex',
              alignItems: 'center',
              gap: collapsed ? 0 : 1,
              width: collapsed ? 'auto' : '100%',
              minWidth: 0,
              textAlign: 'left',
              borderRadius: 1,
              px: collapsed ? 0 : 0.5,
              py: 0.5,
              mx: collapsed ? 0 : -0.5,
              color: tokens.textPrimary,
              transition: 'background-color 160ms ease, color 160ms ease',
              // Chevron is invisible at rest, surfaces on hover/focus so the
              // affordance stays calm but discoverable.
              '& .workspace-switcher-chevron': {
                opacity: workspaceMenuOpen ? 1 : 0,
                transition: 'opacity 160ms ease, transform 160ms ease',
                transform: workspaceMenuOpen
                  ? 'rotate(-180deg)'
                  : 'rotate(0deg)',
              },
              '&:hover': {
                backgroundColor: collapsed ? 'transparent' : tokens.rowHover,
                '& .workspace-switcher-chevron': { opacity: 1 },
              },
              '&:focus-visible': {
                outline: `2px solid ${tokens.accent}`,
                outlineOffset: 2,
                '& .workspace-switcher-chevron': { opacity: 1 },
              },
            }}
          >
            <WorkspaceAvatar label={workspaceHeaderLabel} />
            {!collapsed && (
              <>
                <Box
                  sx={{
                    display: 'flex',
                    flexDirection: 'column',
                    flex: 1,
                    minWidth: 0,
                  }}
                >
                  <Typography
                    noWrap
                    sx={{
                      fontSize: '0.84375rem',
                      fontWeight: 600,
                      letterSpacing: '-0.01em',
                      lineHeight: 1.1,
                      color: tokens.textPrimary,
                    }}
                  >
                    {workspaceHeaderLabel}
                  </Typography>
                  <Typography
                    noWrap
                    sx={{
                      fontSize: '0.6875rem',
                      fontWeight: 400,
                      letterSpacing: '-0.005em',
                      lineHeight: 1.2,
                      mt: '1px',
                      color: tokens.textFaint,
                    }}
                  >
                    {/* Sub-label per the reference design. We don't yet have
                        a workspace plan/type field, so "Personal workspace"
                        is the safe default — when plan info ships, swap this
                        to the plan name (e.g. "Team · Pro"). */}
                    Personal workspace
                  </Typography>
                </Box>
                {/* Workspace lifetime cost — tiny muted dollar amount tucked
                    next to the chevron. Self-hides when loading or when the
                    total is 0. */}
                {currentWorkspace?.id && (
                  <WorkspaceCostTag workspaceId={currentWorkspace.id} />
                )}
                <KeyboardArrowDownIcon
                  className="workspace-switcher-chevron"
                  sx={{
                    fontSize: 14,
                    flexShrink: 0,
                    color: tokens.textMuted,
                  }}
                />
              </>
            )}
          </ButtonBase>
        </Tooltip>
      </Box>

      {/* ── Workspace switcher menu ──────────────────────────────────────────
          MUI Menu anchored to the sidebar header pill. Each workspace is
          a MenuItem; the current one is ticked with a CheckIcon. A divider
          and "Manage workspaces" item below drop the user to the root
          workspace-switcher screen (where they can create/rename/leave). */}
      <Menu
        anchorEl={workspaceMenuAnchor}
        open={workspaceMenuOpen}
        onClose={closeWorkspaceMenu}
        anchorOrigin={{ vertical: 'bottom', horizontal: 'left' }}
        transformOrigin={{ vertical: 'top', horizontal: 'left' }}
        slotProps={{
          paper: {
            sx: {
              mt: 0.5,
              minWidth: 248,
              maxWidth: 320,
              border: `1px solid ${tokens.hairline}`,
              borderRadius: 2,
              boxShadow: '0 6px 24px rgba(0,0,0,0.08)',
              backgroundColor: tokens.canvasBg,
            },
          },
          list: { sx: { py: 0.5 } },
        }}
      >
        {workspaces.length === 0 && (
          // Defensive: the menu should never open with no workspaces (the
          // user is INSIDE one), but if /api/me/workspaces hasn't returned
          // yet on a deep-link visit, render a calm placeholder rather than
          // an empty popover.
          <Box
            sx={{
              px: 2,
              py: 1.25,
              fontSize: '0.8125rem',
              color: tokens.textMuted,
              letterSpacing: '-0.005em',
            }}
          >
            Loading workspaces…
          </Box>
        )}
        {workspaces.map((workspace) => {
          const isCurrent = workspace.slug === currentSlug
          return (
            <MenuItem
              key={workspace.slug}
              onClick={() => handlePickWorkspace(workspace.slug)}
              selected={isCurrent}
              sx={{
                fontSize: '0.8125rem',
                color: tokens.textPrimary,
                letterSpacing: '-0.005em',
                py: 0.875,
                px: 1.5,
                gap: 1,
                '&.Mui-selected': {
                  backgroundColor: tokens.rowActive,
                  '&:hover': { backgroundColor: tokens.rowActive },
                },
                '&:hover': { backgroundColor: tokens.rowHover },
              }}
            >
              <Box
                sx={{
                  flex: 1,
                  minWidth: 0,
                  whiteSpace: 'nowrap',
                  overflow: 'hidden',
                  textOverflow: 'ellipsis',
                  fontWeight: isCurrent ? 600 : 400,
                }}
              >
                {workspace.name ?? workspace.slug}
              </Box>
              {isCurrent && (
                <CheckIcon
                  sx={{ fontSize: 14, color: tokens.accent, flexShrink: 0 }}
                />
              )}
            </MenuItem>
          )
        })}
        <Divider sx={{ my: 0.5, borderColor: tokens.hairline }} />
        <MenuItem
          onClick={handleManageWorkspaces}
          sx={{
            fontSize: '0.8125rem',
            color: tokens.textMuted,
            letterSpacing: '-0.005em',
            py: 0.875,
            px: 1.5,
            gap: 1,
            '&:hover': {
              backgroundColor: tokens.rowHover,
              color: tokens.textPrimary,
            },
          }}
        >
          <SwapHorizIcon sx={{ fontSize: 16, flexShrink: 0 }} />
          Manage workspaces
        </MenuItem>
      </Menu>

      {/* ── New session — explicit, prominent primary action ───────────────
          The single highest-frequency action when entering a workspace is
          "start a new agent session", so it gets its own labelled button
          sitting just below the workspace switcher. In the full sidebar it
          renders as a full-width tinted pill ("+ New session"); in the
          collapsed 56px rail it shrinks to a single centered `+` button
          with the label moved to a tooltip — keeping the affordance
          reachable without the rail growing. */}
      <Box
        sx={{
          flexShrink: 0,
          padding: collapsed ? '0.625rem 0' : '0.625rem 0.75rem 0.5rem',
          display: 'flex',
          justifyContent: 'center',
        }}
      >
        <Tooltip
          title={collapsed ? 'New session' : ''}
          placement="right"
          enterDelay={400}
          disableHoverListener={!collapsed}
        >
          <ButtonBase
            component={RouterLink}
            to={`/w/${slug}/new-session`}
            aria-label="Start new session"
            sx={{
              display: 'inline-flex',
              alignItems: 'center',
              // Left-anchored layout in expanded mode so the `+` icon sits in
              // the same column as the project chevrons / row glyphs below —
              // visually nesting "New session" into the sidebar's left-edge
              // text rhythm rather than floating it as a centered pill.
              justifyContent: collapsed ? 'center' : 'flex-start',
              gap: 0.75,
              width: collapsed ? 32 : '100%',
              height: 32,
              borderRadius: 1.5,
              px: collapsed ? 0 : 1,
              // Transparent at rest — no chip background, no border — so the
              // affordance reads as a calm "menu item" rather than a heavy
              // primary CTA. The accent-tinted icon + text are the entire
              // visual weight. Hover supplies the only fill, mirroring how
              // the project rows reveal their background on hover.
              backgroundColor: 'transparent',
              color: tokens.accent,
              fontSize: '0.8125rem',
              fontWeight: 500,
              letterSpacing: '-0.005em',
              textDecoration: 'none',
              transition: 'background-color 160ms ease, color 160ms ease',
              '&:hover': {
                backgroundColor: tokens.rowHover,
              },
              '&:focus-visible': {
                outline: `2px solid ${tokens.accent}`,
                outlineOffset: 2,
              },
            }}
          >
            <AddIcon sx={{ fontSize: 16 }} />
            {!collapsed && <span>New session</span>}
          </ButtonBase>
        </Tooltip>
      </Box>

      <Box
        sx={{
          flex: 1,
          minHeight: 0,
          overflowY: 'auto',
          '&::-webkit-scrollbar': { width: 6 },
          '&::-webkit-scrollbar-thumb': {
            backgroundColor: 'rgba(0, 0, 0, 0.12)',
            borderRadius: 3,
          },
        }}
      >
        {/* One-line workspace summary — calm sentence + semantic dot prefix
            telling the user at a glance whether anything needs attention,
            anything is running, or everything is quiet. The explicit
            "New session" button sits above this row (outside the scroll
            container), so this row is now pure status — no `+` button.

            Hidden entirely when the sidebar is collapsed; in icon-rail mode
            the same signal is conveyed by per-project corner status dots. */}
        {!collapsed && (
          <Box
            sx={{
              padding: '0.5rem 0.875rem 0.625rem',
              display: 'flex',
              alignItems: 'center',
              gap: 1,
              minHeight: 28,
            }}
          >
            {summaryLine && !projectsQuery.isLoading ? (
              <Box
                sx={{
                  display: 'flex',
                  alignItems: 'center',
                  gap: 1,
                  minWidth: 0,
                  flex: 1,
                }}
              >
                {/* Status dot — small semantic prefix that gives the user a
                    one-glance "what's happening" before they read the line.
                    - needsAction → attention color, no pulse (it's a static fact)
                    - running     → success color, breathing pulse
                    - quiet       → ghosted (statusDot token), no pulse */}
                <Box
                  aria-hidden
                  sx={{
                    width: 6,
                    height: 6,
                    borderRadius: '50%',
                    flexShrink: 0,
                    backgroundColor:
                      summaryLine.kind === 'needsAction'
                        ? tokens.attention
                        : summaryLine.kind === 'running'
                          ? semanticTokens.success
                          : tokens.statusDot,
                    ...(summaryLine.kind === 'running' && {
                      animation:
                        'sidebarStatusBreathe 1.8s ease-in-out infinite',
                    }),
                  }}
                />
                <Typography
                  noWrap
                  sx={{
                    fontSize: '0.75rem',
                    letterSpacing: '-0.005em',
                    color: summaryLine.color,
                    fontWeight: summaryLine.weight,
                    fontStyle: summaryLine.italic ? 'italic' : 'normal',
                  }}
                >
                  {summaryLine.text}
                </Typography>
              </Box>
            ) : null}
          </Box>
        )}

        {projectsQuery.isLoading ? (
          <ProjectListSkeleton />
        ) : totalVisibleProjects === 0 ? (
          <Box sx={{ px: 2, py: 2 }}>
            <Typography
              variant="body2"
              sx={{ fontSize: '0.8125rem', color: tokens.textMuted }}
            >
              No projects yet —{' '}
              <Box
                component={RouterLink}
                to={`/w/${slug}/projects/new`}
                sx={{
                  color: tokens.accent,
                  textDecoration: 'none',
                  '&:hover': { textDecoration: 'underline' },
                }}
              >
                create one
              </Box>
            </Typography>
          </Box>
        ) : (
          <Box>
            {SECTIONS.map((section) => {
              const rows = sectionedProjects[section.key]
              if (rows.length === 0) return null
              return (
                <Box key={section.key} component="section">
                  {/* Group label — hidden in the collapsed 56px rail because
                      the rail can't accommodate uppercase tracked text
                      without ellipsis chaos. The per-row corner status dots
                      carry the section signal in collapsed mode. */}
                  {!collapsed && (
                    <SectionHeader
                      label={section.label}
                      count={rows.length}
                      color={section.color ?? tokens.textMuted}
                    />
                  )}
                  <Box component="ul" sx={{ m: 0, p: 0, listStyle: 'none' }}>
                    {rows.map(
                      ({ project, effectiveRunningTurnCount }) => (
                        <ProjectRow
                          key={project.id}
                          project={project}
                          slug={slug}
                          expanded={expandedIds.has(project.id)}
                          active={project.id === activeProjectId}
                          effectiveRunningTurnCount={effectiveRunningTurnCount}
                          flashRunning={flashRunningSet.has(project.id)}
                          activeRowRef={
                            project.id === activeProjectId && !activeBranchId
                              ? activeRowRef
                              : null
                          }
                          onToggle={() => toggleExpanded(project.id)}
                          onSelect={() => onProjectRowSelect(project)}
                          onReconnect={() => setReconnectProjectId(project.id)}
                          onOpenSettings={() => setSettingsProjectId(project.id)}
                          activeBranchId={
                            project.id === activeProjectId ? activeBranchId : ''
                          }
                          activeBranchRowRef={
                            project.id === activeProjectId && activeBranchId
                              ? activeRowRef
                              : null
                          }
                          collapsed={collapsed}
                          sectionKey={section.key}
                        />
                      ),
                    )}
                  </Box>
                </Box>
              )
            })}
          </Box>
        )}
      </Box>

      {/* ── Sidebar footer — stacked, activity log above meta-tools row ────
          Affordances pinned at the bottom of the sidebar so they stay visible
          while the user scrolls the project list:
            1. WorkspaceActivityLog — the live activity feed. Self-hides when
               there are no entries, so a quiet workspace doesn't get a
               permanently-empty strip. Suppressed entirely when the sidebar
               is collapsed so the 56px rail stays free of text rows.
            2. Meta-tools row — when expanded, settings + theme toggle + debug
               group on the LEFT bracketed by a collapse toggle on the RIGHT
               (mirroring the reference design). When collapsed, just a
               single expand toggle centered in the 56px rail — the other
               affordances stay reachable from the expanded sidebar. */}
      {!collapsed && <WorkspaceActivityLog />}
      <Box
        sx={{
          flexShrink: 0,
          height: 36,
          // Padding flips between the expanded layout (gear flush left, bug
          // flush right against the standard sidebar gutters) and the
          // collapsed icon-rail layout (single button centered in 56px).
          pl: collapsed ? 0 : 0.75,
          pr: collapsed ? 0 : 1.5,
          display: 'flex',
          alignItems: 'center',
          justifyContent: collapsed ? 'center' : 'space-between',
          gap: 0.5,
          borderTop: `1px solid ${tokens.hairline}`,
          backgroundColor: tokens.chromeBg,
        }}
      >
        {collapsed ? (
          // Collapsed: single expand button, centered. The tooltip carries
          // the affordance label since the icon alone isn't self-explanatory.
          <Tooltip title="Expand sidebar" placement="right" enterDelay={400}>
            <IconButton
              size="small"
              aria-label="Expand sidebar"
              onClick={() => setCollapsed(false)}
              sx={{
                width: 28,
                height: 28,
                color: tokens.textMuted,
                transition: 'color 160ms ease, background-color 160ms ease',
                '&:hover': {
                  color: tokens.textPrimary,
                  backgroundColor: tokens.rowHover,
                },
                '&:focus-visible': {
                  outline: `2px solid ${tokens.accent}`,
                  outlineOffset: 1,
                },
              }}
            >
              <KeyboardDoubleArrowRightIcon sx={{ fontSize: 16 }} />
            </IconButton>
          </Tooltip>
        ) : (
          <>
            {/* Left cluster: settings + theme + debug. Reads as a
                trio of "rare, configuration-level" affordances. */}
            <Box sx={{ display: 'flex', alignItems: 'center', gap: 0.25 }}>
              <Tooltip
                title="Workspace settings"
                placement="top"
                enterDelay={400}
              >
                <IconButton
                  size="small"
                  aria-label="Workspace settings"
                  component={RouterLink}
                  to={`/w/${slug}/settings`}
                  sx={{
                    width: 26,
                    height: 26,
                    color: tokens.textMuted,
                    transition:
                      'color 160ms ease, background-color 160ms ease',
                    '&:hover': {
                      color: tokens.accent,
                      backgroundColor: 'transparent',
                    },
                    '&:focus-visible': {
                      outline: `2px solid ${tokens.accent}`,
                      outlineOffset: 1,
                    },
                  }}
                >
                  <SettingsIcon sx={{ fontSize: 16 }} />
                </IconButton>
              </Tooltip>
              <Tooltip
                title={isDarkMode ? 'Switch to light mode' : 'Switch to dark mode'}
                placement="top"
                enterDelay={400}
              >
                <IconButton
                  size="small"
                  aria-label={isDarkMode ? 'Switch to light mode' : 'Switch to dark mode'}
                  aria-pressed={isDarkMode}
                  onClick={toggleMode}
                  sx={{
                    width: 26,
                    height: 26,
                    color: isDarkMode ? tokens.accent : tokens.textMuted,
                    transition:
                      'color 160ms ease, background-color 160ms ease',
                    '&:hover': {
                      color: tokens.accent,
                      backgroundColor: 'transparent',
                    },
                    '&:focus-visible': {
                      outline: `2px solid ${tokens.accent}`,
                      outlineOffset: 1,
                    },
                  }}
                >
                  {isDarkMode ? (
                    <LightModeOutlinedIcon sx={{ fontSize: 16 }} />
                  ) : (
                    <DarkModeOutlinedIcon sx={{ fontSize: 16 }} />
                  )}
                </IconButton>
              </Tooltip>
              <Tooltip
                title={
                  debugPanel.open ? 'Hide debug panel' : 'Show debug panel'
                }
                placement="top"
                enterDelay={400}
              >
                <IconButton
                  size="small"
                  aria-label={
                    debugPanel.open ? 'Hide debug panel' : 'Show debug panel'
                  }
                  aria-pressed={debugPanel.open}
                  onClick={debugPanel.togglePanel}
                  sx={{
                    width: 26,
                    height: 26,
                    color: debugPanel.open ? tokens.accent : tokens.textMuted,
                    transition:
                      'color 160ms ease, background-color 160ms ease',
                    '&:hover': {
                      color: tokens.accent,
                      backgroundColor: 'transparent',
                    },
                    '&:focus-visible': {
                      outline: `2px solid ${tokens.accent}`,
                      outlineOffset: 1,
                    },
                  }}
                >
                  <BugReportIcon sx={{ fontSize: 16 }} />
                </IconButton>
              </Tooltip>
            </Box>
            {/* Right edge: collapse toggle — mirrors the reference design's
                "panel-left" affordance and pairs with the expand button in
                the collapsed rail above. */}
            <Tooltip title="Collapse sidebar" placement="top" enterDelay={400}>
              <IconButton
                size="small"
                aria-label="Collapse sidebar"
                onClick={() => setCollapsed(true)}
                sx={{
                  width: 26,
                  height: 26,
                  mr: '-4px',
                  color: tokens.textMuted,
                  transition:
                    'color 160ms ease, background-color 160ms ease',
                  '&:hover': {
                    color: tokens.textPrimary,
                    backgroundColor: tokens.rowHover,
                  },
                  '&:focus-visible': {
                    outline: `2px solid ${tokens.accent}`,
                    outlineOffset: 1,
                  },
                }}
              >
                <KeyboardDoubleArrowLeftIcon sx={{ fontSize: 16 }} />
              </IconButton>
            </Tooltip>
          </>
        )}
      </Box>

      <ReconnectProjectsDialog
        open={reconnectOpen}
        onClose={() => setReconnectProjectId(null)}
        workspaceSlug={slug}
        presetProjectIds={reconnectProjectId ? [reconnectProjectId] : undefined}
      />

      {settingsProjectId && (
        <ProjectSettingsDrawer
          open
          onClose={() => setSettingsProjectId(null)}
          projectId={settingsProjectId}
          branchId={
            settingsProjectId === activeProjectId && activeBranchId
              ? activeBranchId
              : undefined
          }
          slug={slug}
        />
      )}
    </Box>
  )
}

// ── Section header ──────────────────────────────────────────────────────────

interface SectionHeaderProps {
  label: string
  count: number
  color: string
}

function SectionHeader({ label, count, color }: SectionHeaderProps) {
  return (
    <Box
      sx={{
        pl: 2,
        // 12px right padding (vs 16 left) keeps the count digit aligned on the
        // same column as the "+" icon, the project status dots, and the debug
        // bug icon at the sidebar's bottom. Left padding stays at 16 because
        // the label is text-like and benefits from the breathing room.
        pr: 1.5,
        pt: 2,
        pb: 0.75,
        display: 'flex',
        alignItems: 'center',
        justifyContent: 'space-between',
        gap: 1,
      }}
    >
      <Typography
        component="div"
        sx={{
          fontSize: '0.6875rem',
          fontWeight: 600,
          letterSpacing: '0.08em',
          textTransform: 'uppercase',
          color,
        }}
      >
        {label}
      </Typography>
      <Typography
        component="span"
        sx={{
          fontSize: '0.6875rem',
          fontWeight: 600,
          color,
          opacity: 0.7,
          fontFamily: workspaceFontFamily.mono,
          fontVariantNumeric: 'tabular-nums',
        }}
      >
        {count}
      </Typography>
    </Box>
  )
}

// ── Building blocks ─────────────────────────────────────────────────────────

interface ProjectRowProps {
  project: ProjectSummaryDto
  slug: string
  expanded: boolean
  active: boolean
  effectiveRunningTurnCount: number
  /** When true, render the row with a one-shot ~1.5s warm-amber wash. */
  flashRunning: boolean
  /** Set when this row is the currently-active project AND no branch is selected. */
  activeRowRef: React.RefObject<HTMLDivElement | null> | null
  onToggle: () => void
  /**
   * Side-effect fired on click (regular or modifier) — typically expands the
   * row. Navigation itself is handled by the {@code <RouterLink>} {@code href},
   * NOT this handler, so {@code ⌘+click}/middle-click correctly open in a new
   * tab while still firing this side-effect in the source tab.
   */
  onSelect: () => void
  /**
   * Fired when the user clicks the small "detached" pill next to the project
   * name. Surfaces the reconnect dialog preset to this single project.
   */
  onReconnect: () => void
  /** Opens project settings for this row's project. */
  onOpenSettings: () => void
  activeBranchId: string
  activeBranchRowRef: React.RefObject<HTMLDivElement | null> | null
  /**
   * When true, render the row as a single 22px avatar (with a corner status
   * dot) centered in the 56px collapsed-rail column. The chevron, name, cost
   * tag, detached pill, and the entire expanded branches list are all hidden
   * — the row is reduced to a square icon target that still navigates to
   * the project on click.
   */
  collapsed: boolean
  /**
   * Section bucket this project lives in. Used in collapsed mode to pick the
   * corner status dot's color so the user can still scan their parallel
   * agents at a glance without the section headers being visible.
   */
  sectionKey: SectionKey
}

function ProjectRow({
  project,
  slug,
  expanded,
  active,
  flashRunning,
  activeRowRef,
  onToggle,
  onSelect,
  onReconnect,
  onOpenSettings,
  activeBranchId,
  activeBranchRowRef,
  collapsed,
  sectionKey,
}: ProjectRowProps) {
  const isDetached = project.githubInstallationId == null

  // ── Collapsed render ───────────────────────────────────────────────────
  // 32×32 icon target margin-inset 8px from the rail edges, matching the
  // reference design (sidebar.jsx#80-96). The status dot in the top-right
  // corner carries the per-project signal that section headers + dot
  // prefixes would in the expanded layout. We reuse the workspace
  // accent for the avatar fill so the rail reads as a coherent column
  // of accent-tinted squares against the panel background.
  if (collapsed) {
    return (
      <Box
        component="li"
        sx={{ position: 'relative', listStyle: 'none' }}
      >
        <Tooltip title={project.name} placement="right" enterDelay={400}>
          <Box
            ref={activeRowRef ?? undefined}
            component={RouterLink}
            to={`/w/${slug}/projects/${project.id}`}
            onClick={onSelect}
            aria-label={project.name}
            sx={{
              position: 'relative',
              display: 'flex',
              alignItems: 'center',
              justifyContent: 'center',
              height: 32,
              borderRadius: 1,
              mx: 1,
              my: 0.25,
              textDecoration: 'none',
              color: 'inherit',
              cursor: 'pointer',
              backgroundColor: active ? tokens.rowActive : 'transparent',
              transition: 'background-color 120ms ease',
              ...(flashRunning && {
                animation: `sidebarRunningWash ${RUNNING_WASH_DURATION_MS}ms ease-out 1`,
              }),
              '&:hover': {
                backgroundColor: active ? tokens.rowActive : tokens.rowHover,
              },
              '&:focus-visible': {
                outline: `2px solid ${tokens.accent}`,
                outlineOffset: -2,
              },
            }}
          >
            <ProjectAvatar label={project.name} />
            <ProjectCornerDot sectionKey={sectionKey} />
          </Box>
        </Tooltip>
      </Box>
    )
  }

  return (
    <Box
      component="li"
      sx={{
        position: 'relative',
        '&:hover .project-row-settings-action, &:focus-within .project-row-settings-action':
          {
            opacity: 1,
          },
      }}
    >
      <Box
        ref={activeRowRef ?? undefined}
        component={RouterLink}
        to={`/w/${slug}/projects/${project.id}`}
        onClick={onSelect}
        sx={{
          position: 'relative',
          display: 'flex',
          alignItems: 'center',
          gap: 0.5,
          pl: 1,
          pr: 1.25,
          py: 0.75,
          cursor: 'pointer',
          textDecoration: 'none',
          color: 'inherit',
          backgroundColor: active ? tokens.rowActive : 'transparent',
          transition: 'background-color 120ms ease',
          // One-shot warm-amber wash when this project just transitioned
          // INTO the running section. background-color only (no transforms,
          // no opacity on text) so the eye is drawn without anything jarring.
          ...(flashRunning && {
            animation: `sidebarRunningWash ${RUNNING_WASH_DURATION_MS}ms ease-out 1`,
          }),
          '&:hover': {
            backgroundColor: active ? tokens.rowActive : tokens.rowHover,
          },
          '&:focus-visible': {
            outline: `2px solid ${tokens.accent}`,
            outlineOffset: -2,
          },
        }}
      >
        {/* Accent left edge for the active project */}
        {active && (
          <Box
            aria-hidden
            sx={{
              position: 'absolute',
              left: 0,
              top: 0,
              bottom: 0,
              width: 2,
              backgroundColor: tokens.accent,
            }}
          />
        )}

        {/* Chevron — toggles expansion without navigating. The parent row is
            an anchor, so both {@code preventDefault} and {@code stopPropagation}
            are required: stopPropagation alone wouldn't suppress the browser's
            native anchor-follow on click. */}
        <Box
          role="button"
          tabIndex={-1}
          aria-label={expanded ? 'Collapse project' : 'Expand project'}
          onClick={(e) => {
            e.preventDefault()
            e.stopPropagation()
            onToggle()
          }}
          sx={{
            display: 'inline-flex',
            alignItems: 'center',
            justifyContent: 'center',
            width: 18,
            height: 18,
            color: tokens.textMuted,
            cursor: 'pointer',
            borderRadius: 0.5,
            flexShrink: 0,
            '&:hover': { color: tokens.textPrimary },
          }}
        >
          {expanded ? (
            <ExpandMoreIcon sx={{ fontSize: 16 }} />
          ) : (
            <ChevronRightIcon sx={{ fontSize: 16 }} />
          )}
        </Box>

        {/* Project name */}
        <SidebarTruncatedText
          text={project.name}
          placement="right"
          sx={{
            fontSize: '0.8125rem',
            fontWeight: active ? 600 : 500,
            color: tokens.textPrimary,
            letterSpacing: '-0.005em',
          }}
        />

        {/* Detached pill (icon-only) — surfaces when GitHub installation was
            soft-detached. Sidebar rows are tight so we use the compact variant
            (12px icon, 18px hit target) with a tooltip describing the action. */}
        {isDetached && (
          <DetachedGithubPill
            variant="compact"
            project={{
              id: project.id,
              name: project.name,
              githubRepoOwner: project.githubRepoOwner,
            }}
            onClick={onReconnect}
          />
        )}

        <Tooltip title="Project settings" placement="top" enterDelay={400}>
          <IconButton
            className="project-row-settings-action"
            size="small"
            aria-label={`Project settings for ${project.name}`}
            onClick={(e) => {
              e.preventDefault()
              e.stopPropagation()
              onOpenSettings()
            }}
            onKeyDown={(e) => {
              if (e.key === 'Enter' || e.key === ' ') {
                e.preventDefault()
                e.stopPropagation()
              }
            }}
            sx={{
              opacity: 0,
              transition: 'opacity 120ms ease',
              padding: 0.25,
              color: tokens.textMuted,
              flexShrink: 0,
              ml: 'auto',
              '&:hover': { color: tokens.textPrimary },
              '&:focus-visible': {
                opacity: 1,
                outline: `2px solid ${tokens.accent}`,
                outlineOffset: 1,
              },
            }}
          >
            <SettingsIcon sx={{ fontSize: 14 }} />
          </IconButton>
        </Tooltip>

        {/* Project lifetime cost — tiny muted number, hidden when $0. Anchors
            to the right edge as the visual full-stop of the row. Self-hides
            when loading or when total is 0. */}
        <ProjectCostTag projectId={project.id} />

        {/* The project-row {@link StatusDot} used to live here, encoding
            the runtime state (green=Online, amber=Booting, rust=Failed,
            grey=Suspended). It pulled visual weight away from the actual
            inbox/running signals on the BRANCH rows below — a calm green
            "Online" badge on every healthy project row added chrome
            without telling the user anything actionable (the section
            bucket headers + the chrome top bar both surface runtime state
            more clearly). Removing it lets the branch-row pulse + unread
            dots own the "what's happening" channel without competition. */}
      </Box>

      {/* Branches (only when expanded) */}
      {expanded && (
        <BranchesList
          projectId={project.id}
          defaultBranchName={null}
          slug={slug}
          activeBranchId={activeBranchId}
          activeBranchRowRef={activeBranchRowRef}
        />
      )}
    </Box>
  )
}

interface BranchesListProps {
  projectId: string
  /** Reserved for future use — branch default flag is also on each row. */
  defaultBranchName: string | null
  slug: string
  activeBranchId: string
  activeBranchRowRef: React.RefObject<HTMLDivElement | null> | null
}

function BranchesList({
  projectId,
  slug,
  activeBranchId,
  activeBranchRowRef,
}: BranchesListProps) {
  const branchesQuery = useGetApiProjectsProjectIdBranches(
    projectId,
    undefined,
    {
      query: {
        enabled: !!projectId,
        refetchInterval: 15_000,
      },
    },
  )

  // ── Copy-branch dialog state ────────────────────────────────────────────
  // Track WHICH branch the user is copying so the dialog can prefill its
  // suggested name and post to the right source id. We keep the source
  // metadata around even after `open` flips to false so the closing animation
  // doesn't flash an empty title.
  const [copySource, setCopySource] = useState<{
    id: string
    name: string
  } | null>(null)
  const [copyDialogOpen, setCopyDialogOpen] = useState(false)

  // ── Branch sort ─────────────────────────────────────────────────────────
  // 1. Running branches (runningTurnCount > 0) surface first — these are the
  //    branches with an agent in flight right now.
  // 2. Then most-recently-active (lastActivityAt desc).
  // 3. Default branch wins remaining ties so it sits above equally-quiet
  //    siblings (matches user expectation when a project is fresh).
  const sortedBranches = useMemo(() => {
    const list = branchesQuery.data ?? []
    return [...list].sort((a, b) => {
      const aRunning = (a.runningTurnCount ?? 0) > 0 ? 1 : 0
      const bRunning = (b.runningTurnCount ?? 0) > 0 ? 1 : 0
      if (aRunning !== bRunning) return bRunning - aRunning
      const ta = a.lastActivityAt ? Date.parse(a.lastActivityAt) : 0
      const tb = b.lastActivityAt ? Date.parse(b.lastActivityAt) : 0
      if (ta !== tb) return tb - ta
      if (a.isDefault !== b.isDefault) return a.isDefault ? -1 : 1
      return 0
    })
  }, [branchesQuery.data])

  const existingBranchNames = useMemo(
    () => (branchesQuery.data ?? []).map((b) => b.name),
    [branchesQuery.data],
  )

  if (branchesQuery.isLoading) {
    return (
      <Box sx={{ pl: 4, py: 1 }}>
        <CircularProgress size={12} sx={{ color: tokens.textMuted }} />
      </Box>
    )
  }

  if (sortedBranches.length === 0) {
    return (
      <Box sx={{ pl: 4, py: 0.75 }}>
        <Typography
          sx={{
            fontSize: '0.75rem',
            color: tokens.textMuted,
            fontStyle: 'italic',
          }}
        >
          No branches
        </Typography>
      </Box>
    )
  }

  const handleCopyClick = (branch: ProjectBranchDto) => {
    setCopySource({ id: branch.id, name: branch.name })
    setCopyDialogOpen(true)
  }

  return (
    <>
      <Box component="ul" sx={{ m: 0, p: 0, listStyle: 'none' }}>
        {sortedBranches.map((branch) => (
          <BranchRow
            key={branch.id}
            branch={branch}
            slug={slug}
            projectId={projectId}
            active={branch.id === activeBranchId}
            activeRowRef={branch.id === activeBranchId ? activeBranchRowRef : null}
            onCopyClick={() => handleCopyClick(branch)}
          />
        ))}
      </Box>
      {copySource && (
        <CopyBranchDialog
          open={copyDialogOpen}
          onClose={() => setCopyDialogOpen(false)}
          slug={slug}
          projectId={projectId}
          sourceBranchId={copySource.id}
          sourceBranchName={copySource.name}
          existingBranchNames={existingBranchNames}
        />
      )}
    </>
  )
}

interface BranchRowProps {
  branch: ProjectBranchDto
  slug: string
  projectId: string
  active: boolean
  activeRowRef: React.RefObject<HTMLDivElement | null> | null
  onCopyClick: () => void
}

function BranchRow({
  branch,
  slug,
  projectId,
  active,
  activeRowRef,
  onCopyClick,
}: BranchRowProps) {
  const queryClient = useQueryClient()
  const { showSuccess, showError } = useNotification()
  const { user } = useAuth()
  const isSuperAdmin = !!user?.roles?.includes(ApplicationRoles.SuperAdmin)
  const isRunning = (branch.runningTurnCount ?? 0) > 0
  const relative = formatCompactRelative(branch.lastActivityAt ?? null)

  const [menuAnchorEl, setMenuAnchorEl] = useState<HTMLElement | null>(null)
  const [menuPosition, setMenuPosition] = useState<{
    mouseX: number
    mouseY: number
  } | null>(null)
  const branchMenuOpen = menuAnchorEl !== null || menuPosition !== null

  const runtimeStatusQuery = useGetApiProjectsProjectIdBranchesBranchIdRuntimeStatus(
    projectId,
    branch.id,
    {
      query: {
        enabled: branchMenuOpen && !!projectId && !!branch.id,
      },
    },
  )
  const runtimeState = runtimeStatusQuery.data?.state
  const isSuspended = runtimeState === RuntimeState.Suspended
  const isFailed =
    runtimeState === RuntimeState.Failed ||
    runtimeState === RuntimeState.Crashed
  const isOnline = runtimeState === RuntimeState.Online
  const canPutToSleep = isOnline

  const archiveMut = usePostApiProjectsProjectIdBranchesBranchIdArchive()
  const restartMut = usePostApiProjectsProjectIdBranchesBranchIdRuntimeRestart()
  const suspendMut = usePostApiProjectsProjectIdBranchesBranchIdRuntimeSuspend()
  const forceStopMut = usePostApiProjectsProjectIdBranchesBranchIdRuntimeForceStop()

  const invalidateRuntimeStatus = () => {
    void queryClient.invalidateQueries({
      queryKey: getGetApiProjectsProjectIdBranchesBranchIdRuntimeStatusQueryKey(
        projectId,
        branch.id,
      ),
    })
  }

  const invalidateBranchLists = () => {
    queryClient.invalidateQueries({
      queryKey: getGetApiProjectsProjectIdBranchesQueryKey(projectId),
    })
    queryClient.invalidateQueries({
      queryKey: getGetApiWorkspacesSlugProjectsQueryKey(slug),
    })
  }

  const closeBranchMenu = () => {
    setMenuAnchorEl(null)
    setMenuPosition(null)
  }

  const handleContextMenu = (e: React.MouseEvent) => {
    e.preventDefault()
    e.stopPropagation()
    setMenuAnchorEl(null)
    setMenuPosition({ mouseX: e.clientX + 2, mouseY: e.clientY - 6 })
  }

  const handleMenuButtonClick = (e: React.MouseEvent<HTMLButtonElement>) => {
    e.preventDefault()
    e.stopPropagation()
    if (menuAnchorEl === e.currentTarget && branchMenuOpen) {
      closeBranchMenu()
      return
    }
    setMenuPosition(null)
    setMenuAnchorEl(e.currentTarget)
  }

  const handleCopyFromMenu = () => {
    closeBranchMenu()
    onCopyClick()
  }

  const handleArchive = () => {
    closeBranchMenu()
    archiveMut.mutate(
      { projectId, branchId: branch.id },
      {
        onSuccess: () => {
          showSuccess(`Archived branch "${branch.name}".`)
          invalidateBranchLists()
        },
        onError: (err: unknown) => {
          const data = (err as {
            response?: { data?: { error?: string; message?: string } }
          })?.response?.data
          if (data?.error === 'has_running_session') {
            showError('Stop the running turn first to archive this branch.')
          } else if (data?.error === 'is_default') {
            showError('The default branch cannot be archived.')
          } else {
            showError(
              `Failed to archive branch: ${data?.message ?? 'unknown error'}`,
            )
          }
        },
      },
    )
  }

  const handleRestartRuntime = () => {
    closeBranchMenu()
    restartMut.mutate(
      { projectId, branchId: branch.id },
      {
        onSuccess: () => {
          showSuccess(
            isSuspended
              ? `Waking runtime for "${branch.name}".`
              : isFailed
                ? `Restarting runtime for "${branch.name}".`
                : `Restart requested for "${branch.name}".`,
          )
          invalidateRuntimeStatus()
        },
        onError: (err: unknown) => {
          showError(mapBranchRuntimeActionError(err))
        },
      },
    )
  }

  const handlePutToSleep = () => {
    closeBranchMenu()
    suspendMut.mutate(
      { projectId, branchId: branch.id },
      {
        onSuccess: () => {
          showSuccess(`Putting "${branch.name}" to sleep.`)
          invalidateRuntimeStatus()
          invalidateBranchLists()
        },
        onError: (err: unknown) => {
          showError(mapBranchRuntimeActionError(err))
        },
      },
    )
  }

  const handleForceStop = () => {
    closeBranchMenu()
    forceStopMut.mutate(
      { projectId, branchId: branch.id },
      {
        onSuccess: () => {
          showSuccess(`Force-stopping runtime for "${branch.name}".`)
          invalidateRuntimeStatus()
          invalidateBranchLists()
        },
        onError: (err: unknown) => {
          showError(mapBranchRuntimeActionError(err))
        },
      },
    )
  }

  const canArchive =
    !branch.isDefault && !isRunning && !branch.isArchived
  const archiveDisabledReason = branch.isDefault
    ? 'The default branch cannot be archived'
    : isRunning
      ? 'Stop the running turn first'
      : null
  const restartLabel = isSuspended ? 'Wake runtime' : 'Restart runtime'
  const sleepDisabledReason =
    runtimeStatusQuery.isLoading || runtimeStatusQuery.isFetching
      ? 'Checking runtime state…'
      : !canPutToSleep
        ? 'Only online runtimes can be put to sleep'
        : null

  // ── Inbox affordance ─────────────────────────────────────────────────────
  // Subscribes to the workspace activity store and surfaces a small unread
  // dot beside the branch name when this branch has an unacknowledged
  // terminal-turn event. The dot's color encodes the entry's outcome:
  //   * olive → the turn ended cleanly ("there's something new to read")
  //   * rust  → the turn failed         ("something needs your attention")
  // The row treats the unread badge as mutually exclusive with the
  // in-flight pulse dot: while a turn is mid-flight there's no terminal
  // event yet, and once the next terminal event arrives the per-branch
  // dedup in {@code pushActivity} replaces any older entry — so the two
  // states never overlap visually.
  const unreadStatus = useBranchUnreadActivityStatus(branch.id)
  const hasUnread = !isRunning && unreadStatus !== null
  const unreadDotColor =
    unreadStatus === 'failed' ? tokens.unreadDotFailed : tokens.unreadDotIdle
  const unreadTooltip =
    unreadStatus === 'failed'
      ? 'Turn failed — unread'
      : unreadStatus === 'running'
        ? 'Turn started — unread'
        : 'Turn completed — unread'

  const branchMenuItemSx = {
    fontSize: '0.75rem',
    color: tokens.textPrimary,
    letterSpacing: '-0.005em',
    py: 0.375,
    px: 1,
    gap: 0.75,
    minHeight: 28,
    '&:hover': { backgroundColor: tokens.rowHover },
    '&.Mui-disabled': { opacity: 0.45 },
  } as const

  const branchMenuIconSx = { fontSize: 14, flexShrink: 0 } as const

  const branchMenuDividerSx = { my: 0.25, borderColor: tokens.hairline } as const

  return (
    <Box
      component="li"
      onContextMenu={handleContextMenu}
      sx={{
        position: 'relative',
        // Hover-only reveal for the branch actions menu — keeps the calm scan
        // intact, but the menu becomes discoverable the moment the user
        // gestures at a row.
        '&:hover .branch-row-menu-action, &:focus-within .branch-row-menu-action':
          {
            opacity: 1,
          },
      }}
    >
      <Box
        ref={activeRowRef ?? undefined}
        component={RouterLink}
        to={branchWorkspaceHref(slug, projectId, branch.id)}
        sx={{
          position: 'relative',
          display: 'flex',
          alignItems: 'center',
          gap: 0.75,
          // 32px left inset — leaves room for the 2px accent rail at left:16 to
          // breathe (the rail is a short rounded pill inset from the sidebar
          // edge, NOT a full-bleed left edge — see the reference design).
          pl: 4,
          pr: 1.25,
          py: 0.5,
          cursor: 'pointer',
          textDecoration: 'none',
          color: 'inherit',
          backgroundColor: active ? tokens.rowActive : 'transparent',
          transition: 'background-color 120ms ease',
          '&:hover': {
            backgroundColor: active ? tokens.rowActive : tokens.rowHover,
          },
          '&:focus-visible': {
            outline: `2px solid ${tokens.accent}`,
            outlineOffset: -2,
          },
        }}
      >
        {/* Active-branch accent rail — a short 2px pill anchored 16px in from
            the sidebar's left edge, with 6px top/bottom breathing room so it
            reads as a "this branch" badge rather than a sidebar-spanning bar.
            Inset (not full-bleed) so it visually nests INSIDE the project
            row's column, anchoring the branch into the project hierarchy. */}
        {active && (
          <Box
            aria-hidden
            sx={{
              position: 'absolute',
              left: 16,
              top: 6,
              bottom: 6,
              width: 2,
              backgroundColor: tokens.accent,
              borderRadius: 999,
            }}
          />
        )}

        {/* Running indicator — amber pulse only when a turn is in flight on
            this branch. Quiet rows get no dot so the list reads calmly. */}
        {isRunning && (
          <Box sx={{ flexShrink: 0, display: 'inline-flex', alignItems: 'center' }}>
            <StatusDot state={RuntimeState.Booting} size={6} />
          </Box>
        )}

        {/* Unread terminal-event indicator. Inbox semantics: the dot
            appears when the most recent terminal turn on this branch is
            still unacknowledged, and clears the moment the user opens the
            branch (route effect upstream calls {@code markBranchRead}).
            Only shown when not currently running so the running pulse
            never has to share its slot. */}
        {hasUnread && (
          <Tooltip title={unreadTooltip} placement="right" enterDelay={400}>
            <Box
              aria-label={unreadTooltip}
              sx={{
                flexShrink: 0,
                width: 6,
                height: 6,
                borderRadius: '50%',
                backgroundColor: unreadDotColor,
                display: 'inline-block',
              }}
            />
          </Tooltip>
        )}

        <SidebarTruncatedText
          text={branch.name}
          placement="right"
          sx={{
            fontSize: '0.75rem',
            fontWeight: active ? 500 : 400,
            color: active ? tokens.textPrimary : tokens.textMuted,
            letterSpacing: '-0.005em',
            fontFamily: workspaceFontFamily.mono,
          }}
        />

        {branch.isDefault && !relative && (
          <Tooltip title="Default branch" placement="left" enterDelay={500}>
            <Box
              component="span"
              sx={{
                flexShrink: 0,
                fontSize: '0.625rem',
                color: tokens.textMuted,
                fontStyle: 'italic',
              }}
            >
              default
            </Box>
          </Tooltip>
        )}

        {relative && (
          <Tooltip
            title={branch.isDefault ? 'Default branch' : ''}
            placement="left"
            enterDelay={500}
          >
            <Box
              component="span"
              sx={{
                flexShrink: 0,
                fontSize: '0.625rem',
                color: tokens.textMuted,
                fontFamily: workspaceFontFamily.mono,
                fontVariantNumeric: 'tabular-nums',
                opacity: 0.85,
              }}
            >
              {relative}
            </Box>
          </Tooltip>
        )}

        {/* Branch actions — hover- or focus-revealed hamburger opens the same
            menu as right-click (copy, sleep, restart, archive, …). */}
        <Tooltip title="Branch actions" placement="top" enterDelay={400}>
          <IconButton
            className="branch-row-menu-action"
            size="small"
            aria-label={`Branch actions for ${branch.name}`}
            aria-haspopup="menu"
            aria-expanded={branchMenuOpen && menuAnchorEl !== null}
            onClick={handleMenuButtonClick}
            onKeyDown={(e) => {
              if (e.key === 'Enter' || e.key === ' ') {
                e.preventDefault()
                e.stopPropagation()
              }
            }}
            sx={{
              opacity: 0,
              transition: 'opacity 120ms ease',
              padding: 0.25,
              color: tokens.textMuted,
              flexShrink: 0,
              ml: 0.25,
              '&:hover': { color: tokens.textPrimary },
              '&:focus-visible': {
                opacity: 1,
                outline: `2px solid ${tokens.accent}`,
                outlineOffset: 1,
              },
            }}
          >
            <MenuIcon sx={{ fontSize: 14 }} />
          </IconButton>
        </Tooltip>
      </Box>

      <Menu
        open={branchMenuOpen}
        onClose={closeBranchMenu}
        anchorReference={menuAnchorEl ? 'anchorEl' : 'anchorPosition'}
        anchorEl={menuAnchorEl ?? undefined}
        anchorPosition={
          menuPosition
            ? { top: menuPosition.mouseY, left: menuPosition.mouseX }
            : undefined
        }
        anchorOrigin={{ vertical: 'bottom', horizontal: 'right' }}
        transformOrigin={{ vertical: 'top', horizontal: 'right' }}
        slotProps={{
          paper: {
            sx: {
              minWidth: 168,
              border: `1px solid ${tokens.hairline}`,
              borderRadius: 1.5,
              boxShadow: '0 4px 16px rgba(0,0,0,0.07)',
              backgroundColor: tokens.canvasBg,
            },
          },
          list: { dense: true, sx: { py: 0.25 } },
        }}
      >
        <MenuItem dense onClick={handleCopyFromMenu} sx={branchMenuItemSx}>
          <ContentCopyIcon sx={branchMenuIconSx} />
          Copy branch
        </MenuItem>
        <Divider sx={branchMenuDividerSx} />
        <Tooltip
          title={sleepDisabledReason ?? ''}
          placement="right"
          disableHoverListener={canPutToSleep && !suspendMut.isPending}
        >
          <span>
            <MenuItem
              dense
              onClick={handlePutToSleep}
              disabled={
                !canPutToSleep ||
                suspendMut.isPending ||
                runtimeStatusQuery.isLoading
              }
              sx={branchMenuItemSx}
            >
              <BedtimeOutlinedIcon sx={branchMenuIconSx} />
              Put to sleep
            </MenuItem>
          </span>
        </Tooltip>
        <MenuItem
          dense
          onClick={handleRestartRuntime}
          disabled={restartMut.isPending}
          sx={branchMenuItemSx}
        >
          <RestartAltIcon sx={branchMenuIconSx} />
          {restartLabel}
        </MenuItem>
        <Divider sx={branchMenuDividerSx} />
        <Tooltip
          title={archiveDisabledReason ?? ''}
          placement="right"
          disableHoverListener={canArchive}
        >
          <span>
            <MenuItem
              dense
              onClick={handleArchive}
              disabled={!canArchive || archiveMut.isPending}
              sx={branchMenuItemSx}
            >
              <ArchiveOutlinedIcon sx={branchMenuIconSx} />
              Archive branch
            </MenuItem>
          </span>
        </Tooltip>
        {isSuperAdmin && (
          <>
            <Divider sx={branchMenuDividerSx} />
            <MenuItem
              dense
              onClick={handleForceStop}
              disabled={forceStopMut.isPending}
              sx={branchMenuItemSx}
            >
              <StopCircleOutlinedIcon sx={branchMenuIconSx} />
              Force stop
            </MenuItem>
          </>
        )}
      </Menu>
    </Box>
  )
}

function mapBranchRuntimeActionError(err: unknown): string {
  const data = (err as {
    response?: { data?: { error?: string; detail?: string }; status?: number }
  })?.response?.data
  const raw = data?.error ?? data?.detail
  if (raw) {
    return raw.replace(/^(conflict:|not-found:)\s*/, '').trim()
  }
  if ((err as { response?: { status?: number } })?.response?.status === 409) {
    return "Runtime is in a state that can't be restarted right now."
  }
  return "Couldn't restart the runtime. Try again in a moment."
}

function ProjectListSkeleton() {
  return (
    <Box sx={{ px: 1, pt: 0.5 }}>
      {[0, 1, 2].map((i) => (
        <Box
          key={i}
          sx={{
            display: 'flex',
            alignItems: 'center',
            gap: 1,
            py: 1,
            pl: 1.5,
            pr: 1.5,
            opacity: 0.4 - i * 0.08,
          }}
        >
          <Box
            sx={{
              width: 16,
              height: 16,
              borderRadius: 0.5,
              backgroundColor: 'rgba(0,0,0,0.06)',
            }}
          />
          <Box
            sx={{
              flex: 1,
              height: 12,
              borderRadius: 1,
              backgroundColor: 'rgba(0,0,0,0.06)',
            }}
          />
          <Box
            sx={{
              width: 8,
              height: 8,
              borderRadius: '50%',
              backgroundColor: 'rgba(0,0,0,0.08)',
            }}
          />
        </Box>
      ))}
    </Box>
  )
}

// ── Helpers ─────────────────────────────────────────────────────────────────

interface SidebarTruncatedTextProps {
  text: string
  sx?: SxProps<Theme>
  placement?: TooltipProps['placement']
}

/**
 * Ellipsis-truncated sidebar label that only surfaces a {@link Tooltip} when
 * the text is actually clipped — avoids noisy native {@code title} tooltips
 * on short branch names that fit comfortably.
 */
function SidebarTruncatedText({
  text,
  sx,
  placement = 'right',
}: SidebarTruncatedTextProps) {
  const textRef = useRef<HTMLElement>(null)
  const [isTruncated, setIsTruncated] = useState(false)

  const measureTruncation = useCallback(() => {
    const el = textRef.current
    if (!el) return
    setIsTruncated(el.scrollWidth > el.clientWidth)
  }, [])

  useLayoutEffect(() => {
    measureTruncation()
  }, [text, measureTruncation])

  useEffect(() => {
    const el = textRef.current
    if (!el) return
    const observer = new ResizeObserver(measureTruncation)
    observer.observe(el)
    return () => observer.disconnect()
  }, [measureTruncation])

  return (
    <Tooltip
      title={text}
      placement={placement}
      enterDelay={400}
      disableHoverListener={!isTruncated}
    >
      <Typography
        ref={textRef}
        component="span"
        sx={{
          flex: 1,
          minWidth: 0,
          display: 'block',
          whiteSpace: 'nowrap',
          overflow: 'hidden',
          textOverflow: 'ellipsis',
          ...sx,
        }}
      >
        {text}
      </Typography>
    </Tooltip>
  )
}

/**
 * Compact relative-time formatter for branch rows. Returns very short strings
 * ("12m", "3h", "yesterday", "3d", "2w") instead of the verbose date-fns
 * default ("about 3 hours ago"). Tiny font + monospace digits => visually
 * inert until the eye actually rests on a row.
 */
function formatCompactRelative(iso: string | null | undefined): string {
  if (!iso) return ''
  let parsed: Date
  try {
    parsed = parseISO(iso)
  } catch {
    return ''
  }
  if (Number.isNaN(parsed.getTime())) return ''
  const diffMs = Date.now() - parsed.getTime()
  if (diffMs < 0) return 'now'
  const seconds = Math.floor(diffMs / 1000)
  if (seconds < 60) return 'now'
  const minutes = Math.floor(seconds / 60)
  if (minutes < 60) return `${minutes}m`
  const hours = Math.floor(minutes / 60)
  if (hours < 24) return `${hours}h`
  const days = Math.floor(hours / 24)
  if (days === 1) return 'yesterday'
  if (days < 7) return `${days}d`
  const weeks = Math.floor(days / 7)
  if (weeks < 4) return `${weeks}w`
  // Anything older — fall back to relative date-fns string, capped sensibly.
  try {
    return formatDistanceToNow(parsed, { addSuffix: false })
      .replace('about ', '')
      .replace(' months', 'mo')
      .replace(' month', 'mo')
      .replace(' years', 'y')
      .replace(' year', 'y')
  } catch {
    return ''
  }
}

// ── Workspace cost tag ──────────────────────────────────────────────────────
//
// Tiny muted "$X.XX" rendered next to the workspace name in the sidebar
// header. Co-located rather than pulled into its own file because it's
// header-specific (the {@code Tooltip} placement / muted token reach into
// the sidebar's design language) and the entire surface is roughly twenty
// lines.

interface WorkspaceCostTagProps {
  workspaceId: string
}

function WorkspaceCostTag({ workspaceId }: WorkspaceCostTagProps) {
  const costQuery = useGetApiWorkspacesWorkspaceIdCost(workspaceId, {
    query: {
      enabled: !!workspaceId,
      staleTime: 60_000,
    },
  })
  const total = costQuery.data?.totalCostUsd ?? 0
  if (costQuery.isLoading || costQuery.isError) return null
  if (total <= 0) return null

  return (
    <Tooltip
      title={`Workspace lifetime spend across ${costQuery.data?.sessionCount ?? 0} session${
        (costQuery.data?.sessionCount ?? 0) === 1 ? '' : 's'
      }`}
      enterDelay={400}
      placement="right"
    >
      <Box
        component="span"
        sx={{
          flexShrink: 0,
          fontSize: '0.625rem',
          fontWeight: 500,
          color: tokens.textMuted,
          letterSpacing: '-0.005em',
          lineHeight: 1,
          fontVariantNumeric: 'tabular-nums',
          cursor: 'default',
        }}
      >
        {formatCostUsd(total)}
      </Box>
    </Tooltip>
  )
}

// ThemeTweaksButtons was removed in Phase 2 footer cleanup — the light/dark
// toggle + accent rotator now live in the dedicated Tweaks panel (TBD,
// Phase 11). Until that ships, theme tweaks are reachable via the workspace
// playground at /w/:slug/playground or the standard system colour-scheme
// preference. Keeping this banner so a future search for the symbol lands on
// a real explanation rather than a "where did it go?" mystery.
