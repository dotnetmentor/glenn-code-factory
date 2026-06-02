import { useMediaQuery, Box, Button, Fade, Stack, Typography } from '@mui/material'
import { useTheme } from '@mui/material/styles'
import { useNavigate } from 'react-router-dom'
import {
  surfaceTokens,
  workspaceCanvasInset,
  workspacePanelShellSx,
  workspaceSidebarWidth,
  workspaceFontFamily,
} from '../../shared/designTokens'
import { useMovie } from './movie/useMovie'
import { DemoSidebar } from './components/DemoSidebar'
import { DemoChat } from './components/DemoChat'
import { DemoAppPanel } from './components/DemoAppPanel'

const SANS = workspaceFontFamily.sans

/**
 * Public marketing landing at `/` for logged-out visitors. It looks and feels
 * like the real workspace IDE shell, then auto-plays a one-shot "movie" of
 * GlennCode building a waitlist landing page — climaxing in the genuinely live
 * waitlist form inside the preview panel. The "Log in" button routes existing
 * users into the auth flow (via the gated `/signin` bounce).
 *
 * See ./movie/script.ts for the choreography and ./components/LiveWaitlistForm
 * for the one real, wired-to-backend piece.
 */
export function LandingRoute() {
  const theme = useTheme()
  const navigate = useNavigate()
  const isNarrow = useMediaQuery(theme.breakpoints.down('md'))
  const state = useMovie()

  return (
    <Box
      sx={{
        position: 'relative',
        height: '100vh',
        '@supports (height: 100dvh)': { height: '100dvh' },
        width: '100vw',
        display: 'flex',
        flexDirection: 'row',
        backgroundColor: surfaceTokens.canvasBg,
        color: surfaceTokens.textPrimary,
        overflow: 'hidden',
        ...(!isNarrow && {
          p: workspaceCanvasInset.desktopPadding,
          gap: workspaceCanvasInset.panelGap,
          [`@media (min-width: ${theme.breakpoints.values.lg}px)`]: {
            gap: workspaceCanvasInset.panelGapLg,
          },
        }),
      }}
    >
      {/* Log in — the one piece of real chrome. Routes to the gated /signin
          bounce, which renders the login form and lands authed users home. */}
      <Button
        onClick={() => navigate('/signin')}
        sx={{
          position: 'absolute',
          top: isNarrow ? 12 : 22,
          right: isNarrow ? 12 : 28,
          zIndex: 20,
          textTransform: 'none',
          fontFamily: SANS,
          fontWeight: 600,
          fontSize: '0.85rem',
          color: surfaceTokens.textPrimary,
          backgroundColor: 'rgba(255,255,255,0.7)',
          backdropFilter: 'blur(8px)',
          border: `1px solid ${surfaceTokens.hairline}`,
          borderRadius: 1.5,
          px: 2,
          py: 0.6,
          '&:hover': { backgroundColor: 'rgba(255,255,255,0.95)' },
        }}
      >
        Log in
      </Button>

      {/* Sidebar (desktop only — mobile leans on the canvas) */}
      {!isNarrow && (
        <Box sx={{ width: workspaceSidebarWidth, flexShrink: 0, height: '100%', ...workspacePanelShellSx }}>
          <DemoSidebar />
        </Box>
      )}

      {/* Right canvas: chat + app panel side by side */}
      <Box
        component="main"
        sx={{
          flex: 1,
          minWidth: 0,
          height: '100%',
          display: 'flex',
          flexDirection: { xs: 'column', md: 'row' },
          gap: isNarrow ? 0 : workspaceCanvasInset.panelGap,
          [`@media (min-width: ${theme.breakpoints.values.lg}px)`]: {
            gap: workspaceCanvasInset.panelGapLg,
          },
        }}
      >
        <Box sx={{ flex: { xs: '0 0 auto', md: '0 0 40%' }, minHeight: 0, height: { xs: '45%', md: '100%' }, ...workspacePanelShellSx }}>
          <DemoChat state={state} />
        </Box>
        <Box sx={{ flex: 1, minWidth: 0, minHeight: 0, height: { xs: '55%', md: '100%' }, ...workspacePanelShellSx }}>
          <DemoAppPanel state={state} />
        </Box>
      </Box>

      {/* Narrating caption — the "explaining parts". A calm floating pill at the
          bottom that swaps text as the movie advances. */}
      <Box
        sx={{
          position: 'absolute',
          bottom: isNarrow ? 12 : 22,
          left: 0,
          right: 0,
          display: 'flex',
          justifyContent: 'center',
          px: 2,
          zIndex: 15,
          pointerEvents: 'none',
        }}
      >
        <Fade in key={state.caption} timeout={400}>
          <Stack
            direction="row"
            alignItems="center"
            spacing={1}
            sx={{
              maxWidth: 640,
              px: 2,
              py: 1,
              borderRadius: 999,
              backgroundColor: 'rgba(20,20,22,0.86)',
              backdropFilter: 'blur(8px)',
              boxShadow: '0 8px 30px rgba(0,0,0,0.18)',
            }}
          >
            <Typography
              sx={{
                fontFamily: SANS,
                fontSize: { xs: '0.78rem', md: '0.85rem' },
                fontWeight: 500,
                color: 'rgba(255,255,255,0.95)',
                textAlign: 'center',
              }}
            >
              {state.caption}
            </Typography>
          </Stack>
        </Fade>
      </Box>
    </Box>
  )
}

export default LandingRoute
