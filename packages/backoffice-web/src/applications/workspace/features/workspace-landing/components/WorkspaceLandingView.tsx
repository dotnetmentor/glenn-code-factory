import { useMemo } from 'react'
import { Link as RouterLink, useNavigate } from 'react-router-dom'
import {
  Alert,
  Box,
  Button,
  Skeleton,
  Stack,
  Typography,
} from '@mui/material'
import AddIcon from '@mui/icons-material/Add'
import ArrowForwardIcon from '@mui/icons-material/ArrowForward'
import GitHubIcon from '@mui/icons-material/GitHub'
import RocketLaunchIcon from '@mui/icons-material/RocketLaunch'
import { formatDistanceToNow, parseISO } from 'date-fns'
import {
  RuntimeState,
  useGetApiMe,
  useGetApiWorkspacesSlugBranchesRecent,
  useGetApiWorkspacesSlugGithubInstallations,
  useGetApiWorkspacesSlugProjects,
  type RecentBranchDto,
} from '../../../../../api/queries-commands'
import { useWorkspace } from '../../../../shared/contexts/WorkspaceContext'
import { useDocumentTitle } from '../../../../shared/hooks'
import { StatusDot } from '../../project-workspace/components/StatusDot'

import {
  chromeTokens,
  semanticTokens,
  surfaceTokens,
  workspaceAccent,
  workspaceFontFamily,
} from '../../../shared/designTokens'

const tokens = { ...surfaceTokens, ...chromeTokens, ...semanticTokens }

/** How many recent branches to surface in the "Recent work" section. */
const RECENT_LIMIT = 10

/**
 * Workspace landing canvas — what the user sees when they hit {@code /w/{slug}}.
 *
 * <p>Replaces the legacy {@code ProjectsPage} grid (which lived in the old
 * top-nav chrome) with a calm resume surface inside the new {@link
 * WorkspaceShellLayout}. The sidebar already lists every project and every
 * branch — repeating that list on the canvas would be noise. Instead the
 * canvas answers "what should I do next?":</p>
 * <ul>
 *   <li>A warm "Welcome back, {firstName}" hero so the user knows they're home.</li>
 *   <li>A single bright CTA — "Start a new session" — for the most common
 *       intent on a fresh visit.</li>
 *   <li>Up to ten most-recently-active <em>branches</em> across the
 *       workspace, sorted by {@code lastActivityAt}, so the user can hop
 *       straight back into the exact thread of work they were last in.
 *       Clicking a row deep-links to {@code /projects/{id}/branches/{id}}
 *       — no project-default-branch redirect dance.</li>
 *   <li>If the workspace has no GitHub installation, a calm empty-state card
 *       inviting the user to "Connect your GitHub" deep-links to
 *       {@code /w/{slug}/settings} — the integration tab is the only way
 *       forward, so we make it impossible to miss.</li>
 * </ul>
 *
 * <p>Layout mirrors {@code NewSessionView}: 880px centred paper column,
 * generous vertical rhythm, hairline dividers between sections.</p>
 */
