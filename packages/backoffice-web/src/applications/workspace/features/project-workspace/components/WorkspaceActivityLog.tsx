import { useEffect, useMemo, useState } from 'react'
import { useNavigate, useParams } from 'react-router-dom'
import { useQueries } from '@tanstack/react-query'
import { Box, Tooltip, Typography } from '@mui/material'
import {
  getGetApiProjectsProjectIdBranchesQueryOptions,
  useGetApiWorkspacesSlugProjects,
} from '../../../../../api/queries-commands'
import {
  markAllRead,
  markRead,
  purgeActivityWhere,
  useWorkspaceActivity,
  type WorkspaceActivityEntry,
} from '../hooks/useWorkspaceActivityStore'
import { branchWorkspaceHref } from '../hooks/branchConversationMemory'

import { chromeTokens, semanticTokens, surfaceTokens, workspaceAccent, workspaceFontFamily } from '../../../shared/designTokens'

const tokens = { ...surfaceTokens, ...chromeTokens, ...semanticTokens }

const MAX_RENDER_ENTRIES = 5
const MAX_AGE_MS = 60 * 60 * 1000

interface WorkspaceActivityLogProps {
  /**
   * Optional override for the time source — lets us reactively re-filter
   * stale entries without leaking a {@code setInterval} into every render.
   * Internally we tick once per minute so the 1h cutoff slides smoothly
   * without thrashing the React tree.
   */
  nowMs?: number
}

/**
 * Compact activity log rendered at the bottom of the workspace sidebar.
 *
 * <p>Reads from {@link useWorkspaceActivity}, applies the 1h age filter and
 * the 5-entry cap on render, and hides the whole section when there's
 * nothing to show. Entries are click-through: clicking navigates to the
 * branch the event came from (if we resolved a projectId/branchId) and
 * marks the entry read.</p>
 *
 * <p>Project / branch names are resolved here at render-time via the
 * workspace projects list + per-project branch lists. Resolution is
 * reactive — if the user renames a project / branch the activity row's
 * label updates immediately. While a branch list is still loading we paint
 * "Loading…" rather than a stale fallback; if an ID truly can't be resolved
 * (project deleted, branch gone) we paint "Unknown project" /
 * "Unknown branch".</p>
 */
