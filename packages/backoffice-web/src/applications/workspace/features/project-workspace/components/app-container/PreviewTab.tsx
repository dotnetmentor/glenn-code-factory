import { useEffect, useState } from 'react'
import { Box, Button, CircularProgress, Stack, Typography } from '@mui/material'
import { useQueryClient } from '@tanstack/react-query'
import { useNotification } from '../../../../../shared/contexts/NotificationContext'
import {
  RuntimeState,
  getGetApiProjectsProjectIdQueryKey,
  getGetApiProjectsProjectIdBranchesQueryKey,
  usePostApiProjectsProjectIdBranchesBranchIdAssignSubdomain,
} from '../../../../../../api/queries-commands'
import type { AgentHubConnection } from '../../../../../../lib/signalr'
import { appContainerTokens } from './tokens'
import { PreviewChrome } from './PreviewChrome'
import {
  DEFAULT_VIEWPORT,
  getViewportPreset,
  type ViewportId,
} from './viewport-presets'

interface PreviewTabProps {
  /** Project id — used to gate {@code previewPortChanged} events. */
  projectId: string
  /**
   * Branch id — needed to wire the "Assign subdomain" recovery action on the
   * empty state for legacy branches that pre-date the cloudflare-tunnel-preview
   * Phase 3 pool.
   */
  branchId: string
  /** Branch's preview subdomain (hostname only — no scheme). Null when absent. */
  previewHostname: string | null
  /** Current runtime state — drives the "waiting for runtime" empty state. */
  runtimeState: RuntimeState | string | undefined
  /** When false, the tab is hidden via display:none but still mounted. */
  active: boolean
  /**
   * Shared AgentHub connection. When provided, the tab subscribes to
   * {@code previewPortChanged} pushes and key-bumps the iframe so the live
   * preview refreshes against the new Cloudflare ingress automatically.
   */
  connection: AgentHubConnection | null
}

/**
 * Preview tab — the live iframe surface for the user's running app.
 *
 * <p>This component is ALWAYS mounted while its parent AppContainer is
 * rendered. The {@code active} prop toggles {@code display: none} so that
 * switching to another tab in the bottom strip doesn't unmount the iframe
 * and reset whatever state the user has built up inside their app.</p>
 *
 * <p>The iframe is reloaded via a key-bump trick rather than poking at
 * {@code contentWindow.location.reload()} — that's the only safe approach
 * across origin boundaries.</p>
 */
export function PreviewTab({
  projectId,
  branchId,
  previewHostname,
  runtimeState,
  active,
  connection,
}: PreviewTabProps) {
  const queryClient = useQueryClient()
  const { showSuccess, showError } = useNotification()
  const [viewport, setViewport] = useState<ViewportId>(DEFAULT_VIEWPORT)
  const [reloadNonce, setReloadNonce] = useState(0)

  // ── Realtime preview-port refresh ──────────────────────────────────────────
  //
  // When the project's preview port is hot-swapped (PATCH /preview-port), the
  // backend re-points every branch's Cloudflare ingress AND fans a
  // {@code previewPortChanged} push out to every group this connection
  // already belongs to (workspace + branch). We bump the iframe key so the
  // user sees the new port at the edge the moment it goes live — no restart,
  // no manual reload. We also invalidate the project query so any open
  // settings field reflects the new value.
  //
  // Deliberately no optimistic bump on Save click — the event drives the UI
  // so partial failures and other-tab changes get the same uniform refresh.
  useEffect(() => {
    if (!connection) return
    const unsubscribe = connection.onPreviewPortChanged((payload) => {
      if (payload.projectId !== projectId) return
      setReloadNonce((n) => n + 1)
      queryClient.invalidateQueries({
        queryKey: getGetApiProjectsProjectIdQueryKey(projectId),
      })
    })
    return unsubscribe
  }, [connection, projectId, queryClient])

  const preset = getViewportPreset(viewport)
  const previewUrl = previewHostname ? `https://${previewHostname}` : null
  const isOnline = runtimeState === RuntimeState.Online

  const handleCopyUrl = async () => {
    if (!previewUrl) return
    try {
      await navigator.clipboard.writeText(previewUrl)
      showSuccess('Preview URL copied')
    } catch {
      showError('Could not copy URL')
    }
  }

  const handleReload = () => {
    setReloadNonce((n) => n + 1)
  }

  // ── Empty state selection ────────────────────────────────────────────────
  //
  // Two distinct empty states with deliberately different copy: missing
  // hostname is a permanent state the user has to remedy (recreate the
  // branch); missing-Online is transient and resolves on its own.
  const showNoHostnameState = !previewHostname
  const showWaitingState = !!previewHostname && !isOnline
  const showIframe = !!previewHostname && isOnline
  const chromeDisabled = !previewHostname

  return (
    <Box
      sx={{
        flex: 1,
        minHeight: 0,
        display: active ? 'flex' : 'none',
        flexDirection: 'column',
        backgroundColor: appContainerTokens.canvasBg,
      }}
      aria-hidden={!active}
    >
      <PreviewChrome
        previewUrl={previewUrl}
        viewport={viewport}
        onViewportChange={setViewport}
        onReload={handleReload}
        onCopyUrl={handleCopyUrl}
        disabled={chromeDisabled}
      />

      <Box
        sx={{
          flex: 1,
          minHeight: 0,
          display: 'flex',
          alignItems: 'stretch',
          justifyContent: 'center',
          backgroundColor:
            preset.pixelWidth === null
              ? appContainerTokens.canvasBg
              : 'rgba(0, 0, 0, 0.025)',
          transition: 'background-color 200ms ease',
          overflow: 'hidden',
        }}
      >
        {showNoHostnameState && (
          <NoHostnameState projectId={projectId} branchId={branchId} />
        )}
        {showWaitingState && <WaitingForRuntimeState />}
        {showIframe && (
          <Box
            sx={{
              width: preset.width,
              maxWidth: '100%',
              height: '100%',
              display: 'flex',
              flexDirection: 'column',
              // Letterboxed viewports get a hairline so the device frame
              // reads as intentional rather than a layout glitch.
              ...(preset.pixelWidth !== null && {
                borderLeft: `1px solid ${appContainerTokens.hairline}`,
                borderRight: `1px solid ${appContainerTokens.hairline}`,
                backgroundColor: appContainerTokens.surface,
              }),
            }}
          >
            <iframe
              key={reloadNonce}
              title="Application preview"
              src={previewUrl ?? undefined}
              style={{
                width: '100%',
                height: '100%',
                border: 0,
                display: 'block',
                backgroundColor: appContainerTokens.surface,
              }}
              referrerPolicy="no-referrer"
              allow="clipboard-read; clipboard-write"
              loading="eager"
            />
          </Box>
        )}
      </Box>
    </Box>
  )
}