export function WorkspaceLandingView() {
  const navigate = useNavigate()
  const { currentWorkspace, currentSlug } = useWorkspace()
  const slug = currentSlug ?? ''

  const meQuery = useGetApiMe()
  const recentBranchesQuery = useGetApiWorkspacesSlugBranchesRecent(
    slug,
    { limit: RECENT_LIMIT },
    {
      query: { enabled: !!slug, refetchInterval: 15_000 },
    },
  )
  // We still need a "does this workspace have ANY projects?" signal for the
  // empty-state copy below — `recentBranches.length === 0` is ambiguous (it
  // could mean "no projects yet" or "projects exist, just no activity").
  // Projects list is cheap and already cached elsewhere in the shell.
  const projectsQuery = useGetApiWorkspacesSlugProjects(slug, {
    query: { enabled: !!slug },
  })
  const installationsQuery = useGetApiWorkspacesSlugGithubInstallations(slug, {
    query: { enabled: !!slug },
  })

  // Pull the user's first name, falling back through display name → email
  // local-part → a generic greeting. We do this defensively because /api/me
  // sometimes returns empty firstName for SSO-only accounts that haven't
  // filled out a profile yet.
  const firstName = useMemo(() => {
    const raw = meQuery.data?.firstName?.trim()
    if (raw) return raw
    const email = meQuery.data?.email
    if (email) {
      const localPart = email.split('@')[0]
      if (localPart) return localPart
    }
    return null
  }, [meQuery.data?.firstName, meQuery.data?.email])

  // Backend already orders branches by lastActivityAt desc and respects the
  // limit; no client-side sort/filter needed. Keeping a stable reference
  // through useMemo so the row list doesn't re-key on every render.
  const recentBranches = useMemo<RecentBranchDto[]>(
    () => recentBranchesQuery.data ?? [],
    [recentBranchesQuery.data],
  )

  const hasAnyProjects = (projectsQuery.data ?? []).length > 0
  const isRecentLoading = recentBranchesQuery.isLoading
  const installationsLoaded = !installationsQuery.isLoading
  const hasGitHub = (installationsQuery.data ?? []).length > 0
  const showGitHubEmptyState = installationsLoaded && !hasGitHub

  const workspaceName = currentWorkspace?.name ?? slug ?? 'Workspace'

  // Browser tab reads "{workspace} · GlennCode" — when juggling four
  // workspaces in four tabs, this is the only thing that lets the user pick
  // the right one without focusing each.
  useDocumentTitle(workspaceName ? `${workspaceName} · GlennCode` : null)

  return (
    <Box
      sx={{
        width: '100%',
        height: '100%',
        overflow: 'auto',
        backgroundColor: tokens.canvasBg,
      }}
    >
      <Box
        sx={{
          maxWidth: 880,
          mx: 'auto',
          px: { xs: 3, md: 4 },
          py: { xs: 4, md: 6 },
        }}
      >
        {/* ── Page header ────────────────────────────────────────────────── */}
        <Stack spacing={1} sx={{ mb: 5 }}>
          <Typography
            component="h1"
            sx={{
              fontSize: { xs: '1.5rem', md: '1.75rem' },
              fontWeight: 600,
              letterSpacing: '-0.015em',
              color: tokens.textPrimary,
              lineHeight: 1.2,
            }}
          >
            {firstName ? `Welcome back, ${firstName}` : 'Welcome back'}
          </Typography>
          <Typography
            sx={{
              fontSize: '0.875rem',
              color: tokens.textMuted,
              letterSpacing: '-0.005em',
              lineHeight: 1.5,
            }}
          >
            Pick up where you left off, or start something new.
          </Typography>
          {workspaceName && (
            <Typography
              sx={{
                fontSize: '0.75rem',
                color: tokens.textFaint,
                letterSpacing: '0.04em',
                textTransform: 'uppercase',
                fontWeight: 500,
                mt: 0.5,
              }}
            >
              {workspaceName}
            </Typography>
          )}
        </Stack>

        {/* ── GitHub empty state ─────────────────────────────────────────────
            Shown ONLY when the installations query has resolved and returned
            zero rows. We don't show this while loading — a "Connect GitHub"
            card flashing in for 200ms then disappearing would feel wrong. */}
        {showGitHubEmptyState && (
          <Box sx={{ mb: 5 }}>
            <ConnectGitHubCard slug={slug} />
          </Box>
        )}

        {/* ── Quick action — start a new session ──────────────────────────
            The primary verb on this canvas. Sits above "Recent work" because
            on a brand-new workspace there IS no recent work, and on a
            returning visit the user often wants a fresh session anyway. */}
        <Stack spacing={2} sx={{ mb: 6 }}>
          <SectionHeading>Start something new</SectionHeading>
          <Box
            sx={{
              border: `1px solid ${tokens.hairline}`,
              borderRadius: 2,
              backgroundColor: tokens.cardBg,
              p: { xs: 2.5, md: 3 },
              display: 'flex',
              flexDirection: { xs: 'column', sm: 'row' },
              alignItems: { xs: 'flex-start', sm: 'center' },
              justifyContent: 'space-between',
              gap: 2,
            }}
          >
            <Stack spacing={0.5} sx={{ minWidth: 0 }}>
              <Typography
                sx={{
                  fontSize: '1rem',
                  fontWeight: 600,
                  color: tokens.textPrimary,
                  letterSpacing: '-0.005em',
                }}
              >
                Start a new session
              </Typography>
              <Typography
                sx={{
                  fontSize: '0.8125rem',
                  color: tokens.textMuted,
                  letterSpacing: '-0.005em',
                  lineHeight: 1.55,
                }}
              >
                Pick a project, attach to a git branch or fork a new one — then
                hand it off to an agent.
              </Typography>
            </Stack>
            <Button
              variant="pill" color="primary"
              startIcon={<AddIcon sx={{ fontSize: 16 }} />}
              onClick={() => navigate(`/w/${slug}/new-session`)}
              disabled={!slug}
              sx={{ flexShrink: 0 }}
            >
              New session
            </Button>
          </Box>
        </Stack>

        {/* ── Recent work ─────────────────────────────────────────────────
            Last N branches you (or anyone in the workspace) touched, ordered
            by lastActivityAt desc. Each row links to the EXACT branch route
            (not the project root that redirects to default), so a click is
            literally "resume that thread". We surface {project · branch},
            {owner}/{repo}, and "X ago" so similar branch names stay
            distinguishable at a glance.

            When the workspace has projects but no activity yet, we still
            show the empty-state CTA — the user shouldn't have to wonder
            whether the page is broken. */}
        <Stack spacing={2}>
          <SectionHeading>Recent work</SectionHeading>

          {isRecentLoading ? (
            <RecentWorkSkeleton />
          ) : recentBranchesQuery.isError ? (
            <Alert
              severity="error"
              variant="quiet"
            >
              Could not load recent work.
            </Alert>
          ) : recentBranches.length === 0 ? (
            <EmptyRecentWork
              slug={slug}
              canStartProject={hasGitHub}
              hasAnyProjects={hasAnyProjects}
            />
          ) : (
            <Stack
              divider={
                <Box
                  aria-hidden
                  sx={{ height: '1px', backgroundColor: tokens.hairline }}
                />
              }
              sx={{
                border: `1px solid ${tokens.hairline}`,
                borderRadius: 2,
                backgroundColor: tokens.cardBg,
                overflow: 'hidden',
              }}
            >
              {recentBranches.map((branch) => (
                <RecentBranchRow
                  key={branch.branchId}
                  branch={branch}
                  to={`/w/${slug}/projects/${branch.projectId}/branches/${branch.branchId}`}
                />
              ))}
            </Stack>
          )}
        </Stack>
      </Box>
    </Box>
  )
}

