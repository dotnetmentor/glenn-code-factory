/**
 * TurnFooter — the single quiet footer rendered beneath a terminal assistant
 * turn (cursor-native-chat-ux scene 6, card 7).
 *
 * <p>One line, neutral weight, type-driven. Never shouts. Examples:</p>
 * <pre>
 *   Finished in 14.2s · claude-sonnet-4 · 5 files edited · view PR ↗
 *   Cancelled after 6.1s · claude-sonnet-4
 *   Stopped after 2.3s · claude-sonnet-4
 *   Timed out after 30.0s · claude-sonnet-4
 * </pre>
 *
 * <p>Pieces are conditionally rendered — missing model / artifacts / PR are
 * gracefully omitted with their leading dot. If we have no RunResult at all
 * (event stream didn't emit one and the REST query 404'd), the footer
 * renders a minimal "Finished" / "Cancelled" line with no duration.</p>
 */
import { useState } from 'react'
import {
  Box,
  Dialog,
  DialogContent,
  DialogTitle,
  IconButton,
  List,
  ListItem,
  ListItemText,
  Typography,
} from '@mui/material'
import CloseIcon from '@mui/icons-material/Close'
import OpenInNewIcon from '@mui/icons-material/OpenInNew'
import type { RunResultDto } from '../../../../../api/queries-commands'
import { semanticTokens, surfaceTokens, workspaceText } from '../../../shared/designTokens'

// ── Public terminal status discriminator ───────────────────────────────────
//
// Aligned with the {@code RunStatus} enum from spec §3 — Finished /
// Cancelled / Error / Expired. The caller maps from the AgentEventRunStatus
// wire enum (Cancelled / Error / Expired same; "Finished" matches verbatim).
export type TerminalRunStatus = 'Finished' | 'Cancelled' | 'Error' | 'Expired'

export interface TurnFooterProps {
  /** Terminal run status — drives the verb ("Finished" / "Cancelled" / …). */
  status: TerminalRunStatus
  /**
   * The {@link RunResultDto} for this turn, or {@code null} when unavailable
   * (the daemon hasn't emitted one yet, the REST query failed, the row
   * predates RunResult plumbing). The footer degrades to a minimal "verb"-
   * only line in that case.
   */
  runResult?: RunResultDto | null
  /** Stable test id prefix so multiple footers on one page don't collide. */
  testIdPrefix?: string
}

// ── Helpers ────────────────────────────────────────────────────────────────

/**
 * Format a duration in ms as the most-compact human reading:
 *   < 60s → "14.2s"
 *   < 1h  → "1m 14s"
 *   ≥ 1h  → "1h 02m"
 */
export function humanizeDuration(ms: number): string {
  if (!Number.isFinite(ms) || ms < 0) return '0.0s'
  if (ms < 60_000) {
    return `${(ms / 1000).toFixed(1)}s`
  }
  const totalSeconds = Math.floor(ms / 1000)
  const totalMinutes = Math.floor(totalSeconds / 60)
  const seconds = totalSeconds % 60
  if (totalMinutes < 60) {
    return `${totalMinutes}m ${seconds.toString().padStart(2, '0')}s`
  }
  const hours = Math.floor(totalMinutes / 60)
  const minutes = totalMinutes % 60
  return `${hours}h ${minutes.toString().padStart(2, '0')}m`
}

/**
 * Map a terminal status to its verb form. {@code Finished} → "Finished in";
 * {@code Cancelled} → "Cancelled after"; {@code Error} → "Stopped after";
 * {@code Expired} → "Timed out after". Spec §6.
 */
export function statusVerb(
  status: TerminalRunStatus,
): { verb: string; tone: 'normal' | 'error' } {
  switch (status) {
    case 'Finished':
      return { verb: 'Finished in', tone: 'normal' }
    case 'Cancelled':
      return { verb: 'Cancelled after', tone: 'normal' }
    case 'Error':
      return { verb: 'Stopped after', tone: 'error' }
    case 'Expired':
      return { verb: 'Timed out after', tone: 'normal' }
  }
}

// ── Files dialog ───────────────────────────────────────────────────────────

interface FilesDialogProps {
  open: boolean
  onClose: () => void
  artifacts: RunResultDto['artifacts']
}

function FilesDialog({ open, onClose, artifacts }: FilesDialogProps) {
  return (
    <Dialog open={open} onClose={onClose} maxWidth="sm" fullWidth>
      <DialogTitle sx={{ pr: 6, fontSize: 16, fontWeight: 600 }}>
        Files edited
        <IconButton
          onClick={onClose}
          aria-label="Close"
          sx={{
            position: 'absolute',
            right: 8,
            top: 8,
            color: workspaceText.muted,
          }}
        >
          <CloseIcon fontSize="small" />
        </IconButton>
      </DialogTitle>
      <DialogContent sx={{ pt: 0 }}>
        {artifacts.length === 0 ? (
          <Typography
            component="div"
            sx={{
              fontSize: 13,
              color: workspaceText.muted,
              fontStyle: 'italic',
              py: 1,
            }}
          >
            No files recorded for this turn.
          </Typography>
        ) : (
          <List dense disablePadding>
            {artifacts.map((a) => (
              <ListItem
                key={a.path}
                disablePadding
                sx={{
                  borderBottom: `1px solid ${surfaceTokens.hairline}`,
                  py: 0.5,
                }}
              >
                <ListItemText
                  primary={a.path}
                  primaryTypographyProps={{
                    sx: {
                      fontFamily:
                        'ui-monospace, SFMono-Regular, "SF Mono", Menlo, monospace',
                      fontSize: 12.5,
                      color: workspaceText.primary,
                      wordBreak: 'break-all',
                    },
                  }}
                  secondary={`${(a.sizeBytes / 1024).toFixed(1)} KB`}
                  secondaryTypographyProps={{
                    sx: {
                      fontSize: 11,
                      color: workspaceText.faint,
                    },
                  }}
                />
              </ListItem>
            ))}
          </List>
        )}
      </DialogContent>
    </Dialog>
  )
}