export function WorkspaceActivityLog({ nowMs }: WorkspaceActivityLogProps) {
  const navigate = useNavigate()
  const { slug = '' } = useParams<{ slug: string }>()
  const allEntries = useWorkspaceActivity()

  // ── Slide the 1h cutoff on a 1-minute tick ──────────────────────────────
  // Re-rendering once a minute is cheap and keeps the visible list honest
  // without each entry owning its own timer.
  const [internalNow, setInternalNow] = useState(() => Date.now())
  useEffect(() => {
    if (nowMs !== undefined) return // externally controlled — skip our tick
    const interval = setInterval(() => setInternalNow(Date.now()), 60_000)
    return () => clearInterval(interval)
  }, [nowMs])
  const now = nowMs ?? internalNow

  const visibleEntries = useMemo(() => {
    const cutoff = now - MAX_AGE_MS
    return (
      allEntries
        .filter((e) => e.timestamp >= cutoff)
        // Inbox semantics: read entries vanish from view the moment they
        // flip. They're still kept in the store (so the dedup-by-id guard
        // can stop a SignalR reconnect from re-pushing the same event as
        // unread) — they just stop rendering. The panel auto-hides
        // entirely once everything is read, courtesy of the
        // {@code resolvedEntries.length === 0} early return below.
        .filter((e) => e.unread)
        // Defensive sort by timestamp DESC — pushActivity prepends so insertion
        // order is normally correct, but a SignalR reconnect replay could in
        // theory deliver events out of chronological order. Sorting here
        // guarantees the row at the top is always the most recent.
        .slice()
        .sort((a, b) => b.timestamp - a.timestamp)
        .slice(0, MAX_RENDER_ENTRIES)
    )
  }, [allEntries, now])

  // ── Name resolution (render-time) ───────────────────────────────────────
  //
  // Pull the workspace projects list — same cache the sidebar polls so this
  // is virtually always a hit. The hook subscribes to cache updates so a
  // rename refreshes the activity log automatically.
  const projectsQuery = useGetApiWorkspacesSlugProjects(slug, {
    query: { enabled: !!slug },
  })

  // For each unique projectId in the visible entries, subscribe to that
  // project's branches list. {@code useQueries} composes the per-id calls
  // dynamically. Bounded by MAX_RENDER_ENTRIES (5) so the request fan-out
  // is naturally capped.
  const uniqueProjectIds = useMemo(() => {
    const seen = new Set<string>()
    for (const e of visibleEntries) {
      if (e.projectId) seen.add(e.projectId)
    }
    return Array.from(seen)
  }, [visibleEntries])

  const branchQueries = useQueries({
    queries: uniqueProjectIds.map((projectId) =>
      getGetApiProjectsProjectIdBranchesQueryOptions(projectId, undefined, {
        query: { staleTime: 30_000 },
      }),
    ),
  })

  const branchesByProjectId = useMemo(() => {
    const map = new Map<
      string,
      {
        branches: ReadonlyArray<{ id: string; name: string }> | undefined
        /** Only true when the underlying query has settled successfully and we
         *  can trust an "id not present" answer. */
        isSettled: boolean
      }
    >()
    uniqueProjectIds.forEach((projectId, idx) => {
      const q = branchQueries[idx]
      map.set(projectId, {
        branches: q?.data,
        isSettled: q?.status === 'success',
      })
    })
    return map
  }, [uniqueProjectIds, branchQueries])

  const projectsList = projectsQuery.data
  // Only when the projects query has SETTLED successfully can we trust an
  // "id not in data" answer to mean "the project was deleted". Anything
  // else (pending, fetching, error) must show "Loading…" so we don't flash
  // "Unknown project" during refetch / hard reload / cross-workspace nav.
  const isProjectsSettled = projectsQuery.status === 'success'

  // ── Cross-workspace purge ───────────────────────────────────────────────
  // When the workspace slug changes, activity entries from the previous
  // workspace point at projectIds that aren't in this workspace's list.
  // Those entries can never resolve and would otherwise paint
  // "Unknown project" forever. Once the projects query settles for the
  // current slug, drop anything whose projectId isn't in the list.
  useEffect(() => {
    if (!isProjectsSettled || !projectsList) return
    const validIds = new Set(projectsList.map((p) => p.id))
    purgeActivityWhere(
      (e) => e.projectId !== null && !validIds.has(e.projectId),
    )
  }, [isProjectsSettled, projectsList])

  const resolvedEntries = useMemo(() => {
    return visibleEntries.map((entry) => {
      // Project name
      // Predicate:
      //   • No projectId at all on the entry → "Unknown project"
      //   • Query settled (status === 'success') AND id not in data → "Unknown project"
      //   • Anything else (pending / fetching / error / not yet settled) → "Loading…"
      let projectName: string
      if (!entry.projectId) {
        projectName = 'Unknown project'
      } else if (!isProjectsSettled || !projectsList) {
        projectName = 'Loading…'
      } else {
        const found = projectsList.find((p) => p.id === entry.projectId)
        projectName = found?.name ?? 'Unknown project'
      }

      // Branch name
      // Same predicate shape as projectName: only call it deleted when the
      // per-project branches query has settled and the id genuinely isn't
      // in the returned list.
      let branchName: string
      if (!entry.branchId) {
        branchName = 'Unknown branch'
      } else if (!entry.projectId) {
        branchName = 'Unknown branch'
      } else {
        const bucket = branchesByProjectId.get(entry.projectId)
        if (!bucket || !bucket.isSettled || !bucket.branches) {
          branchName = 'Loading…'
        } else {
          const found = bucket.branches.find((b) => b.id === entry.branchId)
          branchName = found?.name ?? 'Unknown branch'
        }
      }

      return { entry, projectName, branchName }
    })
  }, [visibleEntries, projectsList, isProjectsSettled, branchesByProjectId])

  // No entries → render nothing. With {@code visibleEntries} now filtered
  // to {@code unread: true} only, this is the inbox-cleared state — the
  // panel disappears the moment the user has dismissed (or auto-cleared
  // via branch navigation) every outstanding entry.
  if (resolvedEntries.length === 0) return null

  const handleEntryClick = (entry: WorkspaceActivityEntry) => {
    markRead(entry.id)
    if (slug && entry.projectId && entry.branchId) {
      navigate(
        branchWorkspaceHref(
          slug,
          entry.projectId,
          entry.branchId,
          entry.conversationId,
        ),
      )
    }
  }

  return (
    <Box
      component="section"
      aria-label="Workspace activity"
      sx={{
        borderTop: `1px solid ${tokens.hairline}`,
        backgroundColor: 'transparent',
        flexShrink: 0,
      }}
    >
      {/* Section header — matches the section header style upstream so the
          log reads as another section, not a new chrome surface. */}
      <Box
        sx={{
          px: 2,
          pt: 1.5,
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
            color: tokens.textMuted,
          }}
        >
          Activity
        </Typography>
        {/* Always render the dismiss-all affordance when the panel is
            visible — every visible entry is now unread by construction, so
            "Mark all as read" can never be a no-op while the panel is on
            screen. */}
        <Box
          component="button"
          type="button"
          onClick={(e) => {
            e.preventDefault()
            markAllRead()
          }}
          sx={{
            all: 'unset',
            cursor: 'pointer',
            fontSize: '0.6875rem',
            color: tokens.textMuted,
            opacity: 0.85,
            fontWeight: 500,
            '&:hover': { color: tokens.textPrimary, opacity: 1 },
            '&:focus-visible': {
              outline: `2px solid ${workspaceAccent.ink}`,
              outlineOffset: 2,
              borderRadius: 2,
            },
          }}
        >
          Mark all as read
        </Box>
      </Box>
      <Box component="ul" sx={{ m: 0, p: 0, listStyle: 'none' }}>
        {resolvedEntries.map(({ entry, projectName, branchName }) => (
          <ActivityRow
            key={entry.id}
            entry={entry}
            projectName={projectName}
            branchName={branchName}
            onClick={() => handleEntryClick(entry)}
          />
        ))}
      </Box>
    </Box>
  )
}

