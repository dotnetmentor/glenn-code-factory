/**
 * Workspace CSS variables — wires the resolved Instrument tokens into a flat
 * `--ws-*` namespace so the workspace's `designTokens.ts` can reference them
 * as plain CSS variables (e.g. `var(--ws-canvas-bg)`).
 *
 * These vars are emitted by {@link buildInstrumentTheme} via the
 * `MuiCssBaseline.styleOverrides[':root']` slot. When the active (mode, accent)
 * changes, the theme rebuilds, emotion re-emits the baseline, and every
 * workspace surface reactively repaints — no component-level migration needed.
 *
 * The light-mode overlay tints (`text/X`) collapse to `rgba(29,29,31,X)`; the
 * dark-mode equivalents flip to `rgba(255,255,255,X)` so the same alpha
 * recipes (chips, hovers, accent rings) read correctly on either background.
 */

import type { InstrumentTokens } from './instrumentTokens'

const WS_VAR = {
  // Structural surfaces
  canvasBg: '--ws-canvas-bg',
  chromeBg: '--ws-chrome-bg',
  panelMuted: '--ws-panel-muted',
  surface: '--ws-surface',
  hairline: '--ws-hairline',
  hairlineStrong: '--ws-hairline-strong',
  chipBg: '--ws-chip-bg',
  chipHoverBg: '--ws-chip-hover-bg',
  rowHover: '--ws-row-hover',
  rowActive: '--ws-row-active',
  codeBg: '--ws-code-bg',
  codeBorder: '--ws-code-border',
  bubbleUser: '--ws-bubble-user',
  inputBg: '--ws-input-bg',

  // Log terminal — theme-aware console surface. In light mode these collapse
  // to the existing light chip/text tokens (so the terminal stays light in
  // today's light-only app); in dark mode they flip to the prototype's
  // high-contrast dark console palette. The hex literals live here in the
  // token layer rather than inline in the LogViewer components.
  terminalBg: '--ws-terminal-bg',
  terminalText: '--ws-terminal-text',
  terminalTextDim: '--ws-terminal-text-dim',
  terminalScrollThumb: '--ws-terminal-scroll-thumb',

  // Text
  textPrimary: '--ws-text-primary',
  textMuted: '--ws-text-muted',
  textFaint: '--ws-text-faint',
  textGhost: '--ws-text-ghost',
  textDisabled: '--ws-text-disabled',

  // Accent (live tied to current accent name)
  accentBase: '--ws-accent-base',
  accentSoft: '--ws-accent-soft',
  accentSurface: '--ws-accent-surface',
  accentRing: '--ws-accent-ring',
  accentOn: '--ws-accent-on',

  // Workspace accent overlays — ink/text-derived tints used for chip surfaces,
  // hover/active rows, focus rings. Flip with mode (light → ink/dark, dark → light).
  overlay06: '--ws-overlay-06',
  overlay08: '--ws-overlay-08',
  overlay10: '--ws-overlay-10',
  overlay14: '--ws-overlay-14',
  overlay18: '--ws-overlay-18',
  overlay25: '--ws-overlay-25',
  overlay35: '--ws-overlay-35',

  // Semantic
  runtimeOnline: '--ws-runtime-online',
  runtimeBooting: '--ws-runtime-booting',
  runtimeFailed: '--ws-runtime-failed',
  runtimeFailedHover: '--ws-runtime-failed-hover',
  runtimeSuspended: '--ws-runtime-suspended',
  successSoft: '--ws-success-soft',
  warningSoft: '--ws-warning-soft',
  errorSoft: '--ws-error-soft',
  info: '--ws-info',
} as const

export type WsTokenKey = keyof typeof WS_VAR

/** Build a `var(--ws-…)` reference string from a logical token name. */
export const wsVar = (name: WsTokenKey): string => `var(${WS_VAR[name]})`

/**
 * Mode-aware "ink overlay" recipe — light mode uses the dark ink at varying
 * alphas; dark mode flips to the light text color. Matches the prototype's
 * chip / hover / row / accent border ladder.
 */
function overlayRgba(mode: 'light' | 'dark', alpha: number): string {
  return mode === 'dark'
    ? `rgba(255,255,255,${alpha})`
    : `rgba(29,29,31,${alpha})`
}