// ── Section heading primitive ─────────────────────────────────────────────

function SectionHeading({ children }: { children: React.ReactNode }) {
  return (
    <Typography
      component="h2"
      sx={{
        fontSize: '1.125rem',
        fontWeight: 600,
        letterSpacing: '-0.01em',
        color: tokens.textPrimary,
        lineHeight: 1.3,
      }}
    >
      {children}
    </Typography>
  )
}

// ── Connect-GitHub empty state card ───────────────────────────────────────

interface ConnectGitHubCardProps {
  slug: string
}

/**
 * Calm "Connect your GitHub" card. Surfaced ONLY when the workspace has zero
 * GitHub installations — without one the user literally cannot create a
 * project, so we want this to be impossible to miss without being noisy.
 *
 * <p>Deep-links to the workspace settings page; the Integrations section
 * lives there. We don't pre-select the Integrations tab via URL hash today
 * (the settings page renders all four sections stacked vertically), so the
 * user will scroll to find it — a future polish pass can add a {@code
 * #integrations} anchor and {@code scrollIntoView} on mount.</p>
 */
function ConnectGitHubCard({ slug }: ConnectGitHubCardProps) {
  return (
    <Box
      sx={{
        border: `1px solid ${tokens.accent}`,
        backgroundColor: tokens.cardBg,
        borderRadius: 2,
        p: { xs: 2.5, md: 3 },
        display: 'flex',
        flexDirection: { xs: 'column', sm: 'row' },
        alignItems: { xs: 'flex-start', sm: 'center' },
        justifyContent: 'space-between',
        gap: 2,
      }}
    >
      <Stack
        direction="row"
        spacing={2}
        alignItems="flex-start"
        sx={{ minWidth: 0 }}
      >
        <Box
          sx={{
            flexShrink: 0,
            width: 36,
            height: 36,
            borderRadius: '50%',
            backgroundColor: tokens.accentSoft,
            display: 'flex',
            alignItems: 'center',
            justifyContent: 'center',
            color: tokens.accent,
          }}
        >
          <GitHubIcon sx={{ fontSize: 20 }} />
        </Box>
        <Stack spacing={0.5} sx={{ minWidth: 0 }}>
          <Typography
            sx={{
              fontSize: '1rem',
              fontWeight: 600,
              color: tokens.textPrimary,
              letterSpacing: '-0.005em',
            }}
          >
            Connect your GitHub to get started
          </Typography>
          <Typography
            sx={{
              fontSize: '0.8125rem',
              color: tokens.textMuted,
              letterSpacing: '-0.005em',
              lineHeight: 1.55,
            }}
          >
            Projects pull from a GitHub repository — install the GlennCode
            GitHub App on the org or account you want to work with.
          </Typography>
        </Stack>
      </Stack>
      <Button
        component={RouterLink}
        to={`/w/${slug}/settings`}
        variant="pill" color="primary"
        endIcon={<ArrowForwardIcon sx={{ fontSize: 16 }} />}
        sx={{
          flexShrink: 0,
          bgcolor: tokens.accent,
          color: '#fff',
          textTransform: 'none',
          fontWeight: 500,
          fontSize: '0.8125rem',
          borderRadius: 999,
          px: 2,
          py: 0.75,
          boxShadow: 'none',
          '&:hover': {
            bgcolor: workspaceAccent.hover,
            boxShadow: 'none',
          },
        }}
      >
        Connect GitHub
      </Button>
    </Box>
  )
}

