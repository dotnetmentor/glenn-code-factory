import { useEffect, useMemo, useState } from 'react'
import { Link as RouterLink, useNavigate } from 'react-router-dom'
import {
  Alert,
  Box,
  Button,
  CircularProgress,
  Skeleton,
  Stack,
  Step,
  StepLabel,
  Stepper,
  TextField,
  ToggleButton,
  ToggleButtonGroup,
  Typography,
} from '@mui/material'
import { useQueryClient } from '@tanstack/react-query'
import {
  getGetApiMeWorkspacesQueryKey,
  getGetApiWorkspacesSlugProjectsQueryKey,
  useGetApiGithubInstallationsInstallationIdRepos,
  useGetApiGithubInstallationsInstallationIdReposOwnerRepoBranches,
  useGetApiProjectTemplates,
  useGetApiWorkspacesSlugGithubInstallations,
  usePostApiProjects,
} from '../../../../../api/queries-commands'
import type {
  CreateProjectRequest,
  GithubInstallationListItem,
  GithubRepoListItemDto,
  ProblemDetails,
} from '../../../../../api/queries-commands'
import { useNotification } from '../../../../shared/contexts/NotificationContext'
import { useWorkspace } from '../../../../shared/contexts/WorkspaceContext'
import { BranchPicker } from '../components/BranchPicker'
import { InstallationPicker } from '../components/InstallationPicker'
import { RepoPicker } from '../components/RepoPicker'
import { StarterPicker, type StartingPoint } from '../components/StarterPicker'
import { parseGithubUrl } from '../utils/parseGithubUrl'
import {
  GithubReauthorizeBanner,
  WorkspacePageHeader,
  WorkspacePageShell,
  bodySx,
  buildGithubInstallationManageUrl,
  isPoolEmptyError,
  PoolEmptyErrorAlert,
  sectionTitleSx,
  workspaceAccent,
  workspaceColors,
  workspaceFontFamily,
  workspaceRuntime,
  workspaceText,
} from '../../../shared'

/**
 * Unpack a useful message from an axios-shaped error. Tries three wire
 * shapes in order:
 *  1. {@code response.data.error} — flat <c>{ error: "code: arg" }</c>
 *     emitted by feature controllers like {@code ProjectTemplatesController}
 *     and {@code ProjectsController} (the create-project endpoint).
 *  2. ASP.NET's <see cref="ProblemDetails"/> (<c>detail</c>/<c>title</c>) —
 *     the framework's default error shape for model-binding failures etc.
 *  3. {@code err.message} — last-resort raw network error.
 * The {@code error} field is checked FIRST so backend stable codes like
 * <c>github_repo_create_failed: GitHub refused…</c> surface verbatim
 * instead of axios's generic "Request failed with status code 400".
 */
function readErrorDetail(err: unknown): string | null {
  const maybe = err as
    | {
        response?: {
          data?: (ProblemDetails & { error?: string }) | { error?: string }
        }
        message?: string
      }
    | undefined
  const data = maybe?.response?.data as
    | { error?: string; detail?: string; title?: string }
    | undefined
  return data?.error ?? data?.detail ?? data?.title ?? maybe?.message ?? null
}

/**
 * Stable machine-readable prefix the backend uses when the create-repo path
 * needs a fresh User Access Token (UAT) — either because the user installed
 * the GitHub App before OAuth-during-install shipped, or because the UAT's
 * refresh token expired (6+ months idle). We pivot on this exact prefix to
 * render the {@link GithubReauthorizeBanner} instead of a generic error.
 * Mirrors {@code CreateProjectHandler.GithubUserAuthRequiredError}.
 */
const GITHUB_USER_AUTH_REQUIRED_PREFIX = 'github_user_auth_required'

/**
 * True when the error wrapped by {@code usePostApiProjects} matches the
 * backend's {@code github_user_auth_required: …} contract. The match is
 * intentionally prefix-based: the backend appends a short {@code Reason: …}
 * tail for diagnostics, which we don't surface to the user.
 */
function isGithubUserAuthRequiredError(err: unknown): boolean {
  const detail = readErrorDetail(err)
  if (!detail) return false
  return detail.startsWith(GITHUB_USER_AUTH_REQUIRED_PREFIX)
}

