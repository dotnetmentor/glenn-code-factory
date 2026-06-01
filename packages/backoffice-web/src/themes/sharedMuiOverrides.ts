/**
 * Shared MUI component overrides for {@link instrumentTheme}. Keeps pill buttons,
 * quiet actions, dialogs, and global baseline in one place so components stop
 * re-implementing them inline.
 */

import type { Components, Theme } from '@mui/material/styles'

export type SharedThemePalette = {
  canvasBg: string
  chromeBg: string
  surface: string
  hairline: string
  hairlineStrong: string
  ink: string
  inkHover: string
  textPrimary: string
  textMuted: string
  textFaint: string
  textDisabled: string
  inputBg: string
  chipBg: string
  chipHoverBg: string
  accent: string
  accentHover: string
  errorMain: string
  errorDark: string
  sans: string
  scrollbarThumb: string
  scrollbarThumbHover: string
  /** Pill CTA fill — near-black on both presets (not bronze on paper). */
  pillFill: string
  pillFillHover: string
  pillContrast: string
}

const tint = (hex: string, alphaHex: string) => `${hex}${alphaHex}`

export function buildSharedButtonOverrides(p: SharedThemePalette): NonNullable<Components<Theme>['MuiButton']> {
  return {
    defaultProps: { disableElevation: true, size: 'small' },
    variants: [
      {
        props: { variant: 'pill' },
        style: {
          borderRadius: 999,
          textTransform: 'none',
          fontWeight: 500,
          letterSpacing: '0.01em',
          boxShadow: 'none',
          px: 2,
          py: 0.75,
          '&:hover': { boxShadow: 'none' },
        },
      },
      {
        props: { variant: 'pill', color: 'primary' },
        style: {
          backgroundColor: p.pillFill,
          color: p.pillContrast,
          '&:hover': { backgroundColor: p.pillFillHover, boxShadow: 'none' },
          '&.Mui-disabled': {
            backgroundColor: p.chipBg,
            color: p.textFaint,
          },
        },
      },
      {
        props: { variant: 'pill', color: 'error' },
        style: {
          backgroundColor: p.errorMain,
          color: '#FFFFFF',
          '&:hover': { backgroundColor: p.errorDark, boxShadow: 'none' },
          '&.Mui-disabled': {
            backgroundColor: p.chipBg,
            color: p.textFaint,
          },
        },
      },
      {
        props: { variant: 'pillOutlined' },
        style: {
          borderRadius: 999,
          textTransform: 'none',
          fontWeight: 500,
          letterSpacing: '0.01em',
          boxShadow: 'none',
          px: 2,
          py: 0.75,
          borderColor: p.hairlineStrong,
          color: p.textPrimary,
          backgroundColor: 'transparent',
          '&:hover': {
            borderColor: p.textMuted,
            backgroundColor: 'rgba(0, 0, 0, 0.03)',
            boxShadow: 'none',
          },
        },
      },
      {
        props: { variant: 'pillOutlined', color: 'error' },
        style: {
          borderColor: tint(p.errorMain, '4D'),
          color: p.errorMain,
          backgroundColor: 'transparent',
          '&:hover': {
            borderColor: p.errorMain,
            backgroundColor: tint(p.errorMain, '0F'),
            boxShadow: 'none',
          },
        },
      },
      {
        props: { variant: 'quietText' },
        style: {
          textTransform: 'none',
          color: p.textMuted,
          fontSize: 13,
          fontWeight: 500,
          borderRadius: 1,
          px: 1.25,
          py: 0.5,
          minWidth: 0,
          transition: 'background-color 160ms ease, color 160ms ease',
          '&:hover': {
            bgcolor: p.chipHoverBg,
            color: p.textPrimary,
          },
          '&.Mui-disabled': { color: p.textDisabled },
        },
      },
      {
        props: { variant: 'quietOutlined' },
        style: {
          textTransform: 'none',
          color: p.textMuted,
          fontSize: 13,
          fontWeight: 500,
          borderRadius: 1,
          px: 1.5,
          py: 0.5,
          border: `1px solid ${p.hairline}`,
          bgcolor: 'transparent',
          boxShadow: 'none',
          transition: 'background-color 160ms ease, color 160ms ease, border-color 160ms ease',
          '&:hover': {
            bgcolor: p.chipHoverBg,
            color: p.textPrimary,
            border: `1px solid ${p.accent}`,
          },
          '&.Mui-disabled': {
            color: p.textDisabled,
            border: `1px solid ${p.hairline}`,
          },
        },
      },
    ],
    styleOverrides: {
      root: {
        borderRadius: 6,
        boxShadow: 'none',
        transition: 'background-color 0.15s ease, border-color 0.15s ease, color 0.15s ease',
        '&:hover': { boxShadow: 'none' },
        '&:active': { transform: 'translateY(0.5px)' },
      },
      text: {
        color: p.textMuted,
        textTransform: 'none',
        '&:hover': { color: p.textPrimary, backgroundColor: 'transparent' },
      },
      sizeSmall: { padding: '4px 10px', fontSize: '0.8125rem', minHeight: 28 },
      sizeMedium: { padding: '6px 14px', fontSize: '0.875rem', minHeight: 34 },
      sizeLarge: { padding: '8px 18px', fontSize: '0.9375rem', minHeight: 40 },
    },
  }
}

