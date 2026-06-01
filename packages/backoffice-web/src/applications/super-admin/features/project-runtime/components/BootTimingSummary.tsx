import { useMemo } from 'react'
import { Box, Stack, Typography } from '@mui/material'
import BoltIcon from '@mui/icons-material/Bolt'
import type { RuntimeEventDto } from '@/api/queries-commands'
import {
  workspaceColors,
  workspaceFontFamily,
  workspaceText,
} from '@/applications/workspace/shared/designTokens'
import { IconTile } from '@/applications/workspace/shared/primitives'
import { formatDuration, parseEventPayload } from '../utils/runtimeEventDisplay'

/**
 * Header widget rendered at the top of the Timeline tab. Surfaces the most
 * recent boot's aggregate timing — the "Boot completed in 14.2s" line called
 * out in the runtime-spec-v2's "Timing — first-class concern" section.
 *
 * <p>Two ingredients:
 * <ul>
 *   <li>The latest {@code BootstrapStageCompleted} event with a
 *       {@code bootstrapTotalMs} field on its payload — drives the top-line
 *       duration.</li>
 *   <li>The latest {@code SpecDeltaApplied} event whose payload carries a
 *       {@code phaseTimings} breakdown — appended as parenthetical detail
 *       so the user can see whether install, services, or setup dominated.</li>
 * </ul></p>
 *
 * <p>If neither is present the widget renders nothing — callers wrap it in a
 * fragment, so a missing boot doesn't leave an empty card on screen.</p>
 */
export interface BootTimingSummaryProps {
  events: RuntimeEventDto[]
}

interface PhaseTimings {
  installMs?: number
  servicesMs?: number
  setupMs?: number
}

function readNumber(value: unknown): number | undefined {
  return typeof value === 'number' && Number.isFinite(value) ? value : undefined
}

function readPhaseTimings(payload: Record<string, unknown> | null): PhaseTimings | undefined {
  if (!payload) return undefined
  const pt = payload['phaseTimings']
  if (!pt || typeof pt !== 'object') return undefined
  const record = pt as Record<string, unknown>
  const result: PhaseTimings = {
    installMs: readNumber(record['installMs']),
    servicesMs: readNumber(record['servicesMs']),
    setupMs: readNumber(record['setupMs']),
  }
  // Nothing useful inside? Treat as absent so the widget doesn't render an
  // empty parenthetical.
  if (
    result.installMs === undefined &&
    result.servicesMs === undefined &&
    result.setupMs === undefined
  ) {
    return undefined
  }
  return result
}

export function BootTimingSummary({ events }: BootTimingSummaryProps) {
  const { bootTotalMs, phases } = useMemo(() => {
    let totalMs: number | undefined
    let phaseBreakdown: PhaseTimings | undefined

    // Walk newest → oldest until we find one of each. Events arrive
    // reverse-chronologically so this short-circuits quickly on a live
    // runtime.
    for (const event of events) {
      if (totalMs === undefined && event.type === 'BootstrapStageCompleted') {
        const parsed = parseEventPayload(event.payload)
        const candidate = parsed ? readNumber(parsed['bootstrapTotalMs']) : undefined
        if (candidate !== undefined) totalMs = candidate
      }
      if (phaseBreakdown === undefined && event.type === 'SpecDeltaApplied') {
        const parsed = parseEventPayload(event.payload)
        phaseBreakdown = readPhaseTimings(parsed)
      }
      if (totalMs !== undefined && phaseBreakdown !== undefined) break
    }

    return { bootTotalMs: totalMs, phases: phaseBreakdown }
  }, [events])

  if (bootTotalMs === undefined && phases === undefined) {
    return null
  }

  const phaseParts: string[] = []
  if (phases) {
    if (phases.installMs !== undefined) {
      phaseParts.push(`Install ${formatDuration(phases.installMs)}`)
    }
    if (phases.servicesMs !== undefined) {
      phaseParts.push(`Services ${formatDuration(phases.servicesMs)}`)
    }
    if (phases.setupMs !== undefined) {
      phaseParts.push(`Setup ${formatDuration(phases.setupMs)}`)
    }
  }

  return (
    <Box
      sx={{
        display: 'flex',
        alignItems: 'center',
        gap: 1.25,
        px: 1.5,
        py: 1,
        borderRadius: 1.25,
        backgroundColor: workspaceColors.chipBg,
        border: `1px solid ${workspaceColors.hairline}`,
      }}
    >
      <IconTile icon={BoltIcon} tone="warn" />
      <Stack spacing={0} sx={{ minWidth: 0 }}>
        <Typography
          sx={{
            fontSize: 12.5,
            fontWeight: 500,
            color: workspaceText.primary,
            letterSpacing: '-0.005em',
          }}
        >
          {bootTotalMs !== undefined ? 'Last boot' : 'Last boot'}
          {bootTotalMs !== undefined && (
            <Box
              component="span"
              sx={{
                ml: 0.75,
                fontFamily: workspaceFontFamily.mono,
                fontVariantNumeric: 'tabular-nums',
                fontSize: 11.5,
                color: workspaceText.muted,
              }}
            >
              {formatDuration(bootTotalMs)}
            </Box>
          )}
          {phaseParts.length > 0 && (
            <Box
              component="span"
              sx={{
                ml: 0.75,
                fontSize: 11.5,
                color: workspaceText.faint,
                letterSpacing: '-0.005em',
              }}
            >
              ({phaseParts.join(', ')})
            </Box>
          )}
        </Typography>
        {bootTotalMs === undefined && phases && (
          <Typography
            sx={{ fontSize: 11, color: workspaceText.faint, mt: 0.25 }}
          >
            No aggregate boot timing yet — showing latest phase breakdown.
          </Typography>
        )}
      </Stack>
    </Box>
  )
}
