import { Box, CircularProgress, Stack, Typography } from '@mui/material'
import { appContainerTokens } from '../tokens'
import type { CompareScope } from './types'

/**
 * The five calm empty states the Changes tab can render.
 *
 * <p>All five share the same visual idiom as PreviewTab's
 * {@code NoHostnameState} / {@code WaitingForRuntimeState}: a centred
 * stack with muted body copy, no chrome, no traffic-light colour. The
 * tab's calm tone hinges on these reading like ledger marginalia rather
 * than error dialogs.</p>
 */

interface CenteredStackProps {
  children: React.ReactNode
}

function CenteredStack({ children }: CenteredStackProps) {
  return (
    <Stack
      spacing={1}
      sx={{
        alignSelf: 'center',
        textAlign: 'center',
        px: 4,
        maxWidth: 420,
        m: 'auto',
      }}
    >
      {children}
    </Stack>
  )
}

export function RuntimeOfflineState() {
  return (
    <CenteredStack>
      <Typography
        variant="body1"
        sx={{
          color: appContainerTokens.textPrimary,
          fontWeight: 500,
          letterSpacing: '-0.005em',
        }}
      >
        Diff unavailable
      </Typography>
      <Typography
        variant="body2"
        sx={{
          color: appContainerTokens.textMuted,
          letterSpacing: '-0.005em',
        }}
      >
        The runtime needs to be online before changes can be read from
        the working tree.
      </Typography>
    </CenteredStack>
  )
}

export function LoadingListState() {
  return (
    <CenteredStack>
      <Box sx={{ display: 'flex', justifyContent: 'center' }}>
        <CircularProgress
          size={20}
          thickness={4}
          sx={{ color: appContainerTokens.textMuted, opacity: 0.7 }}
        />
      </Box>
    </CenteredStack>
  )
}

interface NoChangesStateProps {
  scope: CompareScope
  /**
   * Name of the branch the user is currently on. When the active scope
   * is {@code branch} and this matches {@code scope.base}, we render a
   * tailored "you're on the base branch" message instead of the generic
   * "this branch matches the base" copy — that case is a self-compare
   * and is empty by definition, which would otherwise confuse the user.
   */
  currentBranch?: string
}

export function NoChangesState({ scope, currentBranch }: NoChangesStateProps) {
  let body: string
  switch (scope.kind) {
    case 'workingTree':
      body = 'Working tree is clean — every edit is committed.'
      break
    case 'branch':
      body =
        currentBranch && currentBranch === scope.base
          ? "You're on the base branch — nothing to compare against itself. Switch to Working tree above to see uncommitted edits, or pick a different base."
          : 'This branch matches the base.'
      break
    case 'commit':
    case 'range':
      body = 'These commits are identical.'
      break
  }
  return (
    <CenteredStack>
      <Typography
        variant="body1"
        sx={{
          color: appContainerTokens.textPrimary,
          fontWeight: 500,
          letterSpacing: '-0.005em',
        }}
      >
        No changes
      </Typography>
      <Typography
        variant="body2"
        sx={{
          color: appContainerTokens.textMuted,
          letterSpacing: '-0.005em',
        }}
      >
        {body}
      </Typography>
    </CenteredStack>
  )
}

export function ErrorState() {
  return (
    <CenteredStack>
      <Typography
        variant="body1"
        sx={{
          color: appContainerTokens.textPrimary,
          fontWeight: 500,
          letterSpacing: '-0.005em',
        }}
      >
        Couldn't load changes
      </Typography>
      <Typography
        variant="body2"
        sx={{
          color: appContainerTokens.textMuted,
          letterSpacing: '-0.005em',
        }}
      >
        The diff service didn't respond. Try Refresh — if it keeps
        failing, the runtime daemon may need a restart.
      </Typography>
    </CenteredStack>
  )
}

export function NoSelectionState() {
  return (
    <CenteredStack>
      <Typography
        variant="body2"
        sx={{
          color: appContainerTokens.textMuted,
          letterSpacing: '-0.005em',
        }}
      >
        Select a file to view its diff.
      </Typography>
    </CenteredStack>
  )
}

interface TruncatedFooterProps {
  shown: number
  total: number
}

export function TruncatedFooter({ shown, total }: TruncatedFooterProps) {
  return (
    <Box
      sx={{
        px: 1.5,
        py: 1,
        borderTop: `1px solid ${appContainerTokens.hairline}`,
      }}
    >
      <Typography
        variant="caption"
        sx={{
          color: appContainerTokens.textMuted,
          letterSpacing: '-0.005em',
          fontSize: '0.75rem',
        }}
      >
        Showing first {shown.toLocaleString()} files of {total.toLocaleString()}.
      </Typography>
    </Box>
  )
}