export function buildSharedCssBaselineOverrides(p: SharedThemePalette): Components<Theme>['MuiCssBaseline'] {
  return {
    styleOverrides: {
      html: {
        WebkitTextSizeAdjust: '100%',
        textSizeAdjust: '100%',
        backgroundColor: p.canvasBg,
      },
      body: {
        overscrollBehavior: 'none',
        WebkitOverflowScrolling: 'touch',
        backgroundColor: p.canvasBg,
        color: p.textPrimary,
        fontFamily: p.sans,
      },
      ':focus-visible': {
        outline: `1.5px solid ${p.ink}`,
        outlineOffset: 2,
      },
      '::selection': {
        backgroundColor: tint(p.ink, '1A'),
        color: p.textPrimary,
      },
      '::-webkit-scrollbar': { width: 6, height: 6 },
      '::-webkit-scrollbar-track': { background: 'transparent' },
      '::-webkit-scrollbar-thumb': {
        background: p.scrollbarThumb,
        borderRadius: 3,
      },
      '::-webkit-scrollbar-thumb:hover': { background: p.scrollbarThumbHover },
      '*': {
        scrollbarWidth: 'thin',
        scrollbarColor: `${p.scrollbarThumb} transparent`,
      },
      '@media screen and (max-width: 899px)': {
        'input, textarea, select': { fontSize: '16px !important' },
      },
    },
  }
}

export function buildSharedDialogOverrides(
  p: SharedThemePalette,
  _elevatedShadow: string,
): Pick<Components<Theme>, 'MuiDialog' | 'MuiDialogTitle' | 'MuiDialogContent' | 'MuiDialogActions'> {
  return {
    MuiDialog: {
      defaultProps: { scroll: 'body' },
      styleOverrides: {
        paper: {
          borderRadius: 10,
          border: `1px solid ${p.hairline}`,
          boxShadow: 'none',
          backgroundColor: p.canvasBg,
          backgroundImage: 'none',
        },
      },
    },
    MuiDialogTitle: {
      styleOverrides: {
        root: {
          fontSize: '1.0625rem',
          fontWeight: 500,
          letterSpacing: '-0.01em',
          color: p.textPrimary,
          fontFamily: p.sans,
          padding: '20px 24px 8px',
        },
      },
    },
    MuiDialogContent: {
      styleOverrides: {
        root: { padding: '8px 24px 20px' },
      },
    },
    MuiDialogActions: {
      styleOverrides: {
        root: { padding: '8px 24px 16px' },
      },
    },
  }
}

export function buildSharedOutlinedInputOverrides(p: SharedThemePalette): Components<Theme>['MuiOutlinedInput'] {
  return {
    styleOverrides: {
      root: {
        borderRadius: 6,
        backgroundColor: p.inputBg,
        '& .MuiOutlinedInput-notchedOutline': {
          borderColor: p.hairlineStrong,
        },
        '&:hover .MuiOutlinedInput-notchedOutline': {
          borderColor: p.textMuted,
        },
        '&.Mui-focused .MuiOutlinedInput-notchedOutline': {
          borderColor: p.accent,
          borderWidth: 1,
        },
        '&.Mui-disabled': {
          backgroundColor: p.chipBg,
          '& .MuiOutlinedInput-notchedOutline': { borderColor: p.hairline },
        },
      },
      input: {
        fontSize: '0.875rem',
        fontFamily: p.sans,
        '&::placeholder': { color: p.textFaint, opacity: 1 },
        '&:-webkit-autofill, &:-webkit-autofill:hover, &:-webkit-autofill:focus': {
          WebkitBoxShadow: `0 0 0 1000px ${p.inputBg} inset`,
          WebkitTextFillColor: p.textPrimary,
          caretColor: p.textPrimary,
        },
      },
    },
  }
}

export function buildSharedInputLabelOverrides(p: SharedThemePalette): Components<Theme>['MuiInputLabel'] {
  return {
    styleOverrides: {
      root: {
        color: p.textMuted,
        fontSize: '0.8125rem',
        fontFamily: p.sans,
        '&.Mui-focused': { color: p.accent },
      },
    },
  }
}

export function buildSharedFormHelperTextOverrides(p: SharedThemePalette): Components<Theme>['MuiFormHelperText'] {
  return {
    styleOverrides: {
      root: {
        fontSize: '0.75rem',
        color: p.textMuted,
        marginLeft: 0,
        '&.Mui-error': { color: p.errorMain },
      },
    },
  }
}

