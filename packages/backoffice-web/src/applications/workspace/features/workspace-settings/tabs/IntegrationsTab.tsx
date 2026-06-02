import { useMemo, useState } from 'react'
import {
  Alert,
  Avatar,
  Box,
  Button,
  Chip,
  CircularProgress,
  Dialog,
  DialogActions,
  DialogContent,
  DialogContentText,
  DialogTitle,
  IconButton,
  Skeleton,
  Stack,
  Tooltip,
  Typography,
} from '@mui/material'
import GitHubIcon from '@mui/icons-material/GitHub'
import LinkOffIcon from '@mui/icons-material/LinkOff'
import { useQueryClient } from '@tanstack/react-query'
import {
  getGetApiWorkspacesSlugGithubInstallationsQueryKey,
  useDeleteApiWorkspacesSlugGithubInstallationsId,
  useGetApiWorkspacesSlugGithubInstallations,
  useGetApiWorkspacesSlugProjectsDetached,
  WorkspaceRole,
} from '../../../../../api/queries-commands'
import type {
  DetachedProjectDto,
  GithubInstallationListItem,
  ProblemDetails,
} from '../../../../../api/queries-commands'
import { useNotification } from '../../../../shared/contexts/NotificationContext'
import { useWorkspace } from '../../../../shared/contexts/WorkspaceContext'
import {
  ManageGitHubAccessHint,
  ReconnectProjectsDialog,
  bodySx,
  buildGithubInstallationManageUrl,
  captionSx,
  pageCardEmptySx,
  pageCardFlushSx,
  pageCardPaddedSx,
  sectionTitleSx,
  workspaceAccent,
  workspaceFontFamily,
  workspaceText,
} from '../../../shared'

function canManage(role: WorkspaceRole | undefined | null): boolean {
  return role === WorkspaceRole.Owner || role === WorkspaceRole.Admin
}

function readErrorDetail(err: unknown): string | null {
  // Our controllers return `{ error: "..." }` on Result.Failure (see BaseApiController +
  // GithubInstallController.RemoveInstallation). Orval/Axios surface the parsed body
  // on `error.response.data`. Check `.error` first — that's where backend messages
  // actually land — then fall back to standard ProblemDetails shapes for any
  // middleware-emitted errors (ValidationProblemDetails, etc.).
  const maybe = err as
    | { response?: { data?: ProblemDetails & { error?: string; message?: string } } }
    | undefined
  return (
    maybe?.response?.data?.error ??
    maybe?.response?.data?.detail ??
    maybe?.response?.data?.title ??
    maybe?.response?.data?.message ??
    null
  )
}

/**
 * Integrations tab inside {@code WorkspaceSettingsDrawer}.
 *
 * <p>Drops the legacy MUI Card chrome in favour of hairline-divider rows so
 * the drawer feels less form-and-Crud-y and more "settings inside the chrome
 * I already trust". Backend wiring is preserved 1:1 with {@code IntegrationsPage}:
 * top-level navigation to start install, Orval mutation to disconnect.</p>
 */