// ── Recent branch row ─────────────────────────────────────────────────────

interface RecentBranchRowProps {
  branch: RecentBranchDto
  /** URL to navigate to when the row is activated — points at the exact
   *  branch route (no project-default-branch redirect dance). Passed in as
   *  a string so the row can be a real anchor and honour {@code ⌘+click} /
   *  middle-click for "open in new tab". */
  to: string
}

function RecentBranchRow({ branch, to }: RecentBranchRowProps) {
  // Best-effort relative timestamp. `lastActivityAt` may be null for a
  // freshly-created branch with no turns yet — we still want the row to
  // appear (so users see the branch exists), labelled honestly.
  const activity = useMemo(() => {
    const iso = branch.lastActivityAt
    if (!iso) return null
    try {
      return formatDistanceToNow(parseISO(iso), { addSuffix: true })
    } catch {
      return null
    }
  }, [branch.lastActivityAt])

  const isRunning = branch.runningTurnCount > 0
  const hasRepo = !!branch.githubRepoOwner && !!branch.githubRepoName

  // Rendered as a real {@code <a>} via {@code RouterLink} so the browser's
  // native multi-tab modifiers (⌘+click, middle-click, right-click → open
  // in new tab) work without any custom handling.
  return (
    <Box
      component={RouterLink}
      to={to}
      sx={{
        position: 'relative',
        cursor: 'pointer',
        display: 'flex',
        alignItems: 'center',
        gap: 2,
        px: { xs: 2.5, md: 3 },
        py: 2,
        textDecoration: 'none',
        color: 'inherit',
        transition: 'background-color 150ms ease',
        '&:hover': {
          backgroundColor: tokens.rowHover,
        },
        '&:hover .recent-row-title': {
          color: tokens.accent,
        },
        '&:focus-visible': {
          outline: `2px solid ${tokens.accent}`,
          outlineOffset: -2,
        },
      }}
    >
      <Box sx={{ flex: 1, minWidth: 0 }}>
        <Stack
          direction="row"
          spacing={1}
          alignItems="center"
          sx={{ minWidth: 0 }}
        >
          {/* Running-pulse dot — surfaces ONLY when a turn is in flight on
              this branch right now. Most rows won't render this; the empty
              slot is intentional so the list reads quiet by default. We
              reuse {@code StatusDot} with {@code Booting} for the same
              amber heartbeat the sidebar uses on running branches. */}
          {isRunning && (
            <Box sx={{ flexShrink: 0, display: 'inline-flex', alignItems: 'center' }}>
              <StatusDot state={RuntimeState.Booting} size={6} />
            </Box>
          )}
          <Typography
            className="recent-row-title"
            sx={{
              fontSize: '0.9375rem',
              color: tokens.textPrimary,
              letterSpacing: '-0.005em',
              transition: 'color 150ms ease',
              whiteSpace: 'nowrap',
              overflow: 'hidden',
              textOverflow: 'ellipsis',
              minWidth: 0,
            }}
            title={`${branch.projectName} · ${branch.branchName}`}
          >
            {/* Project name normal weight (context), then middot, then the
                branch name in 500 — the branch is what the user is actually
                clicking back into, so it gets the visual weight. */}
            <Box component="span" sx={{ fontWeight: 400 }}>
              {branch.projectName}
            </Box>
            <Box
              component="span"
              aria-hidden
              sx={{ mx: 0.75, color: tokens.textFaint }}
            >
              ·
            </Box>
            <Box
              component="span"
              sx={{ fontWeight: 500, fontFamily: workspaceFontFamily.mono, fontSize: '0.875rem' }}
            >
              {branch.branchName}
            </Box>
          </Typography>
          {branch.isDefault && (
            <Typography
              component="span"
              sx={{
                flexShrink: 0,
                fontSize: '0.6875rem',
                color: tokens.textFaint,
                letterSpacing: '0.04em',
                textTransform: 'uppercase',
                fontWeight: 500,
              }}
            >
              default
            </Typography>
          )}
        </Stack>
        <Typography
          sx={{
            mt: 0.25,
            fontFamily: workspaceFontFamily.mono,
            fontSize: '0.75rem',
            color: tokens.textMuted,
            whiteSpace: 'nowrap',
            overflow: 'hidden',
            textOverflow: 'ellipsis',
          }}
          title={
            hasRepo
              ? `${branch.githubRepoOwner}/${branch.githubRepoName}`
              : undefined
          }
        >
          {hasRepo
            ? `${branch.githubRepoOwner}/${branch.githubRepoName}`
            : '—'}
        </Typography>
      </Box>
      <Typography
        sx={{
          flexShrink: 0,
          fontSize: '0.75rem',
          color: tokens.textFaint,
          letterSpacing: '-0.005em',
        }}
      >
        {activity ?? 'no activity yet'}
      </Typography>
    </Box>
  )
}

