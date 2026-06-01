import { Box, Typography } from '@mui/material'
import { DriftSeverity } from '@/api/queries-commands'
import { workspaceRuntime } from '@/applications/workspace/shared/designTokens'

export const SEVERITY_COLORS: Record<DriftSeverity, string> = {
  Ok: workspaceRuntime.online,
  Low: workspaceRuntime.booting,
  Medium: workspaceRuntime.booting,
  High: workspaceRuntime.failed,
  Critical: workspaceRuntime.failed,
}

const SEVERITY_LABELS: Record<DriftSeverity, string> = {
  Ok: 'Ok',
  Low: 'Low',
  Medium: 'Medium',
  High: 'High',
  Critical: 'Critical',
}

interface SeverityBadgeProps {
  severity: DriftSeverity
  size?: 'small' | 'large'
}

export function SeverityBadge({ severity, size = 'small' }: SeverityBadgeProps) {
  const color = SEVERITY_COLORS[severity]
  const dotSize = size === 'large' ? 12 : 8
  const fontVariant = size === 'large' ? 'subtitle1' : 'body2'
  const fontWeight = size === 'large' ? 700 : 500

  return (
    <Box sx={{ display: 'inline-flex', alignItems: 'center', gap: 1 }}>
      <Box
        sx={{
          width: dotSize,
          height: dotSize,
          borderRadius: '50%',
          bgcolor: color,
          flexShrink: 0,
        }}
      />
      <Typography variant={fontVariant} sx={{ color, fontWeight }}>
        {SEVERITY_LABELS[severity]}
      </Typography>
    </Box>
  )
}
