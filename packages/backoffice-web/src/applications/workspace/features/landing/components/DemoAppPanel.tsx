import { Box, Stack, Typography } from '@mui/material'
import CheckRoundedIcon from '@mui/icons-material/CheckRounded'
import LockRoundedIcon from '@mui/icons-material/LockRounded'
import RefreshRoundedIcon from '@mui/icons-material/RefreshRounded'
import {
  surfaceTokens,
  chromeTokens,
  workspaceColors,
  workspaceFontFamily,
  semanticTokens,
  workspaceChromeHeight,
} from '../../../shared/designTokens'
import type { AppTab, KanbanColumn, MovieState } from '../movie/script'
import { KANBAN_CARDS, SPEC_TITLE } from '../movie/script'
import { LiveWaitlistForm } from './LiveWaitlistForm'

const SANS = workspaceFontFamily.sans
const MONO = workspaceFontFamily.mono

const TABS: Array<{ id: AppTab; label: string }> = [
  { id: 'spec', label: 'Spec' },
  { id: 'kanban', label: 'Tasks' },
  { id: 'preview', label: 'Preview' },
]

function TabStrip({ active }: { active: AppTab }) {
  return (
    <Stack
      direction="row"
      alignItems="center"
      spacing={0.5}
      sx={{
        height: workspaceChromeHeight,
        flexShrink: 0,
        px: 1.5,
        borderBottom: `1px solid ${surfaceTokens.hairline}`,
      }}
    >
      {TABS.map((t) => {
        const on = t.id === active
        return (
          <Box
            key={t.id}
            sx={{
              px: 1.5,
              py: 0.6,
              borderRadius: 1.25,
              fontFamily: SANS,
              fontSize: '0.8rem',
              fontWeight: 600,
              color: on ? surfaceTokens.textPrimary : surfaceTokens.textFaint,
              backgroundColor: on ? chromeTokens.accentSurface : 'transparent',
              transition: 'background-color 200ms ease, color 200ms ease',
            }}
          >
            {t.label}
          </Box>
        )
      })}
    </Stack>
  )
}

function SpecView({ state }: { state: MovieState }) {
  return (
    <Box sx={{ p: 3, overflowY: 'auto', height: '100%' }}>
      <Typography sx={{ fontFamily: SANS, fontWeight: 700, fontSize: '1.1rem', letterSpacing: '-0.01em', color: surfaceTokens.textPrimary }}>
        {SPEC_TITLE}
      </Typography>
      <Typography sx={{ fontFamily: SANS, fontSize: '0.8rem', color: surfaceTokens.textFaint, mt: 0.25, mb: 2 }}>
        Proposed scope
      </Typography>
      <Stack spacing={1.25}>
        {state.specLines.map((line, i) => (
          <Stack
            key={i}
            direction="row"
            spacing={1.25}
            alignItems="flex-start"
            sx={{ animation: 'wsFade 320ms ease', '@keyframes wsFade': { from: { opacity: 0, transform: 'translateY(4px)' }, to: { opacity: 1 } } }}
          >
            <Box sx={{ mt: '7px', width: 5, height: 5, borderRadius: '50%', backgroundColor: surfaceTokens.textFaint, flexShrink: 0 }} />
            <Typography sx={{ fontFamily: SANS, fontSize: '0.9rem', color: surfaceTokens.textPrimary, lineHeight: 1.5 }}>
              {line}
            </Typography>
          </Stack>
        ))}
      </Stack>
      {state.specAccepted && (
        <Stack
          direction="row"
          alignItems="center"
          spacing={1}
          sx={{
            mt: 3,
            px: 1.5,
            py: 1,
            borderRadius: 1.5,
            width: 'fit-content',
            backgroundColor: semanticTokens.successSoft,
            animation: 'wsFade 320ms ease',
          }}
        >
          <CheckRoundedIcon sx={{ fontSize: 16, color: semanticTokens.success }} />
          <Typography sx={{ fontFamily: SANS, fontSize: '0.85rem', fontWeight: 600, color: surfaceTokens.textPrimary }}>
            Accepted
          </Typography>
        </Stack>
      )}
    </Box>
  )
}

const COLUMNS: Array<{ id: KanbanColumn; label: string }> = [
  { id: 'backlog', label: 'Backlog' },
  { id: 'doing', label: 'In progress' },
  { id: 'done', label: 'Done' },
]

