import { useEffect, useMemo, useState } from 'react'
import {
  Alert,
  Avatar,
  Box,
  Button,
  Chip,
  CircularProgress,
  IconButton,
  InputAdornment,
  Skeleton,
  Stack,
  TextField,
  Tooltip,
  Typography,
} from '@mui/material'
import LockOutlinedIcon from '@mui/icons-material/LockOutlined'
import NavigateBeforeIcon from '@mui/icons-material/NavigateBefore'
import NavigateNextIcon from '@mui/icons-material/NavigateNext'
import SearchIcon from '@mui/icons-material/Search'
import SyncIcon from '@mui/icons-material/Sync'
import { useQueryClient } from '@tanstack/react-query'
import { formatDistanceToNow, parseISO } from 'date-fns'
import {
  getGetApiWorkspacesSlugGithubRepositoriesQueryKey,
  useGetApiWorkspacesSlugGithubInstallations,
  useGetApiWorkspacesSlugGithubRepositories,
  usePostApiWorkspacesSlugGithubRepositoriesSync,
  WorkspaceRole,
} from '../../../../../api/queries-commands'
import type {
  GithubInstallationListItem,
  GithubRepositoryListItem,
  ProblemDetails,
} from '../../../../../api/queries-commands'
import { useNotification } from '../../../../shared/contexts/NotificationContext'
import { useWorkspace } from '../../../../shared/contexts/WorkspaceContext'
import {
  ManageGitHubAccessHint,
  bodySx,
  buildGithubInstallationManageUrl,
  captionSx,
  pageCardFlushSx,
  sectionTitleSx,
  workspaceAccent,
  workspaceFontFamily,
  workspaceText,
} from '../../../shared'

function canManage(role: WorkspaceRole | undefined | null): boolean {
  return role === WorkspaceRole.Owner || role === WorkspaceRole.Admin
}

function readErrorDetail(err: unknown): string | null {
  const maybe = err as { response?: { data?: ProblemDetails } } | undefined
  return maybe?.response?.data?.detail ?? maybe?.response?.data?.title ?? null
}

function formatRelative(value: string | null | undefined): string {
  if (!value) return 'Never'
  try {
    return formatDistanceToNow(parseISO(value), { addSuffix: true })
  } catch {
    return 'Unknown'
  }
}

const REPOS_PAGE_SIZE = 5

/**
 * Repositories tab inside {@code WorkspaceSettingsDrawer}.
 *
 * <p>Replaces the DataGrid-based legacy {@code ReposPage} with a hairline-row
 * list grouped per installation. Sync stays as an outline pill on each
 * installation header so the action remains discoverable but quiet.</p>
 */
