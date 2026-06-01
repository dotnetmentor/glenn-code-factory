/**
 * UnderlineTabs — accent-underline tab bar used as the debug panel's primary
 * tab strip.
 *
 * <p>Where {@link SegmentedTabs} is a compact pill group for in-pane view
 * toggles, this is the larger, calmer "top of the panel" tab bar from the
 * prototype's {@code DrawerTabs}: a hairline-divided row where the active tab
 * carries a 2px accent underline and brighter, heavier text. Use it for the
 * panel-level Logs / Services / Timeline / Sysstats / Spec / Fly switch.
 *
 * <pre>
 *   &lt;UnderlineTabs
 *     value={tab}
 *     onChange={setTab}
 *     items={[
 *       { id: 'logs', label: 'Logs' },
 *       { id: 'services', label: 'Services' },
 *     ]}
 *   /&gt;
 * </pre>
 *
 * <p>Colors come from {@link chromeTokens} / {@link surfaceTokens} /
 * {@link workspaceText}, so the bar flips light → dark with the workspace and
 * tracks the active accent.
 */
import { Box, Typography } from '@mui/material'
import {
  chromeTokens,
  surfaceTokens,
  workspaceFontFamily,
  workspaceText,
} from '../designTokens'

export interface UnderlineTabItem<TId extends string = string> {
  id: TId
  label: string
}

export interface UnderlineTabsProps<TId extends string = string> {
  value: TId
  onChange: (next: TId) => void
  items: UnderlineTabItem<TId>[]
  /** Optional aria-label for the tablist. */
  ariaLabel?: string
}

export function UnderlineTabs<TId extends string = string>({
  value,
  onChange,
  items,
  ariaLabel,
}: UnderlineTabsProps<TId>) {
  return (
    <Box
      role="tablist"
      aria-label={ariaLabel}
      sx={{
        display: 'flex',
        gap: 0.5,
        px: 2,
        borderBottom: `1px solid ${surfaceTokens.hairline}`,
        flexShrink: 0,
      }}
    >
      {items.map((item) => {
        const active = value === item.id
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
            sx={{
              position: 'relative',
              padding: '12px 14px 11px',
              border: 0,
              outline: 'none',
              backgroundColor: 'transparent',
              color: active ? workspaceText.primary : workspaceText.muted,
              fontWeight: active ? 600 : 500,
              fontSize: '0.8125rem',
              fontFamily: workspaceFontFamily.sans,
              letterSpacing: '-0.005em',
              lineHeight: 1,
              cursor: 'pointer',
              userSelect: 'none',
              transition: 'color 120ms ease',
              '&:hover': !active ? { color: workspaceText.primary } : undefined,
              '&:focus-visible': {
                outline: `2px solid ${workspaceText.primary}`,
                outlineOffset: -2,
                borderRadius: '4px',
              },
            }}
          >
            <Typography
              component="span"
              sx={{
                fontFamily: 'inherit',
                fontSize: 'inherit',
                fontWeight: 'inherit',
                letterSpacing: 'inherit',
                lineHeight: 'inherit',
              }}
            >
              {item.label}
            </Typography>
            {active ? (
              <Box
                aria-hidden
                sx={{
                  position: 'absolute',
                  left: 12,
                  right: 12,
                  bottom: -1,
                  height: 2,
                  backgroundColor: chromeTokens.accent,
                  borderRadius: 999,
                }}
              />
            ) : null}
          </Box>
        )
      })}
    </Box>
  )
}
