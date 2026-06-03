import { Box, Stack, Typography } from '@mui/material'
import CheckRoundedIcon from '@mui/icons-material/CheckRounded'
import DescriptionOutlinedIcon from '@mui/icons-material/DescriptionOutlined'
import ViewKanbanOutlinedIcon from '@mui/icons-material/ViewKanbanOutlined'
import PublicOutlinedIcon from '@mui/icons-material/PublicOutlined'
import DnsOutlinedIcon from '@mui/icons-material/DnsOutlined'
import RefreshOutlinedIcon from '@mui/icons-material/RefreshOutlined'
import DesktopWindowsOutlinedIcon from '@mui/icons-material/DesktopWindowsOutlined'
import TabletMacOutlinedIcon from '@mui/icons-material/TabletMacOutlined'
import PhoneIphoneOutlinedIcon from '@mui/icons-material/PhoneIphoneOutlined'
import OpenInNewOutlinedIcon from '@mui/icons-material/OpenInNewOutlined'
import ContentCopyOutlinedIcon from '@mui/icons-material/ContentCopyOutlined'
import {
  surfaceTokens,
  workspaceColors,
  workspaceFontFamily,
  semanticTokens,
  workspaceChromeHeight,
} from '../../../shared/designTokens'
import { SegmentedTabs, StatusDot } from '../../../shared/primitives'
import { RuntimeState } from '@/api/queries-commands'
import type { AppTab, KanbanColumn, MovieState } from '../movie/script'
import { KANBAN_CARDS, SPEC_TITLE } from '../movie/script'
import { LiveWaitlistForm } from './LiveWaitlistForm'

const SANS = workspaceFontFamily.sans
const MONO = workspaceFontFamily.mono

const TAB_ITEMS = [
  { id: 'services' as const, label: 'Setup', icon: DnsOutlinedIcon },
  { id: 'spec' as const, label: 'Spec', icon: DescriptionOutlinedIcon },
  { id: 'kanban' as const, label: 'Tasks', icon: ViewKanbanOutlinedIcon },
  { id: 'preview' as const, label: 'Preview', icon: PublicOutlinedIcon },
]

function TabStrip({ active }: { active: AppTab }) {
  return (
    <Stack
      direction="row"
      alignItems="center"
      sx={{
        height: workspaceChromeHeight,
        flexShrink: 0,
        px: 1.25,
        borderBottom: `1px solid ${surfaceTokens.hairline}`,
      }}
    >
      {/* Real workspace primitive — the movie drives `value`, so onChange is a no-op. */}
      <SegmentedTabs
        value={active === 'chat' ? 'preview' : active}
        onChange={() => {}}
        items={TAB_ITEMS}
        ariaLabel="App view"
      />
    </Stack>
  )
}

function SpecView({ state }: { state: MovieState }) {
  return (
    <Box sx={{ height: '100%', display: 'flex', flexDirection: 'column', justifyContent: 'center', p: 3, overflowY: 'auto' }}>
      <Box sx={{ maxWidth: 520, mx: 'auto', width: '100%' }}>
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
    </Box>
  )
}

