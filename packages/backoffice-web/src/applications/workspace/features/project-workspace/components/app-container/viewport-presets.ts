/**
 * Viewport presets for the AppContainer Preview tab.
 *
 * <p>Three calm device profiles inspired by Glenn's preview frame. Desktop
 * fills the column (100% width). Tablet and Mobile are letterboxed to a
 * fixed width and centered with a hairline border so the user can sanity-
 * check their app at realistic widths without leaving the workspace.</p>
 *
 * <p>Promoting these to the theme is a non-goal; the AppContainer is a
 * self-contained surface with its own restrained vocabulary.</p>
 */
export type ViewportId = 'desktop' | 'tablet' | 'mobile'

export interface ViewportPreset {
  id: ViewportId
  label: string
  /** CSS width value — either '100%' (Desktop) or a fixed pixel string. */
  width: string
  /** Numeric pixel width for letterbox sizing logic. Null for Desktop. */
  pixelWidth: number | null
}

export const VIEWPORT_PRESETS: readonly ViewportPreset[] = [
  { id: 'desktop', label: 'Desktop', width: '100%', pixelWidth: null },
  { id: 'tablet', label: 'Tablet', width: '768px', pixelWidth: 768 },
  { id: 'mobile', label: 'Mobile', width: '390px', pixelWidth: 390 },
] as const

export const DEFAULT_VIEWPORT: ViewportId = 'desktop'

export function getViewportPreset(id: ViewportId): ViewportPreset {
  return VIEWPORT_PRESETS.find((v) => v.id === id) ?? VIEWPORT_PRESETS[0]
}