// ── Footer ─────────────────────────────────────────────────────────────────

const DOT = '·'

export function TurnFooter({ status, runResult, testIdPrefix }: TurnFooterProps) {
  const [filesDialogOpen, setFilesDialogOpen] = useState(false)

  const tid = (suffix: string) =>
    testIdPrefix ? `${testIdPrefix}-${suffix}` : suffix

  const { verb, tone } = statusVerb(status)
  const durationText = runResult ? humanizeDuration(runResult.durationMs) : null
  const model = runResult?.model ?? null
  const artifacts = runResult?.artifacts ?? []
  const prUrl = runResult?.gitPrUrl ?? null

  const labelColor =
    tone === 'error' ? semanticTokens.error : workspaceText.muted

  // Build the inline pieces. Empty strings get filtered out so we don't
  // emit "Finished in · · · · view PR" on degenerate inputs.
  const pieces: Array<React.ReactNode> = []

  pieces.push(
    <Box
      key="status"
      component="span"
      data-testid={tid('footer-status')}
      sx={{ color: labelColor }}
    >
      {durationText ? `${verb} ${durationText}` : status}
    </Box>,
  )

  if (model) {
    pieces.push(
      <Box
        key="model"
        component="code"
        data-testid={tid('footer-model')}
        sx={{
          fontFamily:
            'ui-monospace, SFMono-Regular, "SF Mono", Menlo, monospace',
          fontSize: 11,
          color: workspaceText.faint,
          backgroundColor: surfaceTokens.chipBg,
          border: `1px solid ${surfaceTokens.hairline}`,
          borderRadius: '4px',
          px: 0.625,
          py: 0.125,
        }}
      >
        {model}
      </Box>,
    )
  }

  if (artifacts.length > 0) {
    pieces.push(
      <Box
        key="files"
        component="button"
        type="button"
        onClick={() => setFilesDialogOpen(true)}
        data-testid={tid('footer-files-chip')}
        sx={{
          // Reset native button chrome.
          background: 'none',
          border: `1px solid ${surfaceTokens.hairline}`,
          padding: '1px 8px',
          borderRadius: '999px',
          cursor: 'pointer',
          fontFamily: 'inherit',
          fontSize: 11.5,
          color: workspaceText.muted,
          letterSpacing: '0.005em',
          transition: 'background-color 180ms ease, color 180ms ease, border-color 180ms ease',
          '&:hover': {
            color: workspaceText.primary,
            backgroundColor: surfaceTokens.chipHoverBg,
            borderColor: surfaceTokens.hairlineStrong,
          },
          '&:focus-visible': {
            outline: 'none',
            color: workspaceText.primary,
            borderColor: surfaceTokens.hairlineStrong,
          },
        }}
      >
        {artifacts.length} {artifacts.length === 1 ? 'file' : 'files'} edited
      </Box>,
    )
  }

  if (prUrl) {
    pieces.push(
      <Box
        key="pr"
        component="a"
        href={prUrl}
        target="_blank"
        rel="noopener noreferrer"
        data-testid={tid('footer-pr-chip')}
        sx={{
          display: 'inline-flex',
          alignItems: 'center',
          gap: 0.375,
          textDecoration: 'none',
          padding: '1px 8px',
          borderRadius: '999px',
          border: `1px solid ${surfaceTokens.hairline}`,
          fontSize: 11.5,
          color: workspaceText.muted,
          letterSpacing: '0.005em',
          transition: 'background-color 180ms ease, color 180ms ease, border-color 180ms ease',
          '&:hover': {
            color: workspaceText.primary,
            backgroundColor: surfaceTokens.chipHoverBg,
            borderColor: surfaceTokens.hairlineStrong,
          },
          '&:focus-visible': {
            outline: 'none',
            color: workspaceText.primary,
            borderColor: surfaceTokens.hairlineStrong,
          },
        }}
      >
        view PR
        <OpenInNewIcon sx={{ fontSize: 11 }} />
      </Box>,
    )
  }

  // Interleave with the dot separator. Pure render, no list mutation.
  const interleaved: React.ReactNode[] = []
  pieces.forEach((node, i) => {
    if (i > 0) {
      interleaved.push(
        <Box
          key={`dot-${i}`}
          component="span"
          aria-hidden
          sx={{ color: workspaceText.faint, mx: 0.5 }}
        >
          {DOT}
        </Box>,
      )
    }
    interleaved.push(node)
  })

  return (
    <>
      <Box
        data-testid={tid('turn-footer')}
        data-status={status}
        sx={{
          display: 'inline-flex',
          alignItems: 'center',
          flexWrap: 'wrap',
          gap: 0.25,
          fontSize: 12,
          color: workspaceText.muted,
          letterSpacing: '0.005em',
          // Footer sits beneath the bubble at the bubble's left edge; the
          // parent provides horizontal alignment. No background, no border —
          // type-driven only.
          mt: 0.5,
        }}
      >
        {interleaved}
      </Box>
      <FilesDialog
        open={filesDialogOpen}
        onClose={() => setFilesDialogOpen(false)}
        artifacts={artifacts}
      />
    </>
  )
}
