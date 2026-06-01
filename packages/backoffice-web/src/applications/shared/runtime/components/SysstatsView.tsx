import { useMemo, useState } from 'react'
import {
  Box,
  Collapse,
  IconButton,
  LinearProgress,
  Stack,
  Tooltip,
  Typography,
} from '@mui/material'
import type { SvgIconComponent } from '@mui/icons-material'
import KeyboardArrowDownIcon from '@mui/icons-material/KeyboardArrowDown'
import KeyboardArrowRightIcon from '@mui/icons-material/KeyboardArrowRight'
import HourglassEmptyOutlinedIcon from '@mui/icons-material/HourglassEmptyOutlined'
import StorageOutlinedIcon from '@mui/icons-material/StorageOutlined'
import MemoryOutlinedIcon from '@mui/icons-material/MemoryOutlined'
import SpeedOutlinedIcon from '@mui/icons-material/SpeedOutlined'
import SwapVertOutlinedIcon from '@mui/icons-material/SwapVert'
import {
  monoNumberSx,
  surfaceTokens,
  workspaceColors,
  workspaceFontFamily,
  workspaceRuntime,
  workspaceText,
} from '@/applications/workspace/shared/designTokens'
import { IconTile, type IconTileTone } from '@/applications/workspace/shared/primitives'
import { formatBytes } from '@/lib/format/formatBytes'
import type { HeartbeatSnapshot } from '@/applications/super-admin/features/project-runtime/hooks/useRuntimeEventStream'

export interface SysstatsViewProps {
  /**
   * Latest heartbeat snapshot derived from the polled runtime/status row.
   * Null while no heartbeat has landed; in that case the body renders a
   * subtle waiting state instead of empty cards so the operator can tell the
   * difference between "panel just opened" and "daemon never reported".
   */
  heartbeatSnapshot: HeartbeatSnapshot | null
  /**
   * Render mode:
   * <ul>
   *   <li><b>false (default)</b> — wrapped in the collapsible accordion
   *       header used by the super-admin drawer; the body only renders when
   *       expanded.</li>
   *   <li><b>true</b> — bare body, no accordion, fills the parent. Used by
   *       the workspace runtime panel where the view IS the tab body.</li>
   * </ul>
   */
  embedded?: boolean
}

/**
 * Shared sysstats view. Surfaces the live disk / per-process memory /
 * per-process CPU% / network counters that the daemon attaches to every
 * heartbeat.
 *
 * <p>Four cards inside a responsive CSS grid:
 * <ol>
 *   <li><b>Disk</b> — used / total with a MUI LinearProgress that tints amber
 *       above 80% and red above 95%, plus the sample timestamp.</li>
 *   <li><b>Top 5 processes by RSS</b> — pure-CSS horizontal bars normalised
 *       against the top-5 max, so the heaviest process always fills its bar
 *       and the others are relative to it.</li>
 *   <li><b>Top 5 processes by CPU%</b> — same shape, normalised against
 *       100% (one full CPU). Bar caps at 100% so a multi-core process at
 *       e.g. 300% still renders cleanly inside the row.</li>
 *   <li><b>Network</b> — rx/tx rates per second prominently, cumulative
 *       bytes-since-boot below as a smaller caption.</li>
 * </ol></p>
 *
 * <p>Everything in this component reads off {@link HeartbeatSnapshot}; the
 * snapshot itself is plumbed from {@code useRuntimeEventStream}'s
 * status-derived heartbeat field, which updates on the existing 5s status
 * poll cadence — no new subscription required here.</p>
 */
