import { useMediaQuery, Box, Button, Fade, Stack, Typography } from '@mui/material'
import { useTheme } from '@mui/material/styles'
import { useNavigate } from 'react-router-dom'
import ReplayRoundedIcon from '@mui/icons-material/ReplayRounded'
import {
  surfaceTokens,
  workspaceCanvasInset,
  workspacePanelShellSx,
  workspaceSidebarWidth,
  workspaceFontFamily,
} from '../../shared/designTokens'
import { useMovie } from './movie/useMovie'
import type { MovieFocus } from './movie/script'
import { DemoSidebar } from './components/DemoSidebar'
import { DemoChat } from './components/DemoChat'
import { DemoAppPanel } from './components/DemoAppPanel'
import { LiveWaitlistForm } from './components/LiveWaitlistForm'

const SANS = workspaceFontFamily.sans

/**
 * Camera presets. The stage is transformed with `transform-origin: 0 0` so a
 * focus point at fraction (fx, fy) of the stage can be moved to viewport center
 * and scaled by `s` via translate((0.5 - fx*s), (0.5 - fy*s)). Result: the
 * active panel glides to center and enlarges — a cinematic "lean in".
 */
const CAMERA: Record<MovieFocus, { fx: number; fy: number; s: number }> = {
  overview: { fx: 0.5, fy: 0.5, s: 1 },
  chat: { fx: 0.34, fy: 0.5, s: 1.22 },
  app: { fx: 0.74, fy: 0.5, s: 1.16 },
}

function cameraTransform(focus: MovieFocus): string {
  const { fx, fy, s } = CAMERA[focus]
  const tx = (0.5 - fx * s) * 100
  const ty = (0.5 - fy * s) * 100
  return `translate(${tx}%, ${ty}%) scale(${s})`
}

/**
 * Public marketing landing at `/` for logged-out visitors. It looks and feels
 * like the real workspace IDE shell, then auto-plays a one-shot cinematic
 * "movie" of GlennCode building a waitlist landing page. A virtual camera zooms
 * to whatever the system is doing; at the finale every other element fades away
 * and the genuinely-live waitlist form takes center stage.
 *
 * See ./movie/script.ts for the choreography and ./components/LiveWaitlistForm
 * for the one real, wired-to-backend piece.
 */
