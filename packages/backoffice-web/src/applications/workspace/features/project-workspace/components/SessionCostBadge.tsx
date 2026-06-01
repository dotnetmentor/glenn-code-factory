import { Box, Tooltip } from '@mui/material'
import { workspaceText } from '../../../shared/designTokens'
import { formatCostUsd, formatTokens } from './costFormat'

// Hardcoded copy of the ambient muted color used by {@link ComposerModelPickerInline}
// — keeps the badge in the same quiet design family as the rest of the chrome
// chrome accents (model picker, branch chip, runtime label).
const COLOR_MUTED = 'rgba(0, 0, 0, 0.45)'

interface SessionCostBadgeProps {
  costUsd: number | null | undefined
  inputTokens: number | null | undefined
  outputTokens: number | null | undefined
  cacheReadTokens: number | null | undefined
  cacheWriteTokens: number | null | undefined
  reasoningTokens: number | null | undefined
  /**
   * Compact whisper mode — the cost badge becomes smaller and is rendered as
   * an absolutely-positioned tag in the bottom-right corner of its parent
   * (which must itself be {@code position: relative}). Used to tuck the
   * per-turn cost inside the user message bubble as a gentle annotation
   * rather than a freestanding row below the assistant prose. When
   * {@code false} (default) the badge keeps its original right-aligned inline
   * appearance for legacy / non-bubble surfaces.
   */
  compact?: boolean
}

/**
 * Tiny right-aligned cost badge for one completed assistant turn.
 *
 * <p>Reads the per-turn cost + token breakdown straight off the
 * {@code SessionSummary} fields the backend now stamps when a turn settles.
 * Renders {@code null} when {@code costUsd} is null — that's the v1 contract
 * for "this session predates cost tracking" / "this session was canceled
 * before any tokens were billed", and we silently hide the badge in both
 * cases so legacy / canceled turns don't sprout an empty "$0.00" anchor.</p>
 *
 * <p>In {@code compact} mode the badge ALSO hides when {@code costUsd} is
 * exactly zero — a $0 turn inside a user bubble would just be visual noise.
 * The classic (non-compact) surface still surfaces $0 because that mode is
 * the user's only signal that the turn was free (cache-only reads etc.).</p>
 *
 * <p>Hover surfaces the input / output / cache-read / cache-write breakdown in
 * a fixed-width monospace tooltip. The reasoning row only renders when the
 * turn actually consumed reasoning tokens (a Claude-extended-thinking quirk),
 * so vanilla turns don't get a phantom "Reasoning: 0" line.</p>
 */
export function SessionCostBadge({
  costUsd,
  inputTokens,
  outputTokens,
  cacheReadTokens,
  cacheWriteTokens,
  reasoningTokens,
  compact = false,
}: SessionCostBadgeProps) {
  // The "this turn predates cost tracking" hide condition. In the non-compact
  // surface we deliberately DON'T hide on costUsd === 0 — a real $0 turn is
  // signal worth surfacing. In compact mode (tucked inside a user bubble) we
  // hide $0 as well — silence is gentler than a noisy zero next to the prose.
  if (costUsd == null) return null
  if (compact && costUsd === 0) return null

  const hasReasoning = reasoningTokens != null && reasoningTokens > 0

  return (
    <Tooltip
      enterDelay={300}
      placement="top-end"
      title={
        <Box
          component="pre"
          sx={{
            m: 0,
            fontFamily:
              'ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, "Liberation Mono", monospace',
            fontSize: '0.6875rem',
            lineHeight: 1.5,
            whiteSpace: 'pre',
          }}
        >
          {/* Right-align the numeric column so the eye can scan the breakdown.
              padStart on the value gives the column a consistent gutter
              without us having to thread a CSS grid into a tooltip. */}
          {`Input:     ${formatTokens(inputTokens).padStart(9)} tokens
Output:    ${formatTokens(outputTokens).padStart(9)} tokens
Cache R:   ${formatTokens(cacheReadTokens).padStart(9)} tokens
Cache W:   ${formatTokens(cacheWriteTokens).padStart(9)} tokens`}
          {hasReasoning &&
            `\nReasoning: ${formatTokens(reasoningTokens).padStart(9)} tokens`}
        </Box>
      }
    >
      <Box
        component="span"
        sx={
          compact
            ? {
                // Absolutely positioned whisper in the bottom-right of the
                // user bubble. Smaller type, lower contrast, no hover lift —
                // the goal is to be present without attracting attention.
                position: 'absolute',
                bottom: 4,
                right: 8,
                display: 'inline-flex',
                alignItems: 'center',
                fontSize: '0.625rem',
                fontWeight: 400,
                color: COLOR_MUTED,
                letterSpacing: '-0.005em',
                lineHeight: 1,
                cursor: 'default',
                fontVariantNumeric: 'tabular-nums',
                opacity: 0.75,
                pointerEvents: 'auto',
              }
            : {
                alignSelf: 'flex-end',
                display: 'inline-flex',
                alignItems: 'center',
                fontSize: '0.6875rem',
                fontWeight: 500,
                color: COLOR_MUTED,
                letterSpacing: '-0.005em',
                lineHeight: 1.3,
                cursor: 'default',
                fontVariantNumeric: 'tabular-nums',
                // Subtle hover lift — matches the ambient picker.
                transition: 'color 120ms ease',
                '&:hover': { color: workspaceText.primary },
              }
        }
        aria-label={`Turn cost ${formatCostUsd(costUsd)}`}
      >
        {formatCostUsd(costUsd)}
      </Box>
    </Tooltip>
  )
}
