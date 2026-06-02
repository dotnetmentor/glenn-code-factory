import { Box, Stack, Typography } from '@mui/material'
import FolderRoundedIcon from '@mui/icons-material/FolderRounded'
import UnfoldMoreRoundedIcon from '@mui/icons-material/UnfoldMoreRounded'
import { RuntimeState } from '@/api/queries-commands'
import {
  surfaceTokens,
  chromeTokens,
  workspaceFontFamily,
  workspaceChromeHeight,
} from '../../../shared/designTokens'
import { StatusDot } from '../../../shared/primitives'

const SANS = workspaceFontFamily.sans

const PROJECTS = [
  { name: 'barbershop-site', branch: 'main', active: true },
  { name: 'invoice-tool', branch: 'feat/export', active: false },
  { name: 'team-wiki', branch: 'main', active: false },
]

/**
 * Decorative projects + branches navigator that mirrors the real
 * ProjectsBranchesSidebar's shape and tokens, without the live-data coupling.
 */
export function DemoSidebar() {
  return (
    <Box sx={{ display: 'flex', flexDirection: 'column', height: '100%' }}>
      {/* Workspace switcher row — same chrome y-grid. */}
      <Stack
        direction="row"
        alignItems="center"
        spacing={1}
        sx={{ height: workspaceChromeHeight, flexShrink: 0, px: 1.75, borderBottom: `1px solid ${surfaceTokens.hairline}` }}
      >
        <Box sx={{ width: 22, height: 22, borderRadius: 1, backgroundColor: chromeTokens.accentSurface, display: 'grid', placeItems: 'center' }}>
          <Typography sx={{ fontFamily: SANS, fontWeight: 700, fontSize: '0.7rem', color: surfaceTokens.textPrimary }}>G</Typography>
        </Box>
        <Typography noWrap sx={{ fontFamily: SANS, fontWeight: 600, fontSize: '0.875rem', color: surfaceTokens.textPrimary, flex: 1 }}>
          GlennCode
        </Typography>
        <UnfoldMoreRoundedIcon sx={{ fontSize: 15, color: surfaceTokens.textFaint }} />
      </Stack>

      <Box sx={{ flex: 1, overflowY: 'auto', px: 1, py: 1.5 }}>
        <Typography sx={{ fontFamily: SANS, fontSize: '0.68rem', fontWeight: 700, textTransform: 'uppercase', letterSpacing: '0.06em', color: surfaceTokens.textFaint, px: 1, mb: 0.75 }}>
          Projects
        </Typography>
        <Stack spacing={0.25}>
          {PROJECTS.map((p) => (
            <Stack
              key={p.name}
              direction="row"
              alignItems="center"
              spacing={1}
              sx={{
                px: 1, py: 0.85, borderRadius: 1.25,
                backgroundColor: p.active ? chromeTokens.rowActive : 'transparent',
              }}
            >
              <FolderRoundedIcon sx={{ fontSize: 15, color: p.active ? surfaceTokens.textPrimary : surfaceTokens.textFaint }} />
              <Box sx={{ minWidth: 0, flex: 1 }}>
                <Typography noWrap sx={{ fontFamily: SANS, fontSize: '0.82rem', fontWeight: p.active ? 600 : 500, color: p.active ? surfaceTokens.textPrimary : surfaceTokens.textMuted }}>
                  {p.name}
                </Typography>
                <Typography noWrap sx={{ fontFamily: SANS, fontSize: '0.7rem', color: surfaceTokens.textFaint }}>
                  {p.branch}
                </Typography>
              </Box>
              {p.active && <StatusDot state={RuntimeState.Online} size={7} hideTooltip />}
            </Stack>
          ))}
        </Stack>
      </Box>
    </Box>
  )
}
