import { Box, IconButton, Tooltip } from '@mui/material'
import RefreshOutlinedIcon from '@mui/icons-material/RefreshOutlined'
import DesktopWindowsOutlinedIcon from '@mui/icons-material/DesktopWindowsOutlined'
import TabletMacOutlinedIcon from '@mui/icons-material/TabletMacOutlined'
import PhoneIphoneOutlinedIcon from '@mui/icons-material/PhoneIphoneOutlined'
import OpenInNewOutlinedIcon from '@mui/icons-material/OpenInNewOutlined'
import ContentCopyOutlinedIcon from '@mui/icons-material/ContentCopyOutlined'
import PublicOutlinedIcon from '@mui/icons-material/PublicOutlined'
import IosShareOutlinedIcon from '@mui/icons-material/IosShareOutlined'
import { workspaceChromeHeight } from '../../../../shared/designTokens'
import { appContainerTokens } from './tokens'
import {
  VIEWPORT_PRESETS,
  type ViewportId,
} from './viewport-presets'

interface PreviewChromeProps {
  /** Full preview URL (https://...) or null when no hostname is available. */
  previewUrl: string | null
  /** Currently active viewport profile. */
  viewport: ViewportId
  /** Fires when the user picks a different device profile. */
  onViewportChange: (next: ViewportId) => void
  /** Fires when the user clicks the reload button. */
  onReload: () => void
  /** Fires when the user clicks the URL pill (we copy it to clipboard). */
  onCopyUrl: () => void
  /** When true, render in a desaturated "no preview yet" mode. */
  disabled?: boolean
}

const VIEWPORT_ICON: Record<ViewportId, typeof DesktopWindowsOutlinedIcon> = {
  desktop: DesktopWindowsOutlinedIcon,
  tablet: TabletMacOutlinedIcon,
  mobile: PhoneIphoneOutlinedIcon,
}

const VIEWPORT_TOOLTIP: Record<ViewportId, string> = {
  desktop: 'Desktop',
  tablet: 'Tablet',
  mobile: 'Phone',
}

/**
 * Chrome bar that sits above the preview iframe.
 *
 * <p>Holds — from left to right — the reload button, a read-only URL pill
 * (globe icon + URL + click-to-copy), a three-way viewport segmented
 * control (Desktop / Tablet / Phone), a vertical hairline divider, then
 * the open-in-new-tab anchor and a placeholder Share button.</p>
 *
 * <p>The pill recipe matches the reference: 28px tall, fully-rounded
 * (999px), chip background with hairline border, mono URL text. The
 * device chip group reuses the same chip recipe so the two read as a
 * pair. All affordances tint to the accent on hover and stay quiet
 * otherwise — the chrome is service-trim, not advertising.</p>
 *
 * <p><b>Gotcha:</b> the vertical divider uses {@code width: '1px'} as a
 * literal string. MUI's {@code sx} treats numeric {@code width: 1} as
 * theme spacing {@code 100%}, which would expand the divider to fill
 * the row and crush every sibling to its right out of view.</p>
 */
