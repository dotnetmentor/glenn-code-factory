import {
  useEffect,
  useMemo,
  useRef,
  useState,
  type ReactNode,
} from 'react'
import { Link as RouterLink, useNavigate, useParams } from 'react-router-dom'
import { useQueryClient } from '@tanstack/react-query'
import {
  Alert,
  Autocomplete,
  Box,
  Button,
  CircularProgress,
  FormControl,
  FormControlLabel,
  MenuItem,
  Radio,
  RadioGroup,
  Select,
  Skeleton,
  Stack,
  TextField,
  Typography,
} from '@mui/material'
import AddIcon from '@mui/icons-material/Add'
import ArrowBackIcon from '@mui/icons-material/ArrowBack'
import { formatDistanceToNow, parseISO } from 'date-fns'
import {
  RuntimeState,
  getGetApiProjectsProjectIdBranchesQueryKey,
  getGetApiWorkspacesSlugProjectsQueryKey,
  useGetApiGithubInstallationsInstallationIdRepos,
  useGetApiGithubInstallationsInstallationIdReposOwnerRepoBranches,
  useGetApiProjectsProjectIdBranches,
  useGetApiProjectsProjectIdGithubBranches,
  useGetApiProjectTemplates,
  useGetApiWorkspacesSlugGithubInstallations,
  useGetApiWorkspacesSlugProjects,
  useGetApiWorkspacesWorkspaceIdSpecs,
  usePostApiProjects,
  usePostApiProjectsProjectIdBranchesAttach,
  usePostApiProjectsProjectIdBranchesForkFromGit,
  type CreateProjectRequest,
  type GithubBranchListItemDto,
  type GithubInstallationListItem,
  type GithubRepoListItemDto,
  type ProblemDetails,
  type ProjectSummaryDto,
  type WorkspaceSpecListItem,
} from '../../../../../api/queries-commands'
import { useWorkspace } from '../../../../shared/contexts/WorkspaceContext'
import { useDocumentTitle } from '../../../../shared/hooks'
import { StatusDot } from '../../project-workspace/components/StatusDot'
import { BranchPicker } from '../../new-project/components/BranchPicker'
import { InstallationPicker } from '../../new-project/components/InstallationPicker'
import { RepoPicker } from '../../new-project/components/RepoPicker'
import {
  StarterPicker,
  type StartingPoint,
} from '../../new-project/components/StarterPicker'
import { parseGithubUrl } from '../../new-project/utils/parseGithubUrl'

import { buildGithubInstallationManageUrl, isPoolEmptyError, PoolEmptyErrorAlert } from '../../../shared'
import {
  chromeTokens,
  pageCardPaddedSx,
  semanticTokens,
  surfaceTokens,
  workspaceFontFamily,
} from '../../../shared/designTokens'

const tokens = { ...surfaceTokens, ...chromeTokens, ...semanticTokens }

// ── Auto-suggested name helper (mirrors CopyBranchDialog.suggestCopyName) ─

/**
 * Suggest a fork name for the given source branch. Tries
 * {@code <sourceName>-copy} first, then {@code -copy-2}, {@code -copy-3}, …
 * until one is not in {@code existingNames}.
 */
function suggestForkName(sourceName: string, existingNames: string[]): string {
  const set = new Set(existingNames)
  const base = `${sourceName}-copy`
  if (!set.has(base)) return base
  let n = 2
  while (n < 1000) {
    const candidate = `${base}-${n}`
    if (!set.has(candidate)) return candidate
    n += 1
  }
  return `${base}-${Date.now()}`
}

// ── Error mapping ─────────────────────────────────────────────────────────

interface AxiosLikeError {
  response?: {
    status?: number
    data?: ProblemDetails & { error?: string }
  }
  message?: string
}

function readErrorDetail(err: unknown): string | null {
  // Check `.error` FIRST so backend stable codes like
  // `github_repo_create_failed: GitHub refused…` surface verbatim instead of
  // axios's generic "Request failed with status code 400". Then fall back to
  // ASP.NET's ProblemDetails shape, then to the raw network error.
  const e = err as AxiosLikeError | null | undefined
  return (
    e?.response?.data?.error ??
    e?.response?.data?.detail ??
    e?.response?.data?.title ??
    e?.message ??
    null
  )
}

function isConflictError(err: unknown): boolean {
  const e = err as AxiosLikeError | null | undefined
  return e?.response?.status === 409
}

/**
 * Inspect an error from {@code usePostApiProjects} and, if it matches the
 * structured {@code RepositoryAlreadyLinkedConflict} 409 shape, extract the
 * existing project info so callers can render a dedicated "Open existing
 * project" affordance. Returns null for anything else (including plain 409s
 * that don't carry the `existingProjectId` field — those fall back to the
 * generic error path).
 */
function readDuplicateConflict(
  err: unknown,
): { existingProjectId: string; existingProjectName?: string } | null {
  const e = err as
    | {
        response?: {
          status?: number
          data?: {
            code?: string
            existingProjectId?: string
            existingProjectName?: string
          }
        }
      }
    | null
    | undefined
  if (e?.response?.status !== 409) return null
  const data = e.response?.data
  if (!data?.existingProjectId) return null
  if (data.code && data.code !== 'RepositoryAlreadyLinked') return null
  return {
    existingProjectId: data.existingProjectId,
    existingProjectName: data.existingProjectName,
  }
}

// ── Public surface ────────────────────────────────────────────────────────

export function NewSessionView() {
  const { slug = '' } = useParams<{ slug: string }>()
  const { currentWorkspace } = useWorkspace()

  // "New session · {workspace} · GlennCode" — keeps the in-flight new-session
  // tab distinct from the landing tab when the user has both open.
  useDocumentTitle(
    currentWorkspace?.name
      ? `New session · ${currentWorkspace.name} · GlennCode`
      : slug
        ? `New session · ${slug} · GlennCode`
        : null,
  )

  const projectsQuery = useGetApiWorkspacesSlugProjects(slug, {
    query: { enabled: !!slug, refetchInterval: 15_000 },
  })

  const [selectedProjectId, setSelectedProjectId] = useState<string | null>(
    null,
  )
  const [selectedGitBranch, setSelectedGitBranch] =
    useState<GithubBranchListItemDto | null>(null)
  const [inlineCreateOpen, setInlineCreateOpen] = useState(false)

  const step2Ref = useRef<HTMLDivElement | null>(null)
  const step3Ref = useRef<HTMLDivElement | null>(null)

  // Smooth-scroll step 2 into view whenever the project changes.
  useEffect(() => {
    if (!selectedProjectId) return
    step2Ref.current?.scrollIntoView({ behavior: 'smooth', block: 'start' })
  }, [selectedProjectId])

  useEffect(() => {
    if (!selectedGitBranch) return
    step3Ref.current?.scrollIntoView({ behavior: 'smooth', block: 'start' })
  }, [selectedGitBranch])

  const handleSelectProject = (project: ProjectSummaryDto) => {
    // Same-click is a no-op (avoid re-resetting branch state on accidental
    // double-click).
    if (selectedProjectId === project.id) return
    setSelectedProjectId(project.id)
    setSelectedGitBranch(null)
    setInlineCreateOpen(false)
  }

  const handleSwitchProject = () => {
    setSelectedProjectId(null)
    setSelectedGitBranch(null)
  }

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
        {/* ── Header ──────────────────────────────────────────────────── */}
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
            Start a new session
          </Typography>
          <Typography
            sx={{
              fontSize: '0.875rem',
              color: tokens.textMuted,
              letterSpacing: '-0.005em',
              lineHeight: 1.5,
            }}
          >
            Pick a project, then choose how to start.
          </Typography>
          {currentWorkspace && (
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
              {currentWorkspace.name}
            </Typography>
          )}
        </Stack>

        {/* ── Step 1: Pick project ────────────────────────────────────── */}
        <Stack spacing={2}>
          <StepHeading>Pick a project</StepHeading>

          {projectsQuery.isLoading ? (
            <ProjectGridSkeleton />
          ) : projectsQuery.isError ? (
            <Alert
              severity="error"
              variant="quiet"
            >
              {readErrorDetail(projectsQuery.error) ??
                'Could not load projects.'}
            </Alert>
          ) : (
            <ProjectGrid
              projects={projectsQuery.data ?? []}
              selectedProjectId={selectedProjectId}
              onSelect={handleSelectProject}
              inlineCreateOpen={inlineCreateOpen}
              onOpenInlineCreate={() => {
                setInlineCreateOpen(true)
                setSelectedProjectId(null)
                setSelectedGitBranch(null)
              }}
              onCloseInlineCreate={() => setInlineCreateOpen(false)}
              slug={slug}
            />
          )}
        </Stack>

        {/* ── Step 2: Pick a git branch ───────────────────────────────── */}
        {selectedProjectId && (
          <Box ref={step2Ref} sx={{ mt: 6 }}>
            <Step2GitBranches
              projectId={selectedProjectId}
              projectName={
                projectsQuery.data?.find((p) => p.id === selectedProjectId)
                  ?.name ?? 'project'
              }
              onSwitchProject={handleSwitchProject}
              selectedGitBranchName={selectedGitBranch?.name ?? null}
              onSelect={setSelectedGitBranch}
            />
          </Box>
        )}

        {/* ── Step 3: Choose action ───────────────────────────────────── */}
        {selectedProjectId && selectedGitBranch && (
          <Box ref={step3Ref} sx={{ mt: 6, pb: 8 }}>
            <Step3Action
              slug={slug}
              projectId={selectedProjectId}
              sourceBranch={selectedGitBranch}
              onReset={() => {
                setSelectedGitBranch(null)
              }}
            />
          </Box>
        )}
      </Box>
    </Box>
  )
}

