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
 * to whatever the system is doing.
 *
 * At the finale the sidebar, chat, tab strip and browser chrome melt away while
 * the **same live preview document** (the wired-to-backend waitlist form) expands
 * and centers — one continuous element, not a separate hero. See ./movie/script.ts
 * for the choreography and ./components/LiveWaitlistForm for the real piece.
 */
export function LandingRoute() {
  const theme = useTheme()
  const navigate = useNavigate()
  const isNarrow = useMediaQuery(theme.breakpoints.down('md'))
  const { state, replay } = useMovie()
  const finale = state.atFinale

  // Camera runs on wide viewports only; at the finale it eases back to neutral
  // (scale 1) so the expanding preview panel maps to the real viewport.
  const transform = !isNarrow && !finale ? cameraTransform(state.focus) : 'none'

  const shellOrNone = (on: boolean) => (on ? {} : workspacePanelShellSx)

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
      {/* ── Workspace stage. The camera transforms it; at the finale the camera
          eases to neutral and the side panels collapse, leaving the preview. ── */}
      <Box
        sx={{
          position: 'absolute',
          inset: 0,
          display: 'flex',
          flexDirection: 'row',
          transformOrigin: '0 0',
          transform,
          // Slower, more luxurious camera moves (pan to spec + finale ease-out).
          transition: 'transform 1500ms cubic-bezier(.22,.61,.36,1)',
          ...(!isNarrow && {
            p: finale ? 0 : workspaceCanvasInset.desktopPadding,
            gap: finale ? 0 : workspaceCanvasInset.panelGap,
            [`@media (min-width: ${theme.breakpoints.values.lg}px)`]: {
              gap: finale ? 0 : workspaceCanvasInset.panelGapLg,
            },
          }),
        }}
      >
        {/* Sidebar — collapses to nothing at the finale. */}
        {!isNarrow && (
          <Box
            sx={{
              width: finale ? 0 : workspaceSidebarWidth,
              flexShrink: 0,
              height: '100%',
              opacity: finale ? 0 : state.focus === 'overview' ? 1 : 0.5,
              overflow: 'hidden',
              transition: 'width 1400ms cubic-bezier(.22,.61,.36,1), opacity 700ms ease',
              ...shellOrNone(finale),
            }}
          >
            <DemoSidebar />
          </Box>
        )}

        {/* Right canvas: chat + app panel */}
        <Box
          component="main"
          sx={{
            flex: 1,
            minWidth: 0,
            height: '100%',
            display: 'flex',
            flexDirection: { xs: 'column', md: 'row' },
            gap: isNarrow || finale ? 0 : workspaceCanvasInset.panelGap,
            [`@media (min-width: ${theme.breakpoints.values.lg}px)`]: {
              gap: finale ? 0 : workspaceCanvasInset.panelGapLg,
            },
          }}
        >
          {/* Chat — collapses to nothing at the finale. */}
          <Box
            sx={{
              flexGrow: 0,
              flexShrink: finale ? 1 : 0,
              flexBasis: finale ? 0 : { xs: 'auto', md: '40%' },
              width: finale ? 0 : undefined,
              minHeight: 0,
              height: { xs: finale ? 0 : '45%', md: '100%' },
              opacity: finale ? 0 : state.focus === 'app' ? 0.45 : 1,
              overflow: 'hidden',
              transition: 'flex-basis 1400ms cubic-bezier(.22,.61,.36,1), width 1400ms cubic-bezier(.22,.61,.36,1), height 1400ms ease, opacity 700ms ease',
              ...shellOrNone(finale),
            }}
          >
            <DemoChat state={state} />
          </Box>

          {/* App panel — grows to fill at the finale; `bare` strips its chrome so
              the live document is all that remains. */}
          <Box
            sx={{
              flex: 1,
              minWidth: 0,
              minHeight: 0,
              height: { xs: finale ? '100%' : '55%', md: '100%' },
              opacity: !finale && state.focus === 'chat' ? 0.45 : 1,
              transition: 'opacity 700ms ease',
              ...shellOrNone(finale),
            }}
          >
            <DemoAppPanel state={state} bare={finale} />
          </Box>
        </Box>
      </Box>

      {/* Run the demo again — fades in at the finale. */}
      <Box
        sx={{
          position: 'absolute',
          bottom: isNarrow ? 16 : 28,
          left: 0,
          right: 0,
          display: 'flex',
          justifyContent: 'center',
          zIndex: 18,
          opacity: finale ? 1 : 0,
          pointerEvents: finale ? 'auto' : 'none',
          transition: 'opacity 700ms ease 600ms',
        }}
      >
        <Button
          onClick={replay}
          startIcon={<ReplayRoundedIcon sx={{ fontSize: 16 }} />}
          sx={{
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

      {/* Narrating caption — hidden at the finale so the waitlist owns the screen. */}
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