export function buildSharedAlertOverrides(p: SharedThemePalette): NonNullable<Components<Theme>['MuiAlert']> {
  return {
    styleOverrides: {
      message: {
        fontFamily: p.sans,
      },
    },
    variants: [
      {
        props: { variant: 'quiet' },
        style: {
          backgroundColor: 'transparent',
          borderColor: p.hairline,
          color: p.textPrimary,
        },
      },
      {
        props: { variant: 'panel' },
        style: {
          backgroundColor: p.chromeBg,
          borderColor: p.hairline,
          color: p.textPrimary,
          paddingTop: 8,
          paddingBottom: 8,
          '& .MuiAlert-message': {
            fontSize: '0.8125rem',
            color: p.textPrimary,
            opacity: 1,
          },
        },
      },
      {
        props: { variant: 'errorStrip' },
        style: {
          display: 'flex',
          alignItems: 'center',
          gap: 8,
          borderRadius: 0,
          padding: '8px 16px',
          fontSize: 12.5,
          color: p.errorMain,
          backgroundColor: tint(p.errorMain, '0F'),
          borderTop: `1px solid ${tint(p.errorMain, '2E')}`,
          borderBottom: `1px solid ${tint(p.errorMain, '2E')}`,
          '& .MuiAlert-icon': {
            padding: 0,
            marginRight: 0,
            fontSize: 14,
            color: p.errorMain,
            opacity: 1,
          },
          '& .MuiAlert-message': {
            padding: 0,
            fontSize: 12.5,
            color: p.errorMain,
            opacity: 1,
          },
        },
      },
      {
        props: { variant: 'infoStrip' },
        style: {
          display: 'flex',
          alignItems: 'center',
          gap: 8,
          borderRadius: 0,
          padding: '8px 16px',
          fontSize: 12.5,
          color: p.textMuted,
          backgroundColor: p.chipBg,
          borderTop: `1px solid ${p.hairline}`,
          borderBottom: `1px solid ${p.hairline}`,
          '& .MuiAlert-icon': {
            padding: 0,
            marginRight: 0,
            fontSize: 14,
            color: p.textMuted,
            opacity: 1,
          },
          '& .MuiAlert-message': {
            padding: 0,
            fontSize: 12.5,
            color: p.textMuted,
            opacity: 1,
          },
        },
      },
    ],
  }
}

export function buildSharedIconButtonOverrides(p: SharedThemePalette): Components<Theme>['MuiIconButton'] {
  return {
    defaultProps: { size: 'small' },
    styleOverrides: {
      root: {
        borderRadius: 6,
        color: p.textMuted,
        transition: 'background-color 0.15s ease, color 0.15s ease',
        '&:hover': {
          backgroundColor: p.chipHoverBg,
          color: p.textPrimary,
        },
      },
      sizeSmall: { padding: 6 },
      sizeMedium: { padding: 8 },
    },
  }
}

/** Compact, muted DataGrid footer — pagination row + row count. */
export function buildSharedDataGridFooterStyles(p: SharedThemePalette) {
  return {
    minHeight: 40,
    padding: '0 12px',
    backgroundColor: '#FFFFFF',
    '& .MuiTablePagination-root': {
      width: '100%',
      overflow: 'hidden',
    },
    '& .MuiTablePagination-toolbar': {
      minHeight: 40,
      padding: '0 4px',
      gap: 8,
    },
    '& .MuiTablePagination-selectLabel, & .MuiTablePagination-displayedRows': {
      fontSize: '0.75rem',
      color: p.textMuted,
      letterSpacing: '-0.005em',
      margin: 0,
    },
    '& .MuiTablePagination-select': {
      fontSize: '0.75rem',
      color: p.textPrimary,
    },
    '& .MuiTablePagination-input': {
      marginRight: 8,
      marginLeft: 4,
    },
    '& .MuiTablePagination-actions': {
      marginLeft: 8,
      '& .MuiIconButton-root': {
        padding: 4,
        color: p.textMuted,
        '&:hover': {
          color: p.textPrimary,
          backgroundColor: 'rgba(0, 0, 0, 0.04)',
        },
        '&.Mui-disabled': {
          color: p.textFaint,
        },
      },
    },
    '& .MuiDataGrid-selectedRowCount': {
      fontSize: '0.75rem',
      color: p.textMuted,
      padding: '0 8px',
    },
  } as const
}

/** Pill shape only — pair with color="error" etc. Replaces workspacePillButtonSx. */
export const workspacePillShapeSx = {
  borderRadius: 999,
  fontWeight: 500,
  px: 2,
  py: 0.75,
  textTransform: 'none',
  boxShadow: 'none',
  '&:hover': { boxShadow: 'none' },
} as const

// =============================================================================
// Instrument Mono — component override builders (used by instrumentTheme.ts)
// =============================================================================

export type InstrumentSemanticColors = {
  success: string
  warning: string
  error: string
  info: string
}

export type InstrumentShadowTokens = {
  cardHover: string
  menu: string
}