// ── Step heading primitive ────────────────────────────────────────────────

function StepHeading({ children }: { children: ReactNode }) {
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

// ── Step 1: Project grid ──────────────────────────────────────────────────

interface ProjectGridProps {
  projects: ProjectSummaryDto[]
  selectedProjectId: string | null
  onSelect: (project: ProjectSummaryDto) => void
  inlineCreateOpen: boolean
  onOpenInlineCreate: () => void
  onCloseInlineCreate: () => void
  slug: string
}

function ProjectGrid({
  projects,
  selectedProjectId,
  onSelect,
  inlineCreateOpen,
  onOpenInlineCreate,
  onCloseInlineCreate,
  slug,
}: ProjectGridProps) {
  // Filter out deleting/deleted — these would surface a project the user
  // can't open. Mirrors the sidebar's filter.
  const visibleProjects = useMemo(
    () =>
      projects.filter(
        (p) =>
          p.runtimeState !== RuntimeState.Deleting &&
          p.runtimeState !== RuntimeState.Deleted,
      ),
    [projects],
  )

  // When inline-create is open, it occupies the full row by spanning every
  // grid column. We keep the existing project cards rendered above, just
  // hide nothing — the create card "expands" by replacing its own slot with
  // a wide span.
  return (
    <Box
      sx={{
        display: 'grid',
        gridTemplateColumns: {
          xs: '1fr',
          sm: 'repeat(2, 1fr)',
          md: 'repeat(3, 1fr)',
        },
        gap: 2,
      }}
    >
      {visibleProjects.map((project) => (
        <ProjectCard
          key={project.id}
          project={project}
          selected={project.id === selectedProjectId}
          onClick={() => onSelect(project)}
        />
      ))}

      {inlineCreateOpen ? (
        <Box
          sx={{
            gridColumn: '1 / -1',
            border: `1px solid ${tokens.accent}`,
            borderRadius: 2,
            backgroundColor: tokens.cardBg,
            p: { xs: 2, md: 3 },
          }}
        >
          <InlineNewProject
            slug={slug}
            onClose={onCloseInlineCreate}
          />
        </Box>
      ) : (
        <NewProjectCard onClick={onOpenInlineCreate} />
      )}
    </Box>
  )
}

interface ProjectCardProps {
  project: ProjectSummaryDto
  selected: boolean
  onClick: () => void
}

function ProjectCard({ project, selected, onClick }: ProjectCardProps) {
  const activity = useMemo(() => {
    if (!project.latestActivityAt) return null
    try {
      return formatDistanceToNow(parseISO(project.latestActivityAt), {
        addSuffix: true,
      })
    } catch {
      return null
    }
  }, [project.latestActivityAt])

  return (
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
        position: 'relative',
        cursor: 'pointer',
        borderRadius: 2,
        border: `1px solid ${selected ? tokens.accent : tokens.hairline}`,
        backgroundColor: selected ? tokens.accentSoft : tokens.cardBg,
        p: 2,
        transition:
          'border-color 160ms ease, background-color 160ms ease',
        '&:hover': {
          borderColor: selected ? tokens.accent : 'rgba(0, 0, 0, 0.12)',
          backgroundColor: selected ? tokens.accentSoft : tokens.rowHover,
        },
        '&:focus-visible': {
          outline: `2px solid ${tokens.accent}`,
          outlineOffset: 2,
        },
      }}
    >
      <Stack spacing={0.75}>
        <Stack
          direction="row"
          alignItems="center"
          justifyContent="space-between"
          spacing={1}
        >
          <Typography
            sx={{
              fontSize: '1rem',
              fontWeight: 600,
              letterSpacing: '-0.005em',
              color: tokens.textPrimary,
              whiteSpace: 'nowrap',
              overflow: 'hidden',
              textOverflow: 'ellipsis',
              minWidth: 0,
              flex: 1,
            }}
            title={project.name}
          >
            {project.name}
          </Typography>
          <Box sx={{ flexShrink: 0 }}>
            <StatusDot state={project.runtimeState} />
          </Box>
        </Stack>
        <Typography
          sx={{
            fontFamily: workspaceFontFamily.mono,
            fontSize: '0.75rem',
            color: tokens.textMuted,
            whiteSpace: 'nowrap',
            overflow: 'hidden',
            textOverflow: 'ellipsis',
          }}
          title={`${project.githubRepoOwner} / ${project.githubRepoName}`}
        >
          {project.githubRepoOwner} / {project.githubRepoName}
        </Typography>
        {activity && (
          <Typography
            sx={{
              fontSize: '0.75rem',
              color: tokens.textFaint,
              letterSpacing: '-0.005em',
            }}
          >
            {activity}
          </Typography>
        )}
      </Stack>
    </Box>
  )
}

function NewProjectCard({ onClick }: { onClick: () => void }) {
  return (
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
        cursor: 'pointer',
        borderRadius: 2,
        border: `1px dashed ${tokens.hairline}`,
        backgroundColor: 'transparent',
        p: 2,
        minHeight: 92,
        display: 'flex',
        alignItems: 'center',
        justifyContent: 'center',
        color: tokens.textMuted,
        transition: 'border-color 160ms ease, color 160ms ease, background-color 160ms ease',
        '&:hover': {
          borderColor: tokens.accent,
          color: tokens.accent,
          backgroundColor: tokens.accentSoft,
        },
        '&:focus-visible': {
          outline: `2px solid ${tokens.accent}`,
          outlineOffset: 2,
        },
      }}
    >
      <Stack direction="row" spacing={0.75} alignItems="center">
        <AddIcon sx={{ fontSize: 18 }} />
        <Typography
          sx={{
            fontSize: '0.875rem',
            fontWeight: 500,
            letterSpacing: '-0.005em',
          }}
        >
          New project
        </Typography>
      </Stack>
    </Box>
  )
}

function ProjectGridSkeleton() {
  return (
    <Box
      sx={{
        display: 'grid',
        gridTemplateColumns: {
          xs: '1fr',
          sm: 'repeat(2, 1fr)',
          md: 'repeat(3, 1fr)',
        },
        gap: 2,
      }}
    >
      {[0, 1, 2, 3, 4, 5].map((i) => (
        <Box
          key={i}
          sx={{
            borderRadius: 2,
            border: `1px solid ${tokens.hairline}`,
            backgroundColor: tokens.cardBg,
            p: 2,
            opacity: 0.5 - i * 0.05,
          }}
        >
          <Stack spacing={1}>
            <Skeleton variant="text" width="60%" height={24} />
            <Skeleton variant="text" width="80%" height={18} />
            <Skeleton variant="text" width="40%" height={14} />
          </Stack>
        </Box>
      ))}
    </Box>
  )
}

// ── Inline new-project mini-wizard ────────────────────────────────────────

type InlineWizardStep = 'installation' | 'repo' | 'branch'

/**
 * The inline new-project pane has two entry points before the user picks a
 * concrete path:
 *  - {@code 'starter'} routes into {@link StarterPicker} and the curated
 *    starter / BYO-URL / empty sub-flows ported from {@code NewProjectPage}.
 *  - {@code 'repo'} routes into the legacy Installation → Repo → Branch
 *    wizard for connecting an existing repository.
 * {@code 'choose'} is the initial state — a two-card chooser.
 */
type InlineEntryMode = 'choose' | 'starter' | 'repo'

interface InlineNewProjectProps {
  slug: string
  onClose: () => void
}

