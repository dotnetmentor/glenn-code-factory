import '@mui/material/Alert'
import '@mui/material/Button'
import '@mui/material/Dialog'

declare module '@mui/material/Alert' {
  interface AlertPropsVariantOverrides {
    /** Transparent canvas banner with hairline border — workspace dialogs. */
    quiet: true
    /** Chrome panel with primary text — info callouts without severity tint. */
    panel: true
    /** Compact error strip — validation / submit failures in runtime editors. */
    errorStrip: true
    /** Compact neutral info strip — unchanged-state hints in runtime editors. */
    infoStrip: true
  }
}

declare module '@mui/material/Button' {
  interface ButtonPropsVariantOverrides {
    pill: true
    pillOutlined: true
    quietText: true
    quietOutlined: true
  }
}

declare module '@mui/material/styles' {
  interface InstrumentPalette {
    canvas: string
    chrome: string
    surface: string
    hairline: string
    accent: string
    accentSoft: string
    chipBg: string
    chipHoverBg: string
    inputBg: string
    codeBg: string
    success: string
    warning: string
    error: string
  }

  interface Palette {
    instrument: InstrumentPalette
  }
  interface PaletteOptions {
    instrument?: Partial<InstrumentPalette>
  }
}
