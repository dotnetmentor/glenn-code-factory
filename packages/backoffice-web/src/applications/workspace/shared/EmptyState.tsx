import type { ReactNode } from 'react'
import { Box, Button, Stack, Typography } from '@mui/material'
import {
  bodySx,
  captionSx,
  pageTitleSx,
  workspaceColors,
  workspaceText,
} from './designTokens'

/**
 * A friendly empty state for routed workspace pages.
 *
 * <p>Lifted from {@code ChatCanvas}'s empty-canvas composition — generous
 * vertical breathing room, a tracked-tight light-weight headline, muted body
 * copy underneath, and at most one near-black pill CTA. Use this anywhere a
 * list could be empty ("no projects yet", "no integrations", "no members").</p>
 *
 * <p>The CTA uses {@code variant="pill" color="primary"} so the affordance
 * reads as the same primary action a user has seen elsewhere in the workspace.</p>
 */
export interface EmptyStateProps {
  /**
   * Optional decorative element above the headline (e.g. a muted icon). Lives
   * in a 56px circle so individual icon sizes can vary without the layout
   * jumping.
   */
  icon?: ReactNode
  /** Tracked-tight light-weight headline. Should read as "thing is missing". */
  headline: ReactNode
  /** Muted body copy that explains what the user can do about it. */
  body?: ReactNode
  /** Optional primary action — rendered as a near-black pill. */
  cta?: {
    label: ReactNode
    onClick: () => void
    /** Pass-through disabled flag. */
    disabled?: boolean
  }
  /**
   * Optional second-line hint underneath the CTA — typically a "or" clause
   * pointing at a docs link / secondary path. Rendered in caption type.
   */
  secondaryHint?: ReactNode
  /**
   * When {@code true}, the empty state fills the parent height and centers
   * itself. Defaults to {@code false} so it can sit inline inside a section.
   */
  fillHeight?: boolean
}

export function EmptyState({
  icon,
  headline,
  body,
  cta,
  secondaryHint,
  fillHeight = false,
}: EmptyStateProps) {
  return (
    <Box
      sx={{
        display: 'flex',
        flexDirection: 'column',
        alignItems: 'center',
        justifyContent: 'center',
        textAlign: 'center',
        px: { xs: 2, md: 4 },
        py: { xs: 6, md: 8 },
        gap: 2,
        ...(fillHeight ? { flex: 1, minHeight: 0 } : {}),
      }}
    >
      {icon && (
        <Box
          aria-hidden
          sx={{
            width: 56,
            height: 56,
            borderRadius: '50%',
            display: 'inline-flex',
            alignItems: 'center',
            justifyContent: 'center',
            backgroundColor: workspaceColors.chromeBg,
            border: `1px solid ${workspaceColors.hairline}`,
            color: workspaceText.muted,
            mb: 0.5,
          }}
        >
          {icon}
        </Box>
      )}
      <Stack spacing={1.25} alignItems="center" sx={{ maxWidth: 460 }}>
        <Typography
          component="h2"
          sx={{
            ...pageTitleSx,
            fontSize: { xs: '1.375rem', md: '1.625rem' },
          }}
        >
          {headline}
        </Typography>
        {body && (
          <Typography component="p" sx={bodySx}>
            {body}
          </Typography>
        )}
      </Stack>
      {cta && (
        <Box sx={{ mt: 1.5 }}>
          <Button
            variant="pill" color="primary" onClick={cta.onClick}
            disabled={cta.disabled}
          >
            {cta.label}
          </Button>
        </Box>
      )}
      {secondaryHint && (
        <Typography
          component="p"
          sx={{ ...captionSx, color: workspaceText.faint, mt: 0.5 }}
        >
          {secondaryHint}
        </Typography>
      )}
    </Box>
  )
}