function InlineNewProject({ slug, onClose }: InlineNewProjectProps) {
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const { currentWorkspace } = useWorkspace()

  // Entry chooser drives which sub-flow the rest of the pane renders.
  const [mode, setMode] = useState<InlineEntryMode>('choose')

  // Starter sub-flow: which "starting point" tile is selected (or null while
  // the user is still on the StarterPicker grid). Curated starters carry a
  // template; the pinned tiles carry just their `kind` discriminator.
  const [startingPoint, setStartingPoint] = useState<StartingPoint | null>(
    null,
  )
  const [starterRepoName, setStarterRepoName] = useState('')
  const [githubUrlInput, setGithubUrlInput] = useState('')

  const templatesQuery = useGetApiProjectTemplates({
    query: { staleTime: 60_000, enabled: mode === 'starter' },
  })

  // Pre-compute the parsed BYO URL so both validation hints and the submit
  // payload share one source of truth.
  const parsedGithubUrl = useMemo(
    () =>
      githubUrlInput.trim().length > 0 ? parseGithubUrl(githubUrlInput) : null,
    [githubUrlInput],
  )

  const installationsQuery = useGetApiWorkspacesSlugGithubInstallations(slug, {
    query: { enabled: !!slug },
  })
  const installations: GithubInstallationListItem[] = useMemo(
    () => installationsQuery.data ?? [],
    [installationsQuery.data],
  )

  const [installationId, setInstallationId] = useState<string | null>(null)
  const [step, setStep] = useState<InlineWizardStep>('installation')
  const [selectedRepo, setSelectedRepo] = useState<GithubRepoListItemDto | null>(
    null,
  )
  const [selectedBranch, setSelectedBranch] = useState<string | null>(null)
  // Starting-spec picker: 'blank' (default) creates a new project with no
  // services in its first runtime's spec; 'catalog' requires the user to pick
  // one of the workspace's saved catalog specs, which is then copied into
  // the new runtime's `Spec` at fork time. The picker mirrors Scene 3 of the
  // workspace-spec-catalog spec — there is no "Same as source branch" option
  // here because there is no source branch for a brand-new project.
  const [startingSpecMode, setStartingSpecMode] = useState<'blank' | 'catalog'>(
    'blank',
  )
  const [selectedCatalogSpecId, setSelectedCatalogSpecId] = useState<
    string | null
  >(null)
  const [submitError, setSubmitError] = useState<string | null>(null)
  /**
   * When `usePostApiProjects` returns 409 with `RepositoryAlreadyLinkedConflict`,
   * we capture the existing project info here so we can render an inline
   * rust-toned block with an "Open it" button instead of the generic error
   * string.
   */
  const [duplicateConflict, setDuplicateConflict] = useState<{
    existingProjectId: string
    existingProjectName?: string
  } | null>(null)

  // Auto-pick the only installation and skip to repo step. Only kicks in
  // once the user has actually entered the repo sub-flow — otherwise we'd
  // race ahead before they've chosen between "starter" and "repository".
  useEffect(() => {
    if (mode !== 'repo') return
    if (installationId) return
    if (installations.length === 1) {
      setInstallationId(installations[0].id)
      setStep('repo')
    } else if (installations.length > 1) {
      setStep('installation')
    }
  }, [installations, installationId, mode])

  // The starter / BYO-URL sub-flows ALSO need an installation, but they pick
  // it inline (in their own InstallationPicker block) — they don't gate on
  // `step`. We still want a single one to auto-select to skip the picker.
  useEffect(() => {
    if (mode !== 'starter') return
    if (installationId) return
    if (installations.length === 1) {
      setInstallationId(installations[0].id)
    }
  }, [installations, installationId, mode])

  const reposQuery = useGetApiGithubInstallationsInstallationIdRepos(
    installationId ?? '',
    // Pass the current workspace so the backend can populate
    // `linkedProjectId` / `linkedProjectName` on repos that already have a
    // project in this workspace — we use that to gray out duplicates.
    { workspaceId: currentWorkspace?.id },
    {
      query: { enabled: !!installationId && !!currentWorkspace },
    },
  )
  const repos: GithubRepoListItemDto[] = useMemo(
    () => reposQuery.data ?? [],
    [reposQuery.data],
  )

  // The selected installation drives the "Manage GitHub access" deep-link in
  // the repo picker. We compute it once so both the picker and any future
  // tweaks share the same source of truth.
  const selectedInstallation = useMemo<
    GithubInstallationListItem | null
  >(() => {
    if (!installationId) return null
    return installations.find((i) => i.id === installationId) ?? null
  }, [installationId, installations])

  const manageAccessUrl = useMemo(
    () => buildGithubInstallationManageUrl(selectedInstallation),
    [selectedInstallation],
  )

  const branchesQuery =
    useGetApiGithubInstallationsInstallationIdReposOwnerRepoBranches(
      installationId ?? '',
      selectedRepo?.owner ?? '',
      selectedRepo?.name ?? '',
      {
        query: { enabled: !!installationId && !!selectedRepo },
      },
    )
  const branches = useMemo(
    () => branchesQuery.data ?? [],
    [branchesQuery.data],
  )

  // Workspace catalog specs powering the "Pick from catalog…" option of the
  // starting-spec picker. The list may be empty for brand-new workspaces
  // before the system seeders have run — we show an inline empty-state
  // message in that case rather than disabling the option entirely.
  const catalogSpecsQuery = useGetApiWorkspacesWorkspaceIdSpecs(
    currentWorkspace?.id ?? '',
    { query: { enabled: !!currentWorkspace } },
  )
  const catalogSpecs: WorkspaceSpecListItem[] = useMemo(
    () => catalogSpecsQuery.data ?? [],
    [catalogSpecsQuery.data],
  )

  useEffect(() => {
    if (selectedBranch) return
    if (branches.length === 0) return
    const def = branches.find((b) => b.isDefault)
    if (def) {
      setSelectedBranch(def.name)
      return
    }
    const fallback =
      branches.find((b) => b.name === 'main') ??
      branches.find((b) => b.name === 'master')
    if (fallback) setSelectedBranch(fallback.name)
  }, [branches, selectedBranch])

  const createProject = usePostApiProjects()
  const isPoolEmptySubmitError =
    createProject.isError && isPoolEmptyError(createProject.error)

  // Starter-flow project-name validation mirrors the legacy brand-new-repo
  // rules in NewProjectPage. 1–100 chars, alphanumerics/dot/dash/underscore.
  const starterRepoNameTrimmed = starterRepoName.trim()
  const starterRepoNameValid =
    starterRepoNameTrimmed.length >= 1 &&
    starterRepoNameTrimmed.length <= 100 &&
    /^[A-Za-z0-9._-]+$/.test(starterRepoNameTrimmed)

  const handleCreate = () => {
    if (!currentWorkspace || !installationId) return
    setSubmitError(null)
    setDuplicateConflict(null)

    // Branch by which sub-flow is active. The legacy repo path is the only
    // one that sends `catalogSpecId` — the starter / BYO-URL paths defer to
    // the template (or its absence) entirely.
    let payload: CreateProjectRequest
    if (mode === 'starter' && startingPoint?.kind === 'starter') {
      if (!starterRepoNameValid) return
      payload = {
        workspaceId: currentWorkspace.id,
        githubInstallationId: installationId,
        createNewRepo: true,
        newRepoName: starterRepoNameTrimmed,
        templateId: startingPoint.template.id,
      }
    } else if (mode === 'starter' && startingPoint?.kind === 'github-url') {
      if (!parsedGithubUrl) return
      payload = {
        workspaceId: currentWorkspace.id,
        githubInstallationId: installationId,
        createNewRepo: false,
        repoOwner: parsedGithubUrl.owner,
        repoName: parsedGithubUrl.name,
      }
    } else {
      // Repo sub-flow (existing wizard). Empty starter routes here too.
      if (!selectedRepo || !selectedBranch) return
      payload = {
        workspaceId: currentWorkspace.id,
        githubInstallationId: installationId,
        createNewRepo: false,
        repoOwner: selectedRepo.owner,
        repoName: selectedRepo.name,
        branchName: selectedBranch,
        // Starting-spec picker payload. `catalogSpecId` is null when the
        // user keeps the default "Blank — no services"; otherwise it
        // carries the workspace catalog spec they picked, which the
        // backend deep-copies into the new runtime's Spec at fork time.
        catalogSpecId:
          startingSpecMode === 'catalog' ? selectedCatalogSpecId : null,
      }
    }

    createProject.mutate(
      { data: payload },
      {
        onSuccess: (project) => {
          queryClient.invalidateQueries({
            queryKey: getGetApiWorkspacesSlugProjectsQueryKey(slug),
          })
          if (project?.id && project?.defaultBranchId) {
            navigate(
              `/w/${slug}/projects/${project.id}/branches/${project.defaultBranchId}`,
            )
          } else if (project?.id) {
            navigate(`/w/${slug}/projects/${project.id}`)
          }
        },
        onError: (err) => {
          // First, check whether this is the structured 409 from
          // `RepositoryAlreadyLinkedConflict` so we can render the dedicated
          // "Open existing project" affordance instead of a flat error string.
          const dup = readDuplicateConflict(err)
          if (dup) {
            setDuplicateConflict(dup)
            return
          }
          if (isPoolEmptyError(err)) {
            setSubmitError(null)
            return
          }
          setSubmitError(
            readErrorDetail(err) ?? 'Could not create the project.',
          )
        },
      },
    )
  }

  const canSubmit =
    !!currentWorkspace &&
    !!installationId &&
    !!selectedRepo &&
    !!selectedBranch &&
    // Submit stays disabled until a catalog spec is picked when the user
    // selected the "Pick from catalog…" option — otherwise we'd silently
    // ship a Blank create even though the radio said otherwise.
    (startingSpecMode === 'blank' || !!selectedCatalogSpecId) &&
    !createProject.isPending

  // Submit-gating for the curated-starter sub-flow.
  const canSubmitStarter =
    !!currentWorkspace &&
    !!installationId &&
    starterRepoNameValid &&
    !createProject.isPending

  // Submit-gating for the BYO-URL sub-flow.
  const canSubmitGithubUrl =
    !!currentWorkspace &&
    !!installationId &&
    !!parsedGithubUrl &&
    !createProject.isPending

  // "Choose entry point" back-link rendered above the active sub-flow. We
  // use it to return the user to the two-card chooser without blowing away
  // any state they've already entered (so they can swing back to a partially
  // filled form if they change their mind a second time).
  const handleBackToEntryChooser = () => {
    setMode('choose')
    setSubmitError(null)
    setDuplicateConflict(null)
  }

  return (
    <Stack spacing={2}>
      <Stack
        direction="row"
        alignItems="center"
        justifyContent="space-between"
        spacing={1}
      >
        <Typography
          sx={{
            fontSize: '0.9375rem',
            fontWeight: 600,
            color: tokens.textPrimary,
            letterSpacing: '-0.005em',
          }}
        >
          New project
        </Typography>
        <Button
          size="small"
          onClick={onClose}
          disabled={createProject.isPending}
          sx={{
            textTransform: 'none',
            color: tokens.textMuted,
            fontSize: '0.8125rem',
            '&:hover': { color: tokens.textPrimary, bgcolor: 'transparent' },
          }}
        >
          Cancel
        </Button>
      </Stack>

      {installationsQuery.isLoading ? (
        <Skeleton variant="rounded" height={56} />
      ) : installations.length === 0 ? (
        <Box sx={pageCardPaddedSx}>
          <Stack spacing={2}>
            <Typography
              sx={{
                fontSize: '0.9375rem',
                fontWeight: 600,
                color: tokens.textPrimary,
                letterSpacing: '-0.005em',
              }}
            >
              GitHub not connected
            </Typography>
            <Typography
              sx={{
                fontSize: '0.8125rem',
                color: tokens.textMuted,
                letterSpacing: '-0.005em',
                lineHeight: 1.55,
              }}
            >
              Connect GitHub in workspace settings before creating a project.
            </Typography>
            <Button
              variant="pill"
              color="primary"
              component={RouterLink}
              to={`/w/${slug}/settings`}
            >
              Open workspace settings
            </Button>
          </Stack>
        </Box>
      ) : mode === 'choose' ? (
        // Two-card entry chooser. The user picks how they want to start
        // (curated/BYO starter, or existing repository) BEFORE we drop them
        // into the relevant sub-wizard. This keeps the workspace shell +
        // sidebar visible while letting them switch entry points easily.
        <EntryChooser
          onChoose={(next) => {
            setMode(next)
            setSubmitError(null)
            setDuplicateConflict(null)
          }}
        />
      ) : mode === 'starter' ? (
        <InlineStarterFlow
          slug={slug}
          installations={installations}
          installationId={installationId}
          onChangeInstallation={(id) => setInstallationId(id)}
          templates={templatesQuery.data ?? []}
          templatesLoading={templatesQuery.isLoading}
          templatesErrorMessage={
            templatesQuery.isError ? readErrorDetail(templatesQuery.error) : null
          }
          startingPoint={startingPoint}
          onSelectStartingPoint={(next) => {
            setStartingPoint(next)
            setSubmitError(null)
            setDuplicateConflict(null)
            // Reset the sub-inputs whenever the user flips between tiles so
            // we don't carry stale state into a different sub-flow.
            if (next.kind !== 'starter') setStarterRepoName('')
            if (next.kind !== 'github-url') setGithubUrlInput('')
            // The pinned Empty tile routes straight into the repo wizard —
            // it IS the connect-existing-repo flow with no extra surface.
            if (next.kind === 'empty') {
              setMode('repo')
            }
          }}
          starterRepoName={starterRepoName}
          onStarterRepoNameChange={setStarterRepoName}
          starterRepoNameValid={starterRepoNameValid}
          githubUrlInput={githubUrlInput}
          onGithubUrlChange={setGithubUrlInput}
          parsedGithubUrl={parsedGithubUrl}
          submitting={createProject.isPending}
          submitError={submitError}
          poolEmptyError={isPoolEmptySubmitError}
          duplicateConflict={duplicateConflict}
          canSubmitStarter={canSubmitStarter}
          canSubmitGithubUrl={canSubmitGithubUrl}
          onSubmit={handleCreate}
          onBackToEntryChooser={handleBackToEntryChooser}
          onBackToStarterGrid={() => {
            setStartingPoint(null)
            setStarterRepoName('')
            setGithubUrlInput('')
            setSubmitError(null)
            setDuplicateConflict(null)
          }}
        />
      ) : (
        <>
          {/* "← Choose entry point" so the user can swing back to the
              two-card chooser without losing context. */}
          <Stack direction="row" alignItems="center" spacing={1}>
            <Button
              size="small"
              startIcon={<ArrowBackIcon sx={{ fontSize: 14 }} />}
              onClick={handleBackToEntryChooser}
              disabled={createProject.isPending}
              sx={{
                textTransform: 'none',
                color: tokens.textMuted,
                fontSize: '0.75rem',
                '&:hover': {
                  color: tokens.textPrimary,
                  bgcolor: 'transparent',
                },
              }}
            >
              Choose entry point
            </Button>
          </Stack>
          {step === 'installation' && installations.length > 1 && (
            <Stack spacing={1}>
              <Typography
                sx={{
                  fontSize: '0.75rem',
                  color: tokens.textMuted,
                  letterSpacing: '0.04em',
                  textTransform: 'uppercase',
                  fontWeight: 500,
                }}
              >
                GitHub installation
              </Typography>
              <InstallationPicker
                installations={installations}
                value={installationId}
                onChange={(id) => {
                  setInstallationId(id)
                  setSelectedRepo(null)
                  setSelectedBranch(null)
                  setStep('repo')
                }}
              />
            </Stack>
          )}

          {step === 'repo' && installationId && (
            <Stack spacing={1.5}>
              <Stack
                direction="row"
                alignItems="center"
                spacing={1}
              >
                {installations.length > 1 && (
                  <Button
                    size="small"
                    startIcon={<ArrowBackIcon sx={{ fontSize: 14 }} />}
                    onClick={() => setStep('installation')}
                    sx={{
                      textTransform: 'none',
                      color: tokens.textMuted,
                      fontSize: '0.75rem',
                      '&:hover': {
                        color: tokens.textPrimary,
                        bgcolor: 'transparent',
                      },
                    }}
                  >
                    Installation
                  </Button>
                )}
                <Typography
                  sx={{
                    fontSize: '0.75rem',
                    color: tokens.textMuted,
                    letterSpacing: '0.04em',
                    textTransform: 'uppercase',
                    fontWeight: 500,
                  }}
                >
                  Pick a repository
                </Typography>
              </Stack>
              <RepoPicker
                repos={repos}
                isLoading={reposQuery.isLoading}
                isFetching={reposQuery.isFetching}
                errorMessage={
                  reposQuery.isError
                    ? readErrorDetail(reposQuery.error)
                    : null
                }
                selected={
                  selectedRepo
                    ? { owner: selectedRepo.owner, name: selectedRepo.name }
                    : null
                }
                onSelect={(repo) => {
                  setSelectedRepo(repo)
                  setSelectedBranch(null)
                  setSubmitError(null)
                  setDuplicateConflict(null)
                  setStep('branch')
                }}
                slug={slug}
                manageAccessUrl={manageAccessUrl}
              />
            </Stack>
          )}

          {step === 'branch' && selectedRepo && (
            <Stack spacing={1.5}>
              <Stack direction="row" alignItems="center" spacing={1}>
                <Button
                  size="small"
                  startIcon={<ArrowBackIcon sx={{ fontSize: 14 }} />}
                  onClick={() => setStep('repo')}
                  sx={{
                    textTransform: 'none',
                    color: tokens.textMuted,
                    fontSize: '0.75rem',
                    '&:hover': {
                      color: tokens.textPrimary,
                      bgcolor: 'transparent',
                    },
                  }}
                >
                  Repository
                </Button>
                <Typography
                  sx={{
                    fontSize: '0.75rem',
                    color: tokens.textMuted,
                    letterSpacing: '0.04em',
                    textTransform: 'uppercase',
                    fontWeight: 500,
                  }}
                >
                  Pick a branch for {selectedRepo.owner}/{selectedRepo.name}
                </Typography>
              </Stack>
              <BranchPicker
                branches={branches}
                isLoading={branchesQuery.isLoading}
                isFetching={branchesQuery.isFetching}
                errorMessage={
                  branchesQuery.isError
                    ? readErrorDetail(branchesQuery.error)
                    : null
                }
                selectedBranch={selectedBranch}
                onSelect={setSelectedBranch}
              />
              <StartingSpecPicker
                mode={startingSpecMode}
                onChangeMode={(next) => {
                  setStartingSpecMode(next)
                  // Reset the picked spec when the user flips back to Blank
                  // so we don't accidentally ship a stale id if they flip
                  // forward again later.
                  if (next === 'blank') setSelectedCatalogSpecId(null)
                }}
                catalogSpecs={catalogSpecs}
                isLoading={catalogSpecsQuery.isLoading}
                selectedCatalogSpecId={selectedCatalogSpecId}
                onSelectCatalogSpec={setSelectedCatalogSpecId}
                disabled={createProject.isPending}
              />
              {duplicateConflict ? (
                <DuplicateRepoConflictBlock
                  slug={slug}
                  existingProjectId={duplicateConflict.existingProjectId}
                  existingProjectName={duplicateConflict.existingProjectName}
                />
              ) : isPoolEmptySubmitError ? (
                <PoolEmptyErrorAlert />
              ) : (
                submitError && (
                  <Alert
                    severity="error"
                    variant="quiet"
                  >
                    {submitError}
                  </Alert>
                )
              )}
              <Stack direction="row" justifyContent="flex-end">
                <Button
                  variant="pill" color="primary"
                  onClick={handleCreate}
                  disabled={!canSubmit}
                  startIcon={
                    createProject.isPending ? (
                      <CircularProgress size={14} color="inherit" />
                    ) : undefined
                  }
                >
                  {createProject.isPending ? 'Creating…' : 'Create project'}
                </Button>
              </Stack>
            </Stack>
          )}
        </>
      )}
    </Stack>
  )
}

