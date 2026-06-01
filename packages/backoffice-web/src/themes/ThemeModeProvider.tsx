/**
 * ThemeModeProvider — runtime owner of (mode, accent).
 *
 * Holds the current theme mode + accent, rebuilds the MUI theme via
 * {@link buildInstrumentTheme} when either changes, and persists the choice to
 * localStorage. Initial values are read from URL params (`?theme=dark&accent=violet`)
 * first, then localStorage, then default to `('light', 'ink')`.
 *
 * Consumers read/write through {@link useThemeMode}.
 */

import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useState,
  type ReactNode,
} from 'react'
import { ThemeProvider as MuiThemeProvider } from '@mui/material/styles'
import { buildInstrumentTheme } from './instrumentTheme'
import {
  ACCENT_NAMES,
  type AccentName,
  type ThemeMode,
} from './instrumentTokens'

const MODE_STORAGE_KEY = 'workspace:theme'
const ACCENT_STORAGE_KEY = 'workspace:accent'

interface ThemeModeContextValue {
  mode: ThemeMode
  accent: AccentName
  setMode: (mode: ThemeMode) => void
  setAccent: (accent: AccentName) => void
  toggleMode: () => void
}

const ThemeModeContext = createContext<ThemeModeContextValue | null>(null)

function isMode(value: unknown): value is ThemeMode {
  return value === 'light' || value === 'dark'
}

function isAccent(value: unknown): value is AccentName {
  return typeof value === 'string' && (ACCENT_NAMES as readonly string[]).includes(value)
}

function readUrlParam(name: string): string | null {
  if (typeof window === 'undefined') return null
  try {
    return new URLSearchParams(window.location.search).get(name)
  } catch {
    return null
  }
}

function readLocalStorage(key: string): string | null {
  if (typeof window === 'undefined') return null
  try {
    return window.localStorage.getItem(key)
  } catch {
    return null
  }
}

function writeLocalStorage(key: string, value: string): void {
  if (typeof window === 'undefined') return
  try {
    window.localStorage.setItem(key, value)
  } catch {
    /* ignore — non-fatal */
  }
}

function resolveInitial<T extends string>(
  urlName: string,
  storageKey: string,
  isValid: (value: unknown) => value is T,
  fallback: T,
): T {
  const fromUrl = readUrlParam(urlName)
  if (fromUrl && isValid(fromUrl)) return fromUrl
  const fromStorage = readLocalStorage(storageKey)
  if (fromStorage && isValid(fromStorage)) return fromStorage
  return fallback
}

export interface ThemeModeProviderProps {
  children: ReactNode
  /** Override the resolved initial mode (mostly for tests / Storybook). */
  defaultMode?: ThemeMode
  /** Override the resolved initial accent (mostly for tests / Storybook). */
  defaultAccent?: AccentName
}

export function ThemeModeProvider({
  children,
  defaultMode,
  defaultAccent,
}: ThemeModeProviderProps) {
  const [mode, setModeState] = useState<ThemeMode>(() =>
    defaultMode ?? resolveInitial<ThemeMode>('theme', MODE_STORAGE_KEY, isMode, 'light'),
  )
  const [accent, setAccentState] = useState<AccentName>(() =>
    defaultAccent ?? resolveInitial<AccentName>('accent', ACCENT_STORAGE_KEY, isAccent, 'ink'),
  )

  const setMode = useCallback((next: ThemeMode) => {
    setModeState(next)
    writeLocalStorage(MODE_STORAGE_KEY, next)
  }, [])

  const setAccent = useCallback((next: AccentName) => {
    setAccentState(next)
    writeLocalStorage(ACCENT_STORAGE_KEY, next)
  }, [])

  const toggleMode = useCallback(() => {
    setModeState((current) => {
      const next: ThemeMode = current === 'light' ? 'dark' : 'light'
      writeLocalStorage(MODE_STORAGE_KEY, next)
      return next
    })
  }, [])

  const theme = useMemo(() => buildInstrumentTheme(mode, accent), [mode, accent])

  // Reflect on <html> so CSS-only callers and the browser native form widgets
  // (color-scheme, autofill) react to the change.
  useEffect(() => {
    const root = document.documentElement
    root.dataset.themeMode = mode
    root.dataset.themeAccent = accent
    root.style.colorScheme = mode
  }, [mode, accent])

  const value = useMemo<ThemeModeContextValue>(
    () => ({ mode, accent, setMode, setAccent, toggleMode }),
    [mode, accent, setMode, setAccent, toggleMode],
  )

  return (
    <ThemeModeContext.Provider value={value}>
      <MuiThemeProvider theme={theme}>{children}</MuiThemeProvider>
    </ThemeModeContext.Provider>
  )
}

/** Read/write current theme mode + accent. Must be used inside {@link ThemeModeProvider}. */
export function useThemeMode(): ThemeModeContextValue {
  const ctx = useContext(ThemeModeContext)
  if (!ctx) {
    throw new Error('useThemeMode must be used inside <ThemeModeProvider>')
  }
  return ctx
}