export function SysstatsView({
  heartbeatSnapshot,
  embedded = false,
}: SysstatsViewProps) {
  const [open, setOpen] = useState(false)

  // Pre-compute the two top-5 lists once per snapshot. Both sort copies of
  // the same array so the original order in {@code processes} is preserved
  // for any future consumer that wants the raw list.
  const topByRss = useMemo(() => {
    if (!heartbeatSnapshot?.processes?.length) return []
    return [...heartbeatSnapshot.processes]
      .sort((a, b) => b.rssBytes - a.rssBytes)
      .slice(0, 5)
  }, [heartbeatSnapshot])

  const topByCpu = useMemo(() => {
    if (!heartbeatSnapshot?.processes?.length) return []
    return [...heartbeatSnapshot.processes]
      .sort((a, b) => b.cpuPercent - a.cpuPercent)
      .slice(0, 5)
  }, [heartbeatSnapshot])

  const rssMax = topByRss[0]?.rssBytes ?? 0

  const hasAny =
    !!heartbeatSnapshot?.disk ||
    (heartbeatSnapshot?.processes?.length ?? 0) > 0 ||
    !!heartbeatSnapshot?.network

  const body = !hasAny ? (
    <EmptyState />
  ) : (
    <Box
      sx={{
        display: 'grid',
        gap: 1.5,
        gridTemplateColumns: {
          xs: '1fr',
          sm: 'repeat(2, minmax(0, 1fr))',
          md: 'repeat(2, minmax(0, 1fr))',
          lg: 'repeat(4, minmax(0, 1fr))',
        },
      }}
    >
      <DiskCard snapshot={heartbeatSnapshot} />
      <ProcessBarsCard
        title="Top processes by RSS"
        icon={MemoryOutlinedIcon}
        rows={topByRss.map((p) => ({
          name: p.name,
          value: p.rssBytes,
          display: formatBytes(p.rssBytes),
          percent: rssMax > 0 ? (p.rssBytes / rssMax) * 100 : 0,
        }))}
      />
      <ProcessBarsCard
        title="Top processes by CPU"
        icon={SpeedOutlinedIcon}
        rows={topByCpu.map((p) => ({
          name: p.name,
          value: p.cpuPercent,
          display: `${p.cpuPercent.toFixed(1)}%`,
          percent: Math.min(100, p.cpuPercent),
        }))}
      />
      <NetworkCard snapshot={heartbeatSnapshot} />
    </Box>
  )

  // Embedded: just the body, no accordion, no chrome. Used by the workspace
  // panel where the sysstats view IS the tab body and chrome would be wrong.
  if (embedded) {
    return <Box sx={{ p: 1.5 }}>{body}</Box>
  }

  return (
    <Box
      sx={{
        flexShrink: 0,
        borderBottom: 1,
        borderColor: 'divider',
        backgroundColor: workspaceColors.chromeBg,
      }}
    >
      <Stack
        direction="row"
        alignItems="center"
        spacing={0.75}
        onClick={() => setOpen((prev) => !prev)}
        sx={{
          px: 2,
          py: 1,
          cursor: 'pointer',
          userSelect: 'none',
          transition: 'background-color 120ms ease',
          '&:hover': { backgroundColor: workspaceColors.chipBg },
        }}
        role="button"
        aria-expanded={open}
        aria-label="Toggle system stats"
      >
        <IconButton
          size="small"
          sx={{ p: 0.25, color: workspaceText.muted }}
          tabIndex={-1}
          aria-hidden
        >
          {open ? (
            <KeyboardArrowDownIcon fontSize="small" />
          ) : (
            <KeyboardArrowRightIcon fontSize="small" />
          )}
        </IconButton>
        <Typography
          sx={{
            fontSize: '0.6875rem',
            fontWeight: 600,
            letterSpacing: '0.08em',
            textTransform: 'uppercase',
            color: workspaceText.muted,
            flex: 1,
          }}
        >
          System stats
        </Typography>
        <SysstatsHeaderHint snapshot={heartbeatSnapshot} />
      </Stack>
      <Collapse in={open} timeout="auto" unmountOnExit>
        <Box sx={{ px: 2, py: 1.5 }}>{body}</Box>
      </Collapse>
    </Box>
  )
}

// ── Subcomponents ──────────────────────────────────────────────────────────

function SysstatsHeaderHint({ snapshot }: { snapshot: HeartbeatSnapshot | null }) {
  if (!snapshot) return null
  // Quick read of disk usage so a collapsed panel still shows the most
  // alarming metric. CPU/mem changes too quickly to be useful here.
  const disk = snapshot.disk
  if (!disk || disk.totalBytes <= 0) return null
  const percent = Math.round((disk.usedBytes / disk.totalBytes) * 100)
  const tone =
    percent > 95
      ? workspaceRuntime.failed
      : percent > 80
      ? workspaceRuntime.booting
      : workspaceText.muted
  return (
    <Typography
      sx={{
        fontFamily: workspaceFontFamily.mono,
        fontSize: 11.5,
        color: tone,
        fontWeight: 500,
      }}
    >
      Disk {percent}%
    </Typography>
  )
}