/** Pre-baked "failed hover" — slightly darker error for hover states. */
function runtimeFailedHover(mode: 'light' | 'dark'): string {
  return mode === 'dark' ? '#ff7f7b' : '#a02a1f'
}

/**
 * Build the `:root` CSS variable bundle for the given resolved token set.
 *
 * Returns a record suitable for spreading into a `MuiCssBaseline.styleOverrides`
 * selector like `':root'` or `'body'`.
 */
export function buildWorkspaceCssVars(
  tokens: InstrumentTokens,
): Record<string, string> {
  const { mode, colors, text, semantic, accent } = tokens
  return {
    // Structural surfaces ──────────────────────────────────────────────────
    [WS_VAR.canvasBg]: colors.canvasBg,
    [WS_VAR.chromeBg]: colors.chromeBg,
    [WS_VAR.panelMuted]: colors.panelMuted,
    [WS_VAR.surface]: colors.surface,
    [WS_VAR.hairline]: colors.hairline,
    [WS_VAR.hairlineStrong]: colors.hairlineStrong,
    [WS_VAR.chipBg]: colors.chipBg,
    [WS_VAR.chipHoverBg]: colors.chipHoverBg,
    [WS_VAR.rowHover]: colors.rowHover,
    [WS_VAR.rowActive]: colors.rowActive,
    [WS_VAR.codeBg]: colors.codeBg,
    [WS_VAR.codeBorder]: colors.codeBorder,
    [WS_VAR.bubbleUser]: colors.bubbleUser,
    [WS_VAR.inputBg]: colors.inputBg,

    // Log terminal ───────────────────────────────────────────────────────────
    // Light mode: reuse the existing light chip surface + text tokens so the
    // terminal reads as part of today's light-only workspace. Dark mode: flip
    // to the prototype's near-black console (#0a0a0c) with off-white text.
    [WS_VAR.terminalBg]: mode === 'dark' ? '#0a0a0c' : colors.chipBg,
    [WS_VAR.terminalText]: mode === 'dark' ? '#e6e6e8' : text.primary,
    [WS_VAR.terminalTextDim]:
      mode === 'dark' ? 'rgba(230,230,232,0.45)' : text.faint,
    [WS_VAR.terminalScrollThumb]:
      mode === 'dark' ? 'rgba(230,230,232,0.20)' : 'rgba(29,29,31,0.18)',

    // Text ─────────────────────────────────────────────────────────────────
    [WS_VAR.textPrimary]: text.primary,
    [WS_VAR.textMuted]: text.muted,
    [WS_VAR.textFaint]: text.faint,
    [WS_VAR.textGhost]: text.ghost,
    [WS_VAR.textDisabled]: text.disabled,

    // Accent (current accent) ──────────────────────────────────────────────
    [WS_VAR.accentBase]: accent.base,
    [WS_VAR.accentSoft]: accent.soft,
    [WS_VAR.accentSurface]: accent.surface,
    [WS_VAR.accentRing]: accent.ring,
    [WS_VAR.accentOn]: accent.on,

    // Ink overlay ladder ───────────────────────────────────────────────────
    [WS_VAR.overlay06]: overlayRgba(mode, 0.06),
    [WS_VAR.overlay08]: overlayRgba(mode, 0.08),
    [WS_VAR.overlay10]: overlayRgba(mode, 0.10),
    [WS_VAR.overlay14]: overlayRgba(mode, 0.14),
    [WS_VAR.overlay18]: overlayRgba(mode, 0.18),
    [WS_VAR.overlay25]: overlayRgba(mode, 0.25),
    [WS_VAR.overlay35]: overlayRgba(mode, 0.35),

    // Semantic ─────────────────────────────────────────────────────────────
    [WS_VAR.runtimeOnline]: semantic.success,
    [WS_VAR.runtimeBooting]: semantic.warning,
    [WS_VAR.runtimeFailed]: semantic.error,
    [WS_VAR.runtimeFailedHover]: runtimeFailedHover(mode),
    [WS_VAR.runtimeSuspended]: semantic.suspended,
    [WS_VAR.successSoft]: semantic.successSoft,
    [WS_VAR.warningSoft]: semantic.warningSoft,
    [WS_VAR.errorSoft]: semantic.errorSoft,
    [WS_VAR.info]: semantic.info,
  }
}