export function RepositoriesTab() {
  const { currentWorkspace, currentSlug } = useWorkspace()
  const slug = currentSlug ?? ''
  const viewerRole: WorkspaceRole | null =
    (currentWorkspace?.role as WorkspaceRole | undefined) ?? null
  const isManager = canManage(viewerRole)

  const [search, setSearch] = useState('')

  const installationsQuery = useGetApiWorkspacesSlugGithubInstallations(slug, {
    query: { enabled: !!slug },
  })
  const reposQuery = useGetApiWorkspacesSlugGithubRepositories(
    slug,
    undefined,
    { query: { enabled: !!slug } },
  )

  const installations: GithubInstallationListItem[] = useMemo(
    () => installationsQuery.data ?? [],
    [installationsQuery.data],
  )
  const repos: GithubRepositoryListItem[] = useMemo(
    () => reposQuery.data ?? [],
    [reposQuery.data],
  )

  const filteredRepos = useMemo(() => {
    const q = search.trim().toLowerCase()
    if (!q) return repos
    return repos.filter((r) => r.fullName.toLowerCase().includes(q))
  }, [repos, search])

  const reposByInstallation = useMemo(() => {
    const map = new Map<string, GithubRepositoryListItem[]>()
    for (const repo of filteredRepos) {
      const list = map.get(repo.installationId)
      if (list) {
        list.push(repo)
      } else {
        map.set(repo.installationId, [repo])
      }
    }
    return map
  }, [filteredRepos])

  const visibleInstallations = useMemo(() => {
    if (!search.trim()) return installations
    return installations.filter(
      (installation) => (reposByInstallation.get(installation.id) ?? []).length > 0,
    )
  }, [installations, reposByInstallation, search])

  if (!slug) {
    return <Alert severity="error">No workspace selected.</Alert>
  }

  const isLoading = installationsQuery.isLoading || reposQuery.isLoading
  const isError = installationsQuery.isError || reposQuery.isError

  return (
    <Stack spacing={3}>
      <Box>
        <Typography
          component="h3"
          sx={{
            fontSize: '1.25rem',
            fontWeight: 400,
            letterSpacing: '-0.01em',
            color: workspaceText.primary,
            mb: 0.5,
          }}
        >
          Repositories
        </Typography>
        <Typography sx={bodySx}>
          Repositories from your connected GitHub installations. If you changed
          access on GitHub and the list looks stale, use Refresh below.
        </Typography>
      </Box>

      <TextField
        value={search}
        onChange={(e) => setSearch(e.target.value)}
        placeholder="Search repositories"
        size="small"
        fullWidth
        InputProps={{
          startAdornment: (
            <InputAdornment position="start">
              <SearchIcon fontSize="small" sx={{ color: workspaceText.faint }} />
            </InputAdornment>
          ),
          sx: {
            backgroundColor: 'instrument.inputBg',
            fontFamily: workspaceFontFamily.sans,
            fontSize: '0.875rem',
            color: workspaceText.primary,
          },
        }}
        inputProps={{ 'aria-label': 'Search repositories' }}
        sx={{ maxWidth: 420 }}
      />

      {isError && (
        <Alert
          severity="error"
          variant="quiet"
        >
          Could not load repositories.
        </Alert>
      )}

      {isLoading && !isError && (
        <Stack spacing={1.5}>
          <Skeleton variant="rounded" height={120} />
          <Skeleton variant="rounded" height={120} />
        </Stack>
      )}

      {!isLoading && !isError && installations.length === 0 && (
        <Alert
          severity="info"
          variant="quiet"
        >
          No GitHub installations yet. Connect GitHub from the Integrations
          tab.
        </Alert>
      )}

      {!isLoading &&
        !isError &&
        installations.length > 0 &&
        visibleInstallations.length === 0 &&
        search.trim() && (
          <Alert severity="info" variant="quiet">
            No repositories match &ldquo;{search.trim()}&rdquo;.
          </Alert>
        )}

      {!isLoading &&
        !isError &&
        visibleInstallations.map((installation) => (
          <InstallationReposBlock
            key={installation.id}
            slug={slug}
            installation={installation}
            repos={reposByInstallation.get(installation.id) ?? []}
            search={search}
            canSync={isManager}
          />
        ))}
    </Stack>
  )
}

interface InstallationReposBlockProps {
  slug: string
  installation: GithubInstallationListItem
  repos: GithubRepositoryListItem[]
  search: string
  canSync: boolean
}