function EmptyState() {
  return (
    <Stack
      direction="row"
      spacing={1}
      alignItems="center"
      sx={{ py: 1.5, color: workspaceText.muted }}
    >
      <HourglassEmptyOutlinedIcon
        sx={{ fontSize: 16, color: workspaceText.faint }}
      />
      <Typography sx={{ fontSize: 12.5 }}>
        No heartbeat received yet — waiting for daemon sysstats…
      </Typography>
    </Stack>
  )
}

function StatCard({
  title,
  icon,
  tone = 'mute',
  children,
}: {
  title: string
  /** Leading glyph for the card header — rendered in a tone-colored IconTile. */
  icon: SvgIconComponent
  /** Semantic tone for the header IconTile. Defaults to neutral. */
  tone?: IconTileTone
  children: React.ReactNode
}) {
  return (
    <Box
      sx={{
        backgroundColor: surfaceTokens.cardBg,
        border: `1px solid ${surfaceTokens.hairline}`,
        borderRadius: 1,
        p: 1.25,
        display: 'flex',
        flexDirection: 'column',
        gap: 0.75,
        minHeight: 96,
      }}
    >
      <Stack direction="row" spacing={0.75} alignItems="center">
        <IconTile icon={icon} tone={tone} size={22} />
        <Typography
          sx={{
            fontSize: '0.6875rem',
            fontWeight: 600,
            letterSpacing: '0.08em',
            textTransform: 'uppercase',
            color: workspaceText.muted,
          }}
        >
          {title}
        </Typography>
      </Stack>
      {children}
    </Box>
  )
}

function DiskCard({ snapshot }: { snapshot: HeartbeatSnapshot | null }) {
  const disk = snapshot?.disk
  if (!disk) {
    return (
      <StatCard title="Disk" icon={StorageOutlinedIcon}>
        <Typography sx={{ fontSize: 12.5, color: workspaceText.muted }}>
          No disk sample yet.
        </Typography>
      </StatCard>
    )
  }
  const percent =
    disk.totalBytes > 0
      ? Math.min(100, Math.round((disk.usedBytes / disk.totalBytes) * 100))
      : 0
  const barTone =
    percent > 95
      ? workspaceRuntime.failed
      : percent > 80
      ? workspaceRuntime.booting
      : workspaceRuntime.online
  // Mirror the usage-bar tint onto the header icon tile so a near-full disk
  // reads as alarming at a glance, matching the timeline/services tone system.
  const headerTone: IconTileTone =
    percent > 95 ? 'err' : percent > 80 ? 'warn' : 'ok'
  const sampledRelative = formatRelative(disk.sampledAt)
  return (
    <StatCard title="Disk" icon={StorageOutlinedIcon} tone={headerTone}>
      <Stack
        direction="row"
        spacing={0.75}
        alignItems="baseline"
        sx={{ flexWrap: 'wrap' }}
      >
        <Typography
          sx={{
            ...monoNumberSx,
            fontSize: 14,
            fontWeight: 500,
            color: workspaceText.primary,
          }}
        >
          {formatBytes(disk.usedBytes)} / {formatBytes(disk.totalBytes)}
        </Typography>
        <Typography
          sx={{
            ...monoNumberSx,
            fontSize: 12,
            color: workspaceText.muted,
          }}
        >
          ({percent}%)
        </Typography>
      </Stack>
      <LinearProgress
        variant="determinate"
        value={percent}
        sx={{
          height: 6,
          borderRadius: 3,
          backgroundColor: workspaceColors.chipBg,
          '& .MuiLinearProgress-bar': {
            backgroundColor: barTone,
          },
        }}
      />
      {sampledRelative && (
        <Tooltip title={new Date(disk.sampledAt).toLocaleString()}>
          <Typography
            sx={{ fontSize: 11, color: workspaceText.faint }}
          >
            Sampled {sampledRelative}
          </Typography>
        </Tooltip>
      )}
    </StatCard>
  )
}

interface BarRow {
  name: string
  value: number
  display: string
  /** Width % of the bar; caller computes against the relevant denominator. */
  percent: number
}

