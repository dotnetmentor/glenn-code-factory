import { Box } from '@mui/material'
import type { ReactNode } from 'react'
import { workspaceChromeHeight } from '../../../../shared/designTokens'
import { appContainerTokens } from './tokens'

interface SecondaryHeaderProps {
  /** Primary heading text — the tab title (e.g. "Working changes"). */
  label: string
  /**
   * Optional muted sub-text shown after a {@code "·"} middot
   * separator (e.g. {@code "7 files · main"}). Accepts any ReactNode so
   * callers can drop inline chips, mono counts, etc.
   */
  sub?: ReactNode
  /**
   * Optional right-aligned action slot (button, picker, badge…). The
   * slot itself absorbs no padding — render whatever you want; the
   * outer 14px horizontal padding takes care of the gutter.
   */
  right?: ReactNode
  /**
   * When {@code true} the whole header desaturates to 0.55 — matches
   * the {@code disabled} treatment on {@link PreviewChrome} so the
   * runtime-offline state reads consistently across tabs.
   */
  disabled?: boolean
}

/**
 * Per-tab chrome bar shown inside the AppContainer above the Changes,
 * Specs, and Kanban tab bodies.
 *
 * <p>Mirrors the reference design's {@code SecondaryHeader} recipe:
 * locked to {@link workspaceChromeHeight} so the lid lines up with the
 * sidebar workspace switcher, the chat chrome, and the preview URL bar
 * on the same y-grid. Paper-tone background, hairline border-bottom.</p>
 *
 * <p>Left cluster is a bold 13px label optionally followed by a faint
 * 11.5px sub-text after a middot separator. Right cluster is a flexible
 * slot for the tab's primary action (a "New spec" button, a refresh
 * icon, a CompareAgainstPicker, etc.) — kept slot-based rather than
 * prop-driven so each tab decides what belongs there without this
 * component needing to know.</p>
 *
 * @example
 *   <SecondaryHeader
 *     label="Specifications"
 *     sub="4 specs · 2 accepted"
 *     right={<NewSpecButton />}
 *   />
 */
export function SecondaryHeader({
  label,
  sub,
  right,
  disabled = false,
}: SecondaryHeaderProps) {
  return (
    <Box
      sx={{
        // Same lid height as every other panel chrome — see the doc on
        // {@link workspaceChromeHeight} for why all four chromes share
        // this number.
        height: workspaceChromeHeight,
        flexShrink: 0,
        // 14px gutter (reference value). Wide enough for the label to
        // breathe, tight enough that the right cluster sits flush with
        // the panel's right edge minus 14px.
        px: '14px',
        display: 'flex',
        alignItems: 'center',
        gap: '10px',
        backgroundColor: appContainerTokens.chromeBg,
        borderBottom: `1px solid ${appContainerTokens.hairline}`,
        opacity: disabled ? 0.55 : 1,
        transition: 'opacity 200ms ease',
      }}
    >
      <Box
        component="span"
        sx={{
          fontSize: '0.8125rem', // 13px
          fontWeight: 600,
          color: appContainerTokens.textPrimary,
          letterSpacing: '-0.005em',
          whiteSpace: 'nowrap',
        }}
      >
        {label}
      </Box>
      {sub != null && (
        <Box
          component="span"
          sx={{
            fontSize: '0.71875rem', // 11.5px
            color: appContainerTokens.textFaint,
            letterSpacing: '-0.005em',
            whiteSpace: 'nowrap',
            overflow: 'hidden',
            textOverflow: 'ellipsis',
            minWidth: 0,
          }}
        >
          · {sub}
        </Box>
      )}
      {/* Spacer — pushes the right cluster to the panel's right edge. */}
      <Box sx={{ flex: 1, minWidth: 0 }} />
      {right}
    </Box>
  )
}
