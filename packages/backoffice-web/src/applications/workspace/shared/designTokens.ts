/**
 * Workspace design tokens — Instrument Mono single source of truth.
 *
 * Every color in this file is a `var(--ws-…)` reference rather than a literal,
 * so values flip reactively when {@link ThemeModeProvider} swaps the (mode,
 * accent) pair. The CSS variables themselves are emitted by the MUI theme via
 * {@link buildWorkspaceCssVars} into `:root`.
 *
 * Bundles:
 * - {@link surfaceTokens} — canvas, chrome, typography colors, structural surfaces
 * - {@link chromeTokens} — accent, hover/active, interactive chrome
 * - {@link semanticTokens} — runtime / status / meaning colors only
 *
 * Layout, spacing, and typography presets stay literal — they don't depend on
 * mode/accent.
 */

import { instrumentFontFamily } from '../../../themes/instrumentTokens'
import { wsVar } from '../../../themes/workspaceCssVars'

// ── Atomic primitives (stable public API) ───────────────────────────────────

export const workspaceColors = {
  canvasBg: wsVar('canvasBg'),
  chromeBg: wsVar('chromeBg'),
  hairline: wsVar('hairline'),
  hairlineWidth: '1px' as const,
  chipBg: wsVar('chipBg'),
  chipHoverBg: wsVar('chipHoverBg'),
  codeBg: wsVar('codeBg'),
  codeBorder: wsVar('codeBorder'),
  inputBg: wsVar('inputBg'),
  surface: wsVar('surface'),
  /** Whisper tint on white chrome — right-align + soft fill, not a boxed callout. */
  bubbleUser: wsVar('bubbleUser'),
  hairlineStrong: wsVar('hairlineStrong'),
}

export const workspaceText = {
  primary: wsVar('textPrimary'),
  muted: wsVar('textMuted'),
  faint: wsVar('textFaint'),
  ghost: wsVar('textGhost'),
  disabled: wsVar('textDisabled'),
}

/**
 * Log-terminal tokens — theme-aware console surface for the runtime debug
 * panel's Logs tab. In light mode these resolve to the existing light chip +
 * text tokens (so the terminal stays light in today's light-only app); in dark
 * mode they flip to the prototype's high-contrast near-black console palette.
 * The terminal "just works" (goes dark) when app-wide dark mode lands — no
 * component change required.
 */
export const workspaceTerminal = {
  /** Console background. Light: chip surface. Dark: near-black (#0a0a0c). */
  bg: wsVar('terminalBg'),
  /** Console body text. Light: primary ink. Dark: off-white (#e6e6e8). */
  text: wsVar('terminalText'),
  /** Greyed timestamps + cursor. Light: faint ink. Dark: dim off-white. */
  textDim: wsVar('terminalTextDim'),
  /** Scrollbar thumb tint, matched to the surface contrast. */
  scrollThumb: wsVar('terminalScrollThumb'),
}

/**
 * Accent (chrome) tokens — ink/text-derived overlays for chips, hovers,
 * borders, focus rings. The "ink" naming is historic — these now follow the
 * active accent and flip with mode (light → dark ink, dark → light text).
 */
export const workspaceAccent = {
  ink: workspaceText.primary,
  hover: workspaceText.primary,
  soft: wsVar('overlay08'),
  faint: wsVar('overlay06'),
  muted: wsVar('overlay08'),
  surface: wsVar('overlay10'),
  surfaceHover: wsVar('overlay14'),
  active: wsVar('overlay18'),
  border: wsVar('overlay25'),
  borderStrong: wsVar('overlay35'),
}

export const workspaceRuntime = {
  online: wsVar('runtimeOnline'),
  booting: wsVar('runtimeBooting'),
  failed: wsVar('runtimeFailed'),
  failedHover: wsVar('runtimeFailedHover'),
  suspended: wsVar('runtimeSuspended'),
  unknown: wsVar('runtimeSuspended'),
  /** Soft tints for tone-colored surfaces (icon tiles, usage bars). Flip with mode. */
  onlineSoft: wsVar('successSoft'),
  bootingSoft: wsVar('warningSoft'),
  failedSoft: wsVar('errorSoft'),
}