// ── Entry chooser (Starter vs. Existing repo) ────────────────────────────

interface EntryChooserProps {
  onChoose: (mode: 'starter' | 'repo') => void
}

/**
 * Two-card chooser shown at the top of the inline new-project pane. Lets the
 * user pick between the curated/BYO "Starter" path and the existing-repo
 * wizard without leaving the new-session view. Sized to slot into the wide
 * inline box without overwhelming it — same hairline + accent-soft tokens as
 * the surrounding shell.
 */
function EntryChooser({ onChoose }: EntryChooserProps) {
  return (
    <Box
      sx={{
        display: 'grid',
        gridTemplateColumns: { xs: '1fr', sm: '1fr 1fr' },
        gap: 1.5,
      }}
    >
      <EntryChooserCard
        title="Select from a starter"
        description="Begin from a curated template (Empty, GitHub URL, React, Rails) — we create a fresh repo for you."
        onClick={() => onChoose('starter')}
      />
      <EntryChooserCard
        title="Select from your repositories"
        description="Connect an existing GitHub repository. Pick an installation, a repo, and a branch."
        onClick={() => onChoose('repo')}
      />
    </Box>
  )
}

interface EntryChooserCardProps {
  title: string
  description: string
  onClick: () => void
}

function EntryChooserCard({
  title,
  description,
  onClick,
}: EntryChooserCardProps) {
  return (
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
        cursor: 'pointer',
        borderRadius: 2,
        border: `1px solid ${tokens.hairline}`,
        backgroundColor: tokens.cardBg,
        p: { xs: 2, md: 2.5 },
        transition:
          'border-color 160ms ease, background-color 160ms ease, transform 160ms ease',
        '&:hover': {
          borderColor: tokens.accent,
          backgroundColor: tokens.accentSoft,
        },
        '&:focus-visible': {
          outline: `2px solid ${tokens.accent}`,
          outlineOffset: 2,
        },
      }}
    >
      <Stack spacing={0.75}>
        <Typography
          sx={{
            fontSize: '0.9375rem',
            fontWeight: 600,
            color: tokens.textPrimary,
            letterSpacing: '-0.005em',
          }}
        >
          {title}
        </Typography>
        <Typography
          sx={{
            fontSize: '0.8125rem',
            color: tokens.textMuted,
            letterSpacing: '-0.005em',
            lineHeight: 1.5,
          }}
        >
          {description}
        </Typography>
      </Stack>
    </Box>
  )
}

