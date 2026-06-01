import { useEffect, useMemo, useState, type KeyboardEvent } from 'react'
import { useNavigate, useSearchParams } from 'react-router-dom'
import {
  Alert,
  Box,
  Button,
  IconButton,
  InputAdornment,
  Link,
  Skeleton,
  Stack,
  TextField,
  Tooltip,
  Typography,
} from '@mui/material'
import SearchIcon from '@mui/icons-material/Search'
import AddIcon from '@mui/icons-material/Add'
import FolderOpenIcon from '@mui/icons-material/FolderOpen'
import SettingsIcon from '@mui/icons-material/Settings'
import { formatDistanceToNow, parseISO } from 'date-fns'
import {
  useGetApiWorkspacesSlugProjects,
  type ProjectSummaryDto,
} from '../../../../../api/queries-commands'
import { useNotification } from '../../../../shared/contexts/NotificationContext'
import { useWorkspace } from '../../../../shared/contexts/WorkspaceContext'
import {
  DetachedGithubPill,
  EmptyState,
  ReconnectProjectsDialog,
  WorkspacePageHeader,
  WorkspacePageShell,
  WorkspaceSection,
  bodySx,
  captionSx,
  workspaceAccent,
  workspaceColors,
  workspaceFontFamily,
  workspaceText,
} from '../../../shared'
import { WorkspaceSettingsDrawer, type WorkspaceSettingsTab } from '../../workspace-settings'

function formatRelative(value: string | null | undefined): string {
  if (!value) return 'No activity yet'
  try {
    return formatDistanceToNow(parseISO(value), { addSuffix: true })
  } catch {
    return 'Unknown'
  }
}

function compareLatestActivity(a: ProjectSummaryDto, b: ProjectSummaryDto): number {
  const aValue = a.latestActivityAt ?? a.updatedAt
  const bValue = b.latestActivityAt ?? b.updatedAt
  // Newest first
  return bValue.localeCompare(aValue)
}

