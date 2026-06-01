import {
  Box,
  Button,
  CircularProgress,
  Skeleton,
  Stack,
  Typography,
} from '@mui/material'
import RefreshIcon from '@mui/icons-material/Refresh'
import type { WakeMetricsSummaryResponse } from '@/api/queries-commands'

interface SummaryHeaderProps {
  summary: WakeMetricsSummaryResponse | undefined
  isLoading: boolean
  isFetching: boolean
  onRefresh: () => void
}

/**
 * Format a duration in ms for the big summary numbers. Operators read the
 * page at a glance — anything north of a second rounds to seconds with one
 * decimal, sub-second stays in ms so a healthy fleet still has a sensible
 * unit on it.
 */
function formatSummaryDuration(ms: number): string {
  if (!Number.isFinite(ms) || ms < 0) return '–'
  if (ms >= 1000) {
    return `${(ms / 1000).toFixed(1)}s`
  }
  return `${Math.round(ms)}ms`
}

/**
 * Format the server-provided AsOf instant as HH:MM:SS (local time). Per the
 * spec we never compute "as of" client-side — we always render whatever the
 * summary response carries.
 */
function formatAsOf(asOf: string | undefined): string {
  if (!asOf) return '—'
  const d = new Date(asOf)
  if (Number.isNaN(d.getTime())) return asOf
  return d.toLocaleTimeString(undefined, {
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit',
  })
}

export function SummaryHeader({
  summary,
  isLoading,
  isFetching,
  onRefresh,
}: SummaryHeaderProps) {
  const p50 = summary ? formatSummaryDuration(summary.p50Ms) : null
  const p95 = summary ? formatSummaryDuration(summary.p95Ms) : null
  const asOf = formatAsOf(summary?.asOf)

  return (
    <Box
      sx={{
        display: 'flex',
        justifyContent: 'space-between',
        alignItems: 'flex-start',
        gap: 2,
        flexWrap: 'wrap',
      }}
    >
      <Stack spacing={2} direction="row">
        <MetricBlock label="p50" value={p50} loading={isLoading} />
        <MetricBlock label="p95" value={p95} loading={isLoading} />
        <MetricBlock
          label="Wakes in window"
          value={summary ? summary.count.toLocaleString() : null}
          loading={isLoading}
          compact
        />
      </Stack>
      <Stack alignItems="flex-end" spacing={0.5}>
        <Stack direction="row" alignItems="center" spacing={1}>
          {isFetching && <CircularProgress size={14} />}
          <Typography variant="caption" color="text.secondary">
            As of {asOf}
          </Typography>
          <Button
            size="small"
            variant="outlined"
            startIcon={<RefreshIcon fontSize="small" />}
            onClick={onRefresh}
            disabled={isFetching}
          >
            Refresh
          </Button>
        </Stack>
        <Typography variant="caption" color="text.secondary">
          Manual refresh only — no auto-polling.
        </Typography>
      </Stack>
    </Box>
  )
}

interface MetricBlockProps {
  label: string
  value: string | null
  loading: boolean
  compact?: boolean
}

function MetricBlock({ label, value, loading, compact }: MetricBlockProps) {
  return (
    <Box sx={{ minWidth: compact ? 120 : 140 }}>
      <Typography variant="overline" color="text.secondary">
        {label}
      </Typography>
      {loading || value === null ? (
        <Skeleton variant="text" width={compact ? 80 : 110} height={compact ? 40 : 56} />
      ) : (
        <Typography
          variant={compact ? 'h5' : 'h3'}
          component="div"
          sx={{ fontVariantNumeric: 'tabular-nums', lineHeight: 1.1 }}
        >
          {value}
        </Typography>
      )}
    </Box>
  )
}