/** Runtime-spec beat: detected services boot, then come online in the sandbox. */
function ServicesView({ state }: { state: MovieState }) {
  return (
    <Box sx={{ height: '100%', display: 'flex', flexDirection: 'column', justifyContent: 'center', p: 3, overflowY: 'auto' }}>
      <Box sx={{ maxWidth: 460, mx: 'auto', width: '100%' }}>
        <Typography sx={{ fontFamily: SANS, fontWeight: 700, fontSize: '1.1rem', letterSpacing: '-0.01em', color: surfaceTokens.textPrimary }}>
          Sandbox services
        </Typography>
        <Stack direction="row" alignItems="center" spacing={1} sx={{ mt: 0.4, mb: 2 }}>
          <Typography sx={{ fontFamily: SANS, fontSize: '0.8rem', color: surfaceTokens.textFaint }}>
            Detected from your repo
          </Typography>
          <Box sx={{ width: 3, height: 3, borderRadius: '50%', backgroundColor: surfaceTokens.textFaint }} />
          <Typography sx={{ fontFamily: MONO, fontSize: '0.72rem', color: surfaceTokens.textMuted }}>
            Fly.io micro-VM · boots in seconds
          </Typography>
        </Stack>
        <Stack spacing={1}>
          {state.services.map((s) => (
            <Stack
              key={s.id}
              direction="row"
              alignItems="center"
              spacing={1.25}
              sx={{
                px: 1.5,
                py: 1.25,
                borderRadius: 1.5,
                backgroundColor: surfaceTokens.surface,
                border: `1px solid ${surfaceTokens.hairline}`,
                animation: 'wsFade 320ms ease',
                '@keyframes wsFade': { from: { opacity: 0, transform: 'translateY(4px)' }, to: { opacity: 1 } },
              }}
            >
              <StatusDot state={s.online ? RuntimeState.Online : RuntimeState.Booting} size={9} hideTooltip />
              <Box sx={{ flex: 1, minWidth: 0 }}>
                <Typography sx={{ fontFamily: SANS, fontSize: '0.9rem', fontWeight: 600, color: surfaceTokens.textPrimary }}>
                  {s.label}
                </Typography>
                <Typography sx={{ fontFamily: MONO, fontSize: '0.72rem', color: surfaceTokens.textFaint }}>
                  {s.detail}
                </Typography>
              </Box>
              <Typography sx={{ fontFamily: MONO, fontSize: '0.72rem', color: s.online ? semanticTokens.success : surfaceTokens.textMuted }}>
                {s.online ? 'online' : 'starting…'}
              </Typography>
            </Stack>
          ))}
        </Stack>
      </Box>
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
    <Box sx={{ p: 2, height: '100%', display: 'flex', alignItems: 'center', justifyContent: 'center', overflowY: 'auto' }}>
      <Stack direction="row" spacing={1.25} sx={{ width: '100%', maxWidth: 640, mx: 'auto' }} alignItems="stretch">
        {COLUMNS.map((col) => {
          const cards = KANBAN_CARDS.filter((c) => state.kanban[c.id] === col.id)
          return (
            <Box key={col.id} sx={{ flex: 1, minWidth: 0 }}>
              <Typography sx={{ fontFamily: SANS, fontSize: '0.7rem', fontWeight: 700, textTransform: 'uppercase', letterSpacing: '0.06em', color: surfaceTokens.textFaint, mb: 0.75, px: 0.5 }}>
                {col.label}
              </Typography>
              <Stack spacing={1} sx={{ p: 1, minHeight: 168, borderRadius: 1.5, backgroundColor: workspaceColors.chipBg, border: `1px solid ${surfaceTokens.hairline}` }}>
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

const DEVICE_ICONS = [DesktopWindowsOutlinedIcon, TabletMacOutlinedIcon, PhoneIphoneOutlinedIcon]

/** Faithful recreation of the real PreviewChrome bar (reload · URL pill · device toggle · open). */
function PreviewChromeBar() {
  return (
    <Stack
      direction="row"
      alignItems="center"
      spacing={1}
      sx={{ height: workspaceChromeHeight, flexShrink: 0, px: '10px', borderBottom: `1px solid ${surfaceTokens.hairline}` }}
    >
      <RefreshOutlinedIcon sx={{ fontSize: 14, color: surfaceTokens.textMuted }} />
      <Stack
        direction="row"
        alignItems="center"
        spacing={1}
        sx={{ flex: 1, minWidth: 0, height: 28, pl: '10px', pr: '8px', borderRadius: 999, backgroundColor: workspaceColors.chipBg, border: `1px solid ${surfaceTokens.hairline}` }}
      >
        <PublicOutlinedIcon sx={{ fontSize: 11, color: surfaceTokens.textFaint, flexShrink: 0 }} />
        <Box component="span" sx={{ flex: 1, minWidth: 0, fontSize: '0.75rem', fontFamily: MONO, color: surfaceTokens.textPrimary, letterSpacing: '-0.005em', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
          your-app.sandbox.glenn.dev
        </Box>
        <ContentCopyOutlinedIcon sx={{ fontSize: 11, color: surfaceTokens.textMuted, flexShrink: 0 }} />
      </Stack>
      <Stack direction="row" alignItems="center" spacing="1px" sx={{ p: '2px', borderRadius: '7px', border: `1px solid ${surfaceTokens.hairline}`, backgroundColor: workspaceColors.chipBg, flexShrink: 0 }}>
        {DEVICE_ICONS.map((Icon, i) => (
          <Box
            key={i}
            sx={{
              width: 24, height: 22, display: 'grid', placeItems: 'center', borderRadius: '5px',
              backgroundColor: i === 0 ? surfaceTokens.surface : 'transparent',
              color: i === 0 ? surfaceTokens.textPrimary : surfaceTokens.textMuted,
            }}
          >
            <Icon sx={{ fontSize: 12 }} />
          </Box>
        ))}
      </Stack>
      <Box sx={{ width: '1px', height: 18, backgroundColor: surfaceTokens.hairline, flexShrink: 0, mx: 0.25 }} />
      <OpenInNewOutlinedIcon sx={{ fontSize: 13, color: surfaceTokens.textMuted }} />
    </Stack>
  )
}

function PreviewView({ state, bare = false }: { state: MovieState; bare?: boolean }) {
  return (
    <Box sx={{ display: 'flex', flexDirection: 'column', height: '100%', backgroundColor: workspaceColors.canvasBg }}>
      {/* Browser chrome collapses away at the finale so the live document stands alone. */}
      <Box sx={{ flexShrink: 0, maxHeight: bare ? 0 : workspaceChromeHeight, opacity: bare ? 0 : 1, overflow: 'hidden', transition: 'max-height 900ms ease, opacity 500ms ease' }}>
        <PreviewChromeBar />
      </Box>
      {/* Viewport */}
      <Box sx={{ flex: 1, overflowY: 'auto', display: 'grid', placeItems: 'center' }}>
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
 * Spec / Tasks / Preview surfaces (real SegmentedTabs + PreviewChrome recipe),
 * scripted by {@link MovieState}. Only the Preview tab's "live" state mounts a
 * real, interactive component.
 */
export function DemoAppPanel({ state, bare = false }: { state: MovieState; bare?: boolean }) {
  // At the finale (`bare`) the tab strip collapses and the live preview document
  // is all that remains — the same mounted form, now standing on its own.
  const showPreview = bare || state.activeTab === 'preview'
  return (
    <Box sx={{ display: 'flex', flexDirection: 'column', height: '100%', minWidth: 0 }}>
      <Box sx={{ flexShrink: 0, maxHeight: bare ? 0 : workspaceChromeHeight, opacity: bare ? 0 : 1, overflow: 'hidden', transition: 'max-height 900ms ease, opacity 500ms ease' }}>
        <TabStrip active={state.activeTab} />
      </Box>
      <Box sx={{ flex: 1, minHeight: 0 }}>
        {!bare && state.activeTab === 'services' && <ServicesView state={state} />}
        {!bare && state.activeTab === 'spec' && <SpecView state={state} />}
        {!bare && state.activeTab === 'kanban' && <KanbanView state={state} />}
        {showPreview && <PreviewView state={state} bare={bare} />}
      </Box>
    </Box>
  )
}