function RecentWorkSkeleton() {
  return (
    <Stack
      divider={
        <Box
          aria-hidden
          sx={{ height: '1px', backgroundColor: tokens.hairline }}
        />
      }
      sx={{
        border: `1px solid ${tokens.hairline}`,
        borderRadius: 2,
        backgroundColor: tokens.cardBg,
        overflow: 'hidden',
      }}
    >
      {[0, 1, 2].map((i) => (
        <Box
          key={i}
          sx={{
            px: { xs: 2.5, md: 3 },
            py: 2,
            display: 'flex',
            alignItems: 'center',
            gap: 2,
          }}
        >
          <Box sx={{ flex: 1, minWidth: 0 }}>
            <Skeleton variant="text" width="35%" height={20} />
            <Skeleton variant="text" width="55%" height={14} sx={{ mt: 0.5 }} />
          </Box>
          <Skeleton variant="text" width={80} height={14} />
        </Box>
      ))}
    </Stack>
  )
}

// ── Empty "Recent work" state ─────────────────────────────────────────────

interface EmptyRecentWorkProps {
  slug: string
  /**
   * When false (no GitHub installation), the empty state nudges the user to
   * connect GitHub via the empty-state card above rather than offering a
   * "New project" CTA that would dead-end at the installation picker.
   */
  canStartProject: boolean
  /**
   * When true, the workspace has projects but no branch activity yet — copy
   * shifts from "start your first project" to "start your first session" so
   * the next step the user takes is correct.
   */
  hasAnyProjects: boolean
}