function InstallationReposBlock({
  slug,
  installation,
  repos,
  search,
  canSync,
}: InstallationReposBlockProps) {
  const queryClient = useQueryClient()
  const { showSuccess, showError } = useNotification()
  const sync = usePostApiWorkspacesSlugGithubRepositoriesSync()
  const manageAccessUrl = buildGithubInstallationManageUrl(installation)
  const [page, setPage] = useState(0)

  useEffect(() => {
    setPage(0)
  }, [search, repos.length])

  const totalPages = Math.max(1, Math.ceil(repos.length / REPOS_PAGE_SIZE))
  const safePage = Math.min(page, totalPages - 1)
  const pageRepos = useMemo(() => {
    const start = safePage * REPOS_PAGE_SIZE
    return repos.slice(start, start + REPOS_PAGE_SIZE)
  }, [repos, safePage])

  const handleSync = () => {
    sync.mutate(
      { slug, params: { installationId: installation.id } },
      {
        onSuccess: (resp) => {
          const total = resp?.total ?? 0
          showSuccess(
            `Synced ${installation.accountLogin}: ${total} ${total === 1 ? 'repository' : 'repositories'}.`,
          )
          queryClient.invalidateQueries({
            queryKey: getGetApiWorkspacesSlugGithubRepositoriesQueryKey(slug),
          })
        },
        onError: (err) => {
          showError(readErrorDetail(err) ?? 'Could not sync repositories.')
        },
      },
    )
  }

  return (
    <Box sx={pageCardFlushSx}>
      <Stack
        direction="row"
        alignItems="center"
        justifyContent="space-between"
        spacing={2}
        sx={{
          px: 2.5,
          py: 1.5,
          borderBottom: 1,
          borderColor: 'instrument.hairline',
          backgroundColor: 'instrument.chrome',
        }}
      >
        <Stack direction="row" spacing={1.5} alignItems="center" sx={{ minWidth: 0 }}>
          <Avatar
            src={installation.accountAvatarUrl ?? undefined}
            alt={installation.accountLogin}
            variant="rounded"
            sx={{ width: 28, height: 28, fontSize: '0.75rem' }}
          >
            {installation.accountLogin.charAt(0).toUpperCase()}
          </Avatar>
          <Box sx={{ minWidth: 0 }}>
            <Stack direction="row" spacing={1} alignItems="center">
              <Typography
                sx={{
                  ...sectionTitleSx,
                  overflow: 'hidden',
                  textOverflow: 'ellipsis',
                  whiteSpace: 'nowrap',
                }}
              >
                {installation.accountLogin}
              </Typography>
              <Chip label={installation.accountType} size="small" variant="outlined" />
              {installation.suspended && (
                <Chip label="Suspended" size="small" color="warning" variant="outlined" />
              )}
            </Stack>
            <Typography sx={{ ...captionSx, fontSize: '0.75rem' }}>
              {installation.repoCount}{' '}
              {installation.repoCount === 1 ? 'repository' : 'repositories'}
            </Typography>
          </Box>
        </Stack>

        {canSync && (
          <Button
            variant="outlined"
            size="small"
            startIcon={
              sync.isPending ? (
                <CircularProgress size={14} />
              ) : (
                <SyncIcon fontSize="small" />
              )
            }
            onClick={handleSync}
            disabled={sync.isPending}
            aria-label={`Refresh repositories for ${installation.accountLogin}`}
            sx={{
              textTransform: 'none',
              borderRadius: 999,
              borderColor: 'instrument.hairline',
              color: workspaceText.primary,
              fontFamily: workspaceFontFamily.sans,
              fontWeight: 500,
              fontSize: '0.8125rem',
              px: 1.5,
              py: 0.5,
              '&:hover': {
                borderColor: workspaceAccent.ink,
                color: workspaceAccent.ink,
                backgroundColor: 'transparent',
              },
            }}
          >
            {sync.isPending ? 'Refreshing…' : 'Refresh'}
          </Button>
        )}
      </Stack>

      {manageAccessUrl && (
        <Box sx={{ px: 2.5, py: 1.5, borderBottom: 1, borderColor: 'instrument.hairline' }}>
          <ManageGitHubAccessHint url={manageAccessUrl} />
        </Box>
      )}

      {repos.length === 0 ? (
        <Box sx={{ py: 4, px: 3, textAlign: 'center' }}>
          <Stack spacing={1} alignItems="center">
            <Typography sx={captionSx}>
              {search
                ? `No repositories match "${search}".`
                : 'No repositories yet. Click Refresh to fetch the latest list from GitHub.'}
            </Typography>
          </Stack>
        </Box>
      ) : (
        <Box>
          {pageRepos.map((repo, index) => (
            <RepoRow
              key={repo.id}
              repo={repo}
              divider={index < pageRepos.length - 1}
            />
          ))}
          <RepoListPagination
            page={safePage}
            pageSize={REPOS_PAGE_SIZE}
            totalCount={repos.length}
            onPageChange={setPage}
          />
        </Box>
      )}
    </Box>
  )
}

