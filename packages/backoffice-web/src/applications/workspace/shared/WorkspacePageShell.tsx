import type { ReactNode } from 'react'
import { Box, Stack, Typography } from '@mui/material'
import {
  bodySx,
  captionSx,
  pageCardFlushSx,
  pageTitleSx,
  sectionTitleSx,
  workspaceSpacing,
  workspaceText,
} from './designTokens'

/**
 * The outer page container for routed workspace pages.
 *
 * <p>Owns the warm-paper canvas background, the max-width column the page
 * lives in, and the vertical padding rhythm. Pages slot their header +
 * sections inside.</p>
 *
 * <p>Anchored to the same 42px breadcrumb spine height as
 * {@code ProjectWorkspaceShell} — pages that are mounted at the top level
 * (Dashboard / Projects / Members) get their padding here; pages nested
 * inside the project-workspace shell already have their canvas from the
 * shell and should NOT wrap themselves a second time.</p>
 */
export interface WorkspacePageShellProps {
  children: ReactNode
  /**
   * Override the max-width of the centered column. Defaults to {@code 1080px}
   * — wide enough for two-column member/integration lists, narrow enough for
   * the eye to track top-to-bottom without a horizontal swim.
   */
  maxWidth?: number | string
  /**
   * When {@code true}, the shell fills the viewport height and lets the
   * children manage their own scroll region. Defaults to {@code false}, where
   * the shell scrolls naturally with the page.
   */
  fullHeight?: boolean
}

export function WorkspacePageShell({
  children,
  maxWidth = 1080,
  fullHeight = false,
}: WorkspacePageShellProps) {
  return (
    <Box
      sx={{
        backgroundColor: 'instrument.canvas',
        color: workspaceText.primary,
        minHeight: fullHeight ? '100vh' : '100%',
        width: '100%',
        display: 'flex',
        flexDirection: 'column',
      }}
    >
      <Box
        sx={{
          width: '100%',
          maxWidth,
          mx: 'auto',
          px: { xs: 3, md: 5 },
          py: { xs: 4, md: 6 },
          display: 'flex',
          flexDirection: 'column',
          gap: { xs: 3, md: 4 },
          flex: fullHeight ? 1 : 'unset',
          minHeight: fullHeight ? 0 : 'unset',
        }}
      >
        {children}
      </Box>
    </Box>
  )
}

/**
 * The page heading row — the 42px-spine alignment block.
 *
 * <p>Renders the tracked-tight page title on the left and an optional slot of
 * action affordances on the right. The subtitle (when present) sits directly
 * beneath the title in muted body copy.</p>
 *
 * <p>Pages MUST NOT render their own {@code variant="h4"} +
 * {@code variant="overline"} stack — that's the stock-MUI look the design
 * overhaul replaces. Use this primitive instead.</p>
 */
export interface WorkspacePageHeaderProps {
  /** The page title. Rendered in light-weight tracked-tight typography. */
  title: ReactNode
  /** Optional muted line beneath the title. Plain prose, not an overline. */
  subtitle?: ReactNode
  /** Optional right-slotted actions (typically a single near-black pill). */
  actions?: ReactNode
}

export function WorkspacePageHeader({
  title,
  subtitle,
  actions,
}: WorkspacePageHeaderProps) {
  return (
    <Box
      component="header"
      sx={{
        // Match the breadcrumb-spine alignment rhythm — title baseline sits
        // at the same vertical cadence as the breadcrumb above the canvas.
        minHeight: workspaceSpacing.breadcrumbSpine,
        display: 'flex',
        alignItems: 'flex-start',
        justifyContent: 'space-between',
        gap: 2,
        flexWrap: 'wrap',
      }}
    >
      <Box sx={{ minWidth: 0, flex: 1 }}>
        <Typography component="h1" sx={pageTitleSx}>
          {title}
        </Typography>
        {subtitle && (
          <Typography component="p" sx={{ ...bodySx, mt: 0.75 }}>
            {subtitle}
          </Typography>
        )}
      </Box>
      {actions && (
        <Stack
          direction="row"
          spacing={1}
          alignItems="center"
          sx={{ flexShrink: 0, mt: 0.5 }}
        >
          {actions}
        </Stack>
      )}
    </Box>
  )
}

/**
 * A hairline-bordered content block — the "less cruddy" replacement for the
 * stock {@code <Card><CardContent>} pair.
 *
 * <p>Uses the theme {@link pageCardFlushSx} token — white inset card on the
 * cream canvas, hairline border, no shadow. The optional title sits inside
 * the block as a calm section header.</p>
 */
export interface WorkspaceSectionProps {
  /** Optional section title. Rendered in the {@link sectionTitleSx} preset. */
  title?: ReactNode
  /** Optional caption beneath the title. */
  description?: ReactNode
  /** Optional right-slotted actions inside the section header. */
  actions?: ReactNode
  /** Section body. */
  children: ReactNode
  /**
   * When {@code true}, drops the hairline border — useful for "first child"
   * sections that sit flush against the page header. Defaults to {@code false}.
   */
  unbordered?: boolean
  /**
   * When {@code true}, drops the internal padding so callers can render a
   * full-bleed list (e.g. hairline-separated row stack). Defaults to
   * {@code false}.
   */
  flush?: boolean
}

export function WorkspaceSection({
  title,
  description,
  actions,
  children,
  unbordered = false,
  flush = false,
}: WorkspaceSectionProps) {
  const hasHeader = title !== undefined || actions !== undefined
  return (
    <Box
      component="section"
      sx={
        unbordered
          ? { backgroundColor: 'transparent' }
          : pageCardFlushSx
      }
    >
      {hasHeader && (
        <Box
          sx={{
            px: flush ? { xs: 2.5, md: 3 } : { xs: 2.5, md: 3 },
            pt: flush ? { xs: 2, md: 2.5 } : { xs: 2.5, md: 3 },
            pb: flush ? { xs: 2, md: 2.5 } : 0,
            display: 'flex',
            alignItems: 'flex-start',
            justifyContent: 'space-between',
            gap: 2,
            flexWrap: 'wrap',
            borderBottom: flush ? 1 : 0,
            borderColor: 'instrument.hairline',
          }}
        >
          <Box sx={{ minWidth: 0, flex: 1 }}>
            {title && (
              <Typography component="h2" sx={sectionTitleSx}>
                {title}
              </Typography>
            )}
            {description && (
              <Typography component="p" sx={{ ...captionSx, mt: 0.5 }}>
                {description}
              </Typography>
            )}
          </Box>
          {actions && (
            <Stack
              direction="row"
              spacing={1}
              alignItems="center"
              sx={{ flexShrink: 0 }}
            >
              {actions}
            </Stack>
          )}
        </Box>
      )}
      <Box
        sx={{
          px: flush ? 0 : { xs: 2.5, md: 3 },
          pt: flush ? 0 : hasHeader ? 2 : { xs: 2.5, md: 3 },
          pb: flush ? 0 : { xs: 2.5, md: 3 },
        }}
      >
        {children}
      </Box>
    </Box>
  )
}