function EmptyRecentWork({
  slug,
  canStartProject,
  hasAnyProjects,
}: EmptyRecentWorkProps) {
  return (
    <Box
      sx={{
        border: `1px dashed ${tokens.hairline}`,
        borderRadius: 2,
        px: { xs: 2.5, md: 3 },
        py: { xs: 3, md: 4 },
        display: 'flex',
        flexDirection: { xs: 'column', sm: 'row' },
        alignItems: { xs: 'flex-start', sm: 'center' },
        justifyContent: 'space-between',
        gap: 2,
      }}
    >
      <Stack
        direction="row"
        spacing={2}
        alignItems="center"
        sx={{ minWidth: 0 }}
      >
        <Box
          sx={{
            flexShrink: 0,
            width: 32,
            height: 32,
            borderRadius: '50%',
            backgroundColor: 'rgba(0, 0, 0, 0.04)',
            display: 'flex',
            alignItems: 'center',
            justifyContent: 'center',
            color: tokens.textFaint,
          }}
        >
          <RocketLaunchIcon sx={{ fontSize: 16 }} />
        </Box>
        <Stack spacing={0.25}>
          <Typography
            sx={{
              fontSize: '0.9375rem',
              fontWeight: 500,
              color: tokens.textPrimary,
              letterSpacing: '-0.005em',
            }}
          >
            No recent work yet
          </Typography>
          <Typography
            sx={{
              fontSize: '0.8125rem',
              color: tokens.textMuted,
              letterSpacing: '-0.005em',
              lineHeight: 1.55,
            }}
          >
            {!canStartProject
              ? 'Connect GitHub above, then start your first project.'
              : hasAnyProjects
                ? 'Start a session on one of your projects to see it here.'
                : 'Start your first project to see it here.'}
          </Typography>
        </Stack>
      </Stack>
      {canStartProject && (
        <Button
          component={RouterLink}
          to={
            hasAnyProjects
              ? `/w/${slug}/new-session`
              : `/w/${slug}/projects/new`
          }
          variant="outlined"
          size="small"
          startIcon={<AddIcon sx={{ fontSize: 14 }} />}
          sx={{
            flexShrink: 0,
            textTransform: 'none',
            fontWeight: 500,
            fontSize: '0.8125rem',
            color: tokens.textPrimary,
            borderColor: tokens.hairline,
            borderRadius: 999,
            px: 2,
            py: 0.5,
            '&:hover': {
              borderColor: tokens.accent,
              color: tokens.accent,
              backgroundColor: 'transparent',
            },
          }}
        >
          {hasAnyProjects ? 'New session' : 'New project'}
        </Button>
      )}
    </Box>
  )
}