export const workspaceSpacing = {
  base: 8,
  breadcrumbSpine: 42,
  chromeStrip: 54,
} as const

/**
 * Canonical height of every top-of-panel chrome strip in the workspace.
 *
 * <p>Used by the sidebar header (workspace switcher row), the chat chrome
 * (title + cost + runtime pill + cog), and the app-container per-tab
 * chromes (preview URL bar, changes header, etc.). All three live on the
 * same horizontal y-grid so the workspace reads as one continuous shelf
 * rather than three independent panels with mismatched lids.</p>
 *
 * <p>Lifted from the reference design's {@code chromeH: 56} token —
 * 56px is the sweet spot where a 22px avatar + two-line text block on
 * the sidebar, a 28px input pill in the preview URL bar, and the chat
 * title cluster all sit comfortably without crowding the hairline.</p>
 *
 * <p>If you find yourself reaching for {@code height: 44} or {@code 52}
 * on a panel chrome — use this token instead so the horizontal divider
 * line stays unbroken across the three panels.</p>
 */
export const workspaceChromeHeight = 56 as const

export const workspaceFontFamily = instrumentFontFamily

/**
 * Workspace shadows — kept literal for now. Phase 0 doesn't require them to
 * flip; the dark mode shadows in `instrumentShadowsDark` will be wired in
 * when we audit shadow recipes per surface.
 */
export const workspaceShadows = {
  cardHover: '0 1px 2px rgba(29,29,31,0.04), 0 0 0 1px rgba(29,29,31,0.04)',
  menu: '0 6px 24px -8px rgba(29,29,31,0.10), 0 1px 2px rgba(29,29,31,0.04)',
  dialog: '0 16px 40px rgba(0,0,0,0.12), 0 4px 12px rgba(0,0,0,0.06)',
} as const

// ── Canonical bundles ─────────────────────────────────────────────────────

export const surfaceTokens = {
  canvasBg: workspaceColors.canvasBg,
  chromeBg: workspaceColors.chromeBg,
  surface: workspaceColors.surface,
  cardBg: workspaceColors.surface,
  hairline: workspaceColors.hairline,
  hairlineStrong: workspaceColors.hairlineStrong,
  textPrimary: workspaceText.primary,
  textMuted: workspaceText.muted,
  textFaint: workspaceText.faint,
  inputBg: workspaceColors.inputBg,
  chipBg: workspaceColors.chipBg,
  chipHoverBg: workspaceColors.chipHoverBg,
  codeBg: workspaceColors.codeBg,
  codeBorder: workspaceColors.codeBorder,
  bubbleUser: workspaceColors.bubbleUser,
} as const

export const chromeTokens = {
  accent: workspaceAccent.ink,
  accentSoft: workspaceAccent.soft,
  accentFaint: workspaceAccent.faint,
  accentMuted: workspaceAccent.muted,
  accentSurface: workspaceAccent.surface,
  accentSurfaceHover: workspaceAccent.surfaceHover,
  accentActive: workspaceAccent.active,
  accentBorder: workspaceAccent.border,
  accentBorderStrong: workspaceAccent.borderStrong,
  rowHover: workspaceColors.chipHoverBg,
  rowActive: workspaceAccent.soft,
  composerBg: workspaceColors.surface,
  bubbleAssistant: workspaceColors.chromeBg,
  statusDot: workspaceText.faint,
  editBg: workspaceColors.codeBg,
  paperBg: workspaceColors.canvasBg,
} as const

export const semanticTokens = {
  success: workspaceRuntime.online,
  successSoft: workspaceRuntime.onlineSoft,
  warning: workspaceRuntime.booting,
  warningSoft: workspaceRuntime.bootingSoft,
  error: workspaceRuntime.failed,
  errorSoft: workspaceRuntime.failedSoft,
  errorHover: workspaceRuntime.failedHover,
  info: wsVar('info'),
  suspended: workspaceRuntime.suspended,
  unknown: workspaceRuntime.unknown,
  danger: workspaceRuntime.failed,
  attention: workspaceRuntime.failed,
  failureDot: workspaceRuntime.failed,
  runningDot: workspaceRuntime.booting,
  successDot: workspaceRuntime.online,
  errorText: workspaceRuntime.failed,
  statusError: workspaceRuntime.failed,
} as const

