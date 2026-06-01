/**
 * Public surface for the Instrument Mono theme.
 *
 * - {@link ThemeModeProvider} — wrap your app in this. Owns (mode, accent) and
 *   builds the MUI theme reactively.
 * - {@link useThemeMode} — hook for reading/writing the current mode + accent.
 * - {@link buildInstrumentTheme} — low-level: build a Theme for a given pair.
 * - Token resolvers and per-mode/per-accent constants for advanced callers.
 */

export { buildInstrumentTheme } from './instrumentTheme'

export {
  ThemeModeProvider,
  useThemeMode,
  type ThemeModeProviderProps,
} from './ThemeModeProvider'

export {
  ACCENT_NAMES,
  getInstrumentTokens,
  instrumentAccents,
  instrumentColorsDark,
  instrumentColorsLight,
  instrumentFontFamily,
  instrumentSemanticDark,
  instrumentSemanticLight,
  instrumentShadowsDark,
  instrumentShadowsLight,
  instrumentTextDark,
  instrumentTextLight,
} from './instrumentTokens'

export type {
  AccentName,
  InstrumentAccent,
  InstrumentColors,
  InstrumentSemantic,
  InstrumentShadows,
  InstrumentText,
  InstrumentTokens,
  ThemeMode,
} from './instrumentTokens'