export function PreviewChrome({
  previewUrl,
  viewport,
  onViewportChange,
  onReload,
  onCopyUrl,
  disabled = false,
}: PreviewChromeProps) {
  const effectiveOpacity = disabled ? 0.55 : 1
  const hasPreview = !!previewUrl

  return (
    <Box
      sx={{
        // Lock to {@link workspaceChromeHeight} so the preview URL bar
        // shares the same lid height as the sidebar workspace switcher
        // and the chat chrome — every hairline divider across the three
        // panels lines up on the same y-grid.
        height: workspaceChromeHeight,
        flexShrink: 0,
        px: '10px',
        display: 'flex',
        alignItems: 'center',
        gap: 1,
        backgroundColor: appContainerTokens.chromeBg,
        borderBottom: `1px solid ${appContainerTokens.hairline}`,
        opacity: effectiveOpacity,
        transition: 'opacity 200ms ease',
      }}
    >
      {/* Reload */}
      <Tooltip title="Reload preview" enterDelay={400}>
        <span>
          <IconButton
            size="small"
            onClick={onReload}
            disabled={disabled || !hasPreview}
            aria-label="Reload preview"
            sx={iconBtnSx}
          >
            <RefreshOutlinedIcon sx={{ fontSize: 14 }} />
          </IconButton>
        </span>
      </Tooltip>

      {/* URL pill */}
      <Box
        role="button"
        tabIndex={hasPreview && !disabled ? 0 : -1}
        onClick={() => {
          if (!hasPreview || disabled) return
          onCopyUrl()
        }}
        onKeyDown={(e) => {
          if (!hasPreview || disabled) return
          if (e.key === 'Enter' || e.key === ' ') {
            e.preventDefault()
            onCopyUrl()
          }
        }}
        aria-label={hasPreview ? `Copy preview URL ${previewUrl}` : 'No preview URL yet'}
        sx={{
          flex: 1,
          minWidth: 0,
          height: 28,
          pl: '10px',
          pr: '8px',
          display: 'flex',
          alignItems: 'center',
          gap: 1,
          borderRadius: 999,
          backgroundColor: appContainerTokens.chipBg,
          border: `1px solid ${appContainerTokens.hairline}`,
          cursor: hasPreview && !disabled ? 'pointer' : 'default',
          transition:
            'background-color 120ms ease, border-color 120ms ease',
          '&:hover':
            hasPreview && !disabled
              ? {
                  backgroundColor: appContainerTokens.chipHoverBg,
                  borderColor: appContainerTokens.accentBorder,
                }
              : undefined,
          '&:focus-visible': {
            outline: `2px solid ${appContainerTokens.accent}`,
            outlineOffset: 1,
          },
        }}
      >
        <PublicOutlinedIcon
          sx={{
            fontSize: 11,
            color: appContainerTokens.textFaint,
            flexShrink: 0,
          }}
        />
        <Box
          component="span"
          sx={{
            flex: 1,
            minWidth: 0,
            fontSize: '0.75rem', // 12px
            fontFamily: appContainerTokens.fontMono,
            color: hasPreview
              ? appContainerTokens.textPrimary
              : appContainerTokens.textFaint,
            letterSpacing: '-0.005em',
            overflow: 'hidden',
            textOverflow: 'ellipsis',
            whiteSpace: 'nowrap',
          }}
        >
          {previewUrl ?? 'No preview URL yet'}
        </Box>
        {hasPreview && (
          // Inner copy icon mirrors the reference's tiny chip-of-a-chip
          // recipe. Pulled out of the IconButton tap target — the whole
          // pill is the click target, this icon just signals affordance.
          <Box
            aria-hidden
            sx={{
              width: 20,
              height: 20,
              display: 'inline-flex',
              alignItems: 'center',
              justifyContent: 'center',
              borderRadius: '6px',
              color: appContainerTokens.textMuted,
              flexShrink: 0,
            }}
          >
            <ContentCopyOutlinedIcon sx={{ fontSize: 11 }} />
          </Box>
        )}
      </Box>

      {/* Device segmented control — same chip recipe as the URL pill so
          the two read as a pair. */}
      <Box
        role="group"
        aria-label="Viewport size"
        sx={{
          display: 'inline-flex',
          alignItems: 'center',
          gap: '1px',
          padding: '2px',
          borderRadius: '7px',
          border: `1px solid ${appContainerTokens.hairline}`,
          backgroundColor: appContainerTokens.chipBg,
          flexShrink: 0,
        }}
      >
        {VIEWPORT_PRESETS.map((preset) => {
          const Icon = VIEWPORT_ICON[preset.id]
          const active = preset.id === viewport
          return (
            <Tooltip
              key={preset.id}
              title={VIEWPORT_TOOLTIP[preset.id]}
              enterDelay={400}
            >
              <Box
                component="button"
                type="button"
                onClick={() => onViewportChange(preset.id)}
                disabled={disabled}
                aria-label={preset.label}
                aria-pressed={active}
                sx={{
                  width: 24,
                  height: 22,
                  display: 'inline-flex',
                  alignItems: 'center',
                  justifyContent: 'center',
                  borderRadius: '5px',
                  border: 0,
                  background: active
                    ? appContainerTokens.surface
                    : 'transparent',
                  color: active
                    ? appContainerTokens.textPrimary
                    : appContainerTokens.textMuted,
                  cursor: disabled ? 'default' : 'pointer',
                  boxShadow: active
                    ? appContainerTokens.shadowCardHover
                    : 'none',
                  transition:
                    'background-color 120ms ease, color 120ms ease, box-shadow 120ms ease',
                  '&:hover':
                    disabled || active
                      ? undefined
                      : {
                          color: appContainerTokens.textPrimary,
                          backgroundColor: appContainerTokens.chipHoverBg,
                        },
                  '&:focus-visible': {
                    outline: `2px solid ${appContainerTokens.accent}`,
                    outlineOffset: -2,
                  },
                  fontFamily: 'inherit',
                }}
              >
                <Icon sx={{ fontSize: 12 }} />
              </Box>
            </Tooltip>
          )
        })}
      </Box>

      {/* Vertical hairline. {@code width: '1px'} as a literal string —
          numeric {@code 1} would resolve to {@code 100%} via the theme
          spacing multiplier and crush the right cluster off-screen. */}
      <Box
        aria-hidden
        sx={{
          width: '1px',
          height: 18,
          backgroundColor: appContainerTokens.hairline,
          flexShrink: 0,
          mx: 0.25,
        }}
      />

      {/* Open in new tab */}
      <Tooltip title="Open in new tab" enterDelay={400}>
        <span>
          <IconButton
            size="small"
            component="a"
            href={previewUrl ?? undefined}
            target="_blank"
            rel="noreferrer"
            disabled={disabled || !hasPreview}
            aria-label="Open preview in new tab"
            sx={iconBtnSx}
          >
            <OpenInNewOutlinedIcon sx={{ fontSize: 13 }} />
          </IconButton>
        </span>
      </Tooltip>

      {/* Share — disabled stub. The reference includes it; we keep the
          chip present for layout symmetry and signal intent via tooltip,
          matching the composer's attach/slash treatment. */}
      <Tooltip title="Not implemented yet" enterDelay={400}>
        <span>
          <IconButton
            size="small"
            disabled
            aria-label="Share preview"
            sx={iconBtnSx}
          >
            <IosShareOutlinedIcon sx={{ fontSize: 13 }} />
          </IconButton>
        </span>
      </Tooltip>
    </Box>
  )
}

/**
 * Shared sx for the small icon buttons that flank the URL pill. Square
 * 26×26 tap target, transparent until hover, gentle chip-bg lift on
 * hover — matches the reference {@code appIconBtn} recipe.
 */
const iconBtnSx = {
  width: 26,
  height: 26,
  borderRadius: '6px',
  color: appContainerTokens.textMuted,
  padding: 0,
  transition: 'color 120ms ease, background-color 120ms ease',
  '&:hover': {
    color: appContainerTokens.textPrimary,
    backgroundColor: appContainerTokens.chipHoverBg,
  },
  '&.Mui-disabled': {
    color: appContainerTokens.textFaint,
  },
} as const