// ── Inline Starter flow (curated / BYO URL) ──────────────────────────────

interface InlineStarterFlowProps {
  slug: string
  installations: GithubInstallationListItem[]
  installationId: string | null
  onChangeInstallation: (id: string) => void
  templates: Parameters<typeof StarterPicker>[0]['templates']
  templatesLoading: boolean
  templatesErrorMessage: string | null
  startingPoint: StartingPoint | null
  onSelectStartingPoint: (next: StartingPoint) => void
  starterRepoName: string
  onStarterRepoNameChange: (next: string) => void
  starterRepoNameValid: boolean
  githubUrlInput: string
  onGithubUrlChange: (next: string) => void
  parsedGithubUrl: { owner: string; name: string } | null
  submitting: boolean
  submitError: string | null
  poolEmptyError: boolean
  duplicateConflict: {
    existingProjectId: string
    existingProjectName?: string
  } | null
  canSubmitStarter: boolean
  canSubmitGithubUrl: boolean
  onSubmit: () => void
  onBackToEntryChooser: () => void
  onBackToStarterGrid: () => void
}

/**
 * The Starter sub-flow rendered inline inside the new-session view. Mirrors
 * {@code NewProjectPage}'s starter / BYO-URL / empty sub-flows but renders
 * inside the wide accent-bordered box instead of a standalone page — no
 * Stepper, no separate Card chrome.
 *
 * <p>State management (project name, URL input, selected tile, …) lives in
 * the parent {@code InlineNewProject} so the same `usePostApiProjects`
 * mutation can fire from here. We just take props.
 */
function InlineStarterFlow({
  slug,
  installations,
  installationId,
  onChangeInstallation,
  templates,
  templatesLoading,
  templatesErrorMessage,
  startingPoint,
  onSelectStartingPoint,
  starterRepoName,
  onStarterRepoNameChange,
  starterRepoNameValid,
  githubUrlInput,
  onGithubUrlChange,
  parsedGithubUrl,
  submitting,
  submitError,
  poolEmptyError,
  duplicateConflict,
  canSubmitStarter,
  canSubmitGithubUrl,
  onSubmit,
  onBackToEntryChooser,
  onBackToStarterGrid,
}: InlineStarterFlowProps) {
  const selectedInstallation = useMemo<
    GithubInstallationListItem | null
  >(() => {
    if (!installationId) return null
    return installations.find((i) => i.id === installationId) ?? null
  }, [installationId, installations])

  // Stable back-link styling shared by both the entry-chooser link and the
  // "← Starters" link below it (when the user is in a sub-flow).
  const backLinkSx = {
    textTransform: 'none' as const,
    color: tokens.textMuted,
    fontSize: '0.75rem',
    '&:hover': {
      color: tokens.textPrimary,
      bgcolor: 'transparent',
    },
  }

  // The pinned Empty tile is handled in the parent (routes straight to the
  // repo wizard). Here we only render the StarterPicker + curated + URL
  // sub-flows.
  const isStarter = startingPoint?.kind === 'starter'
  const isGithubUrl = startingPoint?.kind === 'github-url'
  const showTilePicker = !isStarter && !isGithubUrl

  return (
    <Stack spacing={2}>
      <Stack direction="row" alignItems="center" spacing={1}>
        <Button
          size="small"
          startIcon={<ArrowBackIcon sx={{ fontSize: 14 }} />}
          onClick={
            showTilePicker ? onBackToEntryChooser : onBackToStarterGrid
          }
          disabled={submitting}
          sx={backLinkSx}
        >
          {showTilePicker ? 'Choose entry point' : 'Starters'}
        </Button>
      </Stack>

      {showTilePicker && (
        <StarterPicker
          templates={templates}
          isLoading={templatesLoading}
          errorMessage={templatesErrorMessage}
          selected={startingPoint}
          onSelect={onSelectStartingPoint}
        />
      )}

      {isStarter && startingPoint?.kind === 'starter' && (
        <Stack spacing={1.5}>
          <Typography
            sx={{
              fontSize: '0.875rem',
              color: tokens.textMuted,
              letterSpacing: '-0.005em',
              lineHeight: 1.5,
            }}
          >
            Starting from{' '}
            <Box component="span" sx={{ fontWeight: 600, color: tokens.textPrimary }}>
              {startingPoint.template.name}
            </Box>
            . We'll create a fresh, private repository on GitHub from{' '}
            <Box component="span" sx={{ fontWeight: 600 }}>
              {startingPoint.template.sourceRepoOwner}/
              {startingPoint.template.sourceRepoName}
            </Box>
            {selectedInstallation?.accountLogin ? (
              <>
                {' '}
                under{' '}
                <Box component="span" sx={{ fontWeight: 600 }}>
                  {selectedInstallation.accountLogin}
                </Box>
              </>
            ) : null}
            .
          </Typography>

          {installations.length > 1 && (
            <Stack spacing={0.75}>
              <Typography
                sx={{
                  fontSize: '0.75rem',
                  color: tokens.textMuted,
                  letterSpacing: '0.04em',
                  textTransform: 'uppercase',
                  fontWeight: 500,
                }}
              >
                GitHub installation
              </Typography>
              <InstallationPicker
                installations={installations}
                value={installationId}
                onChange={onChangeInstallation}
              />
            </Stack>
          )}

          <Stack spacing={0.75}>
            <Typography
              sx={{
                fontSize: '0.75rem',
                color: tokens.textMuted,
                letterSpacing: '0.04em',
                textTransform: 'uppercase',
                fontWeight: 500,
              }}
            >
              Project name
            </Typography>
            <TextField
              autoFocus
              placeholder="my-cool-project"
              fullWidth
              size="small"
              value={starterRepoName}
              onChange={(e) => onStarterRepoNameChange(e.target.value)}
              helperText="1–100 chars. Letters, digits, hyphens, underscores or periods."
              error={starterRepoName.length > 0 && !starterRepoNameValid}
              disabled={submitting}
              inputProps={{ spellCheck: 'false', autoComplete: 'off' }}
              sx={{
                '& .MuiOutlinedInput-root': {
                  bgcolor: tokens.canvasBg,
                  borderRadius: 1.5,
                  fontFamily: workspaceFontFamily.mono,
                  fontSize: '0.8125rem',
                },
              }}
            />
          </Stack>

          {duplicateConflict ? (
            <DuplicateRepoConflictBlock
              slug={slug}
              existingProjectId={duplicateConflict.existingProjectId}
              existingProjectName={duplicateConflict.existingProjectName}
            />
          ) : poolEmptyError ? (
            <PoolEmptyErrorAlert />
          ) : (
            submitError && (
              <Alert
                severity="error"
                variant="quiet"
              >
                {submitError}
              </Alert>
            )
          )}

          <Stack direction="row" justifyContent="flex-end">
            <Button
              variant="pill" color="primary"
              onClick={onSubmit}
              disabled={!canSubmitStarter}
              startIcon={
                submitting ? (
                  <CircularProgress size={14} color="inherit" />
                ) : undefined
              }
            >
              {submitting ? 'Creating from starter…' : 'Create from starter'}
            </Button>
          </Stack>
        </Stack>
      )}

      {isGithubUrl && (
        <Stack spacing={1.5}>
          <Typography
            sx={{
              fontSize: '0.875rem',
              color: tokens.textMuted,
              letterSpacing: '-0.005em',
              lineHeight: 1.5,
            }}
          >
            Paste any GitHub repo URL. We'll clone it as-is using the default
            branch — bring your own template.
          </Typography>

          <Stack spacing={0.75}>
            <Typography
              sx={{
                fontSize: '0.75rem',
                color: tokens.textMuted,
                letterSpacing: '0.04em',
                textTransform: 'uppercase',
                fontWeight: 500,
              }}
            >
              GitHub URL
            </Typography>
            <TextField
              autoFocus
              placeholder="https://github.com/owner/repo"
              fullWidth
              size="small"
              value={githubUrlInput}
              onChange={(e) => onGithubUrlChange(e.target.value)}
              helperText={
                githubUrlInput.trim().length > 0 && !parsedGithubUrl
                  ? "That doesn't look like a GitHub repo URL."
                  : parsedGithubUrl
                    ? `Will create a project from ${parsedGithubUrl.owner}/${parsedGithubUrl.name}.`
                    : 'e.g. https://github.com/rails/rails'
              }
              error={githubUrlInput.trim().length > 0 && !parsedGithubUrl}
              disabled={submitting}
              inputProps={{ spellCheck: 'false', autoComplete: 'off' }}
              sx={{
                '& .MuiOutlinedInput-root': {
                  bgcolor: tokens.canvasBg,
                  borderRadius: 1.5,
                  fontFamily: workspaceFontFamily.mono,
                  fontSize: '0.8125rem',
                },
              }}
            />
          </Stack>

          {installations.length > 1 && (
            <Stack spacing={0.75}>
              <Typography
                sx={{
                  fontSize: '0.75rem',
                  color: tokens.textMuted,
                  letterSpacing: '0.04em',
                  textTransform: 'uppercase',
                  fontWeight: 500,
                }}
              >
                GitHub installation
              </Typography>
              <InstallationPicker
                installations={installations}
                value={installationId}
                onChange={onChangeInstallation}
              />
            </Stack>
          )}

          {duplicateConflict ? (
            <DuplicateRepoConflictBlock
              slug={slug}
              existingProjectId={duplicateConflict.existingProjectId}
              existingProjectName={duplicateConflict.existingProjectName}
            />
          ) : poolEmptyError ? (
            <PoolEmptyErrorAlert />
          ) : (
            submitError && (
              <Alert
                severity="error"
                variant="quiet"
              >
                {submitError}
              </Alert>
            )
          )}

          <Stack direction="row" justifyContent="flex-end">
            <Button
              variant="pill" color="primary"
              onClick={onSubmit}
              disabled={!canSubmitGithubUrl}
              startIcon={
                submitting ? (
                  <CircularProgress size={14} color="inherit" />
                ) : undefined
              }
            >
              {submitting ? 'Creating…' : 'Create project'}
            </Button>
          </Stack>
        </Stack>
      )}
    </Stack>
  )
}