export function IntegrationsTab() {
  const { currentWorkspace, currentSlug } = useWorkspace()
  const slug = currentSlug ?? ''
  const viewerRole: WorkspaceRole | null =
    (currentWorkspace?.role as WorkspaceRole | undefined) ?? null
  const isManager = canManage(viewerRole)

  const { showSuccess, showError } = useNotification()
  const queryClient = useQueryClient()

  // Note: the install-callback `?install=` snackbar is handled by
  // WorkspaceLandingView (the post-install redirect target).

  const installationsQuery = useGetApiWorkspacesSlugGithubInstallations(slug, {
    query: { enabled: !!slug },
  })

  const installations: GithubInstallationListItem[] = installationsQuery.data ?? []

  // Detached projects — surfaced as a banner once we know there are
  // installations whose org matches a detached project's repo owner. The
  // query also drives the empty-state "View detached projects" link below.
  const detachedQuery = useGetApiWorkspacesSlugProjectsDetached(slug, {
    query: { enabled: !!slug },
  })
  const detachedProjects: DetachedProjectDto[] = detachedQuery.data ?? []

  // Compute which detached projects can be reconnected via an existing
  // installation right now. We match case-insensitively against
  // {@code accountLogin} — the same comparison the backend's reconnect handler
  // uses (PostgreSQL {@code ILIKE}). The matching org list is rendered into
  // the banner copy so the user knows exactly which installations the action
  // covers.
  const reconnectable = useMemo(() => {
    const installLogins = new Set(
      installations.map((i) => i.accountLogin.toLowerCase()),
    )
    const matching = detachedProjects.filter((p) =>
      installLogins.has(p.githubRepoOwner.toLowerCase()),
    )
    const matchingOwners = Array.from(
      new Set(matching.map((p) => p.githubRepoOwner)),
    ).sort()
    return { matching, matchingOwners }
  }, [detachedProjects, installations])

  const [reconnectOpen, setReconnectOpen] = useState(false)

  const disconnect = useDeleteApiWorkspacesSlugGithubInstallationsId()
  const [pendingDisconnect, setPendingDisconnect] =
    useState<GithubInstallationListItem | null>(null)

  const handleConfirmDisconnect = () => {
    if (!pendingDisconnect) return
    const target = pendingDisconnect
    disconnect.mutate(
      { slug, id: target.id },
      {
        onSuccess: () => {
          showSuccess(`Disconnected ${target.accountLogin}.`)
          queryClient.invalidateQueries({
            queryKey: getGetApiWorkspacesSlugGithubInstallationsQueryKey(slug),
          })
          setPendingDisconnect(null)
        },
        onError: (err) => {
          showError(readErrorDetail(err) ?? 'Could not disconnect this installation.')
        },
      },
    )
  }

  const startInstall = () => {
    window.location.href = `/api/workspaces/${encodeURIComponent(slug)}/github/install/start`
  }

  if (!slug) {
    return <Alert severity="error">No workspace selected.</Alert>
  }

  return (
    <Stack spacing={3}>
      <Box>
        <Stack
          direction={{ xs: 'column', sm: 'row' }}
          alignItems={{ xs: 'flex-start', sm: 'center' }}
          justifyContent="space-between"
          spacing={2}
          sx={{ mb: 1 }}
        >
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
              Integrations
            </Typography>
            <Typography sx={bodySx}>
              Connect GitHub to sync repositories into this workspace.
            </Typography>
          </Box>
          {isManager && installations.length > 0 && (
            <Button
              variant="pill" color="primary"
              onClick={startInstall}
              startIcon={<GitHubIcon sx={{ fontSize: 18 }} />}
            >
              Add installation
            </Button>
          )}
        </Stack>
      </Box>

      {/* Reconnect banner — surfaces when the workspace has detached projects
          whose org matches one of the currently-connected installations. We
          render this ABOVE the installations list (not inline) because it's a
          "look at the room as a whole" affordance: reconnecting projects spans
          potentially many installations. */}
      {reconnectable.matching.length > 0 && (
        <Box
          sx={{
            ...pageCardPaddedSx,
            p: 2,
            display: 'flex',
            flexDirection: { xs: 'column', sm: 'row' },
            alignItems: { xs: 'flex-start', sm: 'center' },
            justifyContent: 'space-between',
            gap: 2,
          }}
        >
          <Stack
            direction="row"
            spacing={1.5}
            alignItems="flex-start"
            sx={{ minWidth: 0 }}
          >
            <Box
              sx={{
                flexShrink: 0,
                width: 28,
                height: 28,
                borderRadius: '50%',
                backgroundColor: 'instrument.chipBg',
                color: workspaceText.muted,
                display: 'inline-flex',
                alignItems: 'center',
                justifyContent: 'center',
              }}
            >
              <LinkOffIcon sx={{ fontSize: 14 }} />
            </Box>
            <Stack spacing={0.25} sx={{ minWidth: 0 }}>
              <Typography
                sx={{
                  fontFamily: workspaceFontFamily.sans,
                  fontSize: '0.875rem',
                  fontWeight: 500,
                  color: workspaceText.primary,
                  letterSpacing: '-0.005em',
                }}
              >
                {reconnectable.matching.length} detached{' '}
                {reconnectable.matching.length === 1 ? 'project' : 'projects'}{' '}
                ready to reconnect
              </Typography>
              <Typography sx={{ ...captionSx, color: workspaceText.muted }}>
                Match{reconnectable.matchingOwners.length === 1 ? 'es' : ''}{' '}
                {reconnectable.matchingOwners.map((o, i) => (
                  <Box
                    key={o}
                    component="span"
                    sx={{
                      fontFamily: workspaceFontFamily.mono,
                      color: workspaceText.primary,
                      px: 0.5,
                      py: 0.125,
                      backgroundColor: 'instrument.codeBg',
                      borderRadius: 0.5,
                      mr: i < reconnectable.matchingOwners.length - 1 ? 0.5 : 0,
                    }}
                  >
                    {o}
                  </Box>
                ))}
                .
              </Typography>
            </Stack>
          </Stack>
          {isManager && (
            <Button
              size="small"
              variant="pill" color="primary"
              onClick={() => setReconnectOpen(true)}
              sx={{
                flexShrink: 0,
                fontSize: '0.8125rem',
              }}
            >
              Reconnect all
            </Button>
          )}
        </Box>
      )}

      {installationsQuery.isLoading && (
        <Stack spacing={1.5}>
          <Skeleton variant="rounded" height={72} />
          <Skeleton variant="rounded" height={72} />
        </Stack>
      )}

      {installationsQuery.isError && (
        <Alert
          severity="error"
          variant="quiet"
        >
          Could not load GitHub installations.
        </Alert>
      )}

      {!installationsQuery.isLoading &&
        !installationsQuery.isError &&
        installations.length === 0 && (
          <Stack spacing={2}>
            <ConnectEmptyState canConnect={isManager} onConnect={startInstall} />
            {/* When no installations are connected but the workspace has
                detached projects, this row explains the empty state — projects
                from a prior install survived the disconnect and are waiting
                for reconnection. Otherwise the user might think they were
                deleted. */}
            {detachedProjects.length > 0 && (
              <Box
                sx={{
                  display: 'flex',
                  alignItems: 'center',
                  justifyContent: 'space-between',
                  gap: 2,
                  px: 0.5,
                }}
              >
                <Typography sx={captionSx}>
                  {detachedProjects.length} project
                  {detachedProjects.length === 1 ? ' is' : 's are'} waiting
                  for a GitHub installation to reconnect to.
                </Typography>
                <Button
                  size="small"
                  variant="text"
                  onClick={() => setReconnectOpen(true)}
                  sx={{
                    textTransform: 'none',
                    color: workspaceText.muted,
                    fontFamily: workspaceFontFamily.sans,
                    fontSize: '0.8125rem',
                    '&:hover': {
                      color: workspaceAccent.ink,
                      backgroundColor: 'transparent',
                    },
                  }}
                >
                  View detached projects
                </Button>
              </Box>
            )}
          </Stack>
        )}

      {!installationsQuery.isLoading &&
        !installationsQuery.isError &&
        installations.length > 0 && (
          <Box sx={pageCardFlushSx}>
            {installations.map((install, index) => (
              <InstallationRow
                key={install.id}
                installation={install}
                divider={index < installations.length - 1}
                canDisconnect={isManager}
                disconnecting={
                  disconnect.isPending && pendingDisconnect?.id === install.id
                }
                onDisconnect={() => setPendingDisconnect(install)}
              />
            ))}
          </Box>
        )}

      <ReconnectProjectsDialog
        open={reconnectOpen}
        onClose={() => setReconnectOpen(false)}
        workspaceSlug={slug}
      />

      <Dialog
        open={!!pendingDisconnect}
        onClose={() => {
          if (!disconnect.isPending) setPendingDisconnect(null)
        }}
        maxWidth="xs"
        fullWidth
      >
        <DialogTitle
          sx={{
            fontFamily: workspaceFontFamily.sans,
            fontWeight: 500,
            fontSize: '1.0625rem',
            letterSpacing: '-0.01em',
            color: workspaceText.primary,
          }}
        >
          Disconnect GitHub installation?
        </DialogTitle>
        <DialogContent>
          <DialogContentText sx={{ ...bodySx, mb: 2 }}>
            The installation for{' '}
            <Box
              component="span"
              sx={{
                fontFamily: workspaceFontFamily.mono,
                color: workspaceText.primary,
                px: 0.5,
                py: 0.125,
                backgroundColor: 'instrument.codeBg',
                borderRadius: 0.5,
              }}
            >
              {pendingDisconnect?.accountLogin}
            </Box>{' '}
            will be removed from this workspace along with its connected
            repositories.
          </DialogContentText>
          <Alert severity="info" variant="panel" icon={false}>
            To fully revoke access, remove the GitHub App on github.com under
            your account's Installed GitHub Apps settings.
          </Alert>
        </DialogContent>
        <DialogActions sx={{ px: 3, pb: 2 }}>
          <Button
            onClick={() => setPendingDisconnect(null)}
            variant="quietText"
            disabled={disconnect.isPending}
          >
            Cancel
          </Button>
          <Button
            variant="pill" color="error"
            onClick={handleConfirmDisconnect}
            disabled={disconnect.isPending}
            
          >
            {disconnect.isPending ? 'Disconnecting…' : 'Disconnect'}
          </Button>
        </DialogActions>
      </Dialog>
    </Stack>
  )
}