export function buildInstrumentTypography(
  sans: string,
  ink: string,
  muted: string,
  faint: string,
) {
  return {
    fontFamily: sans,
    h1: { fontFamily: sans, fontWeight: 600, fontSize: '2rem', lineHeight: 1.2, letterSpacing: '-0.02em', color: ink },
    h2: { fontFamily: sans, fontWeight: 600, fontSize: '1.75rem', lineHeight: 1.25, letterSpacing: '-0.02em', color: ink },
    h3: { fontFamily: sans, fontWeight: 600, fontSize: '1.5rem', lineHeight: 1.3, letterSpacing: '-0.015em', color: ink },
    h4: { fontFamily: sans, fontWeight: 600, fontSize: '1.25rem', lineHeight: 1.35, letterSpacing: '-0.01em', color: ink },
    h5: { fontFamily: sans, fontWeight: 600, fontSize: '1rem', lineHeight: 1.4, letterSpacing: '-0.005em', color: ink },
    h6: { fontFamily: sans, fontWeight: 600, fontSize: '0.9375rem', lineHeight: 1.45, letterSpacing: '-0.005em', color: ink },
    subtitle1: { fontFamily: sans, fontWeight: 500, fontSize: '0.9375rem', lineHeight: 1.5, letterSpacing: '-0.005em' },
    subtitle2: { fontFamily: sans, fontWeight: 500, fontSize: '0.875rem', lineHeight: 1.5, letterSpacing: '-0.005em' },
    body1: { fontFamily: sans, fontWeight: 500, fontSize: '0.9375rem', lineHeight: 1.55, letterSpacing: '-0.005em' },
    body2: { fontFamily: sans, fontWeight: 500, fontSize: '0.875rem', lineHeight: 1.55, letterSpacing: '-0.005em' },
    button: { fontFamily: sans, fontWeight: 500, fontSize: '0.875rem', textTransform: 'none' as const, letterSpacing: '0.005em' },
    caption: { fontFamily: sans, fontWeight: 500, fontSize: '0.8125rem', lineHeight: 1.5, letterSpacing: '-0.005em', color: muted },
    overline: { fontFamily: sans, fontWeight: 600, fontSize: '0.6875rem', lineHeight: 1.5, letterSpacing: '0.06em', textTransform: 'uppercase' as const, color: faint },
  }
}

export function buildSharedNavigationOverrides(
  p: SharedThemePalette,
  chromeBg: string,
): Pick<Components<Theme>, 'MuiAppBar' | 'MuiDrawer' | 'MuiToolbar' | 'MuiListItemButton' | 'MuiListItemText' | 'MuiListItemIcon'> {
  return {
    MuiAppBar: {
      defaultProps: { elevation: 0, color: 'default' },
      styleOverrides: {
        root: ({ theme: t }) => ({
          boxShadow: 'none',
          borderRadius: 0,
          backgroundColor: chromeBg,
          backgroundImage: 'none',
          color: p.textPrimary,
          borderBottom: `1px solid ${p.hairline}`,
          zIndex: t.zIndex.drawer + 1,
          '& .MuiToolbar-root': { minHeight: '48px !important', height: 48 },
        }),
      },
    },
    MuiDrawer: {
      styleOverrides: {
        paper: {
          borderRight: `1px solid ${p.hairline}`,
          backgroundColor: chromeBg,
          backgroundImage: 'none',
          boxShadow: 'none',
        },
      },
    },
    MuiToolbar: {
      styleOverrides: {
        root: { minHeight: '48px !important' },
      },
    },
    MuiListItemButton: {
      styleOverrides: {
        root: {
          borderRadius: 6,
          margin: '1px 6px',
          padding: '4px 10px',
          minHeight: 30,
          color: p.textMuted,
          transition: 'all 0.12s ease',
          '&.Mui-selected': {
            backgroundColor: tint(p.ink, '14'),
            '& .MuiListItemIcon-root': { color: p.ink },
            '& .MuiListItemText-primary': { fontWeight: 600, color: p.textPrimary },
            '&:hover': { backgroundColor: tint(p.ink, '1F') },
          },
          '&:hover': { backgroundColor: 'rgba(0, 0, 0, 0.03)' },
        },
      },
    },
    MuiListItemText: {
      styleOverrides: {
        primary: { fontSize: '0.8125rem', lineHeight: 1.4, letterSpacing: '-0.005em' },
        secondary: { fontSize: '0.75rem', lineHeight: 1.4, color: p.textFaint },
      },
    },
    MuiListItemIcon: {
      styleOverrides: {
        root: { minWidth: 32, color: p.textMuted },
      },
    },
  }
}

