import { Box, Tooltip } from '@mui/material'
import { keyframes } from '@mui/system'
import { RuntimeState } from '../../../../../api/queries-commands'
import { workspaceRuntime } from '../../../shared/designTokens'

/**
 * Reusable runtime-state dot for the projects sidebar (and anywhere else the
 * agent-native shell wants to surface a quiet "what's happening with that
 * agent" signal). Colours come from {@link workspaceRuntime} — preset-aware.
 */
export interface StatusDotProps {
  state: RuntimeState | string | null | undefined
  /** Pixel size of the dot. Defaults to 8. */
  size?: number
}

const breathe = keyframes`
  0%, 100% { opacity: 0.55; }
  50%      { opacity: 1; }
`

interface Presentation {
  color: string
  label: string
  pulse: boolean
}

function presentationFor(state: RuntimeState | string | null | undefined): Presentation {
  switch (state) {
    case RuntimeState.Online:
      return { color: workspaceRuntime.online, label: 'Online', pulse: false }
    case RuntimeState.Pending:
      return { color: workspaceRuntime.booting, label: 'Pending', pulse: true }
    case RuntimeState.Booting:
      return { color: workspaceRuntime.booting, label: 'Booting', pulse: true }
    case RuntimeState.Bootstrapping:
      return { color: workspaceRuntime.booting, label: 'Bootstrapping', pulse: true }
    case RuntimeState.Waking:
      return { color: workspaceRuntime.booting, label: 'Waking', pulse: true }
    case RuntimeState.Suspended:
      return { color: workspaceRuntime.suspended, label: 'Suspended', pulse: false }
    case RuntimeState.Suspending:
      return { color: workspaceRuntime.suspended, label: 'Suspending', pulse: false }
    case RuntimeState.Failed:
      return { color: workspaceRuntime.failed, label: 'Failed', pulse: false }
    case RuntimeState.Crashed:
      return { color: workspaceRuntime.failed, label: 'Crashed', pulse: false }
    case RuntimeState.Deleting:
      return { color: workspaceRuntime.suspended, label: 'Deleting', pulse: false }
    case RuntimeState.Deleted:
      return { color: workspaceRuntime.suspended, label: 'Deleted', pulse: false }
    default:
      return { color: 'transparent', label: 'Unknown', pulse: false }
  }
}

export function StatusDot({ state, size = 8 }: StatusDotProps) {
  const { color, label, pulse } = presentationFor(state)
  const dot = (
    <Box
      aria-hidden
      sx={{
        width: size,
        height: size,
        borderRadius: '50%',
        backgroundColor: color,
        display: 'inline-block',
        flexShrink: 0,
        transition: 'background-color 200ms ease',
        animation: pulse ? `${breathe} 2s ease-in-out infinite` : 'none',
      }}
    />
  )
  if (color === 'transparent') {
    return dot
  }
  return (
    <Tooltip title={label} placement="left" enterDelay={400}>
      {dot}
    </Tooltip>
  )
}
