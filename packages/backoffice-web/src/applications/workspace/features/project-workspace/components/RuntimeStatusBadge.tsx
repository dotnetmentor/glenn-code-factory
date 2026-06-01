import { Box, Chip, CircularProgress, Typography } from '@mui/material'
import { RuntimeState } from '../../../../../api/queries-commands'

type ChipColor = 'default' | 'primary' | 'secondary' | 'error' | 'info' | 'success' | 'warning'

interface BadgePresentation {
  label: string
  color: ChipColor
  showSpinner: boolean
}

/**
 * Map a {@link RuntimeState} to user-facing chip presentation. Unmapped states
 * fall through to a neutral "unknown" chip so the page never blows up when a
 * new state ships from the backend before the frontend catches up.
 */
function presentationForState(state: RuntimeState | string | undefined): BadgePresentation {
  switch (state) {
    case RuntimeState.Pending:
    case RuntimeState.Booting:
    case RuntimeState.Bootstrapping:
    case RuntimeState.Waking:
      return { label: 'Provisioning…', color: 'warning', showSpinner: true }
    case RuntimeState.Online:
      return { label: 'Online', color: 'success', showSpinner: false }
    case RuntimeState.Failed:
    case RuntimeState.Crashed:
      return { label: 'Failed', color: 'error', showSpinner: false }
    case RuntimeState.Suspended:
    case RuntimeState.Suspending:
      return { label: 'Suspended', color: 'default', showSpinner: false }
    case RuntimeState.Deleting:
    case RuntimeState.Deleted:
      return { label: 'Deleted', color: 'default', showSpinner: false }
    default:
      // Defensive: a server that started reporting a state we don't yet know
      // about should still render something. Log once so the gap is visible
      // in dev and ship a neutral chip.
      // eslint-disable-next-line no-console
      console.warn('[RuntimeStatusBadge] unmapped runtime state:', state)
      return { label: state ? String(state) : 'Unknown', color: 'default', showSpinner: false }
  }
}

export interface RuntimeStatusBadgeProps {
  state: RuntimeState | string | undefined
  /**
   * Human-readable failure message surfaced by the backend when
   * {@code state === Failed}. Rendered as a small caption beneath the chip.
   * Ignored for non-failure states.
   */
  errorMessage?: string | null
}

/**
 * Single-glance runtime status indicator. Renders a coloured MUI Chip with a
 * label that tracks the runtime lifecycle plus an inline spinner whenever the
 * machine is in a transitional state (Pending / Booting / Bootstrapping /
 * Waking).
 *
 * <p>When the runtime is Failed and the backend supplied an
 * <c>errorMessage</c>, the message is rendered as a muted caption beneath the
 * chip so the user can see *why* it failed without having to dig into logs.
 * The internal <c>errorReason</c> machine code is intentionally not surfaced
 * — only the human-readable text.</p>
 */
export function RuntimeStatusBadge({ state, errorMessage }: RuntimeStatusBadgeProps) {
  const { label, color, showSpinner } = presentationForState(state)
  const isFailed = state === RuntimeState.Failed || state === RuntimeState.Crashed
  const showErrorCaption = isFailed && !!errorMessage

  const chip = (
    <Chip
      size="small"
      color={color}
      label={label}
      icon={
        showSpinner ? (
          <CircularProgress
            size={12}
            thickness={6}
            sx={{ color: 'inherit', ml: 1 }}
          />
        ) : undefined
      }
      sx={{ fontWeight: 500 }}
    />
  )

  if (!showErrorCaption) {
    return chip
  }

  return (
    <Box
      sx={{
        display: 'flex',
        flexDirection: 'column',
        alignItems: { xs: 'flex-start', sm: 'flex-end' },
        gap: 0.5,
        maxWidth: 320,
      }}
    >
      {chip}
      <Typography
        variant="caption"
        color="text.secondary"
        sx={{
          textAlign: { xs: 'left', sm: 'right' },
          display: '-webkit-box',
          WebkitLineClamp: 2,
          WebkitBoxOrient: 'vertical',
          overflow: 'hidden',
          textOverflow: 'ellipsis',
        }}
      >
        {errorMessage}
      </Typography>
    </Box>
  )
}