export function buildSharedSurfaceOverrides(
  p: SharedThemePalette,
  shadows: InstrumentShadowTokens,
  surfaceBg: string,
): Pick<Components<Theme>, 'MuiPaper' | 'MuiCard' | 'MuiCardHeader' | 'MuiAccordion'> {
  return {
    MuiPaper: {
      defaultProps: { elevation: 0 },
      styleOverrides: {
        root: { backgroundImage: 'none', backgroundColor: surfaceBg },
        outlined: { border: `1px solid ${p.hairline}` },
        elevation0: { boxShadow: 'none' },
        elevation1: { boxShadow: 'none', border: `1px solid ${p.hairline}` },
        elevation2: { boxShadow: '0 1px 2px rgba(0,0,0,0.04)', border: `1px solid ${p.hairline}` },
        elevation3: { boxShadow: '0 2px 6px rgba(0,0,0,0.05)', border: `1px solid ${p.hairline}` },
        elevation4: { boxShadow: '0 4px 10px rgba(0,0,0,0.06)', border: `1px solid ${p.hairline}` },
      },
    },
    MuiCard: {
      defaultProps: { elevation: 0, variant: 'outlined' },
      styleOverrides: {
        root: {
          borderRadius: 12,
          border: `1px solid ${p.hairline}`,
          boxShadow: 'none',
          backgroundColor: p.surface,
          backgroundImage: 'none',
          overflow: 'hidden',
          transition: 'border-color 0.2s ease, box-shadow 0.2s ease',
          '&:hover': {
            borderColor: tint(p.ink, '1A'),
            boxShadow: shadows.cardHover,
          },
        },
      },
    },
    MuiCardHeader: {
      styleOverrides: {
        title: { fontSize: '1rem', fontWeight: 500, letterSpacing: '-0.005em', color: p.textPrimary },
        subheader: { fontSize: '0.8125rem', color: p.textMuted, marginTop: 2 },
      },
    },
    MuiAccordion: {
      defaultProps: { elevation: 0, disableGutters: true },
      styleOverrides: {
        root: {
          border: `1px solid ${p.hairline}`,
          borderRadius: '6px !important',
          boxShadow: 'none',
          backgroundColor: surfaceBg,
          backgroundImage: 'none',
          '&:before': { display: 'none' },
          '&.Mui-expanded': { margin: 0 },
        },
      },
    },
  }
}

export function buildSharedButtonColorOverrides(
  p: SharedThemePalette,
  semantic: InstrumentSemanticColors,
): NonNullable<Components<Theme>['MuiButton']>['styleOverrides'] {
  return {
    contained: { boxShadow: 'none', '&:hover': { boxShadow: 'none' } },
    containedPrimary: {
      backgroundColor: p.ink,
      color: '#FFFFFF',
      '&:hover': { backgroundColor: p.inkHover },
    },
    containedSecondary: {
      backgroundColor: p.textPrimary,
      color: p.canvasBg,
      '&:hover': { backgroundColor: '#000000' },
    },
    outlined: {
      borderColor: p.hairlineStrong,
      color: p.textPrimary,
      backgroundColor: 'transparent',
      '&:hover': {
        borderColor: p.textMuted,
        backgroundColor: 'rgba(0, 0, 0, 0.03)',
      },
    },
    outlinedPrimary: {
      borderColor: tint(p.ink, '4D'),
      color: p.ink,
      '&:hover': { borderColor: p.ink, backgroundColor: tint(p.ink, '0F') },
    },
    textPrimary: {
      color: p.ink,
      '&:hover': { backgroundColor: tint(p.ink, '0F') },
    },
    containedError: {
      backgroundColor: semantic.error,
      color: '#FFFFFF',
      '&:hover': { backgroundColor: '#d70015' },
    },
    outlinedError: {
      borderColor: tint(semantic.error, '4D'),
      color: semantic.error,
      backgroundColor: 'transparent',
      '&:hover': {
        borderColor: semantic.error,
        backgroundColor: tint(semantic.error, '0F'),
      },
    },
    textError: {
      color: semantic.error,
      '&:hover': { backgroundColor: tint(semantic.error, '0F') },
    },
  }
}

export function buildSharedChipOverrides(
  p: SharedThemePalette,
  semantic: InstrumentSemanticColors,
): Components<Theme>['MuiChip'] {
  return {
    defaultProps: { size: 'small' },
    styleOverrides: {
      root: {
        borderRadius: 4,
        fontWeight: 500,
        height: 22,
        fontSize: '0.75rem',
        letterSpacing: '0.01em',
      },
      sizeSmall: {
        height: 20,
        fontSize: '0.6875rem',
        '& .MuiChip-label': { paddingLeft: 8, paddingRight: 8 },
      },
      sizeMedium: { height: 26, fontSize: '0.8125rem' },
      filled: {
        backgroundColor: p.chipBg,
        color: p.textPrimary,
        border: `1px solid ${p.hairline}`,
        '&:hover': { backgroundColor: p.chipHoverBg },
      },
      outlined: {
        borderColor: p.hairlineStrong,
        backgroundColor: 'transparent',
      },
      colorPrimary: {
        backgroundColor: tint(p.ink, '1A'),
        color: p.ink,
        border: `1px solid ${tint(p.ink, '33')}`,
        '&.MuiChip-outlined': { backgroundColor: 'transparent' },
      },
      colorSuccess: {
        backgroundColor: tint(semantic.success, '1A'),
        color: semantic.success,
        border: `1px solid ${tint(semantic.success, '33')}`,
      },
      colorWarning: {
        backgroundColor: tint(semantic.warning, '1A'),
        color: semantic.warning,
        border: `1px solid ${tint(semantic.warning, '33')}`,
      },
      colorError: {
        backgroundColor: tint(semantic.error, '1A'),
        color: semantic.error,
        border: `1px solid ${tint(semantic.error, '33')}`,
      },
      colorInfo: {
        backgroundColor: tint(semantic.info, '1A'),
        color: semantic.info,
        border: `1px solid ${tint(semantic.info, '33')}`,
      },
    },
  }
}

