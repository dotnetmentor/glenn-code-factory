import { Box, Button, CircularProgress, Stack, Typography } from '@mui/material'
import KeyIcon from '@mui/icons-material/VpnKey'
import { usePostApiWorkspacesSlugGithubUserAuthStart } from '../../../../api/queries-commands'
import { useNotification } from '../../../shared/contexts/NotificationContext'
import {
  bodySx,
  sectionTitleSx,
  workspaceColors,
  workspaceRuntime,
  workspaceText,
} from '../designTokens'

/**
 * Props for the reauthorize banner.
 *
 * @property slug — workspace slug used to scope the OAuth start endpoint.
 * @property installationId — the {@code GithubInstallation.Id} (Guid) the user
 *   picked in the parent form. Required so the backend knows which row to
 *   refresh the UAT for.
 */
export interface GithubReauthorizeBannerProps {
  /** Workspace slug — required for the {@code /api/workspaces/{slug}/…} route. */
  slug: string
  /** Installation row id (Guid) the create-repo path complained about. */
  installationId: string | null
}

/**
 * Inline banner shown above the new-project form when the create-project
 * mutation rejects with the {@code github_user_auth_required} error code.
 *
 * <p>This happens in two cases:
 * <ul>
 *   <li>The user installed the GitHub App on a personal account before the
 *       OAuth-during-install feature shipped, so we never captured a UAT.</li>
 *   <li>The UAT's refresh token expired (more than 6 months idle).</li>
 * </ul>
 * Either way, the fix is identical from the user's perspective: one click
 * sends them through a slim OAuth-only flow on github.com, and they come back
 * with a fresh UAT — no App re-install required.</p>
 *
 * <p>Paper-toned to match {@link DetachedGithubPill} — the workspace's quiet
 * "something needs your attention" idiom, never alert-paint red.</p>
 */
export function GithubReauthorizeBanner({
  slug,
  installationId,
}: GithubReauthorizeBannerProps) {
  const { showError } = useNotification()
  const startAuth = usePostApiWorkspacesSlugGithubUserAuthStart()

  const handleAuthorize = () => {
    if (!installationId) {
      showError('Pick an installation first.')
      return
    }
    startAuth.mutate(
      { slug, params: { installationId } },
      {
        onSuccess: (response) => {
          const target = response?.authorizeUrl
          if (!target) {
            showError('Could not start GitHub authorization.')
            return
          }
          window.location.href = target
        },
        onError: () => {
          showError('Could not start GitHub authorization. Please try again.')
        },
      },
    )
  }

  return (
    <Box
      role="status"
      aria-live="polite"
      sx={{
        display: 'flex',
        alignItems: 'flex-start',
        gap: 2,
        flexWrap: { xs: 'wrap', sm: 'nowrap' },
        backgroundColor: 'rgba(255, 149, 0, 0.08)',
        border: `1px solid ${workspaceRuntime.booting}33`,
        borderRadius: 1.5,
        px: { xs: 2, md: 2.5 },
        py: { xs: 1.75, md: 2 },
        mb: 2,
      }}
    >
      <Box
        sx={{
          flexShrink: 0,
          width: 32,
          height: 32,
          borderRadius: 1,
          backgroundColor: workspaceColors.chipBg,
          display: 'inline-flex',
          alignItems: 'center',
          justifyContent: 'center',
          color: workspaceRuntime.booting,
          mt: 0.25,
        }}
        aria-hidden
      >
        <KeyIcon sx={{ fontSize: 18 }} />
      </Box>

      <Stack spacing={0.5} sx={{ flex: 1, minWidth: 0 }}>
        <Typography
          sx={{
            ...sectionTitleSx,
            color: workspaceText.primary,
          }}
        >
          GitHub needs extra permission to create repos under your account.
        </Typography>
        <Typography
          sx={{
            ...bodySx,
            fontSize: '0.875rem',
          }}
        >
          One-click authorization — we won't ask you to re-install. After
          authorizing, click Create again.
        </Typography>
      </Stack>

      <Box sx={{ flexShrink: 0, alignSelf: { xs: 'stretch', sm: 'center' } }}>
        <Button
          variant="pill" color="primary"
          onClick={handleAuthorize}
          disabled={!installationId || startAuth.isPending}
          startIcon={
            startAuth.isPending ? (
              <CircularProgress size={14} color="inherit" />
            ) : undefined
          }
          sx={{
            whiteSpace: 'nowrap',
          }}
        >
          {startAuth.isPending ? 'Starting…' : 'Authorize GitHub'}
        </Button>
      </Box>
    </Box>
  )
}
