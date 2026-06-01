/**
 * RuntimePill — the canonical runtime-state chip used at the top of every
 * chat surface and inside the workspace landing project rows.
 *
 * <p>Replaces the ad-hoc {@code RuntimeStateStrip} + {@code RuntimeStatusBadge}
 * stack. The pill folds three signals into one row:
 * <ul>
 *   <li>a colored status dot (optionally breathing for transitional states),</li>
 *   <li>a short label ({@code Online}, {@code Booting}, …),</li>
 *   <li>a faint sub-label with the next-step hint ("12d uptime", "tap to wake").</li>
 * </ul>
 *
 * <p>Colors are read from {@link workspaceRuntime} and {@link surfaceTokens},
 * so the pill repaints when the workspace flips light → dark or when the user
 * cycles the accent.
 */
import { Box, Tooltip, Typography } from '@mui/material'
import { keyframes } from '@mui/system'
import { RuntimeState } from '../../../../api/queries-commands'
import {
  surfaceTokens,
  workspaceFontFamily,
  workspaceRuntime,
  workspaceText,
} from '../designTokens'

/**
 * Subset of the backend's RuntimeState enum we surface as a pill. We still
 * accept any string so callers passing raw signalr payloads don't have to
 * upcast.
 */
export type RuntimePillState =
  | RuntimeState
  | 'Online'
  | 'Booting'
  | 'Bootstrapping'
  | 'Waking'
  | 'Pending'
  | 'Suspended'
  | 'Suspending'
  | 'Failed'
  | 'Crashed'
  | 'Deleting'
  | 'Deleted'

export interface RuntimePillProps {
  /** Runtime state — drives color, label, and pulse. */
  state: RuntimePillState | string | null | undefined
  /**
   * Short hint shown after the label (e.g. {@code "12d uptime"},
   * {@code "tap to wake"}). Omit to render label-only.
   */
  subLabel?: string
  /**
   * Override the auto-derived pulse. Transitional states (Pending / Booting /
   * Bootstrapping / Waking) pulse by default.
   */
  pulse?: boolean
  /** Optional click handler — flips the cursor + adds hover affordance. */
  onClick?: () => void
  /** Tooltip override; defaults to {@code `Runtime ${label} · ${subLabel}`}. */
  title?: string
}

const breathe = keyframes`
  0%, 100% { opacity: 0.55; }
  50%      { opacity: 1; }
`

interface Presentation {
  label: string
  color: string
  pulse: boolean
  defaultSub: string
}

function presentationFor(state: RuntimePillProps['state']): Presentation {
  switch (state) {
    case RuntimeState.Online:
    case 'Online':
      return { label: 'Online', color: workspaceRuntime.online, pulse: false, defaultSub: 'ready' }
    case RuntimeState.Pending:
    case 'Pending':
      return { label: 'Pending', color: workspaceRuntime.booting, pulse: true, defaultSub: 'queued' }
    case RuntimeState.Booting:
    case 'Booting':
      return { label: 'Booting', color: workspaceRuntime.booting, pulse: true, defaultSub: 'starting services' }
    case RuntimeState.Bootstrapping:
    case 'Bootstrapping':
      return { label: 'Bootstrapping', color: workspaceRuntime.booting, pulse: true, defaultSub: 'provisioning' }
    case RuntimeState.Waking:
    case 'Waking':
      return { label: 'Waking', color: workspaceRuntime.booting, pulse: true, defaultSub: 'resuming' }
    case RuntimeState.Suspended:
    case 'Suspended':
      return { label: 'Suspended', color: workspaceRuntime.suspended, pulse: false, defaultSub: 'tap to wake' }
    case RuntimeState.Suspending:
    case 'Suspending':
      return { label: 'Suspending', color: workspaceRuntime.suspended, pulse: false, defaultSub: 'winding down' }
    case RuntimeState.Failed:
    case 'Failed':
      return { label: 'Failed', color: workspaceRuntime.failed, pulse: false, defaultSub: 'apply error · retry' }
    case RuntimeState.Crashed:
    case 'Crashed':
      return { label: 'Crashed', color: workspaceRuntime.failed, pulse: false, defaultSub: 'restart required' }
    case RuntimeState.Deleting:
    case 'Deleting':
      return { label: 'Deleting', color: workspaceRuntime.suspended, pulse: false, defaultSub: 'removing' }
    case RuntimeState.Deleted:
    case 'Deleted':
      return { label: 'Deleted', color: workspaceRuntime.suspended, pulse: false, defaultSub: 'gone' }
    default:
      return { label: 'Unknown', color: workspaceRuntime.unknown, pulse: false, defaultSub: '' }
  }
}

export function RuntimePill({
  state,
  subLabel,
  pulse,
  onClick,
  title,
}: RuntimePillProps) {
  const presentation = presentationFor(state)
  const shouldPulse = pulse ?? presentation.pulse
  const effectiveSub = subLabel ?? presentation.defaultSub
  const tooltip = title ?? `Runtime ${presentation.label}${effectiveSub ? ` · ${effectiveSub}` : ''}`
  const interactive = typeof onClick === 'function'

  return (
    <Tooltip title={tooltip} placement="bottom" enterDelay={400}>
      <Box
        onClick={onClick}
        role={interactive ? 'button' : undefined}
        tabIndex={interactive ? 0 : undefined}
        onKeyDown={
          interactive
            ? (e) => {
                if (e.key === 'Enter' || e.key === ' ') {
                  e.preventDefault()
                  onClick?.()
                }
              }
            : undefined
        }
        sx={{
          display: 'inline-flex',
          alignItems: 'center',
          gap: 1,
          height: 28,
          padding: '5px 10px 5px 8px',
          borderRadius: 999,
          backgroundColor: surfaceTokens.chipBg,
          border: `1px solid ${surfaceTokens.hairline}`,
          cursor: interactive ? 'pointer' : 'default',
          transition: 'background-color 120ms ease, border-color 120ms ease',
          outline: 'none',
          '&:hover': interactive
            ? { backgroundColor: surfaceTokens.chipHoverBg }
            : undefined,
          '&:focus-visible': interactive
            ? { borderColor: workspaceText.primary }
            : undefined,
        }}
      >
        <Box
          aria-hidden
          sx={{
            width: 8,
            height: 8,
            borderRadius: '50%',
            backgroundColor: presentation.color,
            // Faint glow ring matches the prototype's `boxShadow: ${color}22` recipe.
            boxShadow: `0 0 0 3px ${presentation.color}22`,
            animation: shouldPulse ? `${breathe} 2s ease-in-out infinite` : 'none',
            transition: 'background-color 200ms ease',
            flexShrink: 0,
            // Respect the OS reduced-motion preference: freeze the breathing
            // pulse to a steady, fully-opaque dot rather than the looping fade.
            '@media (prefers-reduced-motion: reduce)': {
              animation: 'none',
              opacity: 1,
            },
          }}
        />
        <Typography
          component="span"
          sx={{
            fontFamily: workspaceFontFamily.sans,
            fontSize: '0.75rem',
            fontWeight: 600,
            color: workspaceText.primary,
            letterSpacing: '-0.005em',
            lineHeight: 1,
          }}
        >
          {presentation.label}
        </Typography>
        {effectiveSub ? (
          <Typography
            component="span"
            sx={{
              fontFamily: workspaceFontFamily.sans,
              fontSize: '0.6875rem',
              color: workspaceText.faint,
              letterSpacing: '-0.005em',
              lineHeight: 1,
            }}
          >
            · {effectiveSub}
          </Typography>
        ) : null}
      </Box>
    </Tooltip>
  )
}