export function buildSharedTableOverrides(p: SharedThemePalette): Pick<
  Components<Theme>,
  'MuiTableContainer' | 'MuiTable' | 'MuiTableHead' | 'MuiTableRow' | 'MuiTableCell'
> {
  return {
    MuiTableContainer: {
      styleOverrides: { root: { borderRadius: 8 } },
    },
    MuiTable: {
      styleOverrides: {
        root: {
          '& .MuiTableCell-root': { borderBottomColor: p.hairline },
        },
      },
    },
    MuiTableHead: {
      styleOverrides: {
        root: {
          '& .MuiTableCell-root': {
            backgroundColor: p.canvasBg,
            color: p.textMuted,
            fontSize: '0.75rem',
            fontWeight: 500,
            letterSpacing: '0.02em',
            textTransform: 'none',
            borderBottom: `1px solid ${p.hairline}`,
          },
        },
      },
    },
    MuiTableRow: {
      styleOverrides: {
        root: {
          '&:hover': { backgroundColor: 'rgba(0, 0, 0, 0.015)' },
          '&:last-child .MuiTableCell-root': { borderBottom: 'none' },
        },
      },
    },
    MuiTableCell: {
      styleOverrides: {
        root: {
          borderBottomColor: p.hairline,
          fontSize: '0.875rem',
          color: p.textPrimary,
          padding: '10px 14px',
        },
        head: {
          color: p.textMuted,
          fontWeight: 500,
          fontSize: '0.75rem',
          letterSpacing: '0.02em',
        },
      },
    },
  }
}

export function buildSharedDataGridOverrides(p: SharedThemePalette, surfaceBg: string): Components<Theme>['MuiDataGrid'] {
  return {
    defaultProps: {
      density: 'compact',
      rowHeight: 44,
      columnHeaderHeight: 40,
      disableColumnMenu: true,
      disableRowSelectionOnClick: true,
    },
    styleOverrides: {
      root: {
        border: `1px solid ${p.hairline}`,
        borderRadius: 8,
        overflow: 'hidden',
        backgroundColor: surfaceBg,
        '& .MuiDataGrid-columnHeaders': {
          backgroundColor: p.canvasBg,
          borderBottom: `1px solid ${p.hairline}`,
        },
        '& .MuiDataGrid-columnHeaderTitle': {
          fontWeight: 500,
          fontSize: '0.75rem',
          color: p.textMuted,
          letterSpacing: '0.02em',
        },
        '& .MuiDataGrid-row': {
          borderBottom: `1px solid ${p.hairline}`,
          transition: 'background-color 0.1s ease',
          '&:hover': { backgroundColor: 'rgba(0, 0, 0, 0.015)' },
          '&:last-child': { borderBottom: 'none' },
        },
        '& .MuiDataGrid-cell': {
          borderBottom: 'none',
          fontSize: '0.875rem',
          color: p.textPrimary,
          '&:focus, &:focus-within': { outline: 'none' },
        },
        '& .MuiDataGrid-columnHeader:focus, & .MuiDataGrid-columnHeader:focus-within': { outline: 'none' },
        '& .MuiDataGrid-footerContainer': {
          borderTop: `1px solid ${p.hairline}`,
          ...buildSharedDataGridFooterStyles(p),
        },
      },
    },
  }
}

export function buildSharedSwitchOverrides(p: SharedThemePalette): Components<Theme>['MuiSwitch'] {
  return {
    styleOverrides: {
      root: {
        width: 38,
        height: 22,
        padding: 0,
        '& .MuiSwitch-switchBase': {
          padding: 2,
          transitionDuration: '180ms',
          '&.Mui-checked': {
            transform: 'translateX(16px)',
            color: '#FFFFFF',
            '& + .MuiSwitch-track': { backgroundColor: p.ink, opacity: 1, border: 0 },
            '& .MuiSwitch-thumb': { backgroundColor: '#FFFFFF' },
          },
        },
        '& .MuiSwitch-thumb': {
          width: 18,
          height: 18,
          backgroundColor: '#FFFFFF',
          boxShadow: '0 1px 2px rgba(0,0,0,0.15)',
        },
        '& .MuiSwitch-track': {
          borderRadius: 11,
          backgroundColor: p.hairlineStrong,
          opacity: 1,
          transition: 'background-color 200ms ease',
          border: 'none',
        },
      },
    },
  }
}