export function LandingRoute() {
  const theme = useTheme()
  const navigate = useNavigate()
  const isNarrow = useMediaQuery(theme.breakpoints.down('md'))
  const { state, replay } = useMovie()
  const finale = state.atFinale

  // The camera only runs on wide viewports — on mobile the panels stack and a
  // zoom would just clip them. Disabled at the finale (stage is fading out).
  const transform = !isNarrow && !finale ? cameraTransform(state.focus) : 'none'

  return (
    <Box
      sx={{
        position: 'relative',
        height: '100vh',
        '@supports (height: 100dvh)': { height: '100dvh' },
        width: '100vw',
        backgroundColor: surfaceTokens.canvasBg,
        color: surfaceTokens.textPrimary,
        overflow: 'hidden',
      }}
    >
      {/* ── Workspace stage (sidebar + chat + app panel), driven by the camera.
          Fades + recedes at the finale so the waitlist can take over. ── */}
      <Box
        sx={{
          position: 'absolute',
          inset: 0,
          display: 'flex',
          flexDirection: 'row',
          transformOrigin: '0 0',
          transform: finale ? 'scale(1.04)' : transform,
          opacity: finale ? 0 : 1,
          filter: finale ? 'blur(6px)' : 'none',
          pointerEvents: finale ? 'none' : 'auto',
          transition:
            'transform 1100ms cubic-bezier(.22,.61,.36,1), opacity 700ms ease, filter 700ms ease',
          ...(!isNarrow && {
            p: workspaceCanvasInset.desktopPadding,
            gap: workspaceCanvasInset.panelGap,
            [`@media (min-width: ${theme.breakpoints.values.lg}px)`]: {
              gap: workspaceCanvasInset.panelGapLg,
            },
          }),
        }}
      >
        {/* Sidebar — dims slightly when the camera is leaning into a panel. */}
        {!isNarrow && (
          <Box
            sx={{
              width: workspaceSidebarWidth,
              flexShrink: 0,
              height: '100%',
              opacity: state.focus === 'overview' ? 1 : 0.5,
              transition: 'opacity 600ms ease',
              ...workspacePanelShellSx,
            }}
          >
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
          <Box
            sx={{
              flex: { xs: '0 0 auto', md: '0 0 40%' },
              minHeight: 0,
              height: { xs: '45%', md: '100%' },
              opacity: state.focus === 'app' ? 0.45 : 1,
              transition: 'opacity 600ms ease',
              ...workspacePanelShellSx,
            }}
          >
            <DemoChat state={state} />
          </Box>
          <Box
            sx={{
              flex: 1,
              minWidth: 0,
              minHeight: 0,
              height: { xs: '55%', md: '100%' },
              opacity: state.focus === 'chat' ? 0.45 : 1,
              transition: 'opacity 600ms ease',
              ...workspacePanelShellSx,
            }}
          >
            <DemoAppPanel state={state} />
          </Box>
        </Box>
      </Box>

      {/* ── Finale hero — full focus on the live waitlist. Fades in over the
          receding stage; the form is the conversion. ── */}
      <Box
        sx={{
          position: 'absolute',
          inset: 0,
          display: 'flex',
          flexDirection: 'column',
          alignItems: 'center',
          justifyContent: 'center',
          px: 3,
          opacity: finale ? 1 : 0,
          transform: finale ? 'scale(1)' : 'scale(0.96)',
          pointerEvents: finale ? 'auto' : 'none',
          transition: 'opacity 800ms ease 250ms, transform 800ms cubic-bezier(.22,.61,.36,1) 250ms',
        }}
      >
        <Box
          sx={{
            width: '100%',
            maxWidth: 480,
            ...workspacePanelShellSx,
            boxShadow: '0 30px 80px -20px rgba(0,0,0,0.25), 0 4px 12px rgba(0,0,0,0.06)',
          }}
        >
          <LiveWaitlistForm />
        </Box>

        <Button
          onClick={replay}
          startIcon={<ReplayRoundedIcon sx={{ fontSize: 16 }} />}
          sx={{
            mt: 3,
            textTransform: 'none',
            fontFamily: SANS,
            fontSize: '0.82rem',
            fontWeight: 500,
            color: surfaceTokens.textFaint,
            '&:hover': { color: surfaceTokens.textPrimary, backgroundColor: 'transparent' },
          }}
        >
          Run the demo again
        </Button>
      </Box>

      {/* Log in — the one piece of real chrome, always present. */}
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

      {/* Narrating caption — the "explaining parts". Hidden at the finale so the
          waitlist owns the screen. */}
      <Box
        sx={{
          position: 'absolute',
          bottom: isNarrow ? 12 : 26,
          left: 0,
          right: 0,
          display: 'flex',
          justifyContent: 'center',
          px: 2,
          zIndex: 15,
          pointerEvents: 'none',
          opacity: finale ? 0 : 1,
          transition: 'opacity 500ms ease',
        }}
      >
        <Fade in key={state.caption} timeout={450}>
          <Stack
            direction="row"
            alignItems="center"
            spacing={1}
            sx={{
              maxWidth: 680,
              px: 2.25,
              py: 1.1,
              borderRadius: 999,
              backgroundColor: 'rgba(20,20,22,0.86)',
              backdropFilter: 'blur(8px)',
              boxShadow: '0 8px 30px rgba(0,0,0,0.18)',
            }}
          >
            <Typography
              sx={{
                fontFamily: SANS,
                fontSize: { xs: '0.78rem', md: '0.875rem' },
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
