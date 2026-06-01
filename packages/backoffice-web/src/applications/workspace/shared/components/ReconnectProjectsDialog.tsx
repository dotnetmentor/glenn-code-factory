import { useEffect, useMemo, useState } from 'react'
import {
  Alert,
  Avatar,
  Box,
  Button,
  Checkbox,
  CircularProgress,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  MenuItem,
  Select,
  Stack,
  Typography,
} from '@mui/material'
import LinkOffIcon from '@mui/icons-material/LinkOff'
import GitHubIcon from '@mui/icons-material/GitHub'
import { useQueryClient } from '@tanstack/react-query'
import {
  getGetApiWorkspacesSlugProjectsDetachedQueryKey,
  getGetApiWorkspacesSlugProjectsQueryKey,
  usePostApiWorkspacesSlugGithubInstallationsIdReconnectProjects,
  useGetApiWorkspacesSlugGithubInstallations,
  useGetApiWorkspacesSlugProjectsDetached,
  type DetachedProjectDto,
  type GithubInstallationListItem,
  type ProblemDetails,
  type ReconnectProjectsResponse,
} from '../../../../api/queries-commands'
import { useNotification } from '../../../shared/contexts/NotificationContext'
import {
  bodySx,
  captionSx,
  surfaceTokens,
  workspaceAccent,
  workspaceFontFamily,
  workspaceText,
} from '../designTokens'