export function buildSharedMenuOverrides(
  p: SharedThemePalette,
  shadows: InstrumentShadowTokens,
  surfaceBg: string,
): Pick<Components<Theme>, 'MuiMenu' | 'MuiMenuItem' | 'MuiPopover' | 'MuiAutocomplete'> {
  return {
    MuiMenu: {
      styleOverrides: {
        paper: {
          borderRadius: 8,
          border: `1px solid ${p.hairline}`,
          boxShadow: shadows.menu,
          backgroundColor: surfaceBg,
          backgroundImage: 'none',
          marginTop: 4,
        },
        list: { padding: '4px' },
      },
    },
    MuiMenuItem: {
      styleOverrides: {
        root: {
          fontSize: '0.8125rem',
          padding: '6px 10px',
          borderRadius: 4,
          margin: '1px 0',
          minHeight: 30,
          color: p.textPrimary,
          '&:hover': { backgroundColor: 'rgba(0, 0, 0, 0.04)' },
          '&.Mui-selected': {
            backgroundColor: tint(p.ink, '14'),
            '&:hover': { backgroundColor: tint(p.ink, '1F') },
          },
        },
      },
    },
    MuiPopover: {
      styleOverrides: {
        paper: {
          border: `1px solid ${p.hairline}`,
          boxShadow: shadows.menu,
          backgroundColor: surfaceBg,
          backgroundImage: 'none',
        },
      },
    },
    MuiAutocomplete: {
      styleOverrides: {
        paper: {
          border: `1px solid ${p.hairline}`,
          boxShadow: shadows.menu,
        },
        option: {
          fontSize: '0.8125rem',
          '&[aria-selected="true"]': { backgroundColor: tint(p.ink, '14') },
        },
      },
    },
  }
}

export function buildSharedAlertColorOverrides(
  _p: SharedThemePalette,
  semantic: InstrumentSemanticColors,
): NonNullable<Components<Theme>['MuiAlert']>['styleOverrides'] {
  return {
    root: {
      borderRadius: 6,
      border: '1px solid',
      padding: '10px 14px',
      fontSize: '0.875rem',
    },
    standardSuccess: { backgroundColor: tint(semantic.success, '14'), borderColor: tint(semantic.success, '33'), color: '#248a3d' },
    standardError: { backgroundColor: tint(semantic.error, '14'), borderColor: tint(semantic.error, '33'), color: '#d70015' },
    standardWarning: { backgroundColor: tint(semantic.warning, '14'), borderColor: tint(semantic.warning, '33'), color: '#c93400' },
    standardInfo: { backgroundColor: tint(semantic.info, '14'), borderColor: tint(semantic.info, '33'), color: '#005ecb' },
    outlinedSuccess: { backgroundColor: tint(semantic.success, '0A'), borderColor: tint(semantic.success, '4D'), color: '#248a3d' },
    outlinedError: { backgroundColor: tint(semantic.error, '0A'), borderColor: tint(semantic.error, '4D'), color: '#d70015' },
    outlinedWarning: { backgroundColor: tint(semantic.warning, '0A'), borderColor: tint(semantic.warning, '4D'), color: '#c93400' },
    outlinedInfo: { backgroundColor: tint(semantic.info, '0A'), borderColor: tint(semantic.info, '4D'), color: '#005ecb' },
  }
}

export function buildSharedFormControlOverrides(p: SharedThemePalette): Pick<
  Components<Theme>,
  'MuiFilledInput' | 'MuiInput' | 'MuiCheckbox' | 'MuiRadio' | 'MuiSlider' | 'MuiFormControl' | 'MuiTextField'
> {
  return {
    MuiTextField: {
      defaultProps: { variant: 'outlined', size: 'small' },
    },
    MuiFormControl: {
      defaultProps: { size: 'small', margin: 'dense' },
    },
    MuiFilledInput: {
      styleOverrides: {
        root: {
          backgroundColor: 'rgba(0, 0, 0, 0.03)',
          borderRadius: 6,
          '&:hover': { backgroundColor: 'rgba(0, 0, 0, 0.05)' },
          '&.Mui-focused': { backgroundColor: 'rgba(0, 0, 0, 0.04)' },
          '&::before, &::after': { display: 'none' },
        },
      },
    },
    MuiInput: {
      styleOverrides: {
        underline: {
          '&::before': { borderBottomColor: p.hairlineStrong },
          '&:hover:not(.Mui-disabled, .Mui-error):before': { borderBottomColor: p.textMuted },
          '&::after': { borderBottomColor: p.ink },
        },
      },
    },
    MuiCheckbox: {
      styleOverrides: {
        root: {
          color: p.hairlineStrong,
          '&.Mui-checked': { color: p.ink },
        },
      },
    },
    MuiRadio: {
      styleOverrides: {
        root: {
          color: p.hairlineStrong,
          '&.Mui-checked': { color: p.ink },
        },
      },
    },
    MuiSlider: {
      styleOverrides: {
        root: { color: p.ink },
        rail: { backgroundColor: p.hairlineStrong, opacity: 1 },
        track: { backgroundColor: p.ink, border: 'none' },
        thumb: {
          backgroundColor: '#FFFFFF',
          border: `1.5px solid ${p.ink}`,
          '&:hover, &.Mui-focusVisible': {
            boxShadow: `0 0 0 6px ${tint(p.ink, '1F')}`,
          },
        },
      },
    },
  }
}

