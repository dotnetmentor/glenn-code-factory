---
name: instrument-mui
description: Material UI styling for this project's Instrument Mono design system (MUI v7). Use when styling @mui/material components, choosing sx vs theme overrides, or wiring workspace colors. Combines official MUI guidance with our theme variants and tokens.
---

# Instrument Mono + Material UI

This project runs **MUI v7** (`@mui/material` ^7.3). Read `.agents/skills/material-ui-styling/AGENTS.md` first for the official styling decision tree (`sx` → `styled` → theme → global).

## Styling priority (narrowest scope first)

| Need | Use |
|------|-----|
| One-off layout tweak on a single instance | `sx` with **theme palette strings** (see below) |
| Same styled wrapper in many places | `styled()` in a colocated file |
| Every `Button` / `TextField` / `DataGrid` should change | `themes/sharedMuiOverrides.ts` builders, wired in `instrumentTheme.ts` |
| Raw HTML baseline | `MuiCssBaseline` in `sharedMuiOverrides.ts` |

**Do not** add new global theme overrides for a one-off screen. **Do not** import `workspaceColors` in `sx` when a theme key exists.

## Theme palette shortcuts (preferred in `sx`)

`instrumentTheme` exposes custom palette keys (with `cssVariables: true`):

```tsx
<Box sx={{
  bgcolor: 'instrument.canvas',      // warm paper shell
  borderColor: 'instrument.hairline',
  color: 'text.primary',
}} />

<Chip sx={{ color: 'instrument.warning' }} />   // semantic booting
<Alert severity="error" />                       // use MUI severity, not custom red hex
```

| Key | Meaning |
|-----|---------|
| `instrument.canvas` | Outer shell background (#F5F5F7) |
| `instrument.chrome` | White chrome columns (sidebar, header, chat) |
| `instrument.surface` | Raised cards / panels |
| `instrument.hairline` | 1px borders |
| `instrument.accent` | Near-black ink CTA |
| `instrument.accentSoft` | Hover wash |
| `instrument.chipBg` / `.chipHoverBg` | Row hover, quiet fills |
| `instrument.inputBg` / `.codeBg` | Form and code surfaces |
| `instrument.success` / `.warning` / `.error` | iOS semantic colors |

Built-in MUI keys also apply: `background.default`, `background.paper`, `divider`, `text.secondary`, `primary.main`, `error.main`.

## Theme alert variants (prefer over custom error strip sx)

```tsx
<Alert variant="errorStrip" severity="error" icon={<ErrorOutlineIcon sx={{ fontSize: 14 }} />}>
  Fix validation issues before submitting.
</Alert>
<Alert variant="infoStrip" severity="info" icon={<InfoOutlinedIcon sx={{ fontSize: 14 }} />}>
  No changes yet.
</Alert>
<Alert variant="quiet">...</Alert>
<Alert variant="panel" icon={false}>...</Alert>
```

**Never** use `workspaceButtonStyles` or bespoke error-strip `Box` sx — those were removed.

## Theme button variants (prefer over sx spreads)

```tsx
<Button variant="pill" color="primary">Save</Button>
<Button variant="pill" color="error">Delete</Button>
<Button variant="quietText">Cancel</Button>
<Button variant="quietOutlined">Back</Button>
<IconButton color="error" aria-label="Remove">...</IconButton>
```

## Design tokens (non-MUI surfaces only)

Import from `@/applications/workspace/shared/designTokens` when styling **non-MUI** markup or composing local token objects:

- `surfaceTokens`, `chromeTokens`, `semanticTokens` — three bundles only
- Typography presets: `pageTitleSx`, `captionSx`, `labelSx`
- Layout helpers: `pageCardSx`, `pageCardPaddedSx`

Do not re-export a fourth global bundle from `designTokens.ts`. App-container may compose locally in `app-container/tokens.ts`.

## File map

| File | Role |
|------|------|
| `themes/instrumentTokens.ts` | Canonical hex values |
| `themes/instrumentTheme.ts` | `createTheme` assembly (~200 lines) |
| `themes/sharedMuiOverrides.ts` | Shared `Mui*` override builders |
| `applications/workspace/shared/designTokens.ts` | Workspace-facing token bundles |
| `themes/muiAugmentation.d.ts` | Custom `Button`/`Alert` variants + `palette.instrument` |

## Anti-patterns

- MUI Grid — use `Box` flex (see `tech-stack` skill)
- Hardcoded bronze / legacy accent hex — use `instrument.*` or semantic tokens
- `sx={destructiveIconButtonSx}` — use `<IconButton color="error">`
- Importing `designTokens` colors in MUI `sx` when `instrument.*` palette key exists
- Bare global selectors like `.Mui-error { color: red }` — scope to component root (see MUI skill § state classes)

## Version note

Official `material-ui-styling` skill targets MUI v9. This project is on **v7** — APIs are compatible for styling guidance, but verify breaking changes before adopting v9-only features.