function ProcessBarsCard({
  title,
  icon,
  rows,
}: {
  title: string
  icon: SvgIconComponent
  rows: BarRow[]
}) {
  return (
    <StatCard title={title} icon={icon}>
      {rows.length === 0 ? (
        <Typography sx={{ fontSize: 12.5, color: workspaceText.muted }}>
          No processes reported.
        </Typography>
      ) : (
        <Stack spacing={0.5}>
          {rows.map((row) => (
            <Box key={row.name}>
              <Stack
                direction="row"
                alignItems="baseline"
                justifyContent="space-between"
                spacing={1}
              >
                <Typography
                  sx={{
                    fontSize: 12,
                    fontFamily: workspaceFontFamily.mono,
                    color: workspaceText.primary,
                    overflow: 'hidden',
                    textOverflow: 'ellipsis',
                    whiteSpace: 'nowrap',
                    flex: 1,
                  }}
                  title={row.name}
                >
                  {row.name}
                </Typography>
                <Typography
                  sx={{
                    ...monoNumberSx,
                    fontSize: 11.5,
                    color: workspaceText.muted,
                  }}
                >
                  {row.display}
                </Typography>
              </Stack>
              <Box
                sx={{
                  position: 'relative',
                  height: 6,
                  borderRadius: 3,
                  backgroundColor: workspaceColors.chipBg,
                  overflow: 'hidden',
                  mt: 0.25,
                }}
              >
                <Box
                  sx={{
                    position: 'absolute',
                    inset: 0,
                    width: `${Math.max(2, row.percent)}%`,
                    backgroundColor: workspaceRuntime.online,
                    transition: 'width 200ms ease',
                  }}
                />
              </Box>
            </Box>
          ))}
        </Stack>
      )}
    </StatCard>
  )
}

function NetworkCard({ snapshot }: { snapshot: HeartbeatSnapshot | null }) {
  const network = snapshot?.network
  if (!network) {
    return (
      <StatCard title="Network" icon={SwapVertOutlinedIcon}>
        <Typography sx={{ fontSize: 12.5, color: workspaceText.muted }}>
          No interface sampled.
        </Typography>
      </StatCard>
    )
  }
  return (
    <StatCard title={`Network (${network.interface})`} icon={SwapVertOutlinedIcon}>
      <Stack direction="row" spacing={2}>
        <Stack spacing={0.125}>
          <Typography
            sx={{
              fontSize: 11,
              color: workspaceText.muted,
              letterSpacing: '0.04em',
              textTransform: 'uppercase',
              fontWeight: 600,
            }}
          >
            ↓ Rx
          </Typography>
          <Typography
            sx={{
              ...monoNumberSx,
              fontSize: 14,
              fontWeight: 500,
              color: workspaceText.primary,
            }}
          >
            {formatBytes(network.rxBytesPerSec)}/s
          </Typography>
        </Stack>
        <Stack spacing={0.125}>
          <Typography
            sx={{
              fontSize: 11,
              color: workspaceText.muted,
              letterSpacing: '0.04em',
              textTransform: 'uppercase',
              fontWeight: 600,
            }}
          >
            ↑ Tx
          </Typography>
          <Typography
            sx={{
              ...monoNumberSx,
              fontSize: 14,
              fontWeight: 500,
              color: workspaceText.primary,
            }}
          >
            {formatBytes(network.txBytesPerSec)}/s
          </Typography>
        </Stack>
      </Stack>
      <Typography
        sx={{
          ...monoNumberSx,
          fontSize: 11,
          color: workspaceText.faint,
        }}
      >
        Σ {formatBytes(network.rxBytes)} in · {formatBytes(network.txBytes)} out
      </Typography>
    </StatCard>
  )
}

// ── Relative-time helper ───────────────────────────────────────────────────
//
// Tiny self-contained helper so this component doesn't have to import the
// shared date-fns formatDistanceToNow for a single use site. Renders the
// shortest sensible string ("5s ago", "2m ago", "yesterday"). Returns null
// for malformed input.

function formatRelative(value: string | Date | null | undefined): string | null {
  if (!value) return null
  const ts = typeof value === 'string' ? new Date(value) : value
  if (Number.isNaN(ts.getTime())) return null
  const diffSec = Math.max(0, Math.floor((Date.now() - ts.getTime()) / 1000))
  if (diffSec < 5) return 'just now'
  if (diffSec < 60) return `${diffSec}s ago`
  const diffMin = Math.floor(diffSec / 60)
  if (diffMin < 60) return `${diffMin}m ago`
  const diffHr = Math.floor(diffMin / 60)
  if (diffHr < 24) return `${diffHr}h ago`
  const diffDay = Math.floor(diffHr / 24)
  if (diffDay === 1) return 'yesterday'
  return `${diffDay}d ago`
}
