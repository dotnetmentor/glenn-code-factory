/**
 * Instrument Mono — MUI theme tokens (light + dark + 4 accents).
 *
 * Two themes (light/dark) plus four accent presets. The flat singleton exports
 * are gone — consumers call {@link getInstrumentTokens} to resolve a bundle for
 * a given (mode, accent) pair.
 *
 * Companion files:
 * - {@link ThemeModeProvider} owns the (mode, accent) state at runtime.
 * - {@link buildInstrumentTheme} turns a resolved bundle into a MUI Theme.
 * - Workspace consumers live under
 *   `applications/workspace/shared/designTokens.ts`.
 */

export type ThemeMode = 'light' | 'dark'
export type AccentName = 'ink' | 'amber' | 'violet' | 'green'

/** Structural surface colors — backgrounds, hairlines, chips, code/bubble fills. */
export interface InstrumentColors {
  canvasBg: string
  chromeBg: string
  panelMuted: string
  surface: string
  hairline: string
  hairlineStrong: string
  chipBg: string
  chipHoverBg: string
  rowHover: string
  rowActive: string
  codeBg: string
  codeBorder: string
  bubbleUser: string
  inputBg: string
}

export const instrumentColorsLight: InstrumentColors = {
  canvasBg: '#f5f3ef',
  chromeBg: '#ffffff',
  panelMuted: '#fbfaf7',
  surface: '#ffffff',
  hairline: 'rgba(29,29,31,0.08)',
  hairlineStrong: 'rgba(29,29,31,0.14)',
  chipBg: 'rgba(29,29,31,0.05)',
  chipHoverBg: 'rgba(29,29,31,0.08)',
  rowHover: 'rgba(29,29,31,0.035)',
  rowActive: 'rgba(29,29,31,0.06)',
  codeBg: '#f6f4ef',
  codeBorder: 'rgba(29,29,31,0.08)',
  bubbleUser: 'rgba(29,29,31,0.045)',
  inputBg: '#ffffff',
}

export const instrumentColorsDark: InstrumentColors = {
  canvasBg: '#0d0d0f',
  chromeBg: '#161618',
  panelMuted: '#111113',
  surface: '#161618',
  hairline: 'rgba(255,255,255,0.07)',
  hairlineStrong: 'rgba(255,255,255,0.14)',
  chipBg: 'rgba(255,255,255,0.06)',
  chipHoverBg: 'rgba(255,255,255,0.10)',
  rowHover: 'rgba(255,255,255,0.04)',
  rowActive: 'rgba(255,255,255,0.07)',
  codeBg: 'rgba(255,255,255,0.04)',
  codeBorder: 'rgba(255,255,255,0.07)',
  bubbleUser: 'rgba(255,255,255,0.06)',
  inputBg: '#1a1a1d',
}

/** Typography colors — alpha-tinted ink scale (matches prototype spec). */
export interface InstrumentText {
  primary: string
  muted: string
  faint: string
  ghost: string
  disabled: string
}

export const instrumentTextLight: InstrumentText = {
  primary: '#1d1d1f',
  muted: 'rgba(29,29,31,0.62)',
  faint: 'rgba(29,29,31,0.42)',
  ghost: 'rgba(29,29,31,0.28)',
  disabled: 'rgba(29,29,31,0.36)',
}

export const instrumentTextDark: InstrumentText = {
  primary: '#f4f4f6',
  muted: 'rgba(244,244,246,0.62)',
  faint: 'rgba(244,244,246,0.42)',
  ghost: 'rgba(244,244,246,0.26)',
  disabled: 'rgba(244,244,246,0.32)',
}

/** Semantic colors — runtime / status / meaning only. */
export interface InstrumentSemantic {
  success: string
  successSoft: string
  warning: string
  warningSoft: string
  error: string
  errorSoft: string
  errorDark: string
  info: string
  suspended: string
}

export const instrumentSemanticLight: InstrumentSemantic = {
  success: '#117a4a',
  successSoft: 'rgba(17,122,74,0.10)',
  warning: '#b8550a',
  warningSoft: 'rgba(184,85,10,0.10)',
  error: '#c0392b',
  errorSoft: 'rgba(192,57,43,0.10)',
  errorDark: '#a02a1f',
  info: '#007aff',
  suspended: 'rgba(29,29,31,0.36)',
}