export function ProjectsPage() {
  const { currentSlug } = useWorkspace()
  const slug = currentSlug ?? ''
  const navigate = useNavigate()
  const [searchParams, setSearchParams] = useSearchParams()
  const { showSuccess, showError, showInfo } = useNotification()

  // Surface the GitHub install callback's `?install=success|pending|cancelled|error`
  // flag exactly once, then strip it from the URL so a refresh doesn't re-trigger
  // the snackbar. The backend's GithubInstallCallbackController bounces here after
  // the GitHub round-trip (it used to bounce to /integrations, but that route is
  // gone — settings drawer absorbed it).
  useEffect(() => {
    const installResult = searchParams.get('install')
    if (!installResult) return
    if (installResult === 'success') {
      showSuccess('GitHub connected.')
    } else if (installResult === 'pending') {
      showInfo('GitHub installation is pending admin approval.')
    } else if (installResult === 'cancelled') {
      showError('GitHub installation was cancelled.')
    } else if (installResult === 'error') {
      showError('GitHub installation failed. Please try again.')
    }
    const next = new URLSearchParams(searchParams)
    next.delete('install')
    setSearchParams(next, { replace: true })
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  // Surface the standalone GitHub user-OAuth callback's
  // `?reauth=success|error` flag exactly once, then strip it from the URL.
  // The backend's GithubUserAuthCallbackController bounces here after the
  // slim OAuth-only round-trip used to refresh an expired (or never-captured)
  // User Access Token. Lives next to the `?install=` handler above because
  // they share the same surface and the same strip-after-show idiom.
  useEffect(() => {
    const reauthResult = searchParams.get('reauth')
    if (!reauthResult) return
    if (reauthResult === 'success') {
      showSuccess('GitHub re-authorized.')
    } else if (reauthResult === 'error') {
      showError('Re-authorization failed. Please try again.')
    }
    const next = new URLSearchParams(searchParams)
    next.delete('reauth')
    setSearchParams(next, { replace: true })
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  const [search, setSearch] = useState('')
  const [settingsOpen, setSettingsOpen] = useState(false)
  const [settingsInitialTab, setSettingsInitialTab] =
    useState<WorkspaceSettingsTab>('general')
  // Detached-project reconnect dialog. We keep the preset project id around so
  // clicking a detached pill opens the dialog preset to JUST that project; the
  // user can still uncheck other matching projects inside the dialog if they
  // want to act selectively.
  const [reconnectPresetProjectIds, setReconnectPresetProjectIds] =
    useState<string[] | undefined>(undefined)
  const [reconnectOpen, setReconnectOpen] = useState(false)

  const openReconnectForProject = (projectId: string) => {
    setReconnectPresetProjectIds([projectId])
    setReconnectOpen(true)
  }

  const projectsQuery = useGetApiWorkspacesSlugProjects(slug, {
    query: { enabled: !!slug },
  })

  const projects: ProjectSummaryDto[] = useMemo(
    () => projectsQuery.data ?? [],
    [projectsQuery.data],
  )

  const filteredProjects = useMemo(() => {
    const q = search.trim().toLowerCase()
    const base = q
      ? projects.filter((p) => {
          const repo = `${p.githubRepoOwner}/${p.githubRepoName}`.toLowerCase()
          return p.name.toLowerCase().includes(q) || repo.includes(q)
        })
      : projects
    return [...base].sort(compareLatestActivity)
  }, [projects, search])

  const isLoading = projectsQuery.isLoading
  const isError = projectsQuery.isError
  const hasProjects = projects.length > 0

  const handleOpenProject = (project: ProjectSummaryDto) => {
    navigate(`/w/${slug}/projects/${project.id}`)
  }

  const handleCreate = () => {
    navigate(`/w/${slug}/projects/new`)
  }

  const openSettings = (tab: WorkspaceSettingsTab = 'general') => {
    setSettingsInitialTab(tab)
    setSettingsOpen(true)
  }

  if (!slug) {
    return (
      <WorkspacePageShell>
        <Alert severity="error">No workspace selected.</Alert>
      </WorkspacePageShell>
    )
  }

  const headerActions = (
    <>
      <Button
        variant="pill"
        color="primary"
        startIcon={<AddIcon sx={{ fontSize: 18 }} />}
        onClick={handleCreate}
        aria-label="Create a project"
      >
        New project
      </Button>
      <Tooltip title="Workspace settings">
        <span>
          <IconButton
            size="small"
            aria-label="Open workspace settings"
            onClick={() => openSettings('general')}
            sx={{
              color: workspaceText.muted,
              transition: 'color 200ms ease',
              '&:hover': {
                color: workspaceAccent.ink,
                backgroundColor: 'transparent',
              },
            }}
          >
            <SettingsIcon fontSize="small" />
          </IconButton>
        </span>
      </Tooltip>
    </>
  )

  return (
    <>
      <WorkspacePageShell fullHeight={!hasProjects && !isLoading && !isError}>
        <WorkspacePageHeader title="Projects" actions={headerActions} />

        {isError && <Alert severity="error">Could not load projects.</Alert>}

        {isLoading && !isError && <ProjectsLoadingSkeleton />}

        {!isLoading && !isError && !hasProjects && (
          <EmptyState
            fillHeight
            icon={<FolderOpenIcon sx={{ fontSize: 24 }} />}
            headline="Start your first project"
            body="A project connects a GitHub repository to your agents — start one to begin a session and collaborate on changes together."
            cta={{
              label: 'New project',
              onClick: handleCreate,
            }}
            secondaryHint={
              <>
                Need to connect GitHub first?{' '}
                <Link
                  component="button"
                  type="button"
                  underline="hover"
                  onClick={() => openSettings('integrations')}
                  sx={{
                    color: workspaceText.muted,
                    fontFamily: workspaceFontFamily.sans,
                    '&:hover': { color: workspaceAccent.ink },
                  }}
                >
                  Open Settings → Integrations
                </Link>
                .
              </>
            }
          />
        )}

        {!isLoading && !isError && hasProjects && (
          <>
            <TextField
              value={search}
              onChange={(e) => setSearch(e.target.value)}
              placeholder="Search by name or owner/repo"
              size="small"
              variant="outlined"
              InputProps={{
                startAdornment: (
                  <InputAdornment position="start">
                    <SearchIcon sx={{ fontSize: 18, color: workspaceText.faint }} />
                  </InputAdornment>
                ),
                sx: {
                  backgroundColor: workspaceColors.inputBg,
                  fontFamily: workspaceFontFamily.sans,
                  fontSize: '0.875rem',
                  color: workspaceText.primary,
                },
              }}
              inputProps={{ 'aria-label': 'Search projects' }}
              sx={{ maxWidth: 360 }}
            />

            {filteredProjects.length === 0 ? (
              <WorkspaceSection>
                <Typography sx={bodySx}>
                  No projects match &ldquo;{search}&rdquo;.
                </Typography>
              </WorkspaceSection>
            ) : (
              <WorkspaceSection flush>
                <Stack divider={<HairlineDivider />}>
                  {filteredProjects.map((project) => (
                    <ProjectRow
                      key={project.id}
                      project={project}
                      onOpen={() => handleOpenProject(project)}
                      onReconnect={() => openReconnectForProject(project.id)}
                    />
                  ))}
                </Stack>
              </WorkspaceSection>
            )}
          </>
        )}
      </WorkspacePageShell>

      <WorkspaceSettingsDrawer
        open={settingsOpen}
        onClose={() => setSettingsOpen(false)}
        initialTab={settingsInitialTab}
      />

      <ReconnectProjectsDialog
        open={reconnectOpen}
        onClose={() => setReconnectOpen(false)}
        workspaceSlug={slug}
        presetProjectIds={reconnectPresetProjectIds}
      />
    </>
  )
}

function HairlineDivider() {
  return (
    <Box
      sx={{
        height: '1px',
        backgroundColor: workspaceColors.hairline,
      }}
    />
  )
}

interface ProjectRowProps {
  project: ProjectSummaryDto
  onOpen: () => void
  onReconnect: () => void
}

function ProjectRow({ project, onOpen, onReconnect }: ProjectRowProps) {
  const repoLabel = `${project.githubRepoOwner}/${project.githubRepoName}`
  const activityIso = project.latestActivityAt ?? project.updatedAt
  const activityLabel = formatRelative(project.latestActivityAt)
  const branchLabel = `${project.branchCount} ${
    project.branchCount === 1 ? 'branch' : 'branches'
  }`
  const isDetached = project.githubInstallationId == null

  return (
    <Box
      component="div"
      role="button"
      tabIndex={0}
      onClick={onOpen}
      onKeyDown={(e: KeyboardEvent) => {
        if (e.key === 'Enter' || e.key === ' ') {
          e.preventDefault()
          onOpen()
        }
      }}
      aria-label={`Open project ${project.name}`}
      sx={{
        all: 'unset',
        display: 'flex',
        flexDirection: { xs: 'column', sm: 'row' },
        alignItems: { xs: 'flex-start', sm: 'center' },
        justifyContent: 'space-between',
        gap: { xs: 1, sm: 2 },
        px: { xs: 2.5, md: 3 },
        py: 2,
        cursor: 'pointer',
        transition: 'background-color 150ms ease, color 150ms ease',
        '&:hover': {
          backgroundColor: workspaceColors.chipBg,
        },
        '&:hover .project-row-title': {
          color: workspaceAccent.ink,
        },
        '&:focus-visible': {
          outline: `2px solid ${workspaceAccent.ink}`,
          outlineOffset: -2,
        },
      }}
    >
      <Box sx={{ minWidth: 0, flex: 1 }}>
        <Stack
          direction="row"
          alignItems="center"
          spacing={1}
          sx={{ minWidth: 0 }}
        >
          <Typography
            className="project-row-title"
            component="div"
            sx={{
              fontFamily: workspaceFontFamily.sans,
              fontSize: '1.0625rem',
              fontWeight: 500,
              letterSpacing: '-0.01em',
              color: workspaceText.primary,
              lineHeight: 1.3,
              overflow: 'hidden',
              textOverflow: 'ellipsis',
              whiteSpace: 'nowrap',
              transition: 'color 150ms ease',
              minWidth: 0,
            }}
            title={project.name}
          >
            {project.name}
          </Typography>
          {isDetached && (
            <DetachedGithubPill
              project={{
                id: project.id,
                name: project.name,
                githubRepoOwner: project.githubRepoOwner,
              }}
              onClick={onReconnect}
            />
          )}
        </Stack>
        <Typography
          component="div"
          sx={{
            mt: 0.5,
            fontFamily: workspaceFontFamily.mono,
            fontSize: '0.8125rem',
            color: workspaceText.muted,
            overflow: 'hidden',
            textOverflow: 'ellipsis',
            whiteSpace: 'nowrap',
          }}
          title={repoLabel}
        >
          {repoLabel}
        </Typography>
      </Box>
      <Stack
        direction="row"
        spacing={3}
        alignItems="center"
        sx={{ flexShrink: 0, color: workspaceText.muted }}
      >
        <Typography
          component="span"
          sx={{
            ...captionSx,
            fontFamily: workspaceFontFamily.mono,
            color: workspaceText.muted,
          }}
        >
          {branchLabel}
        </Typography>
        <Tooltip title={activityIso}>
          <Typography component="span" sx={captionSx}>
            {activityLabel}
          </Typography>
        </Tooltip>
      </Stack>
    </Box>
  )
}

function ProjectsLoadingSkeleton() {
  return (
    <WorkspaceSection flush>
      <Stack>
        {Array.from({ length: 4 }).map((_, idx) => (
          <Box
            key={idx}
            sx={{
              px: { xs: 2.5, md: 3 },
              py: 2,
              borderBottom:
                idx < 3 ? `1px solid ${workspaceColors.hairline}` : 'none',
              display: 'flex',
              alignItems: 'center',
              justifyContent: 'space-between',
              gap: 2,
            }}
          >
            <Box sx={{ flex: 1, minWidth: 0 }}>
              <Skeleton variant="text" width="40%" height={24} />
              <Skeleton variant="text" width="60%" height={18} sx={{ mt: 0.5 }} />
            </Box>
            <Stack direction="row" spacing={3} alignItems="center">
              <Skeleton variant="text" width={60} height={16} />
              <Skeleton variant="text" width={80} height={16} />
            </Stack>
          </Box>
        ))}
      </Stack>
    </WorkspaceSection>
  )
}