// ── Duplicate-repo conflict block ─────────────────────────────────────────

interface DuplicateRepoConflictBlockProps {
  slug: string
  existingProjectId: string
  existingProjectName?: string
}

/**
 * Inline rust-toned block shown when the user tries to create a project for
 * a repository that is already used by another project in this workspace.
 * Offers a clear "Open it" affordance that deep-links to the existing project
 * so the user can pick up where they left off instead of being stuck in the
 * create flow.
 */
function DuplicateRepoConflictBlock({
  slug,
  existingProjectId,
  existingProjectName,
}: DuplicateRepoConflictBlockProps) {
  return (
    <Box
      sx={{
        display: 'flex',
        alignItems: 'center',
        gap: 2,
        flexWrap: 'wrap',
        justifyContent: 'space-between',
        backgroundColor: 'rgba(160, 91, 62, 0.10)',
        border: '1px solid rgba(160, 91, 62, 0.30)',
        borderRadius: 1.5,
        px: 2,
        py: 1.5,
      }}
    >
      <Stack spacing={0.25} sx={{ flex: 1, minWidth: 0 }}>
        <Typography
          sx={{
            fontSize: '0.875rem',
            color: tokens.danger,
            fontWeight: 500,
            letterSpacing: '-0.005em',
          }}
        >
          This repository is already used by{' '}
          {existingProjectName ? (
            <Box component="span" sx={{ fontWeight: 600 }}>
              {existingProjectName}
            </Box>
          ) : (
            'another project'
          )}
          .
        </Typography>
        <Typography
          sx={{
            fontSize: '0.8125rem',
            color: tokens.textMuted,
            letterSpacing: '-0.005em',
            lineHeight: 1.5,
          }}
        >
          Open the existing project to keep working on it.
        </Typography>
      </Stack>
      <Button
        component={RouterLink}
        to={`/w/${slug}/projects/${existingProjectId}`}
        variant="outlined"
        size="small"
        sx={{
          flexShrink: 0,
          textTransform: 'none',
          fontSize: '0.8125rem',
          fontWeight: 500,
          borderColor: tokens.danger,
          color: tokens.danger,
          borderRadius: 999,
          px: 2,
          py: 0.5,
          '&:hover': {
            borderColor: tokens.danger,
            backgroundColor: 'rgba(160, 91, 62, 0.06)',
          },
        }}
      >
        Open it
      </Button>
    </Box>
  )
}

// ── Starting-spec picker ──────────────────────────────────────────────────

interface StartingSpecPickerProps {
  mode: 'blank' | 'catalog'
  onChangeMode: (next: 'blank' | 'catalog') => void
  catalogSpecs: WorkspaceSpecListItem[]
  isLoading: boolean
  selectedCatalogSpecId: string | null
  onSelectCatalogSpec: (id: string | null) => void
  disabled: boolean
}

/**
 * "Starting spec" picker shown at the end of the new-project flow (Scene 3
 * of the workspace-spec-catalog spec). Two radio options:
 *
 *   1. <b>Blank — no services</b> (default) — creates the project with no
 *      catalog spec applied. The first runtime boots with no preconfigured
 *      services and the user/agent fills them in afterwards.
 *   2. <b>Pick from catalog…</b> — exposes a dropdown of the workspace's
 *      saved catalog specs. The backend deep-copies the chosen spec into
 *      the new runtime's Spec at fork time, so later catalog edits never
 *      retro-affect this project.
 *
 * When the workspace catalog is empty we surface a calm explanatory string
 * inside the catalog option instead of disabling it outright — that way the
 * user still sees what the option would do once a spec exists, and learns
 * how to populate the catalog (by saving from an existing runtime).
 */
function StartingSpecPicker({
  mode,
  onChangeMode,
  catalogSpecs,
  isLoading,
  selectedCatalogSpecId,
  onSelectCatalogSpec,
  disabled,
}: StartingSpecPickerProps) {
  return (
    <Stack spacing={1}>
      <Typography
        sx={{
          fontSize: '0.75rem',
          color: tokens.textMuted,
          letterSpacing: '0.04em',
          textTransform: 'uppercase',
          fontWeight: 500,
        }}
      >
        Starting spec
      </Typography>
      <FormControl disabled={disabled}>
        <RadioGroup
          value={mode}
          onChange={(_, val) => onChangeMode(val as 'blank' | 'catalog')}
        >
          <FormControlLabel
            value="blank"
            control={
              <Radio
                size="small"
                sx={{
                  color: tokens.hairline,
                  '&.Mui-checked': { color: tokens.accent },
                }}
              />
            }
            disableTypography
            label={
              <Stack spacing={0.25} sx={{ py: 0.25 }}>
                <Typography
                  sx={{
                    fontSize: '0.875rem',
                    color: tokens.textPrimary,
                    fontWeight: 500,
                    letterSpacing: '-0.005em',
                  }}
                >
                  Blank — no services
                </Typography>
                <Typography
                  sx={{
                    fontSize: '0.75rem',
                    color: tokens.textMuted,
                    letterSpacing: '-0.005em',
                    lineHeight: 1.5,
                  }}
                >
                  Start fresh. Configure services as you build.
                </Typography>
              </Stack>
            }
            sx={{ alignItems: 'flex-start', mx: 0 }}
          />
          <FormControlLabel
            value="catalog"
            control={
              <Radio
                size="small"
                sx={{
                  color: tokens.hairline,
                  '&.Mui-checked': { color: tokens.accent },
                }}
              />
            }
            disableTypography
            label={
              <Stack spacing={0.25} sx={{ py: 0.25 }}>
                <Typography
                  sx={{
                    fontSize: '0.875rem',
                    color: tokens.textPrimary,
                    fontWeight: 500,
                    letterSpacing: '-0.005em',
                  }}
                >
                  Pick from catalog…
                </Typography>
                <Typography
                  sx={{
                    fontSize: '0.75rem',
                    color: tokens.textMuted,
                    letterSpacing: '-0.005em',
                    lineHeight: 1.5,
                  }}
                >
                  Use one of your workspace's saved specs as a starting point.
                </Typography>
              </Stack>
            }
            sx={{ alignItems: 'flex-start', mx: 0 }}
          />
        </RadioGroup>
      </FormControl>

      {mode === 'catalog' && (
        <Box sx={{ pl: 4 }}>
          {isLoading ? (
            <Skeleton variant="rounded" height={40} />
          ) : catalogSpecs.length === 0 ? (
            <Typography
              sx={{
                fontSize: '0.8125rem',
                color: tokens.textMuted,
                letterSpacing: '-0.005em',
                lineHeight: 1.5,
              }}
            >
              No specs in this workspace's catalog. Save one from an existing
              runtime first.
            </Typography>
          ) : (
            <Select
              value={selectedCatalogSpecId ?? ''}
              onChange={(e) =>
                onSelectCatalogSpec(
                  (e.target.value as string) || null,
                )
              }
              displayEmpty
              size="small"
              fullWidth
              disabled={disabled}
              inputProps={{ 'aria-label': 'Catalog spec' }}
              sx={{
                bgcolor: tokens.canvasBg,
                borderRadius: 1.5,
                fontSize: '0.8125rem',
              }}
            >
              <MenuItem value="" disabled>
                <Typography
                  component="span"
                  sx={{
                    fontSize: '0.8125rem',
                    color: tokens.textFaint,
                  }}
                >
                  Choose a spec…
                </Typography>
              </MenuItem>
              {catalogSpecs.map((spec) => (
                <MenuItem key={spec.id} value={spec.id}>
                  <Stack spacing={0.25} sx={{ py: 0.25 }}>
                    <Typography
                      sx={{
                        fontSize: '0.8125rem',
                        color: tokens.textPrimary,
                        fontWeight: 500,
                        letterSpacing: '-0.005em',
                      }}
                    >
                      {spec.name}
                    </Typography>
                    {spec.description && (
                      <Typography
                        sx={{
                          fontSize: '0.6875rem',
                          color: tokens.textMuted,
                          letterSpacing: '-0.005em',
                        }}
                      >
                        {spec.description}
                      </Typography>
                    )}
                  </Stack>
                </MenuItem>
              ))}
            </Select>
          )}
        </Box>
      )}
    </Stack>
  )
}