export const instrumentSemanticDark: InstrumentSemantic = {
  success: '#3ec48a',
  successSoft: 'rgba(62,196,138,0.12)',
  warning: '#f59e0b',
  warningSoft: 'rgba(245,158,11,0.14)',
  error: '#ef5350',
  errorSoft: 'rgba(239,83,80,0.14)',
  errorDark: '#ff6f6b',
  info: '#0a84ff',
  suspended: 'rgba(244,244,246,0.32)',
}

/** Accent presets — each is a triple of base/soft/surface/ring + `on` foreground. */
export interface InstrumentAccent {
  base: string
  soft: string
  surface: string
  ring: string
  on: string
}

export const instrumentAccents: Record<AccentName, InstrumentAccent> = {
  ink:    { base: '#1d1d1f', soft: 'rgba(29,29,31,0.08)',  surface: 'rgba(29,29,31,0.06)',  ring: 'rgba(29,29,31,0.16)', on: '#ffffff' },
  amber:  { base: '#b8550a', soft: 'rgba(184,85,10,0.10)', surface: 'rgba(184,85,10,0.07)', ring: 'rgba(184,85,10,0.30)', on: '#ffffff' },
  violet: { base: '#5b3df5', soft: 'rgba(91,61,245,0.10)', surface: 'rgba(91,61,245,0.07)', ring: 'rgba(91,61,245,0.30)', on: '#ffffff' },
  green:  { base: '#117a4a', soft: 'rgba(17,122,74,0.10)', surface: 'rgba(17,122,74,0.07)', ring: 'rgba(17,122,74,0.30)', on: '#ffffff' },
}

/** Ordered list of accent names — handy for pickers / iterators. */
export const ACCENT_NAMES: readonly AccentName[] = ['ink', 'amber', 'violet', 'green'] as const

/** Shadow tokens — depth cues for cards, menus, dialogs. */
export interface InstrumentShadows {
  cardHover: string
  menu: string
  dialog: string
}

export const instrumentShadowsLight: InstrumentShadows = {
  cardHover: '0 1px 2px rgba(29,29,31,0.04), 0 0 0 1px rgba(29,29,31,0.04)',
  menu: '0 6px 24px -8px rgba(29,29,31,0.10), 0 1px 2px rgba(29,29,31,0.04)',
  dialog: '0 16px 40px rgba(0,0,0,0.12), 0 4px 12px rgba(0,0,0,0.06)',
}

export const instrumentShadowsDark: InstrumentShadows = {
  cardHover: '0 1px 2px rgba(0,0,0,0.4), 0 0 0 1px rgba(255,255,255,0.04)',
  menu: '0 8px 24px -6px rgba(0,0,0,0.6), 0 1px 2px rgba(0,0,0,0.4)',
  dialog: '0 18px 48px rgba(0,0,0,0.7), 0 6px 16px rgba(0,0,0,0.5)',
}

/**
 * Fonts — mode-agnostic. Geist is loaded via Google Fonts in `index.html`;
 * system fallbacks cover the case where Google Fonts is blocked.
 *
 * Geist (sans) is the primary workspace face — geometric, low-contrast,
 * tuned for UI density. Geist Mono is the matching monospace for code blocks,
 * filenames, hashes, and inline `<code>` chips.
 */
export const instrumentFontFamily = {
  sans:
    '"Geist", -apple-system, BlinkMacSystemFont, "SF Pro Display", "Segoe UI", system-ui, sans-serif',
  mono:
    '"Geist Mono", ui-monospace, "SF Mono", SFMono-Regular, Menlo, Monaco, Consolas, "Liberation Mono", monospace',
} as const

/** Resolved token bundle for a (mode, accent) pair. */
export interface InstrumentTokens {
  mode: ThemeMode
  accentName: AccentName
  colors: InstrumentColors
  text: InstrumentText
  semantic: InstrumentSemantic
  accent: InstrumentAccent
  shadows: InstrumentShadows
  fontFamily: typeof instrumentFontFamily
}

/**
 * Resolve the full token bundle for a (mode, accent) pair.
 * This is the single entry-point used by both the MUI theme builder and
 * workspace component tokens.
 */
export function getInstrumentTokens(
  mode: ThemeMode,
  accentName: AccentName = 'ink',
): InstrumentTokens {
  const isDark = mode === 'dark'
  return {
    mode,
    accentName,
    colors: isDark ? instrumentColorsDark : instrumentColorsLight,
    text: isDark ? instrumentTextDark : instrumentTextLight,
    semantic: isDark ? instrumentSemanticDark : instrumentSemanticLight,
    accent: instrumentAccents[accentName],
    shadows: isDark ? instrumentShadowsDark : instrumentShadowsLight,
    fontFamily: instrumentFontFamily,
  }
}
