import { createTheme, type Theme } from '@mui/material/styles'
import '@mui/x-data-grid/themeAugmentation'
import {
  getInstrumentTokens,
  type AccentName,
  type ThemeMode,
} from './instrumentTokens'
import { buildWorkspaceCssVars } from './workspaceCssVars'
import {
  buildInstrumentTypography,
  buildSharedAlertColorOverrides,
  buildSharedAlertOverrides,
  buildSharedButtonColorOverrides,
  buildSharedButtonOverrides,
  buildSharedChipOverrides,
  buildSharedCssBaselineOverrides,
  buildSharedDataGridOverrides,
  buildSharedDialogOverrides,
  buildSharedFormControlOverrides,
  buildSharedFormHelperTextOverrides,
  buildSharedIconButtonOverrides,
  buildSharedInputLabelOverrides,
  buildSharedMenuOverrides,
  buildSharedMiscOverrides,
  buildSharedNavigationOverrides,
  buildSharedOutlinedInputOverrides,
  buildSharedSurfaceOverrides,
  buildSharedSwitchOverrides,
  buildSharedTableOverrides,
  type InstrumentSemanticColors,
  type SharedThemePalette,
} from './sharedMuiOverrides'

// =============================================================================
// Instrument Mono — MUI theme assembly
//
// Palette + typography live here; component overrides delegate to sharedMuiOverrides.
// The (mode, accent) parameters are resolved into a token bundle via
// `getInstrumentTokens`, then fed into MUI's createTheme.
// =============================================================================

const tint = (hex: string, alphaHex: string) => `${hex}${alphaHex}`

/**
 * Build a MUI theme for the given mode + accent.
 *
 * @param mode    - 'light' (default) or 'dark'
 * @param accent  - 'ink' (default), 'amber', 'violet', or 'green'
 */
