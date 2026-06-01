import { Box, Tooltip, Typography } from '@mui/material'
import LinkOffIcon from '@mui/icons-material/LinkOff'
import {
  workspaceAccent,
  workspaceFontFamily,
  workspaceText,
} from '../designTokens'

/**
 * Minimal project shape needed by the pill. Kept structural rather than
 * importing {@code ProjectSummaryDto} / {@code ProjectDto} so this component
 * works for any caller that can supply the three fields below — including the
 * detached-projects DTO from the integrations surface.
 */
export interface DetachedGithubPillProjectInfo {
  /** The project id — used for {@code aria-label} disambiguation. */
  id: string
  /** Human-friendly project name — surfaced in the pill's accessible label. */
  name: string
  /** GitHub owner login the project remembers — surfaced in the tooltip. */
  githubRepoOwner: string
}

export interface DetachedGithubPillProps {
  /** The project this pill describes. */
  project: DetachedGithubPillProjectInfo
  /**
   * Fired when the user clicks the pill. Surfaces typically open the
   * {@code ReconnectProjectsDialog} preset to this single project.
   */
  onClick?: () => void
  /**
   * Compact mode for tight rows (sidebar). Renders just the icon inside a
   * small chip-shaped target, with the label tucked into a tooltip. The
   * default (full) mode shows the icon + "GitHub disconnected" text inline.
   */
  variant?: 'full' | 'compact'
  /** Optional class name. Plumbed through so callers can position the pill. */
  className?: string
}

/**
 * Quiet "GitHub disconnected" affordance shown next to a project name when
 * its installation has been soft-detached. Paper-tone (NOT red) — the
 * project still works as a record; clicking the pill opens the reconnect
 * flow.
 *
 * <p>Two variants share a single component so the visual idiom (paper chip,
 * link-off icon, bronze hover) stays consistent across surfaces:</p>
 * <ul>
 *   <li>{@code full} — icon + label, used in spacious lists (Projects page,
 *       landing tiles, banners).</li>
 *   <li>{@code compact} — icon-only target with a tooltip, used in the tight
 *       sidebar rows where horizontal real estate is at a premium.</li>
 * </ul>
 */
export function DetachedGithubPill({
  project,
  onClick,
  variant = 'full',
  className,
}: DetachedGithubPillProps) {
  const ariaLabel = `Reconnect ${project.name} to GitHub (currently disconnected)`
  const tooltipTitle = `GitHub disconnected — click to reconnect (${project.githubRepoOwner})`

  if (variant === 'compact') {
    return (
      <Tooltip title={tooltipTitle} placement="top" enterDelay={300}>
        <Box
          component="span"
          role={onClick ? 'button' : undefined}
          tabIndex={onClick ? 0 : undefined}
          onClick={
            onClick
              ? (e: React.MouseEvent) => {
                  // Sidebar rows are anchors — without this the click would
                  // both fire onClick and navigate.
                  e.preventDefault()
                  e.stopPropagation()
                  onClick()
                }
              : undefined
          }
          onKeyDown={
            onClick
              ? (e: React.KeyboardEvent) => {
                  // We render as a <span role="button"> (NOT a real <button>)
                  // so the pill is HTML-valid inside the row's anchor / button
                  // parent. Keyboard semantics have to be re-implemented here.
                  if (e.key === 'Enter' || e.key === ' ') {
                    e.preventDefault()
                    e.stopPropagation()
                    onClick()
                  }
                }
              : undefined
          }
          aria-label={onClick ? ariaLabel : undefined}
          className={className}
          sx={{
            all: 'unset',
            display: 'inline-flex',
            alignItems: 'center',
            justifyContent: 'center',
            width: 18,
            height: 18,
            borderRadius: 0.75,
            backgroundColor: 'instrument.chipBg',
            color: workspaceText.muted,
            cursor: onClick ? 'pointer' : 'default',
            flexShrink: 0,
            transition: 'background-color 150ms ease, color 150ms ease',
            '&:hover': onClick
              ? {
                  backgroundColor: 'instrument.chipHoverBg',
                  color: workspaceAccent.ink,
                }
              : undefined,
            '&:focus-visible': onClick
              ? {
                  outline: `2px solid ${workspaceAccent.ink}`,
                  outlineOffset: 1,
                }
              : undefined,
          }}
        >
          <LinkOffIcon sx={{ fontSize: 12 }} />
        </Box>
      </Tooltip>
    )
  }

  return (
    <Tooltip title={tooltipTitle} placement="top" enterDelay={400}>
      <Box
        component="span"
        role={onClick ? 'button' : undefined}
        tabIndex={onClick ? 0 : undefined}
        onClick={
          onClick
            ? (e: React.MouseEvent) => {
                e.preventDefault()
                e.stopPropagation()
                onClick()
              }
            : undefined
        }
        onKeyDown={
          onClick
            ? (e: React.KeyboardEvent) => {
                // <span role="button"> (NOT a real <button>) — see compact
                // branch above for the rationale. Keyboard semantics manual.
                if (e.key === 'Enter' || e.key === ' ') {
                  e.preventDefault()
                  e.stopPropagation()
                  onClick()
                }
              }
            : undefined
        }
        aria-label={onClick ? ariaLabel : undefined}
        className={className}
        sx={{
          all: 'unset',
          display: 'inline-flex',
          alignItems: 'center',
          gap: 0.5,
          px: 1,
          py: 0.25,
          borderRadius: 999,
          backgroundColor: 'instrument.chipBg',
          color: workspaceText.muted,
          cursor: onClick ? 'pointer' : 'default',
          flexShrink: 0,
          fontFamily: workspaceFontFamily.sans,
          transition: 'background-color 150ms ease, color 150ms ease',
          '&:hover': onClick
            ? {
                backgroundColor: 'instrument.chipHoverBg',
                color: workspaceAccent.ink,
              }
            : undefined,
          '&:focus-visible': onClick
            ? {
                outline: `2px solid ${workspaceAccent.ink}`,
                outlineOffset: 2,
              }
            : undefined,
        }}
      >
        <LinkOffIcon sx={{ fontSize: 12 }} />
        <Typography
          component="span"
          sx={{
            fontFamily: 'inherit',
            fontSize: '0.6875rem',
            lineHeight: 1,
            letterSpacing: '-0.005em',
            color: 'inherit',
            fontWeight: 500,
          }}
        >
          GitHub disconnected
        </Typography>
      </Box>
    </Tooltip>
  )
}