// ── Step 2: Pick a git branch ─────────────────────────────────────────────

interface Step2GitBranchesProps {
  projectId: string
  projectName: string
  onSwitchProject: () => void
  selectedGitBranchName: string | null
  onSelect: (branch: GithubBranchListItemDto) => void
}

/**
 * Safely format a "Updated X ago" relative label from an ISO timestamp.
 * Returns null when the field is missing or unparseable — callers fall back
 * to no "Updated …" line at all rather than rendering something misleading.
 */
function formatBranchUpdated(iso: string | null | undefined): string | null {
  if (!iso) return null
  try {
    return formatDistanceToNow(parseISO(iso), { addSuffix: true })
  } catch {
    return null
  }
}

function Step2GitBranches({
  projectId,
  projectName,
  onSwitchProject,
  selectedGitBranchName,
  onSelect,
}: Step2GitBranchesProps) {
  const branchesQuery = useGetApiProjectsProjectIdGithubBranches(projectId, {
    query: { enabled: !!projectId },
  })

  // Sort by most recently updated. We treat null `lastCommitAt` as 0 so
  // stale/unknown rows sink to the bottom of the autocomplete dropdown.
  //
  // <p>We deliberately DO NOT filter out branches that already have a system
  // branch attached. The user wants to be able to pick those too — they just
  // can't "Continue working on this branch" (that path requires a new
  // ProjectBranch), but they can still "Fork a new branch based on this one."
  // Step 3 disables the Attach card when the source has a linkedSystemBranchId,
  // so the user gets affordance + reason in one place.
  const candidateBranches = useMemo(() => {
    const all = branchesQuery.data ?? []
    return all.slice().sort((a, b) => {
      const aTime = a.lastCommitAt
        ? new Date(a.lastCommitAt).getTime()
        : 0
      const bTime = b.lastCommitAt
        ? new Date(b.lastCommitAt).getTime()
        : 0
      if (aTime !== bTime) return bTime - aTime
      // Default branch wins as the tiebreaker so it stays near the top
      // when timestamps tie (e.g., freshly-cloned repos).
      if (a.isDefault !== b.isDefault) return a.isDefault ? -1 : 1
      return a.name.localeCompare(b.name)
    })
  }, [branchesQuery.data])

  // The Autocomplete is the only picker surface — typing filters in-place
  // and the dropdown shows the sorted results. No separate scrollable list.
  const selectedBranchObject = useMemo(
    () =>
      candidateBranches.find((b) => b.name === selectedGitBranchName) ?? null,
    [candidateBranches, selectedGitBranchName],
  )

  return (
    <Stack spacing={2}>
      <Stack spacing={0.75}>
        <StepHeading>Pick a git branch</StepHeading>
        <Stack
          direction="row"
          spacing={1.5}
          alignItems="baseline"
          flexWrap="wrap"
        >
          <Typography
            sx={{
              fontSize: '0.875rem',
              color: tokens.textMuted,
              letterSpacing: '-0.005em',
            }}
          >
            In{' '}
            <Box
              component="span"
              sx={{ color: tokens.textPrimary, fontWeight: 500 }}
            >
              {projectName}
            </Box>
          </Typography>
          <Button
            size="small"
            onClick={onSwitchProject}
            sx={{
              textTransform: 'none',
              fontSize: '0.75rem',
              color: tokens.accent,
              p: 0,
              minWidth: 0,
              '&:hover': {
                color: tokens.accent,
                bgcolor: 'transparent',
                textDecoration: 'underline',
              },
            }}
          >
            Switch project
          </Button>
        </Stack>
      </Stack>

      {branchesQuery.isLoading ? (
        <Stack spacing={1}>
          {[0, 1, 2].map((i) => (
            <Skeleton key={i} variant="rounded" height={36} />
          ))}
        </Stack>
      ) : branchesQuery.isError ? (
        <Alert
          severity="error"
          variant="quiet"
        >
          {readErrorDetail(branchesQuery.error) ??
            'Could not load GitHub branches.'}
        </Alert>
      ) : candidateBranches.length === 0 ? (
        <Box
          sx={{
            border: `1px dashed ${tokens.hairline}`,
            borderRadius: 2,
            px: 2.5,
            py: 3,
          }}
        >
          <Typography
            sx={{
              fontSize: '0.875rem',
              color: tokens.textMuted,
              letterSpacing: '-0.005em',
              lineHeight: 1.55,
            }}
          >
            No git branches found in this repository. Push a branch to GitHub
            to get started.
          </Typography>
        </Box>
      ) : (
        <Stack spacing={1.5}>
          {/* Autocomplete search — fast path for "I know the name". */}
          <Autocomplete<GithubBranchListItemDto, false, false, false>
            options={candidateBranches}
            value={selectedBranchObject}
            onChange={(_, val) => {
              if (val) onSelect(val)
            }}
            getOptionLabel={(option) => option.name}
            isOptionEqualToValue={(o, v) => o.name === v.name}
            // Match against branch name AND last-commit author/message so a
            // user who only remembers "the build fix" can find it.
            filterOptions={(options, state) => {
              const q = state.inputValue.trim().toLowerCase()
              if (!q) return options
              return options.filter((o) => {
                if (o.name.toLowerCase().includes(q)) return true
                if (
                  o.lastCommitAuthor &&
                  o.lastCommitAuthor.toLowerCase().includes(q)
                )
                  return true
                if (
                  o.lastCommitMessage &&
                  o.lastCommitMessage.toLowerCase().includes(q)
                )
                  return true
                return false
              })
            }}
            size="small"
            fullWidth
            renderOption={(props, option) => {
              const updated = formatBranchUpdated(option.lastCommitAt)
              return (
                <Box
                  component="li"
                  {...props}
                  key={option.name}
                  sx={{ display: 'flex', alignItems: 'center', gap: 1.5 }}
                >
                  <Box sx={{ flex: 1, minWidth: 0 }}>
                    <Stack
                      direction="row"
                      spacing={1}
                      alignItems="center"
                      sx={{ minWidth: 0 }}
                    >
                      <Typography
                        sx={{
                          fontFamily: workspaceFontFamily.mono,
                          fontSize: '0.8125rem',
                          color: tokens.textPrimary,
                          whiteSpace: 'nowrap',
                          overflow: 'hidden',
                          textOverflow: 'ellipsis',
                          minWidth: 0,
                        }}
                      >
                        {option.name}
                      </Typography>
                      {option.isDefault && (
                        <Box
                          component="span"
                          sx={{
                            flexShrink: 0,
                            fontSize: '0.6875rem',
                            color: tokens.textMuted,
                            fontStyle: 'italic',
                          }}
                        >
                          default
                        </Box>
                      )}
                      {option.linkedSystemBranchId && (
                        <Box
                          component="span"
                          sx={{
                            flexShrink: 0,
                            fontSize: '0.6875rem',
                            color: tokens.textMuted,
                            fontStyle: 'italic',
                          }}
                        >
                          has session
                        </Box>
                      )}
                    </Stack>
                    {updated && (
                      <Typography
                        sx={{
                          fontSize: '0.6875rem',
                          color: tokens.textFaint,
                          letterSpacing: '-0.005em',
                          mt: 0.25,
                        }}
                      >
                        Updated {updated}
                        {option.lastCommitAuthor
                          ? ` by ${option.lastCommitAuthor}`
                          : ''}
                      </Typography>
                    )}
                  </Box>
                </Box>
              )
            }}
            renderInput={(params) => (
              <TextField
                {...params}
                placeholder="Search branches"
                inputProps={{
                  ...params.inputProps,
                  'aria-label': 'Search branches',
                }}
                sx={{
                  '& .MuiOutlinedInput-root': {
                    bgcolor: tokens.canvasBg,
                    borderRadius: 1.5,
                    fontSize: '0.8125rem',
                  },
                }}
              />
            )}
            // Calmer dropdown styling consistent with the rest of the page,
            // and a roomier list so 4–5 results show without scrolling. The
            // Autocomplete is the only picker now; we want it to feel
            // generous, not cramped.
            slotProps={{
              paper: {
                sx: {
                  border: `1px solid ${tokens.hairline}`,
                  boxShadow: 'none',
                  borderRadius: 1.5,
                },
              },
              listbox: {
                sx: {
                  maxHeight: 360,
                },
              },
            }}
          />
        </Stack>
      )}
    </Stack>
  )
}

// ── Step 3: Action — attach or fork ───────────────────────────────────────

interface Step3ActionProps {
  slug: string
  projectId: string
  sourceBranch: GithubBranchListItemDto
  // Kept on the props for API stability with the parent; no longer invoked
  // here because the success path navigates straight to the branch URL and
  // the destination chat canvas handles all runtime-warming feedback.
  onReset: () => void
}

