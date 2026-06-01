import {
  surfaceTokens,
  chromeTokens,
  workspaceFontFamily,
  workspaceShadows,
} from '../../../../shared/designTokens'

/**
 * Local token bundle for the AppContainer right-pane (tab strip, preview
 * chrome, per-tab secondary headers).
 *
 * <p>Pulls from the project-wide {@code surfaceTokens} / {@code chromeTokens}
 * so a single accent / mode flip cascades cleanly. We expose specific names
 * here rather than re-exporting the full bundles to keep the AppContainer
 * call-sites short and intent-revealing — when you see
 * {@code appContainerTokens.chipBg} you know it's the SegmentedTabs container
 * fill, not a generic "chip" somewhere.</p>
 */
export const appContainerTokens = {
  // Surfaces
  canvasBg: surfaceTokens.chromeBg,
  chromeBg: surfaceTokens.chromeBg,
  surface: surfaceTokens.surface,
  chipBg: surfaceTokens.chipBg,
  chipHoverBg: surfaceTokens.chipHoverBg,

  // Borders
  hairline: surfaceTokens.hairline,
  hairlineStrong: surfaceTokens.hairlineStrong,

  // Type
  textPrimary: surfaceTokens.textPrimary,
  textMuted: surfaceTokens.textMuted,
  textFaint: surfaceTokens.textFaint,
  /**
   * Telemetry / count-badge / faint URL placeholder text. Slightly
   * thinner than {@code textFaint} when the surrounding token bundle
   * exposes a separate ghost level; falls back to {@code textFaint}
   * otherwise so callers don't need to feature-detect.
   */
  textGhost: surfaceTokens.textFaint,

  // Accent (chrome)
  accent: chromeTokens.accent,
  accentFaint: chromeTokens.accentFaint,
  accentMuted: chromeTokens.accentMuted,
  accentSoft: chromeTokens.accentSoft,
  accentSurface: chromeTokens.accentSurface,
  accentSurfaceHover: chromeTokens.accentSurfaceHover,
  accentActive: chromeTokens.accentActive,
  accentBorder: chromeTokens.accentBorder,
  accentBorderStrong: chromeTokens.accentBorderStrong,

  // Typography
  fontMono: workspaceFontFamily.mono,
  fontSans: workspaceFontFamily.sans,

  // Shadows
  shadowCardHover: workspaceShadows.cardHover,
} as const