/** RGB components for `rgba()` in CSS keyframes — derived from {@link semanticTokens.warning}. */
export const semanticWarningRgb = '255, 149, 0' as const

// ── Layout helpers (compose from surface) ───────────────────────────────────

export const pageCardSx = {
  border: `1px solid ${surfaceTokens.hairline}`,
  borderRadius: 2,
  backgroundColor: surfaceTokens.cardBg,
} as const

export const pageCardPaddedSx = {
  ...pageCardSx,
  p: { xs: 2.5, md: 3 },
} as const

export const pageCardFlushSx = {
  ...pageCardSx,
  overflow: 'hidden',
} as const

export const pageCardEmptySx = {
  ...pageCardSx,
  border: `1px dashed ${surfaceTokens.hairline}`,
} as const

/** Default width of the projects + branches sidebar (desktop). */
export const workspaceSidebarWidth = 248

/** Desktop workspace canvas insets — gaps and radii for floating panels. */
export const workspaceCanvasInset = {
  /** Outer padding around the shell (sidebar + main column). */
  desktopPadding: 1.5,
  /** Horizontal / vertical gap between major panels. */
  panelGap: 1,
  panelGapLg: 1.5,
  /** Corner radius for sidebar, chat, and app surfaces. */
  panelRadius: 1.5,
} as const

/** Shared shell for floating workspace panels (sidebar, chat, app, chrome). */
export const workspacePanelShellSx = {
  borderRadius: workspaceCanvasInset.panelRadius,
  border: `1px solid ${surfaceTokens.hairline}`,
  backgroundColor: surfaceTokens.chromeBg,
  overflow: 'hidden',
} as const

// ── Typography presets ─────────────────────────────────────────────────────

const SANS = workspaceFontFamily.sans

export const pageTitleSx = {
  fontSize: { xs: '1.5rem', md: '2rem' },
  fontWeight: 600,
  letterSpacing: '-0.02em',
  color: workspaceText.primary,
  fontFamily: SANS,
  lineHeight: 1.2,
}

export const sectionTitleSx = {
  fontSize: '1rem',
  fontWeight: 600,
  letterSpacing: '-0.005em',
  color: workspaceText.primary,
  fontFamily: SANS,
  lineHeight: 1.4,
}

export const bodySx = {
  fontSize: '0.9375rem',
  fontWeight: 500,
  lineHeight: 1.55,
  letterSpacing: '-0.005em',
  color: workspaceText.muted,
  fontFamily: SANS,
} as const

export const captionSx = {
  fontSize: '0.8125rem',
  letterSpacing: '-0.005em',
  color: workspaceText.muted,
  fontFamily: SANS,
  lineHeight: 1.5,
} as const

export const labelSx = {
  fontSize: '0.6875rem',
  fontWeight: 600,
  textTransform: 'uppercase',
  letterSpacing: '0.06em',
  color: workspaceText.faint,
  fontFamily: SANS,
  lineHeight: 1.5,
} as const

/**
 * Tabular-mono number style — monospace font + tabular figures so numeric
 * columns (uptime, PID, MEM RSS, CPU%, line counts, durations) line up on a
 * fixed grid and don't jitter as values tick. Spread into any {@code sx} that
 * renders a number that should read as a metric rather than prose.
 *
 * <pre>
 *   &lt;Box component="span" sx={{ ...monoNumberSx, color: workspaceText.muted }}&gt;
 *     {uptime}
 *   &lt;/Box&gt;
 * </pre>
 */
export const monoNumberSx = {
  fontFamily: workspaceFontFamily.mono,
  fontVariantNumeric: 'tabular-nums',
} as const

export const workspaceTokens = {
  ...workspaceColors,
  text: workspaceText,
  accent: workspaceAccent.ink,
  runtime: workspaceRuntime,
  spacing: workspaceSpacing,
  fontFamily: workspaceFontFamily,
  shadows: workspaceShadows,
} as const

export type WorkspaceTokens = typeof workspaceTokens