function readErrorDetail(err: unknown): string | null {
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

function caseInsensitiveEq(a: string | null | undefined, b: string | null | undefined): boolean {
  return (a ?? '').toLowerCase() === (b ?? '').toLowerCase()
}

export interface ReconnectProjectsDialogProps {
  open: boolean
  onClose: () => void
  workspaceSlug: string
  /**
   * Pre-select this installation in every org group where it matches. When
   * provided, the dialog renders as a confirm-and-go for that single
   * installation. Selection can still be overridden per-org.
   */
  presetInstallationId?: string
  /**
   * Pre-select only these project ids (others stay unchecked). Useful for the
   * project-shell "reconnect this project" affordance. Defaults to ALL
   * detached projects in the workspace.
   */
  presetProjectIds?: string[]
}

interface OrgGroup {
  owner: string
  projects: DetachedProjectDto[]
  matchingInstallations: GithubInstallationListItem[]
}

/**
 * Reusable dialog for reconnecting detached projects to a GitHub installation.
 *
 * <p>Backend match rule: a project is reconnectable to an installation when
 * its {@code GithubRepoOwner} case-insensitively equals the installation's
 * {@code AccountLogin}. The endpoint is per-installation and runs the match
 * server-side; this dialog only sequences the calls when a workspace has
 * detached projects across more than one org.</p>
 */
export function ReconnectProjectsDialog({
  open,
  onClose,
  workspaceSlug,
  presetInstallationId,
  presetProjectIds,
}: ReconnectProjectsDialogProps) {
  const { showSuccess, showError } = useNotification()
  const queryClient = useQueryClient()

  const detachedQuery = useGetApiWorkspacesSlugProjectsDetached(workspaceSlug, {
    query: { enabled: open && !!workspaceSlug },
  })
  const installationsQuery = useGetApiWorkspacesSlugGithubInstallations(workspaceSlug, {
    query: { enabled: open && !!workspaceSlug },
  })

  const detachedProjects: DetachedProjectDto[] = useMemo(
    () => detachedQuery.data ?? [],
    [detachedQuery.data],
  )
  const installations: GithubInstallationListItem[] = useMemo(
    () => installationsQuery.data ?? [],
    [installationsQuery.data],
  )

  // ── Org-group derivation ────────────────────────────────────────────────
  // We pivot the detached list by case-insensitive owner so each group
  // collects the projects that all match against the same installation.
  const orgGroups: OrgGroup[] = useMemo(() => {
    const presetSet = presetProjectIds ? new Set(presetProjectIds) : null
    const filtered = presetSet
      ? detachedProjects.filter((p) => presetSet.has(p.id))
      : detachedProjects

    const byOwner = new Map<string, DetachedProjectDto[]>()
    for (const project of filtered) {
      const ownerKey = project.githubRepoOwner.toLowerCase()
      const existing = byOwner.get(ownerKey)
      if (existing) existing.push(project)
      else byOwner.set(ownerKey, [project])
    }
    return Array.from(byOwner.values())
      .map((projects) => {
        const owner = projects[0].githubRepoOwner
        const matchingInstallations = installations.filter((i) =>
          caseInsensitiveEq(i.accountLogin, owner),
        )
        return { owner, projects, matchingInstallations }
      })
      .sort((a, b) => a.owner.localeCompare(b.owner))
  }, [detachedProjects, installations, presetProjectIds])

  // ── Selected installation per org group ─────────────────────────────────
  // Default: the preset (when its owner matches) → otherwise the single match
  // when exactly one exists. Multi-match groups start unselected and the user
  // picks one.
  const [selectedInstallationByOwner, setSelectedInstallationByOwner] = useState<
    Record<string, string>
  >({})
  // Per-project checkbox state. Defaults: all checked.
  const [selectedProjectIds, setSelectedProjectIds] = useState<Set<string>>(new Set())

  useEffect(() => {
    if (!open) return
    // Seed selections when the dialog opens or when data lands.
    const nextInstall: Record<string, string> = {}
    for (const group of orgGroups) {
      const presetMatch = presetInstallationId
        ? group.matchingInstallations.find((i) => i.id === presetInstallationId)
        : null
      if (presetMatch) {
        nextInstall[group.owner] = presetMatch.id
      } else if (group.matchingInstallations.length === 1) {
        nextInstall[group.owner] = group.matchingInstallations[0].id
      }
    }
    setSelectedInstallationByOwner(nextInstall)

    const nextProjects = new Set<string>()
    for (const group of orgGroups) {
      for (const project of group.projects) nextProjects.add(project.id)
    }
    setSelectedProjectIds(nextProjects)
  }, [open, orgGroups, presetInstallationId])

  const reconnectMutation = usePostApiWorkspacesSlugGithubInstallationsIdReconnectProjects()
  const [submitting, setSubmitting] = useState(false)

  const toggleProject = (projectId: string) => {
    setSelectedProjectIds((prev) => {
      const next = new Set(prev)
      if (next.has(projectId)) next.delete(projectId)
      else next.add(projectId)
      return next
    })
  }

  const setInstallationForOwner = (owner: string, installationId: string) => {
    setSelectedInstallationByOwner((prev) => ({ ...prev, [owner]: installationId }))
  }

  // ── Reconnect (parallel across orgs that have a chosen installation) ────
  const handleReconnect = async () => {
    const targets = orgGroups
      .map((group) => {
        const installId = selectedInstallationByOwner[group.owner]
        if (!installId) return null
        const selectedInGroup = group.projects.filter((p) => selectedProjectIds.has(p.id))
        if (selectedInGroup.length === 0) return null
        return { installId, group, selectedInGroup }
      })
      .filter((t): t is { installId: string; group: OrgGroup; selectedInGroup: DetachedProjectDto[] } => t !== null)

    if (targets.length === 0) {
      showError('Pick a GitHub installation for at least one org to reconnect.')
      return
    }

    setSubmitting(true)
    try {
      const results = await Promise.allSettled(
        targets.map((t) =>
          reconnectMutation.mutateAsync({ slug: workspaceSlug, id: t.installId }),
        ),
      )
      let total = 0
      let firstError: string | null = null
      results.forEach((r) => {
        if (r.status === 'fulfilled') {
          const value = r.value as ReconnectProjectsResponse
          total += value.reconnectedCount
        } else if (!firstError) {
          firstError = readErrorDetail(r.reason) ?? 'Could not reconnect one or more orgs.'
        }
      })

      // Invalidate the lists that drive every detached-aware surface.
      queryClient.invalidateQueries({
        queryKey: getGetApiWorkspacesSlugProjectsQueryKey(workspaceSlug),
      })
      queryClient.invalidateQueries({
        queryKey: getGetApiWorkspacesSlugProjectsDetachedQueryKey(workspaceSlug),
      })

      if (total > 0) {
        showSuccess(
          `${total} project${total === 1 ? '' : 's'} reconnected.`,
        )
      }
      if (firstError) {
        showError(firstError)
      }
      if (total > 0 && !firstError) onClose()
    } finally {
      setSubmitting(false)
    }
  }

  const startInstall = () => {
    window.location.href = `/api/workspaces/${encodeURIComponent(workspaceSlug)}/github/install/start`
  }

  const isLoading = detachedQuery.isLoading || installationsQuery.isLoading
  const hasAnyDetached = orgGroups.length > 0
  const nothingSelected = selectedProjectIds.size === 0
  const noInstallChosen = !orgGroups.some(
    (g) => !!selectedInstallationByOwner[g.owner],
  )

  return (
    <Dialog
      open={open}
      onClose={() => {
        if (!submitting) onClose()
      }}
      maxWidth="sm"
      fullWidth
    >
      <DialogTitle
        sx={{
          fontFamily: workspaceFontFamily.sans,
          fontWeight: 500,
          fontSize: '1.0625rem',
          letterSpacing: '-0.01em',
          color: workspaceText.primary,
          display: 'flex',
          alignItems: 'center',
          gap: 1,
        }}
      >
        <LinkOffIcon sx={{ fontSize: 18, color: workspaceText.muted }} />
        Reconnect projects to GitHub
      </DialogTitle>
      <DialogContent>
        {isLoading && (
          <Stack alignItems="center" sx={{ py: 4 }}>
            <CircularProgress size={20} sx={{ color: workspaceText.muted }} />
          </Stack>
        )}

        {!isLoading && !hasAnyDetached && (
          <Alert severity="info" variant="panel" icon={false}>
            No detached projects in this workspace.
          </Alert>
        )}

        {!isLoading && hasAnyDetached && (
          <Stack spacing={2.5}>
            <Typography sx={bodySx}>
              These projects lost their GitHub installation when it was
              disconnected. Pick an installation per org to reconnect them.
            </Typography>

            {orgGroups.map((group) => {
              const selectedInstall = selectedInstallationByOwner[group.owner] ?? ''
              const hasMatch = group.matchingInstallations.length > 0
              return (
                <Box
                  key={group.owner}
                  sx={{
                    border: 1,
                    borderColor: 'instrument.hairline',
                    borderRadius: 2,
                    p: 2,
                    backgroundColor: surfaceTokens.cardBg,
                  }}
                >
                  <Stack
                    direction="row"
                    alignItems="center"
                    spacing={1}
                    sx={{ mb: 1.25 }}
                  >
                    <GitHubIcon sx={{ fontSize: 16, color: workspaceText.muted }} />
                    <Typography
                      component="span"
                      sx={{
                        fontFamily: workspaceFontFamily.mono,
                        fontSize: '0.875rem',
                        fontWeight: 500,
                        color: workspaceText.primary,
                      }}
                    >
                      {group.owner}
                    </Typography>
                    <Typography
                      component="span"
                      sx={{
                        ...captionSx,
                        color: workspaceText.faint,
                      }}
                    >
                      · {group.projects.length}{' '}
                      {group.projects.length === 1 ? 'project' : 'projects'}
                    </Typography>
                  </Stack>

                  {hasMatch ? (
                    <Stack spacing={1.25}>
                      <Box>
                        <Typography
                          component="label"
                          sx={{
                            display: 'block',
                            fontSize: '0.6875rem',
                            color: workspaceText.faint,
                            letterSpacing: '0.04em',
                            textTransform: 'uppercase',
                            fontWeight: 600,
                            mb: 0.5,
                          }}
                        >
                          Reconnect via installation
                        </Typography>
                        <Select
                          size="small"
                          fullWidth
                          displayEmpty
                          value={selectedInstall}
                          onChange={(e) =>
                            setInstallationForOwner(group.owner, String(e.target.value))
                          }
                          sx={{
                            backgroundColor: 'instrument.inputBg',
                            fontFamily: workspaceFontFamily.sans,
                            fontSize: '0.875rem',
                            color: workspaceText.primary,
                          }}
                        >
                          <MenuItem value="" disabled>
                            <Typography
                              component="span"
                              sx={{ color: workspaceText.faint, fontSize: '0.875rem' }}
                            >
                              Pick an installation
                            </Typography>
                          </MenuItem>
                          {group.matchingInstallations.map((install) => (
                            <MenuItem key={install.id} value={install.id}>
                              <Stack direction="row" spacing={1} alignItems="center">
                                <Avatar
                                  src={install.accountAvatarUrl ?? undefined}
                                  alt={install.accountLogin}
                                  variant="rounded"
                                  sx={{ width: 18, height: 18, fontSize: '0.625rem' }}
                                >
                                  {install.accountLogin.charAt(0).toUpperCase()}
                                </Avatar>
                                <Typography
                                  component="span"
                                  sx={{
                                    fontFamily: workspaceFontFamily.sans,
                                    fontSize: '0.875rem',
                                  }}
                                >
                                  {install.accountLogin}{' '}
                                  <Typography
                                    component="span"
                                    sx={{ color: workspaceText.faint, fontSize: '0.75rem' }}
                                  >
                                    ({install.accountType})
                                  </Typography>
                                </Typography>
                              </Stack>
                            </MenuItem>
                          ))}
                        </Select>
                      </Box>

                      <Stack spacing={0.5}>
                        {group.projects.map((project) => {
                          const checked = selectedProjectIds.has(project.id)
                          return (
                            <Stack
                              key={project.id}
                              direction="row"
                              alignItems="center"
                              spacing={1}
                              sx={{
                                px: 1,
                                py: 0.5,
                                borderRadius: 1,
                                '&:hover': {
                                  backgroundColor: 'instrument.chipBg',
                                },
                              }}
                            >
                              <Checkbox
                                size="small"
                                checked={checked}
                                onChange={() => toggleProject(project.id)}
                                sx={{
                                  p: 0.25,
                                  color: workspaceText.muted,
                                  '&.Mui-checked': { color: workspaceAccent.ink },
                                }}
                              />
                              <Box sx={{ flex: 1, minWidth: 0 }}>
                                <Typography
                                  sx={{
                                    fontFamily: workspaceFontFamily.sans,
                                    fontSize: '0.875rem',
                                    color: workspaceText.primary,
                                    fontWeight: 500,
                                    lineHeight: 1.3,
                                    overflow: 'hidden',
                                    textOverflow: 'ellipsis',
                                    whiteSpace: 'nowrap',
                                  }}
                                  title={project.name}
                                >
                                  {project.name}
                                </Typography>
                                <Typography
                                  sx={{
                                    fontFamily: workspaceFontFamily.mono,
                                    fontSize: '0.75rem',
                                    color: workspaceText.muted,
                                  }}
                                >
                                  {project.githubRepoOwner}/{project.githubRepoName}
                                </Typography>
                              </Box>
                            </Stack>
                          )
                        })}
                      </Stack>
                    </Stack>
                  ) : (
                    <Stack spacing={1.25}>
                      <Typography sx={captionSx}>
                        No installation found for {group.owner}. Install the
                        GitHub App on this org to reconnect these projects.
                      </Typography>
                      <Box>
                        <Button
                          size="small"
                          variant="outlined"
                          onClick={startInstall}
                          startIcon={<GitHubIcon sx={{ fontSize: 14 }} />}
                          sx={{
                            textTransform: 'none',
                            borderRadius: 999,
                            borderColor: 'instrument.hairline',
                            color: workspaceText.primary,
                            fontFamily: workspaceFontFamily.sans,
                            fontSize: '0.8125rem',
                            '&:hover': {
                              borderColor: workspaceAccent.ink,
                              color: workspaceAccent.ink,
                              backgroundColor: 'transparent',
                            },
                          }}
                        >
                          Install on {group.owner}
                        </Button>
                      </Box>
                      <Stack spacing={0.25} sx={{ pl: 1 }}>
                        {group.projects.map((project) => (
                          <Typography
                            key={project.id}
                            sx={{
                              fontFamily: workspaceFontFamily.mono,
                              fontSize: '0.75rem',
                              color: workspaceText.muted,
                            }}
                          >
                            {project.githubRepoOwner}/{project.githubRepoName} ·{' '}
                            <Box component="span" sx={{ color: workspaceText.faint }}>
                              {project.name}
                            </Box>
                          </Typography>
                        ))}
                      </Stack>
                    </Stack>
                  )}
                </Box>
              )
            })}
          </Stack>
        )}
      </DialogContent>
      <DialogActions sx={{ px: 3, pb: 2 }}>
        <Button
          onClick={onClose}
          disabled={submitting}
          sx={{
            textTransform: 'none',
            color: workspaceText.muted,
            '&:hover': {
              color: workspaceText.primary,
              backgroundColor: 'transparent',
            },
          }}
        >
          Cancel
        </Button>
        <Button
          variant="pill"
          color="primary"
          onClick={handleReconnect}
          disabled={
            submitting || isLoading || !hasAnyDetached || nothingSelected || noInstallChosen
          }
          startIcon={
            submitting ? (
              <CircularProgress size={14} sx={{ color: 'inherit' }} />
            ) : undefined
          }
        >
          {submitting ? 'Reconnecting…' : 'Reconnect'}
        </Button>
      </DialogActions>
    </Dialog>
  )
}