interface ConnectEmptyStateProps {
  canConnect: boolean
  onConnect: () => void
}

function ConnectEmptyState({ canConnect, onConnect }: ConnectEmptyStateProps) {
  return (
    <Box
      sx={{
        ...pageCardEmptySx,
        py: 6,
        px: 3,
        textAlign: 'center',
      }}
    >
      <Stack spacing={2} alignItems="center">
        <Box
          sx={{
            width: 56,
            height: 56,
            borderRadius: '50%',
            display: 'grid',
            placeItems: 'center',
            backgroundColor: 'instrument.chipBg',
            color: workspaceText.muted,
          }}
        >
          <GitHubIcon fontSize="large" />
        </Box>
        <Box>
          <Typography sx={{ ...sectionTitleSx, mb: 0.5 }}>Connect GitHub</Typography>
          <Typography sx={{ ...bodySx, maxWidth: 420, mx: 'auto' }}>
            Install the GitHub App to sync repositories into this workspace. You
            pick which repositories to grant access to.
          </Typography>
        </Box>
        {canConnect ? (
          <Button
            variant="pill" color="primary"
            onClick={onConnect}
            startIcon={<GitHubIcon sx={{ fontSize: 18 }} />}
          >
            Connect GitHub
          </Button>
        ) : (
          <Typography sx={captionSx}>
            Only workspace owners and admins can connect integrations.
          </Typography>
        )}
      </Stack>
    </Box>
  )
}

