import { useMemo } from 'react'
import {
  Alert,
  Box,
  CircularProgress,
  Paper,
  Stack,
  Typography,
  useTheme,
} from '@mui/material'
import {
  Bar,
  BarChart,
  CartesianGrid,
  Legend,
  ResponsiveContainer,
  Tooltip,
  XAxis,
  YAxis,
} from 'recharts'
import type { WakeStageBreakdownResponse } from '@/api/queries-commands'

interface StageBreakdownChartProps {
  breakdown: WakeStageBreakdownResponse | undefined
  isLoading: boolean
  error: unknown
}

interface ChartRow {
  stageName: string
  p50Ms: number
  p95Ms: number
  count: number
}

/**
 * Stacked-bar view of wake duration per bootstrap stage. The backend returns
 * stages already sorted by p95 desc — we keep that order so the dominant
 * bottleneck is the leftmost bar and the eye lands on it first.
 *
 * <p>Despite the spec calling it a "stacked" bar, p50 and p95 of the same
 * stage aren't additive (p95 already includes the p50 tail) — so rendering
 * them grouped side-by-side per stage is the honest visualization. Recharts'
 * default grouped {@code BarChart} layout achieves this. We keep them in a
 * single chart container so the comparison is direct.</p>
 */
export function StageBreakdownChart({
  breakdown,
  isLoading,
  error,
}: StageBreakdownChartProps) {
  const theme = useTheme()

  const data: ChartRow[] = useMemo(() => {
    if (!breakdown?.stages) return []
    return breakdown.stages.map((s) => ({
      stageName: s.stageName,
      p50Ms: s.p50Ms,
      p95Ms: s.p95Ms,
      count: s.count,
    }))
  }, [breakdown])

  if (isLoading) {
    return (
      <Paper variant="outlined" sx={{ p: 3 }}>
        <Stack direction="row" spacing={1.5} alignItems="center">
          <CircularProgress size={16} />
          <Typography variant="body2" color="text.secondary">
            Loading stage breakdown…
          </Typography>
        </Stack>
      </Paper>
    )
  }

  if (error instanceof Error) {
    return (
      <Alert severity="error">
        Failed to load stage breakdown: {error.message}
      </Alert>
    )
  }

  if (data.length === 0) {
    return (
      <Paper variant="outlined" sx={{ p: 3 }}>
        <Typography variant="body2" color="text.secondary">
          No stage data in this window.
        </Typography>
      </Paper>
    )
  }

  return (
    <Paper variant="outlined" sx={{ p: 3 }}>
      <Stack spacing={1.5}>
        <Box>
          <Typography variant="h6">Stage breakdown</Typography>
          <Typography variant="caption" color="text.secondary">
            Duration per bootstrap stage, sorted by p95 (slowest first).
          </Typography>
        </Box>
        <Box sx={{ width: '100%', height: 360 }}>
          <ResponsiveContainer>
            <BarChart
              data={data}
              margin={{ top: 8, right: 16, left: 8, bottom: 32 }}
            >
              <CartesianGrid strokeDasharray="3 3" stroke={theme.palette.divider} />
              <XAxis
                dataKey="stageName"
                interval={0}
                angle={-20}
                textAnchor="end"
                height={60}
                tick={{ fontSize: 12, fill: theme.palette.text.secondary }}
              />
              <YAxis
                tick={{ fontSize: 12, fill: theme.palette.text.secondary }}
                tickFormatter={(ms: number) => formatMsTick(ms)}
                label={{
                  value: 'Duration',
                  angle: -90,
                  position: 'insideLeft',
                  offset: 10,
                  style: { fill: theme.palette.text.secondary, fontSize: 12 },
                }}
              />
              <Tooltip
                formatter={(value, name) => [
                  typeof value === 'number' ? formatMsTick(value) : String(value ?? ''),
                  String(name ?? ''),
                ]}
                // recharts 3.8+: labelFormatter label is ReactNode, not string (Tooltip labelFormatter API)
                labelFormatter={(label) => (typeof label === 'string' ? label : String(label ?? ''))}
                contentStyle={{
                  background: theme.palette.background.paper,
                  border: `1px solid ${theme.palette.divider}`,
                  borderRadius: 6,
                }}
              />
              <Legend wrapperStyle={{ fontSize: 12 }} />
              <Bar
                dataKey="p50Ms"
                name="p50"
                fill={theme.palette.primary.light}
                radius={[3, 3, 0, 0]}
              />
              <Bar
                dataKey="p95Ms"
                name="p95"
                fill={theme.palette.primary.main}
                radius={[3, 3, 0, 0]}
              />
            </BarChart>
          </ResponsiveContainer>
        </Box>
      </Stack>
    </Paper>
  )
}

function formatMsTick(ms: number): string {
  if (!Number.isFinite(ms)) return ''
  if (ms >= 1000) return `${(ms / 1000).toFixed(1)}s`
  return `${Math.round(ms)}ms`
}