function KanbanView({ state }: { state: MovieState }) {
  return (
    <Box sx={{ p: 2, height: '100%', overflowY: 'auto' }}>
      <Stack direction="row" spacing={1.5} sx={{ height: '100%' }} alignItems="stretch">
        {COLUMNS.map((col) => {
          const cards = KANBAN_CARDS.filter((c) => state.kanban[c.id] === col.id)
          return (
            <Box key={col.id} sx={{ flex: 1, minWidth: 0 }}>
              <Typography sx={{ fontFamily: SANS, fontSize: '0.7rem', fontWeight: 700, textTransform: 'uppercase', letterSpacing: '0.06em', color: surfaceTokens.textFaint, mb: 1, px: 0.5 }}>
                {col.label}
              </Typography>
              <Stack spacing={1}>
                {cards.map((c) => {
                  const done = col.id === 'done'
                  return (
                    <Stack
                      key={c.id}
                      direction="row"
                      alignItems="center"
                      spacing={1}
                      sx={{
                        px: 1.25,
                        py: 1,
                        borderRadius: 1.25,
                        backgroundColor: surfaceTokens.surface,
                        border: `1px solid ${surfaceTokens.hairline}`,
                        animation: 'wsPop 260ms cubic-bezier(.2,.7,.2,1)',
                        '@keyframes wsPop': { from: { opacity: 0, transform: 'scale(.96)' }, to: { opacity: 1, transform: 'scale(1)' } },
                      }}
                    >
                      <Box
                        sx={{
                          width: 14, height: 14, borderRadius: '50%', flexShrink: 0,
                          display: 'grid', placeItems: 'center',
                          backgroundColor: done ? semanticTokens.success : 'transparent',
                          border: done ? 'none' : `1.5px solid ${col.id === 'doing' ? semanticTokens.warning : surfaceTokens.hairlineStrong}`,
                        }}
                      >
                        {done && <CheckRoundedIcon sx={{ fontSize: 10, color: '#fff' }} />}
                      </Box>
                      <Typography sx={{ fontFamily: SANS, fontSize: '0.82rem', color: surfaceTokens.textPrimary }}>
                        {c.title}
                      </Typography>
                    </Stack>
                  )
                })}
              </Stack>
            </Box>
          )
        })}
      </Stack>
    </Box>
  )
}

function PreviewView({ state }: { state: MovieState }) {
  return (
    <Box sx={{ display: 'flex', flexDirection: 'column', height: '100%', backgroundColor: workspaceColors.canvasBg }}>
      {/* Fake browser chrome — sells "this is a real running app". */}
      <Stack direction="row" alignItems="center" spacing={1} sx={{ px: 1.5, py: 1, flexShrink: 0 }}>
        <RefreshRoundedIcon sx={{ fontSize: 14, color: surfaceTokens.textFaint }} />
        <Stack
          direction="row"
          alignItems="center"
          spacing={0.75}
          sx={{ flex: 1, px: 1.25, py: 0.5, borderRadius: 1, backgroundColor: workspaceColors.chipBg, border: `1px solid ${surfaceTokens.hairline}` }}
        >
          <LockRoundedIcon sx={{ fontSize: 11, color: surfaceTokens.textFaint }} />
          <Typography sx={{ fontFamily: MONO, fontSize: '0.72rem', color: surfaceTokens.textMuted }}>
            your-app.sandbox.glenn.dev
          </Typography>
        </Stack>
      </Stack>

      {/* Viewport */}
      <Box sx={{ flex: 1, overflowY: 'auto', display: 'grid', placeItems: 'center', borderTop: `1px solid ${surfaceTokens.hairline}` }}>
        {state.preview === 'live' ? (
          <LiveWaitlistForm />
        ) : state.preview === 'building' ? (
          <Stack spacing={1.5} sx={{ width: '100%', maxWidth: 360, px: 3 }}>
            {[0, 1, 2].map((i) => (
              <Box
                key={i}
                sx={{
                  height: i === 0 ? 28 : 40,
                  borderRadius: 1,
                  backgroundColor: workspaceColors.chipBg,
                  animation: 'wsShimmer 1.1s ease-in-out infinite',
                  animationDelay: `${i * 0.15}s`,
                  '@keyframes wsShimmer': { '0%,100%': { opacity: 0.5 }, '50%': { opacity: 1 } },
                }}
              />
            ))}
            <Typography sx={{ fontFamily: MONO, fontSize: '0.72rem', color: surfaceTokens.textFaint, textAlign: 'center', mt: 1 }}>
              building & testing…
            </Typography>
          </Stack>
        ) : (
          <Typography sx={{ fontFamily: MONO, fontSize: '0.78rem', color: surfaceTokens.textFaint }}>
            waiting for the first build…
          </Typography>
        )}
      </Box>
    </Box>
  )
}

/**
 * The right-side app panel — mirrors the real workspace AppContainer's tabbed
 * Spec / Tasks / Preview surfaces, scripted by {@link MovieState}. Only the
 * Preview tab's "live" state mounts a real, interactive component.
 */
export function DemoAppPanel({ state }: { state: MovieState }) {
  return (
    <Box sx={{ display: 'flex', flexDirection: 'column', height: '100%', minWidth: 0 }}>
      <TabStrip active={state.activeTab} />
      <Box sx={{ flex: 1, minHeight: 0 }}>
        {state.activeTab === 'spec' && <SpecView state={state} />}
        {state.activeTab === 'kanban' && <KanbanView state={state} />}
        {state.activeTab === 'preview' && <PreviewView state={state} />}
      </Box>
    </Box>
  )
}
