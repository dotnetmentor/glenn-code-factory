import { Alert, CircularProgress, Stack, Typography } from '@mui/material'
import type { ReactNode } from 'react'
import { workspaceText } from '@/applications/workspace/shared/designTokens'

export interface RuntimeObservabilityGateProps {
  isLoading: boolean
  isError: boolean
  runtimeId: string | undefined
  children: ReactNode
}

/** Shared loading / error / empty gates for branch runtime observability tabs. */
export function RuntimeObservabilityGate({
  isLoading,
  isError,
  runtimeId,
  children,
}: RuntimeObservabilityGateProps) {
  if (isLoading) {
    return (
      <Stack
        direction="row"
        spacing={1.25}
        alignItems="center"
        sx={{ py: 4, justifyContent: 'center' }}
      >
        <CircularProgress size={16} sx={{ color: workspaceText.muted }} />
        <Typography variant="body2" sx={{ color: workspaceText.muted }}>
          Loading runtime…
        </Typography>
      </Stack>
    )
  }

  if (isError) {
    return (
      <Alert severity="error">
        Failed to load runtime. Try reopening the settings drawer.
      </Alert>
    )
  }

  if (!runtimeId) {
    return (
      <Typography variant="body2" sx={{ color: workspaceText.muted }}>
        No runtime has been provisioned yet — data will appear here once the
        daemon boots.
      </Typography>
    )
  }

  return children
}