function Step3Action({
  slug,
  projectId,
  sourceBranch,
}: Step3ActionProps) {
  const navigate = useNavigate()
  const queryClient = useQueryClient()

  // When the source git branch already has a ProjectBranch attached, the
  // "Continue working on this branch" path is impossible — you can't have
  // two ProjectBranch rows on the same git ref. We still want the user to
  // be able to fork off it, so we disable just the Attach card and keep the
  // Fork card live.
  const alreadyAttached = !!sourceBranch.linkedSystemBranchId

  // Load system branches to compute existing names for collision-avoidance
  // suggestion.
  const systemBranchesQuery = useGetApiProjectsProjectIdBranches(
    projectId,
    undefined,
    {
      query: { enabled: !!projectId },
    },
  )
  const existingBranchNames = useMemo(
    () => (systemBranchesQuery.data ?? []).map((b) => b.name),
    [systemBranchesQuery.data],
  )

  const initialSuggested = useMemo(
    () => suggestForkName(sourceBranch.name, existingBranchNames),
    [sourceBranch.name, existingBranchNames],
  )
  const [forkName, setForkName] = useState<string>(initialSuggested)
  // Keep the input in sync with the suggestion when the source branch or
  // existing-names set changes — but ONLY while the user hasn't touched the
  // input. We track touched manually so the auto-bump-on-409 path can
  // override without breaking the user's typed input.
  const [userTouched, setUserTouched] = useState(false)
  useEffect(() => {
    if (userTouched) return
    setForkName(initialSuggested)
  }, [initialSuggested, userTouched])

  const [attachError, setAttachError] = useState<string | null>(null)
  const [forkError, setForkError] = useState<string | null>(null)

  const attachMutation = usePostApiProjectsProjectIdBranchesAttach()
  const forkMutation = usePostApiProjectsProjectIdBranchesForkFromGit()

  // Once the mutation returns we have the branchId — navigate immediately.
  // The destination chat canvas already handles Pending/Booting/Bootstrapping/
  // Waking states with a warming placeholder and soft-queues any typed
  // messages, so no intermediate "Starting your session…" screen is needed.
  const goToNewBranch = (branchId: string) => {
    queryClient.invalidateQueries({
      queryKey: getGetApiProjectsProjectIdBranchesQueryKey(projectId),
    })
    queryClient.invalidateQueries({
      queryKey: getGetApiWorkspacesSlugProjectsQueryKey(slug),
    })
    navigate(`/w/${slug}/projects/${projectId}/branches/${branchId}`)
  }

  const handleAttach = () => {
    setAttachError(null)
    attachMutation.mutate(
      {
        projectId,
        data: { gitBranchName: sourceBranch.name },
      },
      {
        onSuccess: (response) => {
          goToNewBranch(response.branchId)
        },
        onError: (err) => {
          setAttachError(
            readErrorDetail(err) ?? 'Could not attach the branch.',
          )
        },
      },
    )
  }

  const handleFork = () => {
    setForkError(null)
    const trimmed = forkName.trim()
    if (!trimmed) {
      setForkError('Please enter a branch name.')
      return
    }
    forkMutation.mutate(
      {
        projectId,
        data: {
          sourceGitBranchName: sourceBranch.name,
          newBranchName: trimmed,
          // `forceBlankSpec: false` carries the source-branch behavior:
          // when the user clicks "Fork branch" from Step 3 the new runtime
          // inherits the source branch's spec (the bugfix path from the
          // workspace-spec-catalog spec). The Services picker that would
          // let the user override this lives in CopyBranchDialog, not here.
          forceBlankSpec: false,
        },
      },
      {
        onSuccess: (response) => {
          goToNewBranch(response.branchId)
        },
        onError: (err) => {
          if (isConflictError(err)) {
            // 409 — the typed name already exists. Auto-suggest the next
            // collision-free variant so the user can hit Fork again.
            const next = suggestForkName(sourceBranch.name, [
              ...existingBranchNames,
              trimmed,
            ])
            setForkName(next)
            setForkError(
              `"${trimmed}" already exists. Suggested a new name.`,
            )
          } else {
            setForkError(readErrorDetail(err) ?? 'Could not fork the branch.')
          }
        },
      },
    )
  }

  return (
    <Stack spacing={2}>
      <StepHeading>How do you want to start?</StepHeading>
      <Box
        sx={{
          display: 'grid',
          gridTemplateColumns: { xs: '1fr', md: '1fr 1fr' },
          gap: 2,
        }}
      >
        {/* Attach card. `height: '100%'` lets the card fill the grid row so
            its button can be bottom-pinned via `mt: 'auto'` and line up with
            the taller Fork card next to it.

            ⚠️ `useFlexGap` is required: by default MUI `Stack` spaces children
            with `margin-top` on each child, which *overrides* our `mt: 'auto'`
            on the button Box. Switching to flex `gap` frees the margin so
            `auto` can do its job. */}
        <Stack
          spacing={2}
          useFlexGap
          sx={{
            border: `1px solid ${tokens.hairline}`,
            borderRadius: 2,
            backgroundColor: tokens.cardBg,
            p: { xs: 2.5, md: 3 },
            height: '100%',
            // Soften the whole card when this path is unavailable so the eye
            // gravitates to the Fork card without forcing the user to read.
            opacity: alreadyAttached ? 0.55 : 1,
          }}
        >
          <Stack spacing={0.75}>
            <Typography
              sx={{
                fontSize: '1rem',
                fontWeight: 600,
                color: tokens.textPrimary,
                letterSpacing: '-0.005em',
              }}
            >
              Continue working on this branch
            </Typography>
            <Typography
              sx={{
                fontSize: '0.8125rem',
                color: tokens.textMuted,
                letterSpacing: '-0.005em',
                lineHeight: 1.55,
              }}
            >
              Use the existing git branch as the session — no new branch is
              pushed.
            </Typography>
            <Typography
              sx={{
                fontFamily: workspaceFontFamily.mono,
                fontSize: '0.75rem',
                color: tokens.textMuted,
                mt: 0.5,
              }}
            >
              {sourceBranch.name}
            </Typography>
            {alreadyAttached && (
              <Typography
                sx={{
                  fontSize: '0.75rem',
                  color: tokens.textMuted,
                  fontStyle: 'italic',
                  lineHeight: 1.5,
                  mt: 0.5,
                }}
              >
                This branch already has a session in this project — fork a new
                branch from it instead.
              </Typography>
            )}
          </Stack>
          {attachError && !alreadyAttached && (
            <Typography
              sx={{
                fontSize: '0.75rem',
                color: tokens.danger,
                lineHeight: 1.4,
              }}
            >
              {attachError}
            </Typography>
          )}
          {/* `mt: 'auto'` pushes this button to the bottom of the card so the
              Attach and Fork CTAs align across the row regardless of how
              much content sits above them. */}
          <Box sx={{ mt: 'auto' }}>
            <Button
              variant="pill" color="primary"
              onClick={handleAttach}
              disabled={attachMutation.isPending || alreadyAttached}
              startIcon={
                attachMutation.isPending ? (
                  <CircularProgress size={14} color="inherit" />
                ) : undefined
              }
            >
              {attachMutation.isPending ? 'Starting…' : 'Start session'}
            </Button>
          </Box>
        </Stack>

        {/* Fork card. Same `height: '100%'` + `useFlexGap` + bottom-pinned
            button so the CTA lines up with the Attach card to the left of it.
            See the Attach card above for why `useFlexGap` matters. */}
        <Stack
          spacing={2}
          useFlexGap
          sx={{
            border: `1px solid ${tokens.hairline}`,
            borderRadius: 2,
            backgroundColor: tokens.cardBg,
            p: { xs: 2.5, md: 3 },
            height: '100%',
          }}
        >
          <Stack spacing={0.75}>
            <Typography
              sx={{
                fontSize: '1rem',
                fontWeight: 600,
                color: tokens.textPrimary,
                letterSpacing: '-0.005em',
              }}
            >
              Create a new branch based on this
            </Typography>
            <Typography
              sx={{
                fontSize: '0.8125rem',
                color: tokens.textMuted,
                letterSpacing: '-0.005em',
                lineHeight: 1.55,
              }}
            >
              Fork a new branch off this one. You can edit freely without
              affecting the source.
            </Typography>
          </Stack>
          <Stack spacing={0.75}>
            <Typography
              sx={{
                fontSize: '0.6875rem',
                fontWeight: 600,
                color: tokens.textMuted,
                letterSpacing: '0.08em',
                textTransform: 'uppercase',
              }}
            >
              New branch name
            </Typography>
            <TextField
              value={forkName}
              onChange={(e) => {
                setForkName(e.target.value)
                setUserTouched(true)
                setForkError(null)
              }}
              fullWidth
              size="small"
              disabled={forkMutation.isPending}
              inputProps={{ spellCheck: 'false', autoComplete: 'off' }}
              sx={{
                '& .MuiOutlinedInput-root': {
                  bgcolor: tokens.canvasBg,
                  borderRadius: 1.5,
                  fontFamily: workspaceFontFamily.mono,
                  fontSize: '0.8125rem',
                  color: tokens.textPrimary,
                },
              }}
            />
            {forkError && (
              <Typography
                sx={{
                  fontSize: '0.75rem',
                  color: tokens.danger,
                  lineHeight: 1.4,
                }}
              >
                {forkError}
              </Typography>
            )}
          </Stack>
          {/* `mt: 'auto'` mirrors the Attach card so both CTAs sit on the
              same baseline. */}
          <Box sx={{ mt: 'auto' }}>
            <Button
              variant="pill" color="primary"
              onClick={handleFork}
              disabled={forkMutation.isPending || !forkName.trim()}
              startIcon={
                forkMutation.isPending ? (
                  <CircularProgress size={14} color="inherit" />
                ) : undefined
              }
            >
              {forkMutation.isPending ? 'Forking…' : 'Fork branch'}
            </Button>
          </Box>
        </Stack>
      </Box>
    </Stack>
  )
}