/**
 * Inspect an error from {@code usePostApiProjects} and, if it matches the
 * structured {@code RepositoryAlreadyLinkedConflict} 409 shape, extract the
 * existing project info so callers can render a dedicated "Open existing
 * project" affordance. Returns null for anything else (including 409s for
 * other reasons such as pool_empty).
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

type WizardStep = 0 | 1 | 2

const STEP_LABELS: readonly string[] = ['Installation', 'Repository', 'Branch']

/**
 * Three-step wizard to create a new project from a GitHub repo + branch.
 *
 * <p>Re-skinned to the warm-paper workspace language: dropped the stock
 * {@code Card/CardContent} chrome, wrapped in the shared
 * {@code WorkspacePageShell + WorkspacePageHeader}, and re-painted the Stepper
 * + back/create row in hairline-and-bronze tones so the page reads as part of
 * the same product as the rest of the workspace app.</p>
 */
export function NewProjectPage() {
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const { showSuccess, showError } = useNotification()
  const { currentWorkspace, currentSlug } = useWorkspace()
  const slug = currentSlug ?? ''

  // ---------------- Installations ----------------
  const installationsQuery = useGetApiWorkspacesSlugGithubInstallations(slug, {
    query: { enabled: !!slug },
  })
  const installations: GithubInstallationListItem[] = useMemo(
    () => installationsQuery.data ?? [],
    [installationsQuery.data],
  )

  const [installationId, setInstallationId] = useState<string | null>(null)
  const [step, setStep] = useState<WizardStep>(0)

  // "existing" = connect an existing GitHub repo (original flow)
  // "new"      = create a brand-new empty repo on GitHub and use it as the project repo
  const [mode, setMode] = useState<'existing' | 'new'>('existing')
  const [newRepoName, setNewRepoName] = useState('')
  const [newRepoDescription, setNewRepoDescription] = useState('')

  // ---------------- Starting point (Starters / BYO URL / Empty) ----------------
  // The new TOP section. `null` and `{kind:'empty'}` are deliberately the same
  // shape downstream: both render the legacy New Project UI bit-for-bit and
  // submit WITHOUT a `templateId`. Only `kind:'starter'` and `kind:'github-url'`
  // swap the lower section.
  const [startingPoint, setStartingPoint] = useState<StartingPoint | null>(null)
  // Project name for the Starter path — the new repo we'll create on GitHub
  // under the chosen installation, populated from this field.
  const [starterRepoName, setStarterRepoName] = useState('')
  // Raw text input for the BYO-GitHub-URL path.
  const [githubUrlInput, setGithubUrlInput] = useState('')

  const templatesQuery = useGetApiProjectTemplates({
    query: { staleTime: 60_000 },
  })

  // Pre-computed BEFORE any conditional early-return so hook order stays
  // stable across renders (we used to compute this lower down and tripped
  // the rules-of-hooks linter — moved here so React stays happy).
  const parsedGithubUrl = useMemo(
    () => (githubUrlInput.trim().length > 0 ? parseGithubUrl(githubUrlInput) : null),
    [githubUrlInput],
  )

  // Auto-pick when there's exactly one installation, and bump straight to the
  // repo step.
  useEffect(() => {
    if (installationId) return
    if (installations.length === 1) {
      setInstallationId(installations[0].id)
      setStep(1)
    } else if (installations.length > 1) {
      setStep(0)
    }
  }, [installations, installationId])

  // ---------------- Repos ----------------
  // We pass the current workspace so the backend can populate
  // `linkedProjectId` / `linkedProjectName` on repos that already have a
  // project here, which `RepoPicker` uses to gray those rows out.
  const reposQuery = useGetApiGithubInstallationsInstallationIdRepos(
    installationId ?? '',
    { workspaceId: currentWorkspace?.id },
    {
      query: { enabled: !!installationId && !!currentWorkspace },
    },
  )
  const repos: GithubRepoListItemDto[] = useMemo(() => reposQuery.data ?? [], [reposQuery.data])

  // The selected installation drives the "Manage GitHub access" deep-link in
  // the repo picker — computed here so the picker and any future tweaks
  // share the same source of truth.
  const selectedInstallation = useMemo<GithubInstallationListItem | null>(() => {
    if (!installationId) return null
    return installations.find((i) => i.id === installationId) ?? null
  }, [installationId, installations])

  const manageAccessUrl = useMemo(
    () => buildGithubInstallationManageUrl(selectedInstallation),
    [selectedInstallation],
  )

  const [selectedRepo, setSelectedRepo] = useState<GithubRepoListItemDto | null>(null)

  /**
   * When `usePostApiProjects` returns 409 with `RepositoryAlreadyLinkedConflict`,
   * we capture the existing project info here so we can render an inline
   * "Open it" block instead of a flat error toast.
   */
  const [duplicateConflict, setDuplicateConflict] = useState<{
    existingProjectId: string
    existingProjectName?: string
  } | null>(null)

  // ---------------- Branches ----------------
  const branchesQuery = useGetApiGithubInstallationsInstallationIdReposOwnerRepoBranches(
    installationId ?? '',
    selectedRepo?.owner ?? '',
    selectedRepo?.name ?? '',
    {
      query: { enabled: !!installationId && !!selectedRepo },
    },
  )
  const branches = useMemo(() => branchesQuery.data ?? [], [branchesQuery.data])

  const [selectedBranch, setSelectedBranch] = useState<string | null>(null)

  // Pre-select the default branch the moment branches arrive. Falls back to
  // `main` / `master` if the response somehow lacks an `isDefault` flag.
  useEffect(() => {
    if (selectedBranch) return
    if (branches.length === 0) return
    const def = branches.find((b) => b.isDefault)
    if (def) {
      setSelectedBranch(def.name)
      return
    }
    const fallback =
      branches.find((b) => b.name === 'main') ?? branches.find((b) => b.name === 'master')
    if (fallback) setSelectedBranch(fallback.name)
  }, [branches, selectedBranch])

  // ---------------- Mutation ----------------
  const createProject = usePostApiProjects()

  const handleCreate = () => {
    if (!currentWorkspace || !installationId) return

    // Branch by starting point. The legacy paths (`null` or `kind: 'empty'`)
    // are functionally identical and use the exact same payload they used
    // before Starters existed — no `templateId`, no surprises.
    const isStarter = startingPoint?.kind === 'starter'
    const isGithubUrl = startingPoint?.kind === 'github-url'

    if (isStarter && starterRepoName.trim().length === 0) return
    if (isGithubUrl && !parseGithubUrl(githubUrlInput)) return
    if (!isStarter && !isGithubUrl) {
      if (mode === 'existing' && (!selectedRepo || !selectedBranch)) return
      if (mode === 'new' && newRepoName.trim().length === 0) return
    }
    setDuplicateConflict(null)

    let payload: CreateProjectRequest
    if (isStarter) {
      // Starter path: create a brand-new repo from the template under the
      // chosen installation and link a starter to the new project.
      payload = {
        workspaceId: currentWorkspace.id,
        githubInstallationId: installationId,
        createNewRepo: true,
        newRepoName: starterRepoName.trim(),
        templateId: startingPoint!.kind === 'starter' ? startingPoint.template.id : undefined,
      }
    } else if (isGithubUrl) {
      // BYO URL: parse owner/name, submit via the connect-existing-repo path
      // and let the backend pick the default branch by leaving branchName out.
      const parsed = parseGithubUrl(githubUrlInput)!
      payload = {
        workspaceId: currentWorkspace.id,
        githubInstallationId: installationId,
        createNewRepo: false,
        repoOwner: parsed.owner,
        repoName: parsed.name,
      }
    } else if (mode === 'new') {
      payload = {
        workspaceId: currentWorkspace.id,
        githubInstallationId: installationId,
        createNewRepo: true,
        newRepoName: newRepoName.trim(),
        newRepoDescription: newRepoDescription.trim() || undefined,
      }
    } else {
      payload = {
        workspaceId: currentWorkspace.id,
        githubInstallationId: installationId,
        createNewRepo: false,
        repoOwner: selectedRepo!.owner,
        repoName: selectedRepo!.name,
        branchName: selectedBranch!,
      }
    }

    createProject.mutate(
      { data: payload },
      {
        onSuccess: (project) => {
          const friendly = isStarter
            ? `Created project from starter "${
                startingPoint!.kind === 'starter' ? startingPoint.template.name : ''
              }"`
            : isGithubUrl
              ? `Project ${parseGithubUrl(githubUrlInput)!.owner}/${parseGithubUrl(githubUrlInput)!.name} created.`
              : mode === 'new'
                ? `Created brand-new repo "${newRepoName.trim()}"`
                : `Project ${selectedRepo!.owner}/${selectedRepo!.name} created.`
          showSuccess(friendly)
          queryClient.invalidateQueries({ queryKey: getGetApiMeWorkspacesQueryKey() })
          queryClient.invalidateQueries({
            queryKey: getGetApiWorkspacesSlugProjectsQueryKey(slug),
          })

          if (project?.id && project?.defaultBranchId) {
            navigate(
              `/w/${slug}/projects/${project.id}/branches/${project.defaultBranchId}`,
            )
          } else if (project?.id) {
            navigate(`/w/${slug}/projects/${project.id}`)
          } else {
            navigate(`/w/${slug}`)
          }
        },
        onError: (err) => {
          // Structured 409 → render the inline "Open existing project" block.
          const dup = readDuplicateConflict(err)
          if (dup) {
            setDuplicateConflict(dup)
            return
          }
          if (isPoolEmptyError(err)) {
            return
          }
          // The reauthorize-GitHub case has its own inline banner above the
          // form — don't shout a generic toast on top of it.
          if (isGithubUserAuthRequiredError(err)) {
            return
          }
          showError(readErrorDetail(err) ?? 'Could not create the project.')
        },
      },
    )
  }

  // ---------------- Step navigation helpers ----------------
  const handleSelectInstallation = (id: string) => {
    setInstallationId(id)
    setSelectedRepo(null)
    setSelectedBranch(null)
    setDuplicateConflict(null)
    setStep(1)
  }

  const handleSelectRepo = (repo: GithubRepoListItemDto) => {
    setSelectedRepo(repo)
    setSelectedBranch(null)
    setDuplicateConflict(null)
    setStep(2)
  }

  const handleBack = () => {
    if (step === 2) {
      setSelectedBranch(null)
      setStep(1)
    } else if (step === 1) {
      // Only let the user step back to installation if we actually have a
      // choice to make there.
      if (installations.length > 1) {
        setStep(0)
      } else {
        navigate(`/w/${slug}`)
      }
    } else {
      navigate(`/w/${slug}`)
    }
  }

  const goToIntegrations = () => {
    // Integrations now live behind the workspace settings drawer. Without a
    // hash router for the drawer (yet), we send users to projects and let them
    // open settings from there.
    navigate(`/w/${slug}`)
  }

  // ---------------- Render guards ----------------
  if (!slug) {
    return (
      <WorkspacePageShell>
        <Alert
          severity="error"
          variant="quiet"
        >
          No workspace selected.
        </Alert>
      </WorkspacePageShell>
    )
  }

  if (installationsQuery.isLoading) {
    return (
      <WorkspacePageShell>
        <WorkspacePageHeader
          title="New project"
          subtitle={currentWorkspace?.name ?? 'Workspace'}
        />
        <Stack spacing={2}>
          <Skeleton variant="rounded" height={48} />
          <Skeleton variant="rounded" height={240} />
        </Stack>
      </WorkspacePageShell>
    )
  }

  if (installationsQuery.isError) {
    return (
      <WorkspacePageShell>
        <WorkspacePageHeader
          title="New project"
          subtitle={currentWorkspace?.name ?? 'Workspace'}
        />
        <Alert
          severity="error"
          variant="quiet"
        >
          {readErrorDetail(installationsQuery.error) ?? 'Could not load GitHub installations.'}
        </Alert>
      </WorkspacePageShell>
    )
  }

  if (installations.length === 0) {
    return (
      <WorkspacePageShell>
        <WorkspacePageHeader
          title="New project"
          subtitle={currentWorkspace?.name ?? 'Workspace'}
        />
        <Box
          sx={{
            border: `1px dashed ${workspaceColors.hairline}`,
            borderRadius: 2,
            p: { xs: 3, md: 4 },
          }}
        >
          <Stack spacing={2} alignItems="flex-start">
            <Typography sx={sectionTitleSx}>Connect GitHub first</Typography>
            <Typography sx={bodySx}>
              Install the GitHub App on a workspace before creating a project so
              we can list the repositories you have access to.
            </Typography>
            <Button
              variant="pill" color="primary"
              onClick={goToIntegrations}
            >
              Open workspace settings
            </Button>
          </Stack>
        </Box>
      </WorkspacePageShell>
    )
  }

  const repoErrorMessage = reposQuery.isError ? readErrorDetail(reposQuery.error) : null
  const branchErrorMessage = branchesQuery.isError ? readErrorDetail(branchesQuery.error) : null
  // The reauthorize-required case gets its own dedicated banner above the
  // form — surface it as a boolean instead of dumping the raw `error` string
  // into the generic inline alert.
  const needsGithubUserAuth =
    createProject.isError && isGithubUserAuthRequiredError(createProject.error)
  const isPoolEmptySubmitError =
    createProject.isError && isPoolEmptyError(createProject.error)
  const submitErrorMessage =
    createProject.isError && !needsGithubUserAuth && !isPoolEmptySubmitError
      ? readErrorDetail(createProject.error)
      : null

  // Validation rule for the brand-new-repo name: must match GitHub's allowed character
  // set (alphanumerics, hyphens, underscores, periods) and be 1..100 chars. Loose pre-check
  // — GitHub does the final validation server-side.
  const newRepoNameTrimmed = newRepoName.trim()
  const newRepoNameValid =
    newRepoNameTrimmed.length >= 1 &&
    newRepoNameTrimmed.length <= 100 &&
    /^[A-Za-z0-9._-]+$/.test(newRepoNameTrimmed)

  // Validation for the Starter path's project-name input — same character
  // rules as the legacy brand-new-repo flow.
  const starterRepoNameTrimmed = starterRepoName.trim()
  const starterRepoNameValid =
    starterRepoNameTrimmed.length >= 1 &&
    starterRepoNameTrimmed.length <= 100 &&
    /^[A-Za-z0-9._-]+$/.test(starterRepoNameTrimmed)

  // Which lower-section flow are we in? Legacy (`null` or `kind:'empty'`) is
  // grouped together so the existing UI renders bit-for-bit.
  const isStarterFlow = startingPoint?.kind === 'starter'
  const isGithubUrlFlow = startingPoint?.kind === 'github-url'
  const isLegacyFlow = !isStarterFlow && !isGithubUrlFlow

  const canSubmit =
    !!currentWorkspace &&
    !!installationId &&
    !createProject.isPending &&
    (isStarterFlow
      ? starterRepoNameValid
      : isGithubUrlFlow
        ? !!parsedGithubUrl
        : mode === 'new'
          ? newRepoNameValid
          : !!selectedRepo && !!selectedBranch)

  // Step labels adapt to:
  // - whether we have multiple installations (skip the "Installation" step if not)
  // - the create mode (drop the trailing "Branch" step when creating a brand-new repo,
  //   since the repo starts with "main" auto-init and there's no branch to pick)
  const baseSteps = mode === 'new' ? STEP_LABELS.slice(0, 2) : STEP_LABELS
  const visibleSteps = installations.length > 1 ? baseSteps : baseSteps.slice(1)
  const visibleStepIndex = installations.length > 1 ? step : Math.max(0, step - 1)

  const starterFlowErrorBlock =
    !isLegacyFlow && isPoolEmptySubmitError ? (
      <PoolEmptyErrorAlert />
    ) : !isLegacyFlow && submitErrorMessage ? (
      <Alert
        severity="error"
        variant="quiet"
      >
        {submitErrorMessage}
      </Alert>
    ) : null

  return (
    <WorkspacePageShell maxWidth={880}>
      <WorkspacePageHeader
        title="New project"
        subtitle="Pick a GitHub repository and a branch. We will create a project, set the branch as the default, and start provisioning a runtime."
      />

      {needsGithubUserAuth ? (
        <GithubReauthorizeBanner slug={slug} installationId={installationId} />
      ) : null}

      <Box
        sx={{
          border: `1px solid ${workspaceColors.hairline}`,
          borderRadius: 2,
          p: { xs: 2.5, md: 4 },
          backgroundColor: workspaceColors.canvasBg,
          mb: 2,
        }}
      >
        <StarterPicker
          templates={templatesQuery.data ?? []}
          isLoading={templatesQuery.isLoading}
          errorMessage={
            templatesQuery.isError ? readErrorDetail(templatesQuery.error) : null
          }
          selected={startingPoint}
          onSelect={(next) => {
            setStartingPoint(next)
            setDuplicateConflict(null)
            // Reset starter / URL inputs when switching away from those modes.
            if (next.kind !== 'starter') setStarterRepoName('')
            if (next.kind !== 'github-url') setGithubUrlInput('')
          }}
        />
      </Box>

      {isStarterFlow && startingPoint?.kind === 'starter' ? (
        <Box
          sx={{
            border: `1px solid ${workspaceColors.hairline}`,
            borderRadius: 2,
            p: { xs: 2.5, md: 4 },
            backgroundColor: workspaceColors.canvasBg,
          }}
        >
          <Stack spacing={3}>
            <Stack spacing={1.5}>
              <Typography sx={sectionTitleSx}>
                Starting from "{startingPoint.template.name}"
              </Typography>
              <Typography sx={{ ...bodySx, fontSize: '0.875rem' }}>
                We will create a fresh, private repository on GitHub from{' '}
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
            </Stack>

            {installations.length > 1 ? (
              <Stack spacing={1.5}>
                <Typography sx={sectionTitleSx}>
                  Where should we create the repo?
                </Typography>
                <InstallationPicker
                  installations={installations}
                  value={installationId}
                  onChange={(id) => setInstallationId(id)}
                />
              </Stack>
            ) : null}

            <Stack spacing={1.5}>
              <Typography sx={sectionTitleSx}>Project name</Typography>
              <TextField
                autoFocus
                label="Repository name"
                placeholder="my-cool-project"
                fullWidth
                value={starterRepoName}
                onChange={(e) => setStarterRepoName(e.target.value)}
                helperText="1–100 chars. Letters, digits, hyphens, underscores or periods."
                error={
                  starterRepoName.length > 0 && !starterRepoNameValid
                }
                InputProps={{
                  sx: { fontFamily: workspaceFontFamily.sans },
                }}
              />
            </Stack>

            {starterFlowErrorBlock}

            <Stack
              direction="row"
              spacing={2}
              justifyContent="space-between"
              alignItems="center"
            >
              <Button
                onClick={() => {
                  setStartingPoint(null)
                  setStarterRepoName('')
                  setGithubUrlInput('')
                }}
                disabled={createProject.isPending}
                sx={{
                  textTransform: 'none',
                  fontFamily: workspaceFontFamily.sans,
                  color: workspaceText.muted,
                  '&:hover': {
                    color: workspaceText.primary,
                    backgroundColor: 'transparent',
                  },
                }}
              >
                Back
              </Button>
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
                {createProject.isPending
                  ? 'Creating from starter…'
                  : 'Create from starter'}
              </Button>
            </Stack>
          </Stack>
        </Box>
      ) : null}

      {isGithubUrlFlow ? (
        <Box
          sx={{
            border: `1px solid ${workspaceColors.hairline}`,
            borderRadius: 2,
            p: { xs: 2.5, md: 4 },
            backgroundColor: workspaceColors.canvasBg,
          }}
        >
          <Stack spacing={3}>
            <Stack spacing={1.5}>
              <Typography sx={sectionTitleSx}>Paste a GitHub URL</Typography>
              <Typography sx={{ ...bodySx, fontSize: '0.875rem' }}>
                We will clone the repo as-is using the default branch — bring
                your own template.
              </Typography>
              <TextField
                autoFocus
                label="GitHub URL"
                placeholder="https://github.com/owner/repo"
                fullWidth
                value={githubUrlInput}
                onChange={(e) => setGithubUrlInput(e.target.value)}
                helperText={
                  githubUrlInput.trim().length > 0 && !parsedGithubUrl
                    ? "That doesn't look like a GitHub repo URL."
                    : parsedGithubUrl
                      ? `Will create a project from ${parsedGithubUrl.owner}/${parsedGithubUrl.name}.`
                      : 'e.g. https://github.com/rails/rails'
                }
                error={
                  githubUrlInput.trim().length > 0 && !parsedGithubUrl
                }
                InputProps={{
                  sx: { fontFamily: workspaceFontFamily.sans },
                }}
              />
            </Stack>

            {installations.length > 1 ? (
              <Stack spacing={1.5}>
                <Typography sx={sectionTitleSx}>
                  Pick a GitHub installation
                </Typography>
                <InstallationPicker
                  installations={installations}
                  value={installationId}
                  onChange={(id) => setInstallationId(id)}
                />
              </Stack>
            ) : null}

            {starterFlowErrorBlock}

            <Stack
              direction="row"
              spacing={2}
              justifyContent="space-between"
              alignItems="center"
            >
              <Button
                onClick={() => {
                  setStartingPoint(null)
                  setStarterRepoName('')
                  setGithubUrlInput('')
                }}
                disabled={createProject.isPending}
                sx={{
                  textTransform: 'none',
                  fontFamily: workspaceFontFamily.sans,
                  color: workspaceText.muted,
                  '&:hover': {
                    color: workspaceText.primary,
                    backgroundColor: 'transparent',
                  },
                }}
              >
                Back
              </Button>
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
        </Box>
      ) : null}

      {isLegacyFlow && (
      <Box
        sx={{
          border: `1px solid ${workspaceColors.hairline}`,
          borderRadius: 2,
          p: { xs: 2.5, md: 4 },
          backgroundColor: workspaceColors.canvasBg,
        }}
      >
        <Stack spacing={3}>
          <Stepper
            activeStep={visibleStepIndex}
            alternativeLabel
            sx={{
              '& .MuiStepLabel-label': {
                fontFamily: workspaceFontFamily.sans,
                fontSize: '0.8125rem',
                color: workspaceText.muted,
                '&.Mui-active': {
                  color: workspaceText.primary,
                  fontWeight: 500,
                },
                '&.Mui-completed': {
                  color: workspaceText.primary,
                },
              },
              '& .MuiStepIcon-root': {
                color: workspaceColors.chipBg,
                '&.Mui-active': { color: workspaceAccent.ink },
                '&.Mui-completed': { color: workspaceAccent.ink },
                '& .MuiStepIcon-text': {
                  fill: workspaceColors.canvasBg,
                  fontFamily: workspaceFontFamily.sans,
                  fontWeight: 500,
                },
              },
              '& .MuiStepConnector-line': {
                borderColor: workspaceColors.hairline,
              },
            }}
          >
            {visibleSteps.map((label) => (
              <Step key={label}>
                <StepLabel>{label}</StepLabel>
              </Step>
            ))}
          </Stepper>

          {step === 0 && (
            <Stack spacing={1.5}>
              <Typography sx={sectionTitleSx}>Pick a GitHub installation</Typography>
              <InstallationPicker
                installations={installations}
                value={installationId}
                onChange={handleSelectInstallation}
              />
            </Stack>
          )}

          {step === 1 && (
            <Stack spacing={2}>
              <Typography sx={sectionTitleSx}>How do you want to start?</Typography>
              <ToggleButtonGroup
                value={mode}
                exclusive
                onChange={(_, next) => {
                  if (next === 'existing' || next === 'new') {
                    setMode(next)
                    setDuplicateConflict(null)
                  }
                }}
                sx={{
                  width: '100%',
                  '& .MuiToggleButton-root': {
                    flex: 1,
                    textTransform: 'none',
                    fontFamily: workspaceFontFamily.sans,
                    fontSize: '0.875rem',
                    color: workspaceText.muted,
                    borderColor: workspaceColors.hairline,
                    py: 1.5,
                    '&.Mui-selected': {
                      backgroundColor: workspaceColors.chipBg,
                      color: workspaceText.primary,
                      fontWeight: 500,
                      '&:hover': { backgroundColor: workspaceColors.chipBg },
                    },
                  },
                }}
              >
                <ToggleButton value="existing">Connect existing repo</ToggleButton>
                <ToggleButton value="new">Create brand-new repo</ToggleButton>
              </ToggleButtonGroup>

              {mode === 'existing' ? (
                <Stack spacing={1.5}>
                  <Typography sx={sectionTitleSx}>Pick a repository</Typography>
                  <RepoPicker
                    repos={repos}
                    isLoading={reposQuery.isLoading}
                    isFetching={reposQuery.isFetching}
                    errorMessage={repoErrorMessage}
                    selected={
                      selectedRepo
                        ? { owner: selectedRepo.owner, name: selectedRepo.name }
                        : null
                    }
                    onSelect={handleSelectRepo}
                    slug={slug}
                    manageAccessUrl={manageAccessUrl}
                  />
                </Stack>
              ) : (
                <Stack spacing={1.5}>
                  <Typography sx={sectionTitleSx}>Name your new repository</Typography>
                  <Typography sx={{ ...bodySx, fontSize: '0.875rem' }}>
                    We will create a fresh, private, empty repository on GitHub under{' '}
                    <Box component="span" sx={{ fontWeight: 600 }}>
                      {selectedInstallation?.accountLogin ?? 'your account'}
                    </Box>
                    . The agent starts with a clean slate — perfect for greenfield work.
                  </Typography>
                  <TextField
                    autoFocus
                    label="Repository name"
                    placeholder="my-cool-project"
                    fullWidth
                    value={newRepoName}
                    onChange={(e) => setNewRepoName(e.target.value)}
                    helperText="1–100 chars. Letters, digits, hyphens, underscores or periods."
                    error={newRepoName.length > 0 && !newRepoNameValid}
                    InputProps={{
                      sx: { fontFamily: workspaceFontFamily.sans },
                    }}
                  />
                  <TextField
                    label="Description (optional)"
                    placeholder="What is this project about?"
                    fullWidth
                    value={newRepoDescription}
                    onChange={(e) => setNewRepoDescription(e.target.value)}
                    inputProps={{ maxLength: 350 }}
                    InputProps={{
                      sx: { fontFamily: workspaceFontFamily.sans },
                    }}
                  />
                </Stack>
              )}
            </Stack>
          )}

          {step === 2 && selectedRepo && mode === 'existing' && (
            <Stack spacing={1.5}>
              <Typography sx={sectionTitleSx}>
                Pick a branch for {selectedRepo.owner}/{selectedRepo.name}
              </Typography>
              <BranchPicker
                branches={branches}
                isLoading={branchesQuery.isLoading}
                isFetching={branchesQuery.isFetching}
                errorMessage={branchErrorMessage}
                selectedBranch={selectedBranch}
                onSelect={setSelectedBranch}
              />
            </Stack>
          )}

          {duplicateConflict ? (
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
                    fontFamily: workspaceFontFamily.sans,
                    fontSize: '0.875rem',
                    color: workspaceRuntime.failed,
                    fontWeight: 500,
                    letterSpacing: '-0.005em',
                  }}
                >
                  This repository is already used by{' '}
                  {duplicateConflict.existingProjectName ? (
                    <Box component="span" sx={{ fontWeight: 600 }}>
                      {duplicateConflict.existingProjectName}
                    </Box>
                  ) : (
                    'another project'
                  )}
                  .
                </Typography>
                <Typography
                  sx={{
                    fontFamily: workspaceFontFamily.sans,
                    fontSize: '0.8125rem',
                    color: workspaceText.muted,
                    letterSpacing: '-0.005em',
                    lineHeight: 1.5,
                  }}
                >
                  Open the existing project to keep working on it.
                </Typography>
              </Stack>
              <Button
                component={RouterLink}
                to={`/w/${slug}/projects/${duplicateConflict.existingProjectId}`}
                variant="pillOutlined"
                color="error"
                size="small"
                sx={{ flexShrink: 0, fontSize: '0.8125rem', py: 0.5 }}
              >
                Open it
              </Button>
            </Box>
          ) : isPoolEmptySubmitError ? (
            <PoolEmptyErrorAlert />
          ) : (
            submitErrorMessage && (
              <Alert
                severity="error"
                variant="quiet"
              >
                {submitErrorMessage}
              </Alert>
            )
          )}

          <Stack direction="row" spacing={2} justifyContent="space-between" alignItems="center">
            <Button
              onClick={handleBack}
              disabled={createProject.isPending}
              sx={{
                textTransform: 'none',
                fontFamily: workspaceFontFamily.sans,
                color: workspaceText.muted,
                '&:hover': { color: workspaceText.primary, backgroundColor: 'transparent' },
              }}
            >
              Back
            </Button>

            {step === 2 || (step === 1 && mode === 'new') ? (
              <Button
                variant="pill" color="primary"
                onClick={handleCreate}
                disabled={!canSubmit}
                startIcon={
                  createProject.isPending ? <CircularProgress size={14} color="inherit" /> : undefined
                }
              >
                {createProject.isPending
                  ? mode === 'new'
                    ? 'Creating repo…'
                    : 'Creating…'
                  : mode === 'new'
                    ? 'Create repo & project'
                    : 'Create project'}
              </Button>
            ) : (
              <Box />
            )}
          </Stack>
        </Stack>
      </Box>
      )}
    </WorkspacePageShell>
  )
}
