import { Box, IconButton, Stack, Tooltip } from '@mui/material'
import RefreshOutlinedIcon from '@mui/icons-material/RefreshOutlined'
import { appContainerTokens } from '../tokens'
import { workspaceFontFamily } from '@/applications/workspace/shared/designTokens'
import { SecondaryHeader } from '../SecondaryHeader'
import { CompareAgainstPicker } from './CompareAgainstPicker'
import type { CompareScope } from './types'

interface ChangesChromeProps {
  /** Total file count from the latest changed-files response, or null. */
  fileCount: number | null
  totalAdditions: number | null
  totalDeletions: number | null
  /** Fires when the user clicks the refresh icon. */
  onRefresh: () => void
  /**
   * Indicates an in-flight fetch — used to subtly de-emphasise the
   * stats badge while the response is mid-air. We deliberately do
   * <em>not</em> show a spinner over the refresh button; a frantically
   * spinning glyph would compete for attention with the calm tone the
   * tab targets.
   */
  busy: boolean
  /** When true (runtime offline) the chrome desaturates to 0.55. */
  disabled: boolean
  /** Owned by the parent — drives the picker label and selected mode. */
  scope: CompareScope
  /** Promote a new scope to the parent. */
  onScopeChange: (next: CompareScope) => void
  /** Project id for the picker's commit-list cache scoping. */
  projectId: string
  /** Runtime id the picker queries for the commit list. */
  runtimeId: string
  /** Name of the branch the user is on — drives the "you're on base" copy. */
  currentBranch?: string
}

/**
 * Chrome bar above the file-list / diff split. Composes {@link
 * SecondaryHeader} so it inherits the reference design's shared recipe:
 * the same {@code workspaceChromeHeight} lid, the same 14px gutter, the
 * same "label · sub" typography pair the Specs and Kanban tabs use.
 *
 * <p>Layout, left → right:
 * <ul>
 *   <li>"Working changes" label.</li>
 *   <li>Sub-text: file count and {@code +N −N} delta in mono — mirrors
 *       the reference's "7 files · main" treatment, just swapping the
 *       branch hint for the diff stats since the scope picker already
 *       sits in the right cluster.</li>
 *   <li>Right cluster: a low-key refresh {@link IconButton} (lets the
 *       user re-pull without clicking through the picker) followed by
 *       the {@link CompareAgainstPicker}, the primary control on the
 *       bar.</li>
 * </ul></p>
 *
 * <p>{@code aria-live="polite"} stays on the sub badge so screen readers
 * announce changing file counts after a refresh.</p>
 */
export function ChangesChrome({
  fileCount,
  totalAdditions,
  totalDeletions,
  onRefresh,
  busy,
  disabled,
  scope,
  onScopeChange,
  projectId,
  runtimeId,
  currentBranch,
}: ChangesChromeProps) {
  const sub =
    fileCount === null ? null : (
      <Stack
        component="span"
        direction="row"
        spacing={0.75}
        alignItems="center"
        aria-live="polite"
        aria-atomic="true"
        sx={{
          // The sub slot is mono on this tab — file counts and delta
          // numbers want the figure-aligned treatment. The label still
          // renders in the SecondaryHeader's default sans.
          fontFamily: workspaceFontFamily.mono,
          fontSize: '0.71875rem',
          letterSpacing: '-0.005em',
          color: appContainerTokens.textFaint,
          opacity: busy ? 0.7 : 1,
          transition: 'opacity 200ms ease',
        }}
      >
        <Box component="span">
          {fileCount.toLocaleString()} {fileCount === 1 ? 'file' : 'files'}
        </Box>
        <Box component="span" aria-hidden sx={{ opacity: 0.5 }}>
          ·
        </Box>
        <Box component="span">+{(totalAdditions ?? 0).toLocaleString()}</Box>
        <Box component="span">−{(totalDeletions ?? 0).toLocaleString()}</Box>
      </Stack>
    )

  return (
    <SecondaryHeader
      label="Working changes"
      sub={sub}
      disabled={disabled}
      right={
        <Stack direction="row" spacing={0.5} alignItems="center">
          <Tooltip title="Refresh changed files" enterDelay={400}>
            <span>
              <IconButton
                size="small"
                onClick={onRefresh}
                disabled={disabled}
                aria-label="Refresh changed files"
                sx={{
                  width: 26,
                  height: 26,
                  borderRadius: '6px',
                  color: appContainerTokens.textMuted,
                  padding: 0,
                  transition:
                    'color 120ms ease, background-color 120ms ease',
                  '&:hover': {
                    color: appContainerTokens.textPrimary,
                    backgroundColor: appContainerTokens.chipHoverBg,
                  },
                  '&.Mui-disabled': {
                    color: appContainerTokens.textFaint,
                  },
                }}
              >
                <RefreshOutlinedIcon sx={{ fontSize: 14 }} />
              </IconButton>
            </span>
          </Tooltip>

          <CompareAgainstPicker
            projectId={projectId}
            runtimeId={runtimeId}
            scope={scope}
            onScopeChange={onScopeChange}
            currentBranch={currentBranch}
            disabled={disabled}
          />
        </Stack>
      }
    />
  )
}
