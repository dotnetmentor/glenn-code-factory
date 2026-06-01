import { Typography } from '@mui/material'
import { workspaceRuntime } from '@/applications/workspace/shared/designTokens'

interface HeartbeatAgeProps {
  seconds: number | null | undefined
}

export function HeartbeatAge({ seconds }: HeartbeatAgeProps) {
  if (seconds === null || seconds === undefined) {
    return (
      <Typography variant="body2" color="text.secondary">
        {'\u2014'}
      </Typography>
    )
  }

  const formatted = formatAge(seconds)

  let color: string
  let prefix = ''
  if (seconds < 60) {
    color = 'text.primary'
  } else if (seconds <= 300) {
    color = workspaceRuntime.booting
  } else {
    color = workspaceRuntime.failed
    prefix = 'stale '
  }

  return (
    <Typography variant="body2" sx={{ color }}>
      {prefix}
      {formatted} ago
    </Typography>
  )
}

function formatAge(seconds: number): string {
  if (seconds < 60) return `${Math.round(seconds)}s`
  const minutes = Math.floor(seconds / 60)
  if (minutes < 60) return `${minutes}m`
  const hours = Math.floor(minutes / 60)
  if (hours < 24) return `${hours}h`
  const days = Math.floor(hours / 24)
  return `${days}d`
}