// ── Row ────────────────────────────────────────────────────────────────────

interface ActivityRowProps {
  entry: WorkspaceActivityEntry
  projectName: string
  branchName: string
  onClick: () => void
}

function ActivityRow({ entry, projectName, branchName, onClick }: ActivityRowProps) {
  const dotColor =
    entry.status === 'failed'
      ? tokens.failureDot
      : entry.status === 'running'
        ? tokens.runningDot
        : tokens.successDot

  // Short status label sentence — turns the bare status enum into a calm
  // human reading. Mirrors the reference design ("Bootstrap failed",
  // "Committed", "Idle") rather than echoing the raw token.
  const statusText =
    entry.status === 'failed'
      ? 'Failed'
      : entry.status === 'running'
        ? 'Running'
        : 'Idle'

  // Compact relative timestamp — "4m", "1h" — the right-edge anchor of the
  // row per the reference design. We use the same {@link Date.now} clock the
  // panel ticks against; the parent re-renders once a minute, so the value
  // slides forward without each row owning its own timer.
  const relative = formatCompactRelative(entry.timestamp)

  const tooltipLabel = `${projectName} › ${branchName} · ${statusText}`

  return (
    <Box component="li">
      <Box
        role="button"
        tabIndex={0}
        onClick={onClick}
        onKeyDown={(e) => {
          if (e.key === 'Enter' || e.key === ' ') {
            e.preventDefault()
            onClick()
          }
        }}
        sx={{
          display: 'flex',
          alignItems: 'center',
          gap: 1,
          // 14px horizontal padding matches the reference design's activity
          // row gutters, and pairs with the section-header padding above so
          // the column reads as one visual unit.
          px: 1.75,
          py: 0.625,
          cursor: 'pointer',
          transition: 'background-color 120ms ease',
          '&:hover': { backgroundColor: tokens.rowHover },
          '&:focus-visible': {
            outline: `2px solid ${workspaceAccent.ink}`,
            outlineOffset: -2,
          },
        }}
      >
        {/* Tone dot — LEFT-anchored per the reference design. Color carries
            the entry's outcome at a glance before the eye lands on the text. */}
        <Box
          aria-hidden
          sx={{
            width: 6,
            height: 6,
            borderRadius: '50%',
            backgroundColor: dotColor,
            flexShrink: 0,
          }}
        />
        <Tooltip title={tooltipLabel} placement="top" enterDelay={500}>
          <Typography
            sx={{
              flex: 1,
              minWidth: 0,
              fontSize: '0.75rem',
              fontWeight: 500,
              color: tokens.textMuted,
              whiteSpace: 'nowrap',
              overflow: 'hidden',
              textOverflow: 'ellipsis',
              letterSpacing: '-0.005em',
            }}
          >
            <Box
              component="span"
              sx={{ color: tokens.textPrimary, fontWeight: 500 }}
            >
              {projectName}
            </Box>
            <Box
              component="span"
              sx={{
                color: tokens.textFaint,
                mx: 0.5,
                // Chevron-like project›branch separator per the reference
                // design — softer than a slash, more directional than a dot.
              }}
            >
              ›
            </Box>
            <Box
              component="span"
              sx={{ color: tokens.textMuted, fontWeight: 400 }}
            >
              {branchName}
            </Box>
            <Box
              component="span"
              sx={{
                color: tokens.textFaint,
                mx: 0.625,
              }}
            >
              ·
            </Box>
            <Box
              component="span"
              sx={{ color: tokens.textMuted, fontWeight: 400 }}
            >
              {statusText}
            </Box>
          </Typography>
        </Tooltip>
        {/* Mono relative timestamp anchored right — the visual full-stop of
            each row, mirroring the reference design's branch-row time column. */}
        {relative && (
          <Box
            component="span"
            aria-label={new Date(entry.timestamp).toLocaleString()}
            sx={{
              flexShrink: 0,
              fontSize: '0.625rem',
              fontFamily: workspaceFontFamily.mono,
              fontVariantNumeric: 'tabular-nums',
              color: tokens.textFaint,
              letterSpacing: '-0.005em',
            }}
          >
            {relative}
          </Box>
        )}
      </Box>
    </Box>
  )
}

/**
 * Compact "Nm" / "Nh" relative formatter that matches the branch-row time
 * style. Returns {@code null} for entries less than a minute old (we'd
 * rather show nothing than a noisy "0m" / "5s" tick during a turn).
 */
function formatCompactRelative(timestamp: number): string | null {
  const diffMs = Date.now() - timestamp
  if (diffMs < 60_000) return null
  const minutes = Math.floor(diffMs / 60_000)
  if (minutes < 60) return `${minutes}m`
  const hours = Math.floor(minutes / 60)
  if (hours < 24) return `${hours}h`
  const days = Math.floor(hours / 24)
  return `${days}d`
}
