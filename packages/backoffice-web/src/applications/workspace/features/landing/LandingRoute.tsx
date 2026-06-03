import { useMediaQuery, Box, Button, Fade, Typography } from '@mui/material'
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
import { CINEMATIC_SERIF, type MovieFocus } from './movie/script'
import { DemoSidebar } from './components/DemoSidebar'
import { DemoChat } from './components/DemoChat'
import { DemoAppPanel } from './components/DemoAppPanel'

const SANS = workspaceFontFamily.sans

/** Deep cinema surround that frames the bright product "screen". */
const SURROUND = '#111114'

/**
 * Camera presets. The stage is transformed with `transform-origin: 0 0` so a
 * focus point at fraction (fx, fy) of the stage can be moved to its center and
 * scaled by `s`. Result: the active panel glides to center and enlarges.
 */
const CAMERA: Record<MovieFocus, { fx: number; fy: number; s: number }> = {
  overview: { fx: 0.5, fy: 0.5, s: 1 },
  chat: { fx: 0.37, fy: 0.5, s: 1.3 },
  app: { fx: 0.75, fy: 0.5, s: 1.18 },
}

function cameraTransform(focus: MovieFocus): string {
  const { fx, fy, s } = CAMERA[focus]
  const tx = (0.5 - fx * s) * 100
  const ty = (0.5 - fy * s) * 100
  return `translate(${tx}%, ${ty}%) scale(${s})`
}

/**
 * Public marketing landing at `/` for logged-out visitors, staged like a short
 * film: the real workspace IDE sits in an inset, bordered "screen" within a dark
 * cinema surround, and a one-shot movie of GlennCode building a waitlist landing
 * page plays inside it. A virtual camera zooms to whatever the system is doing,
 * narrated by large subtitle-style captions beneath the screen.
 *
 * At the finale the sidebar, chat, tab strip and browser chrome melt away while
 * the **same live preview document** (the wired-to-backend waitlist form) expands
 * and centers within the screen — one continuous element, not a separate hero.
 */
export function LandingRoute() {
  const theme = useTheme()
  const navigate = useNavigate()
  const isNarrow = useMediaQuery(theme.breakpoints.down('md'))
  const { state, replay } = useMovie()
  const finale = state.atFinale

  // Camera runs on wide viewports only; at the finale it eases back to neutral
  // (scale 1) so the expanding preview maps to the full screen.
  const transform = !isNarrow && !finale ? cameraTransform(state.focus) : 'none'
  const shellOrNone = (bare: boolean) => (bare ? {} : workspacePanelShellSx)

  return (
    <Box
      sx={{
        position: 'relative',
        height: '100vh',
        '@supports (height: 100dvh)': { height: '100dvh' },
        width: '100vw',
        backgroundColor: SURROUND,
        display: 'flex',
        flexDirection: 'column',
        alignItems: 'center',
        justifyContent: 'center',
        gap: { xs: 1.5, md: 2.5 },
        px: { xs: 1.5, md: 4 },
        py: { xs: 2, md: 4 },
        overflow: 'hidden',
      }}
    >
      {/* ── The "screen": inset, bordered, rounded frame holding the workspace ── */}
      <Box
        sx={{
          position: 'relative',
          width: '100%',
          maxWidth: 1280,
          height: { xs: '64vh', md: 'min(74vh, 760px)' },
          flexShrink: 0,
          borderRadius: { xs: 2, md: 3 },
          overflow: 'hidden',
          backgroundColor: surfaceTokens.canvasBg,
          border: '1px solid rgba(255,255,255,0.10)',
          boxShadow: '0 40px 120px -30px rgba(0,0,0,0.7), 0 8px 24px rgba(0,0,0,0.4)',
        }}
      >
        {/* Workspace stage — the camera transforms it; at the finale the camera
            eases to neutral and side panels collapse, leaving the preview. */}
        <Box
          sx={{
            position: 'absolute',
            inset: 0,
            display: 'flex',
            flexDirection: 'row',
            transformOrigin: '0 0',
            transform,
            transition: 'transform 1500ms cubic-bezier(.22,.61,.36,1)',
            ...(!isNarrow && {
              p: finale ? 0 : workspaceCanvasInset.desktopPadding,
              gap: finale ? 0 : workspaceCanvasInset.panelGap,
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
                transition:
                  'flex-basis 1400ms cubic-bezier(.22,.61,.36,1), width 1400ms cubic-bezier(.22,.61,.36,1), height 1400ms ease, opacity 700ms ease',
                ...shellOrNone(finale),
              }}
            >
              <DemoChat state={state} />
            </Box>

            {/* App panel — grows to fill at the finale; `bare` strips its chrome. */}
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

        {/* Log in — the one piece of real chrome, pinned inside the screen. */}
        <Button
          onClick={() => navigate('/signin')}
          sx={{
            position: 'absolute',
            top: isNarrow ? 10 : 16,
            right: isNarrow ? 10 : 18,
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
      </Box>

      {/* ── Cinematic subtitle / narration beneath the screen ── */}
      <Box
        sx={{
          height: { xs: 56, md: 76 },
          flexShrink: 0,
          display: 'flex',
          alignItems: 'center',
          justifyContent: 'center',
          px: 2,
          textAlign: 'center',
        }}
      >
        {finale ? (
          <Button
            onClick={replay}
            startIcon={<ReplayRoundedIcon sx={{ fontSize: 18 }} />}
            sx={{
              textTransform: 'none',
              fontFamily: SANS,
              fontSize: { xs: '0.9rem', md: '1rem' },
              fontWeight: 500,
              color: 'rgba(255,255,255,0.7)',
              '&:hover': { color: '#fff', backgroundColor: 'rgba(255,255,255,0.06)' },
            }}
          >
            Run the demo again
          </Button>
        ) : (
          <Fade in key={state.caption} timeout={500}>
            <Typography
              sx={{
                fontFamily: CINEMATIC_SERIF,
                fontSize: { xs: '1.25rem', md: '1.7rem' },
                fontWeight: 400,
                letterSpacing: '0.005em',
                lineHeight: 1.35,
                color: 'rgba(255,255,255,0.96)',
                maxWidth: 860,
              }}
            >
              {state.caption}
            </Typography>
          </Fade>
        )}
      </Box>
    </Box>
  )
}

export default LandingRoute