interface InstallationRowProps {
  installation: GithubInstallationListItem
  divider: boolean
  canDisconnect: boolean
  disconnecting: boolean
  onDisconnect: () => void
}

function InstallationRow({
  installation,
  divider,
  canDisconnect,
  disconnecting,
  onDisconnect,
}: InstallationRowProps) {
  const manageAccessUrl = buildGithubInstallationManageUrl(installation)

  return (
    <Stack
      spacing={1}
      sx={{
        px: 2.5,
        py: 2,
        borderBottom: divider ? 1 : 0,
        borderColor: 'instrument.hairline',
      }}
    >
      <Stack direction="row" spacing={2} alignItems="center">
        <Avatar
          src={installation.accountAvatarUrl ?? undefined}
          alt={installation.accountLogin}
          variant="rounded"
          sx={{ width: 36, height: 36, fontSize: '0.8125rem' }}
        >
          {installation.accountLogin.charAt(0).toUpperCase()}
        </Avatar>
        <Box sx={{ flex: 1, minWidth: 0 }}>
          <Stack direction="row" spacing={1} alignItems="center" sx={{ mb: 0.25 }}>
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
          <Typography sx={captionSx}>
            {installation.repoCount}{' '}
            {installation.repoCount === 1 ? 'repository' : 'repositories'}
          </Typography>
        </Box>
        {canDisconnect && (
          <Tooltip title="Disconnect">
            <span>
              <IconButton
                size="small"
                color="error"
                aria-label={`Disconnect ${installation.accountLogin}`}
                onClick={onDisconnect}
                disabled={disconnecting}
              >
                {disconnecting ? (
                  <CircularProgress size={16} />
                ) : (
                  <LinkOffIcon fontSize="small" />
                )}
              </IconButton>
            </span>
          </Tooltip>
        )}
      </Stack>
      {manageAccessUrl && <ManageGitHubAccessHint url={manageAccessUrl} />}
    </Stack>
  )
}