/**
 * Empty state for legacy branches that pre-date the Phase 3 Cloudflare-tunnel
 * preview pool — their {@code AssignedSubdomain} is null so the iframe has
 * nothing to point at.
 *
 * <p>The button calls {@code POST /api/projects/{projectId}/branches/{branchId}/assign-subdomain}
 * which claims one row from the pool atomically. On success we invalidate
 * the branches list cache; the workspace shell re-fetches branches, the
 * current branch's {@code previewHostname} flips from null to populated,
 * this empty state unmounts and the iframe takes over.</p>
 *
 * <p>The pool can be exhausted ({@code pool_empty}) — surfaced via a toast
 * pointing at admin batch-create. {@code already_assigned} (409) is a benign
 * race-condition outcome (the branch picked up a hostname between render and
 * click); we still invalidate so the UI catches up.</p>
 */
function NoHostnameState({
  projectId,
  branchId,
}: {
  projectId: string
  branchId: string
}) {
  const queryClient = useQueryClient()
  const { showSuccess, showError } = useNotification()
  const assignMutation =
    usePostApiProjectsProjectIdBranchesBranchIdAssignSubdomain({
      mutation: {
        onSuccess: () => {
          showSuccess('Preview subdomain assigned')
          // Branches list carries `previewHostname` — invalidating it makes
          // the workspace shell re-fetch and the Preview tab re-render with
          // the new hostname populated.
          queryClient.invalidateQueries({
            queryKey: getGetApiProjectsProjectIdBranchesQueryKey(projectId),
          })
        },
        onError: (err) => {
          // Orval surfaces ProblemDetails-ish bodies via the customClient
          // error wrapper. The backend returns { error, message } on 409 for
          // pool_empty / already_assigned — both shapes have a `message` field.
          const body = (err as { data?: { error?: string; message?: string } })
            ?.data
          if (body?.error === 'already_assigned') {
            // Stale UI — refresh the branches list so the button disappears
            // on the next render. Don't shout at the user.
            queryClient.invalidateQueries({
              queryKey: getGetApiProjectsProjectIdBranchesQueryKey(projectId),
            })
            return
          }
          showError(body?.message || 'Could not assign a preview subdomain')
        },
      },
    })

  const handleAssign = () => {
    assignMutation.mutate({ projectId, branchId })
  }

  return (
    <Stack
      spacing={1.5}
      sx={{
        alignSelf: 'center',
        textAlign: 'center',
        px: 4,
        maxWidth: 420,
      }}
    >
      <Typography
        variant="body1"
        sx={{
          color: appContainerTokens.textPrimary,
          fontWeight: 500,
          letterSpacing: '-0.005em',
        }}
      >
        No preview subdomain yet
      </Typography>
      <Typography
        variant="body2"
        sx={{
          color: appContainerTokens.textMuted,
          letterSpacing: '-0.005em',
        }}
      >
        This branch was created before previews were available. Assign one
        from the pool to get a live preview URL.
      </Typography>
      <Box sx={{ pt: 0.5, display: 'flex', justifyContent: 'center' }}>
        <Button
          variant="contained"
          size="small"
          onClick={handleAssign}
          disabled={assignMutation.isPending}
        >
          {assignMutation.isPending ? 'Assigning…' : 'Assign new subdomain'}
        </Button>
      </Box>
    </Stack>
  )
}

function WaitingForRuntimeState() {
  return (
    <Stack
      spacing={1.5}
      alignItems="center"
      sx={{
        alignSelf: 'center',
        textAlign: 'center',
        px: 4,
        maxWidth: 420,
      }}
    >
      <CircularProgress
        size={20}
        thickness={4}
        sx={{ color: appContainerTokens.textMuted, opacity: 0.7 }}
      />
      <Typography
        variant="body1"
        sx={{
          color: appContainerTokens.textPrimary,
          fontWeight: 500,
          letterSpacing: '-0.005em',
        }}
      >
        Waiting for runtime…
      </Typography>
      <Typography
        variant="body2"
        sx={{
          color: appContainerTokens.textMuted,
          letterSpacing: '-0.005em',
        }}
      >
        The preview will appear once the runtime is online.
      </Typography>
    </Stack>
  )
}
