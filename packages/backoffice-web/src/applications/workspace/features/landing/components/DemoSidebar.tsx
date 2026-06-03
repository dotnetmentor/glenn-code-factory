import { Box, Stack, Typography } from '@mui/material'
import ExpandMoreIcon from '@mui/icons-material/ExpandMore'
import UnfoldMoreRoundedIcon from '@mui/icons-material/UnfoldMoreRounded'
import { RuntimeState } from '@/api/queries-commands'
import {
  surfaceTokens,
  chromeTokens,
  semanticTokens,
  workspaceFontFamily,
  workspaceChromeHeight,
} from '../../../shared/designTokens'
import { StatusDot } from '../../../shared/primitives'

const SANS = workspaceFontFamily.sans
const MONO = workspaceFontFamily.mono

type BranchKind = 'running' | 'needsAction' | 'idle'

interface DemoBranch {
  name: string
  time: string
  kind: BranchKind
  active?: boolean
  /** Unread terminal-turn dot for calm idle rows. */
  unread?: 'idle' | 'failed'
}

interface DemoProject {
  name: string
  branch: DemoBranch
}

interface DemoSection {
  key: string
  label: string
  color?: string
  projects: DemoProject[]
}

/**
 * Mirrors the real ProjectsBranchesSidebar: branches bucketed into
 * Needs Action / Running / Idle sections, with the same status-dot language
 * (rust = needs action, amber pulse = running, calm dots when idle). The active
 * branch (barbershop-site · main — the one the movie is building) carries the
 * accent rail.
 */
const SECTIONS: DemoSection[] = [
  {
    key: 'needsAction',
    label: 'Needs Action',
    color: semanticTokens.error,
    projects: [{ name: 'invoice-tool', branch: { name: 'fix/auth-redirect', time: '2h', kind: 'needsAction' } }],
  },
  {
    key: 'running',
    label: 'Running',
    projects: [{ name: 'barbershop-site', branch: { name: 'main', time: 'now', kind: 'running', active: true } }],
  },
  {
    key: 'idle',
    label: 'Idle',
    projects: [
      { name: 'team-wiki', branch: { name: 'main', time: '1d', kind: 'idle', unread: 'idle' } },
      { name: 'api-gateway', branch: { name: 'develop', time: '4d', kind: 'idle' } },
    ],
  },
]

function SectionHeader({ label, count, color }: { label: string; count: number; color?: string }) {
  const c = color ?? surfaceTokens.textFaint
  return (
    <Box sx={{ pl: 2, pr: 1.5, pt: 2, pb: 0.75, display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: 1 }}>
      <Typography sx={{ fontFamily: SANS, fontSize: '0.6875rem', fontWeight: 600, letterSpacing: '0.08em', textTransform: 'uppercase', color: c }}>
        {label}
      </Typography>
      <Typography sx={{ fontFamily: MONO, fontSize: '0.6875rem', fontWeight: 600, color: c, opacity: 0.7, fontVariantNumeric: 'tabular-nums' }}>
        {count}
      </Typography>
    </Box>
  )
}

function BranchRow({ branch }: { branch: DemoBranch }) {
  const { active, kind, unread } = branch
  const needsAction = kind === 'needsAction'
  const running = kind === 'running'
  const nameColor = active ? surfaceTokens.textPrimary : needsAction ? semanticTokens.error : surfaceTokens.textMuted

  return (
    <Box
      sx={{
        position: 'relative',
        display: 'flex',
        alignItems: 'center',
        gap: 0.75,
        pl: 4,
        pr: 1.25,
        py: 0.5,
        borderRadius: 0,
        backgroundColor: active ? chromeTokens.rowActive : 'transparent',
      }}
    >
      {active && (
        <Box aria-hidden sx={{ position: 'absolute', left: 16, top: 6, bottom: 6, width: 2, borderRadius: 999, backgroundColor: chromeTokens.accent }} />
      )}

      {running && <StatusDot state={RuntimeState.Booting} size={6} />}
      {!running && needsAction && <StatusDot state={RuntimeState.Failed} size={6} />}
      {!running && !needsAction && unread && (
        <Box sx={{ flexShrink: 0, width: 6, height: 6, borderRadius: '50%', backgroundColor: unread === 'failed' ? semanticTokens.failureDot : semanticTokens.successDot }} />
      )}

      <Typography
        noWrap
        sx={{ flex: 1, minWidth: 0, fontFamily: MONO, fontSize: '0.75rem', fontWeight: active || needsAction ? 500 : 400, letterSpacing: '-0.005em', color: nameColor }}
      >
        {branch.name}
      </Typography>

      <Typography sx={{ flexShrink: 0, fontFamily: MONO, fontSize: '0.625rem', color: surfaceTokens.textMuted, fontVariantNumeric: 'tabular-nums', opacity: 0.85 }}>
        {branch.time}
      </Typography>
    </Box>
  )
}

function ProjectRow({ project, active }: { project: DemoProject; active: boolean }) {
  return (
    <Box>
      <Box
        sx={{
          position: 'relative',
          display: 'flex',
          alignItems: 'center',
          gap: 0.5,
          pl: 1,
          pr: 1.25,
          py: 0.75,
          backgroundColor: active ? chromeTokens.rowActive : 'transparent',
        }}
      >
        {active && <Box aria-hidden sx={{ position: 'absolute', left: 0, top: 0, bottom: 0, width: 2, backgroundColor: chromeTokens.accent }} />}
        <Box sx={{ display: 'inline-flex', width: 18, height: 18, alignItems: 'center', justifyContent: 'center', color: surfaceTokens.textMuted, flexShrink: 0 }}>
          <ExpandMoreIcon sx={{ fontSize: 16 }} />
        </Box>
        <Typography noWrap sx={{ fontFamily: SANS, fontSize: '0.8125rem', fontWeight: active ? 600 : 500, letterSpacing: '-0.005em', color: surfaceTokens.textPrimary }}>
          {project.name}
        </Typography>
      </Box>
      <BranchRow branch={project.branch} />
    </Box>
  )
}

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

      <Box sx={{ flex: 1, overflowY: 'auto', pb: 1.5 }}>
        {SECTIONS.map((section) => (
          <Box key={section.key}>
            <SectionHeader label={section.label} count={section.projects.length} color={section.color} />
            {section.projects.map((p) => (
              <ProjectRow key={p.name} project={p} active={!!p.branch.active} />
            ))}
          </Box>
        ))}
      </Box>
    </Box>
  )
}
