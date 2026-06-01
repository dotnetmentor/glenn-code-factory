/**
 * StatusDot — semantic state indicator with optional breathing animation.
 *
 * <p>The canonical "what's happening with that runtime" dot. Colors come from
 * {@link workspaceRuntime}, so the dot flips with the active (mode, accent)
 * pair. Auto-pulses on transitional states (Pending/Booting/Bootstrapping/
 * Waking), but consumers can override with the {@code pulse} prop.
 *
 * <p>This is the Phase 1 successor to the older
 * {@code features/project-workspace/components/StatusDot.tsx} — same API plus
 * the explicit {@code pulse} escape hatch the primitives spec calls for.
 */
import { Box, Tooltip } from '@mui/material'
import { keyframes } from '@mui/system'
import { RuntimeState } from '../../../../api/queries-commands'
import { workspaceRuntime } from '../designTokens'

export interface StatusDotProps {
  /** Runtime state (driven by the backend enum) or a raw string fallback. */
  state: RuntimeState | string | null | undefined
  /** Pixel size of the dot. Defaults to 8. */
  size?: number
  /**
   * Override automatic pulse selection. If omitted, the dot pulses for
   * transitional states (Pending / Booting / Bootstrapping / Waking).
   */
  pulse?: boolean
  /** Hide the tooltip — useful when the dot sits next to a label that already says it. */
  hideTooltip?: boolean
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

export function StatusDot({ state, size = 8, pulse, hideTooltip }: StatusDotProps) {
  const presentation = presentationFor(state)
  const shouldPulse = pulse ?? presentation.pulse
  const { color, label } = presentation

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
        animation: shouldPulse ? `${breathe} 2s ease-in-out infinite` : 'none',
        // Respect the OS reduced-motion preference: freeze the breathing pulse
        // to a steady, fully-opaque dot rather than the looping fade.
        '@media (prefers-reduced-motion: reduce)': {
          animation: 'none',
          opacity: 1,
        },
      }}
    />
  )

  if (hideTooltip || color === 'transparent') {
    return dot
  }
  return (
    <Tooltip title={label} placement="left" enterDelay={400}>
      {dot}
    </Tooltip>
  )
}