export function buildSharedMiscOverrides(
  p: SharedThemePalette,
  shadows: InstrumentShadowTokens,
): Pick<
  Components<Theme>,
  | 'MuiButtonGroup'
  | 'MuiFab'
  | 'MuiAvatar'
  | 'MuiBadge'
  | 'MuiTabs'
  | 'MuiTab'
  | 'MuiAlertTitle'
  | 'MuiTooltip'
  | 'MuiLinearProgress'
  | 'MuiCircularProgress'
  | 'MuiSnackbarContent'
  | 'MuiDivider'
  | 'MuiLink'
  | 'MuiBreadcrumbs'
  | 'MuiStepLabel'
  | 'MuiStepIcon'
  | 'MuiPaginationItem'
  | 'MuiSkeleton'
> {
  return {
    MuiButtonGroup: {
      styleOverrides: {
        root: { boxShadow: 'none' },
        grouped: { '&:not(:last-of-type)': { borderRightColor: p.hairline } },
      },
    },
    MuiFab: {
      defaultProps: { color: 'primary' },
      styleOverrides: {
        root: {
          boxShadow: '0 2px 8px rgba(0, 0, 0, 0.08)',
          '&:hover': { boxShadow: shadows.cardHover },
        },
      },
    },
    MuiAvatar: {
      styleOverrides: {
        root: { fontSize: '0.8125rem', fontWeight: 500 },
        colorDefault: {
          backgroundColor: p.chipBg,
          color: p.textPrimary,
        },
      },
    },
    MuiBadge: {
      styleOverrides: {
        badge: {
          fontSize: '0.6875rem',
          fontWeight: 500,
          height: 16,
          minWidth: 16,
        },
      },
    },
    MuiTabs: {
      styleOverrides: {
        root: { minHeight: 38 },
        indicator: {
          height: 2,
          borderRadius: '2px 2px 0 0',
          backgroundColor: p.ink,
        },
      },
    },
    MuiTab: {
      styleOverrides: {
        root: {
          textTransform: 'none',
          fontWeight: 500,
          fontSize: '0.875rem',
          letterSpacing: '-0.005em',
          minHeight: 38,
          padding: '8px 14px',
          color: p.textMuted,
          '&:hover': { color: p.textPrimary },
          '&.Mui-selected': { color: p.ink },
        },
      },
    },
    MuiAlertTitle: {
      styleOverrides: {
        root: { color: 'inherit', fontWeight: 600 },
      },
    },
    MuiTooltip: {
      styleOverrides: {
        tooltip: {
          backgroundColor: p.textPrimary,
          color: p.canvasBg,
          fontSize: '0.75rem',
          fontWeight: 400,
          borderRadius: 4,
          padding: '5px 8px',
          letterSpacing: '-0.005em',
        },
        arrow: { color: p.textPrimary },
      },
    },
    MuiLinearProgress: {
      styleOverrides: {
        root: { backgroundColor: p.hairline, borderRadius: 999, height: 4 },
        bar: { backgroundColor: p.ink, borderRadius: 999 },
      },
    },
    MuiCircularProgress: {
      styleOverrides: {
        colorPrimary: { color: p.ink },
      },
    },
    MuiSnackbarContent: {
      styleOverrides: {
        root: {
          backgroundColor: p.textPrimary,
          color: p.canvasBg,
          borderRadius: 8,
          boxShadow: '0 6px 20px rgba(0, 0, 0, 0.12)',
        },
      },
    },
    MuiDivider: {
      styleOverrides: { root: { borderColor: p.hairline } },
    },
    MuiLink: {
      defaultProps: { underline: 'hover' },
      styleOverrides: {
        root: {
          color: p.ink,
          textDecorationColor: tint(p.ink, '66'),
          '&:hover': { color: p.inkHover },
        },
      },
    },
    MuiBreadcrumbs: {
      styleOverrides: {
        separator: { color: p.textFaint, marginLeft: 6, marginRight: 6 },
        li: { fontSize: '0.8125rem', color: p.textMuted },
      },
    },
    MuiStepLabel: {
      styleOverrides: {
        label: {
          fontSize: '0.8125rem',
          color: p.textMuted,
          '&.Mui-active': { color: p.textPrimary, fontWeight: 500 },
          '&.Mui-completed': { color: p.textPrimary },
        },
      },
    },
    MuiStepIcon: {
      styleOverrides: {
        root: {
          color: p.hairlineStrong,
          '&.Mui-active': { color: p.ink },
          '&.Mui-completed': { color: p.ink },
        },
      },
    },
    MuiPaginationItem: {
      styleOverrides: {
        root: {
          color: p.textMuted,
          borderColor: p.hairline,
          '&.Mui-selected': {
            backgroundColor: tint(p.ink, '14'),
            color: p.ink,
            borderColor: tint(p.ink, '33'),
            '&:hover': { backgroundColor: tint(p.ink, '1F') },
          },
          '&:hover': { backgroundColor: 'rgba(0, 0, 0, 0.04)' },
        },
      },
    },
    MuiSkeleton: {
      styleOverrides: {
        root: { backgroundColor: 'rgba(0, 0, 0, 0.05)' },
      },
    },
  }
}