export function buildInstrumentTheme(
  mode: ThemeMode = 'light',
  accent: AccentName = 'ink',
): Theme {
  const tokens = getInstrumentTokens(mode, accent)
  const { colors, text, semantic, accent: accentTriple, shadows, fontFamily } = tokens
  const isDark = mode === 'dark'

  const SANS = fontFamily.sans
  const INK = text.primary
  // Slightly off-primary for hover states — works as "next step" of the ink scale.
  const INK_HOVER = isDark ? '#e1e1e3' : '#2c2c2e'
  // Pill / CTA fill: always reads as ink-on-canvas — flips in dark mode so it
  // stays visible rather than fading into the background.
  const PILL_FILL = isDark ? text.primary : INK
  const PILL_FILL_HOVER = isDark ? INK_HOVER : INK_HOVER
  const PILL_CONTRAST = isDark ? colors.canvasBg : '#ffffff'

  const semanticColors: InstrumentSemanticColors = {
    success: semantic.success,
    warning: semantic.warning,
    error: semantic.error,
    info: semantic.info,
  }

  const sharedPalette: SharedThemePalette = {
    canvasBg: colors.canvasBg,
    chromeBg: colors.chromeBg,
    surface: colors.surface,
    hairline: colors.hairline,
    hairlineStrong: colors.hairlineStrong,
    ink: INK,
    inkHover: INK_HOVER,
    textPrimary: text.primary,
    textMuted: text.muted,
    textFaint: text.faint,
    textDisabled: text.disabled,
    inputBg: colors.inputBg,
    chipBg: colors.chipBg,
    chipHoverBg: colors.chipHoverBg,
    accent: accentTriple.base,
    accentHover: accentTriple.base,
    errorMain: semantic.error,
    errorDark: semantic.errorDark,
    sans: SANS,
    scrollbarThumb: isDark ? 'rgba(255,255,255,0.18)' : '#DDDDD9',
    scrollbarThumbHover: isDark ? 'rgba(255,255,255,0.28)' : '#C8C8C3',
    pillFill: PILL_FILL,
    pillFillHover: PILL_FILL_HOVER,
    pillContrast: PILL_CONTRAST,
  }

  const sharedDialog = buildSharedDialogOverrides(sharedPalette, shadows.dialog)
  const sharedButton = buildSharedButtonOverrides(sharedPalette)
  const sharedAlert = buildSharedAlertOverrides(sharedPalette)
  const navigation = buildSharedNavigationOverrides(sharedPalette, colors.chromeBg)
  const surfaces = buildSharedSurfaceOverrides(sharedPalette, shadows, colors.surface)
  const tables = buildSharedTableOverrides(sharedPalette)
  const menus = buildSharedMenuOverrides(sharedPalette, shadows, colors.surface)
  const formControls = buildSharedFormControlOverrides(sharedPalette)
  const misc = buildSharedMiscOverrides(sharedPalette, shadows)

  // Workspace CSS variables — emitted on :root so `designTokens.ts` can
  // reference them as `var(--ws-…)`. Re-emitted whenever (mode, accent)
  // changes, which is what makes the workspace flip reactively.
  const wsCssVars = buildWorkspaceCssVars(tokens)
  const baseCssBaseline = buildSharedCssBaselineOverrides(sharedPalette, isDark)
  const baseStyleOverrides =
    (baseCssBaseline?.styleOverrides as Record<string, unknown> | undefined) ?? {}
  const cssBaselineWithVars: typeof baseCssBaseline = {
    ...baseCssBaseline,
    styleOverrides: {
      ...baseStyleOverrides,
      ':root': {
        ...((baseStyleOverrides[':root'] as Record<string, unknown> | undefined) ?? {}),
        ...wsCssVars,
      },
    },
  }

  return createTheme({
    cssVariables: true,

    palette: {
      mode,
      primary: {
        main: accentTriple.base,
        dark: isDark ? '#000000' : '#000000',
        light: INK_HOVER,
        contrastText: accentTriple.on,
      },
      secondary: {
        main: text.muted,
        dark: text.disabled,
        light: text.faint,
        contrastText: colors.canvasBg,
      },
      background: {
        default: colors.canvasBg,
        paper: colors.surface,
      },
      text: {
        primary: text.primary,
        secondary: text.muted,
        disabled: text.faint,
      },
      divider: colors.hairline,
      error: {
        main: semantic.error,
        light: semantic.errorSoft,
        dark: semantic.errorDark,
        contrastText: '#ffffff',
      },
      warning: {
        main: semantic.warning,
        light: semantic.warningSoft,
        dark: isDark ? '#c97f06' : '#8e4108',
        contrastText: '#ffffff',
      },
      success: {
        main: semantic.success,
        light: semantic.successSoft,
        dark: isDark ? '#2ea571' : '#0d5d39',
        contrastText: '#ffffff',
      },
      info: {
        main: semantic.info,
        light: tint(semantic.info, '1A'),
        dark: isDark ? '#0064cb' : '#005ecb',
        contrastText: '#ffffff',
      },
      action: {
        hover: colors.rowHover,
        selected: colors.rowActive,
        disabled: text.faint,
        disabledBackground: colors.chipBg,
        focus: accentTriple.ring,
      },
      grey: {
        50: '#fafafa',
        100: '#f5f5f7',
        200: '#e8e8ed',
        300: '#d2d2d7',
        400: '#aeaeb2',
        500: '#86868b',
        600: '#6e6e73',
        700: '#48484a',
        800: '#2c2c2e',
        900: '#1c1c1e',
        A100: '#f5f5f7',
        A200: '#e8e8ed',
        A400: '#86868b',
        A700: '#48484a',
      },

      instrument: {
        canvas: colors.canvasBg,
        chrome: colors.chromeBg,
        surface: colors.surface,
        hairline: colors.hairline,
        accent: accentTriple.base,
        accentSoft: accentTriple.soft,
        chipBg: colors.chipBg,
        chipHoverBg: colors.chipHoverBg,
        inputBg: colors.inputBg,
        codeBg: colors.codeBg,
        success: semantic.success,
        warning: semantic.warning,
        error: semantic.error,
      },
    },

    shape: { borderRadius: 8 },
    spacing: 8,
    typography: buildInstrumentTypography(SANS, INK, text.muted, text.faint),

    components: {
      MuiCssBaseline: cssBaselineWithVars,
      ...navigation,
      ...surfaces,
      MuiDialog: sharedDialog.MuiDialog,
      MuiDialogTitle: sharedDialog.MuiDialogTitle,
      MuiDialogContent: sharedDialog.MuiDialogContent,
      MuiDialogActions: sharedDialog.MuiDialogActions,
      MuiButton: {
        defaultProps: sharedButton.defaultProps,
        variants: sharedButton.variants,
        styleOverrides: {
          ...sharedButton.styleOverrides,
          ...buildSharedButtonColorOverrides(sharedPalette, semanticColors),
        },
      },
      MuiIconButton: buildSharedIconButtonOverrides(sharedPalette),
      ...formControls,
      MuiOutlinedInput: buildSharedOutlinedInputOverrides(sharedPalette),
      MuiInputLabel: buildSharedInputLabelOverrides(sharedPalette),
      MuiFormHelperText: buildSharedFormHelperTextOverrides(sharedPalette),
      MuiChip: buildSharedChipOverrides(sharedPalette, semanticColors),
      ...tables,
      MuiDataGrid: buildSharedDataGridOverrides(sharedPalette, colors.surface),
      MuiSwitch: buildSharedSwitchOverrides(sharedPalette),
      ...menus,
      MuiAlert: {
        styleOverrides: {
          ...sharedAlert.styleOverrides,
          ...buildSharedAlertColorOverrides(sharedPalette, semanticColors),
        },
        variants: sharedAlert.variants,
      },
      ...misc,
    },
  })
}