interface RepoListPaginationProps {
  page: number
  pageSize: number
  totalCount: number
  onPageChange: (page: number) => void
}

function RepoListPagination({
  page,
  pageSize,
  totalCount,
  onPageChange,
}: RepoListPaginationProps) {
  if (totalCount <= pageSize) return null

  const totalPages = Math.ceil(totalCount / pageSize)
  const start = page * pageSize + 1
  const end = Math.min((page + 1) * pageSize, totalCount)

  return (
    <Stack
      direction="row"
      alignItems="center"
      justifyContent="space-between"
      sx={{
        px: 2.5,
        py: 1.25,
        borderTop: 1,
        borderColor: 'instrument.hairline',
        backgroundColor: 'instrument.chrome',
      }}
    >
      <Typography sx={{ ...captionSx, fontSize: '0.75rem' }}>
        {start}–{end} of {totalCount}
      </Typography>
      <Stack direction="row" alignItems="center" spacing={0.25}>
        <IconButton
          size="small"
          disabled={page === 0}
          onClick={() => onPageChange(page - 1)}
          aria-label="Previous page"
          sx={{ color: workspaceText.muted }}
        >
          <NavigateBeforeIcon sx={{ fontSize: 18 }} />
        </IconButton>
        <Typography sx={{ ...captionSx, fontSize: '0.75rem', px: 0.5 }}>
          {page + 1} / {totalPages}
        </Typography>
        <IconButton
          size="small"
          disabled={page >= totalPages - 1}
          onClick={() => onPageChange(page + 1)}
          aria-label="Next page"
          sx={{ color: workspaceText.muted }}
        >
          <NavigateNextIcon sx={{ fontSize: 18 }} />
        </IconButton>
      </Stack>
    </Stack>
  )
}

interface RepoRowProps {
  repo: GithubRepositoryListItem
  divider: boolean
}

function RepoRow({ repo, divider }: RepoRowProps) {
  return (
    <Stack
      direction="row"
      spacing={2}
      alignItems="center"
      sx={{
        px: 2.5,
        py: 1.5,
        borderBottom: divider ? 1 : 0,
        borderColor: 'instrument.hairline',
      }}
    >
      <Box sx={{ flex: 1, minWidth: 0 }}>
        <Stack direction="row" spacing={1} alignItems="center" sx={{ minWidth: 0 }}>
          <Typography
            sx={{
              fontFamily: workspaceFontFamily.mono,
              fontSize: '0.8125rem',
              color: workspaceText.primary,
              overflow: 'hidden',
              textOverflow: 'ellipsis',
              whiteSpace: 'nowrap',
            }}
          >
            {repo.fullName}
          </Typography>
          {repo.private && (
            <Tooltip title="Private">
              <LockOutlinedIcon
                sx={{ fontSize: 13, color: workspaceText.faint, flexShrink: 0 }}
              />
            </Tooltip>
          )}
        </Stack>
      </Box>
      {repo.defaultBranch && (
        <Chip
          label={repo.defaultBranch}
          size="small"
          sx={{
            backgroundColor: 'instrument.chipBg',
            border: 'none',
            color: workspaceText.muted,
            fontFamily: workspaceFontFamily.mono,
            fontSize: '0.6875rem',
            height: 20,
            flexShrink: 0,
          }}
        />
      )}
      <Tooltip title={repo.lastSyncedAt ?? 'Never synced'}>
        <Typography
          sx={{
            ...captionSx,
            fontSize: '0.75rem',
            minWidth: 110,
            textAlign: 'right',
            flexShrink: 0,
          }}
        >
          {formatRelative(repo.lastSyncedAt)}
        </Typography>
      </Tooltip>
    </Stack>
  )
}
