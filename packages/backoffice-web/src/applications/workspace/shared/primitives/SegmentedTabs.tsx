/**
 * SegmentedTabs — pill-grouped tabs used in the app-container bottom strip,
 * the runtime drawer device toggle, the approval matrix, and project settings.
 *
 * <p>Visually a single rounded chip that contains 2–N segments; the active
 * segment is raised with a subtle surface fill so the group still reads as
 * one control. Designed for compact density (24px tall) — the MUI {@code Tabs}
 * primitive is too chunky for the same role.
 *
 * <p>API mirrors the reference handoff:
 * <pre>
 *   &lt;SegmentedTabs
 *     value="preview"
 *     onChange={setView}
 *     items={[
 *       { id: 'preview', label: 'Preview', icon: PreviewIcon },
 *       { id: 'changes', label: 'Changes', count: 3 },
 *     ]}
 *   /&gt;
 * </pre>
 */
import { Box, Typography } from '@mui/material'
import type { SvgIconComponent } from '@mui/icons-material'
import {
  surfaceTokens,
  workspaceFontFamily,
  workspaceShadows,
  workspaceText,
} from '../designTokens'

export interface SegmentedTabItem<TId extends string = string> {
  id: TId
  label: string
  /** Optional leading icon — rendered at 11px next to the label. */
  icon?: SvgIconComponent
  /**
   * Optional badge count rendered as a tiny mono-font pill at the right of
   * the segment. Renders nothing when null/undefined.
   */
  count?: number | null
  /** Optional tooltip override; default has no tooltip. */
  title?: string
}

export interface SegmentedTabsProps<TId extends string = string> {
  value: TId
  onChange: (next: TId) => void
  items: SegmentedTabItem<TId>[]
  /** Optional aria-label for the tablist. */
  ariaLabel?: string
  /** Force every segment to the same width (handy in narrow chrome). */
  fullWidth?: boolean
}

export function SegmentedTabs<TId extends string = string>({
  value,
  onChange,
  items,
  ariaLabel,
  fullWidth,
}: SegmentedTabsProps<TId>) {
  return (
    <Box
      role="tablist"
      aria-label={ariaLabel}
      sx={{
        display: 'inline-flex',
        alignItems: 'center',
        gap: '2px',
        padding: '3px',
        borderRadius: '9px',
        backgroundColor: surfaceTokens.chipBg,
        border: `1px solid ${surfaceTokens.hairline}`,
        width: fullWidth ? '100%' : undefined,
      }}
    >
      {items.map((item) => {
        const active = value === item.id
        const Icon = item.icon
        return (
          <Box
            key={item.id}
            role="tab"
            aria-selected={active}
            tabIndex={active ? 0 : -1}
            onClick={() => onChange(item.id)}
            onKeyDown={(e) => {
              if (e.key === 'Enter' || e.key === ' ') {
                e.preventDefault()
                onChange(item.id)
              }
            }}
            title={item.title}
            sx={{
              display: 'inline-flex',
              alignItems: 'center',
              justifyContent: 'center',
              gap: '5px',
              height: 24,
              padding: '0 10px',
              borderRadius: '7px',
              border: 0,
              outline: 'none',
              backgroundColor: active ? surfaceTokens.surface : 'transparent',
              color: active ? workspaceText.primary : workspaceText.muted,
              fontWeight: active ? 600 : 500,
              fontSize: '0.71875rem',
              fontFamily: workspaceFontFamily.sans,
              letterSpacing: '-0.005em',
              cursor: 'pointer',
              boxShadow: active ? workspaceShadows.cardHover : 'none',
              transition: 'background-color 120ms ease, color 120ms ease, box-shadow 120ms ease',
              flex: fullWidth ? 1 : undefined,
              userSelect: 'none',
              '&:hover': !active
                ? { backgroundColor: surfaceTokens.chipHoverBg, color: workspaceText.primary }
                : undefined,
              '&:focus-visible': {
                outline: `2px solid ${workspaceText.primary}`,
                outlineOffset: 0,
              },
            }}
          >
            {Icon ? (
              <Icon
                sx={{
                  fontSize: 13,
                  color: active ? workspaceText.primary : workspaceText.muted,
                  transition: 'color 120ms ease',
                }}
              />
            ) : null}
            <Typography
              component="span"
              sx={{
                fontFamily: 'inherit',
                fontSize: 'inherit',
                fontWeight: 'inherit',
                letterSpacing: 'inherit',
                lineHeight: 1,
              }}
            >
              {item.label}
            </Typography>
            {item.count != null ? (
              <Typography
                component="span"
                sx={{
                  fontFamily: workspaceFontFamily.mono,
                  fontVariantNumeric: 'tabular-nums',
                  fontSize: '0.625rem',
                  fontWeight: 600,
                  color: active ? workspaceText.primary : workspaceText.faint,
                  backgroundColor: active ? surfaceTokens.chipBg : 'transparent',
                  padding: '1px 5px',
                  borderRadius: 999,
                  marginLeft: '1px',
                  lineHeight: 1,
                }}
              >
                {item.count}
              </Typography>
            ) : null}
          </Box>
        )
      })}
    </Box>
  )
}
