import { useNavigate } from 'react-router-dom'
import { Box, Button, Stack, Typography } from '@mui/material'
import ArrowBackIcon from '@mui/icons-material/ArrowBack'
import { useWorkspace } from '../../../../shared/contexts/WorkspaceContext'
import { useDocumentTitle } from '../../../../shared/hooks'
import { IntegrationsTab } from '../tabs/IntegrationsTab'
import { MembersTab } from '../tabs/MembersTab'
import { RepositoriesTab } from '../tabs/RepositoriesTab'
import { SpecsTab } from '../tabs/SpecsTab'
import { WorkspaceGeneralTab } from '../tabs/WorkspaceGeneralTab'

import { chromeTokens, surfaceTokens } from '../../../shared/designTokens'

const tokens = { ...surfaceTokens, ...chromeTokens }

/**
 * Routed workspace settings canvas — the four legacy drawer tabs lifted into a
 * single vertically-stacked page inside the {@link WorkspaceShellLayout}.
 *
 * <p>The original {@code WorkspaceSettingsDrawer} hid these panels behind a
 * right-side modal with a left-rail tab nav. That works fine as an overlay,
 * but the workspace shell is now the home of every long-running workspace
 * surface (projects, new-session, settings, eventually billing…). Lifting
 * the panels onto a real route means: deep-linkable, scroll-restorable,
 * keyboard-back navigable, and visually continuous with the rest of the
 * shell — same paper canvas, same 880px centred column, same restrained
 * type hierarchy as {@code NewSessionView}.</p>
 *
 * <p>Each section is rendered by importing the existing tab component as-is.
 * Those components already own their own headings ("General", "Members",
 * "Integrations", "Repositories") and their own restrained body cards, so the
 * page-level chrome here is just: outer canvas, one h1 ("Workspace settings"
 * + workspace name caption), and hairline dividers between sections.</p>
 */
export function WorkspaceSettingsView() {
  const navigate = useNavigate()
  const { currentWorkspace, currentSlug } = useWorkspace()

  const slug = currentSlug ?? ''
  // Fall back to the URL slug while /api/me/workspaces is in flight so the
  // header never paints as an empty row on first load.
  const workspaceName = currentWorkspace?.name ?? slug ?? 'Workspace'

  // "Settings · {workspace} · GlennCode" so a settings tab is distinguishable
  // from the landing tab in a row of tab thumbnails.
  useDocumentTitle(
    workspaceName ? `Settings · ${workspaceName} · GlennCode` : null,
  )

  // The general tab's "delete" flow calls {@code onDeleted} after the workspace
  // is destroyed. The drawer used to close itself; here, the workspace no longer
  // exists, so we send the user back to the workspace switcher at the root.
  const handleWorkspaceDeleted = () => {
    navigate('/')
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
        {/* ── Back affordance ───────────────────────────────────────────────
            A small "Back" link sitting above the page heading. On desktop the
            sidebar gives the user multiple ways out (workspace name → home,
            project rows → workspace), but on mobile the sidebar is hidden in
            an overlay drawer so the user has NO visible way out of settings
            without this affordance. Restrained, muted, paper-tone — matches
            the small step-back buttons inside {@code NewSessionView}.

            Navigates back via {@code navigate(-1)} when there's a meaningful
            history entry, falling back to the workspace home so direct-link
            visits still land somewhere sensible. */}
        <Box sx={{ mb: 2, ml: -0.75 }}>
          <Button
            size="small"
            startIcon={<ArrowBackIcon sx={{ fontSize: 14 }} />}
            onClick={() => {
              // history.length is conservative — any deep-linked visit shows
              // 1 here, so we fall back to the workspace home. Otherwise we
              // honour the user's actual back-trail.
              if (window.history.length > 1) {
                navigate(-1)
              } else {
                navigate(`/w/${slug}`)
              }
            }}
            sx={{
              textTransform: 'none',
              color: tokens.textMuted,
              fontSize: '0.8125rem',
              fontWeight: 400,
              letterSpacing: '-0.005em',
              px: 1,
              '&:hover': {
                color: tokens.textPrimary,
                bgcolor: 'transparent',
              },
              '&:focus-visible': {
                outline: `2px solid ${tokens.accent}`,
                outlineOffset: 2,
                borderRadius: 1,
              },
            }}
          >
            Back
          </Button>
        </Box>

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
            Workspace settings
          </Typography>
          <Typography
            sx={{
              fontSize: '0.875rem',
              color: tokens.textMuted,
              letterSpacing: '-0.005em',
              lineHeight: 1.5,
            }}
          >
            Name, members, integrations, repositories, and the spec catalog
            — all in one place.
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

        {/* ── Sections ───────────────────────────────────────────────────────
            Each tab component owns its own h3 heading + body cards, so we
            simply stack them with generous spacing and hairline dividers
            between sections — no nested cards, no duplicate headings. */}
        <Stack
          divider={
            <Box
              aria-hidden
              sx={{
                height: '1px',
                backgroundColor: tokens.hairline,
                my: { xs: 5, md: 6 },
              }}
            />
          }
        >
          <WorkspaceGeneralTab onDeleted={handleWorkspaceDeleted} />
          <MembersTab />
          <IntegrationsTab />
          <RepositoriesTab />
          <SpecsTab />
        </Stack>
      </Box>
    </Box>
  )
}
