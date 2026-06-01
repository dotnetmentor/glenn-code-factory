/**
 * Theme barrel — re-exports the Instrument Mono runtime.
 *
 * The previous flat singletons (`activeTheme`, `instrumentTheme`) are gone —
 * the theme is now constructed per (mode, accent) by
 * {@link ThemeModeProvider}. Import from here for convenience or directly from
 * `./themes`.
 */
export {
  ThemeModeProvider,
  useThemeMode,
  buildInstrumentTheme,
} from './themes'
